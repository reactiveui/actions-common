// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static System.Environment;

if (args is not [var entryJson, var settingsJson, var resultsDir, var workDir])
{
    Console.WriteLine("::error::Expected the repository entry, settings, results folder and work folder arguments.");
    return Program.UsageError;
}

return await Program.RunAsync(entryJson, settingsJson, resultsDir, workDir, TimeProvider.System);

/// <summary>Moves a repository's ReactiveUI package versions to the train's releases through a pull request, then merges it once its checks pass.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The exit code for a failed step.</summary>
    private const int Failure = 1;

    /// <summary>The result status of a repository that released in this run.</summary>
    private const string Released = "released";

    /// <summary>The value that marks a property shared by packages at different versions.</summary>
    private const string Conflicted = "";

    /// <summary>The time between two looks at the pull request checks.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for required checks before waiting for every check instead.</summary>
    private static readonly TimeSpan RequiredChecksGrace = TimeSpan.FromMinutes(3);

    /// <summary>Runs the update.</summary>
    /// <param name="entryJson">The repository entry from the plan.</param>
    /// <param name="settingsJson">The train settings from the plan.</param>
    /// <param name="resultsDir">The folder holding the earlier levels' results.</param>
    /// <param name="workDir">The folder for the clone and the update record.</param>
    /// <param name="clock">The clock for the waits and the app token.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(string entryJson, string settingsJson, string resultsDir, string workDir, TimeProvider clock)
    {
        var entry = JsonSerializer.Deserialize(entryJson, TrainJson.Default.TrainEntry)!;
        var settings = JsonSerializer.Deserialize(settingsJson, TrainJson.Default.TrainSettings)!;
        var outputs = GetEnvironmentVariable("GITHUB_OUTPUT")!;
        AppToken.Repository = entry.Repository;
        AppToken.Clock = clock;
        _ = Directory.CreateDirectory(workDir);

        var results = ReadResults(resultsDir);
        if (BlockedReason(entry, results) is { Length: > 0 } reason)
        {
            Console.WriteLine($"::warning::{reason}");
            await File.AppendAllTextAsync(outputs, $"blocked=true\nreason={reason}\n").ConfigureAwait(false);
            return 0;
        }

        var packages = await CollectPackagesAsync(entry, settings, results).ConfigureAwait(false);
        var (failure, openPullRequest, files) = await CloneAsync(entry, settings, workDir).ConfigureAwait(false);
        if (failure is not 0)
        {
            return failure;
        }

        var rewriter = new Rewriter(packages);
        rewriter.Rewrite(files);
        foreach (var note in rewriter.Notes)
        {
            Console.WriteLine($"::notice::{entry.Name}: {note}");
        }

        var log = JsonSerializer.SerializeToUtf8Bytes(new(rewriter.Updates, rewriter.Notes), TrainJson.Default.UpdateLog);
        await File.WriteAllBytesAsync(Path.Combine(workDir, "updates.json"), log).ConfigureAwait(false);
        Console.WriteLine($"{rewriter.Updates.Count} version(s) to update in {entry.Repository}.");
        if (rewriter.Updates is [] && openPullRequest is null)
        {
            await File.AppendAllTextAsync(outputs, "blocked=false\nchanged=false\n").ConfigureAwait(false);
            return 0;
        }

        return await PublishAsync(new(entry, settings, workDir, outputs, clock), rewriter.Updates, openPullRequest).ConfigureAwait(false);
    }

    /// <summary>Reads the results of the earlier levels.</summary>
    /// <param name="resultsDir">The results folder, which level 0 does not have.</param>
    /// <returns>The results by repository name.</returns>
    private static Dictionary<string, TrainResult> ReadResults(string resultsDir)
    {
        Dictionary<string, TrainResult> results = [with(StringComparer.OrdinalIgnoreCase)];
        if (!Directory.Exists(resultsDir))
        {
            return results;
        }

        foreach (var file in Directory.EnumerateFiles(resultsDir, "*.json"))
        {
            var result = JsonSerializer.Deserialize(File.ReadAllBytes(file), TrainJson.Default.TrainResult)!;
            results[result.Name] = result;
        }

        return results;
    }

    /// <summary>Explains why the repository waits, when an upstream repository did not release in this run.</summary>
    /// <param name="entry">The repository being updated.</param>
    /// <param name="results">The earlier levels' results.</param>
    /// <returns>The reason, or empty when nothing blocks the repository.</returns>
    private static string BlockedReason(TrainEntry entry, Dictionary<string, TrainResult> results)
    {
        List<string> unreleased = [];
        foreach (var upstream in entry.WaitsFor)
        {
            if (results.GetValueOrDefault(upstream)?.Status is not Released)
            {
                unreleased.Add(upstream);
            }
        }

        return unreleased is [] ? string.Empty : $"Blocked because {string.Join(", ", unreleased)} did not release in this run.";
    }

    /// <summary>Finds the version of every package the other repositories supply: this run's release when it has one, else the latest release.</summary>
    /// <param name="entry">The repository being updated.</param>
    /// <param name="settings">The train settings.</param>
    /// <param name="results">The earlier levels' results.</param>
    /// <returns>The packages by id.</returns>
    private static async Task<Dictionary<string, PackagePin>> CollectPackagesAsync(TrainEntry entry, TrainSettings settings, Dictionary<string, TrainResult> results)
    {
        Dictionary<string, PackagePin> packages = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var source in settings.Catalog)
        {
            if (source.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (results.GetValueOrDefault(source.Name) is { Status: Released } result)
            {
                foreach (var id in result.Packages)
                {
                    packages[id] = new(id, result.Version!, $"{source.Name} (this run)");
                }

                continue;
            }

            if (await GhAsync("api", $"repos/{source.Repository}/releases/latest").ConfigureAwait(false) is not { ExitStatus.ExitCode: 0, StandardOutput: var latestJson })
            {
                Console.WriteLine($"{source.Repository} has no latest release. Its packages stay as they are.");
                continue;
            }

            var (version, ids) = ReleasePackages(JsonSerializer.Deserialize(latestJson, TrainJson.Default.GitHubRelease)!);
            foreach (var id in ids)
            {
                packages[id] = new(id, version!, $"{source.Name} (latest release)");
            }
        }

        foreach (var id in entry.IgnorePackages)
        {
            _ = packages.Remove(id);
        }

        return packages;
    }

    /// <summary>Finds the open update pull request, then clones the branch to update and lists its project files.</summary>
    /// <param name="entry">The repository being updated.</param>
    /// <param name="settings">The train settings.</param>
    /// <param name="workDir">The folder for the clone.</param>
    /// <returns>Zero or the failing exit code, the open pull request, and the project files.</returns>
    private static async Task<(int ExitCode, PullRequestItem? OpenPullRequest, string[] Files)> CloneAsync(TrainEntry entry, TrainSettings settings, string workDir)
    {
        if (await GhAsync("auth", "setup-git").ConfigureAwait(false) is { ExitStatus.ExitCode: not 0 } setup)
        {
            Console.WriteLine($"::error::Could not set up git credentials: {setup.StandardError.Trim()}");
            return (Failure, null, []);
        }

        var pullRequests = await GhAsync("pr", "list", "--repo", entry.Repository, "--head", settings.UpdateBranch, "--base", entry.Branch, "--state", "open", "--json", "number,url")
            .ConfigureAwait(false);
        if (pullRequests is not { ExitStatus.ExitCode: 0 })
        {
            Console.WriteLine($"::error::Could not list pull requests in {entry.Repository}: {pullRequests.StandardError.Trim()}");
            return (Failure, null, []);
        }

        var listedPullRequests = JsonSerializer.Deserialize(pullRequests.StandardOutput, TrainJson.Default.ListPullRequestItem)!;
        var open = listedPullRequests.Count > 0 ? listedPullRequests[0] : null;
        var clone = Path.Combine(workDir, "repository");
        var cloneBranch = open is null ? entry.Branch : settings.UpdateBranch;
        await AppToken.RefreshAsync().ConfigureAwait(false);
        if (await Process.RunAsync("git", ["clone", "--depth", "1", "--branch", cloneBranch, $"https://github.com/{entry.Repository}.git", clone]).ConfigureAwait(false) is { ExitCode: not 0 } cloned)
        {
            Console.WriteLine($"::error::Could not clone {entry.Repository} at {cloneBranch}.");
            return (cloned.ExitCode, open, []);
        }

        Directory.SetCurrentDirectory(clone);
        if (await Process.RunAndCaptureTextAsync("git", ["ls-files", "-z", "--", "*.props", "*.targets", "*.csproj", "*.fsproj", "*.vbproj"]).ConfigureAwait(false)
            is not { ExitStatus.ExitCode: 0, StandardOutput: var listed })
        {
            Console.WriteLine("::error::Could not list the project files.");
            return (Failure, open, []);
        }

        return (0, open, listed.Split('\0', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Pushes the updates, opens or comments on the pull request, then merges it once its checks pass.</summary>
    /// <param name="run">The update run.</param>
    /// <param name="updates">The updates made.</param>
    /// <param name="openPullRequest">The open update pull request, if any.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> PublishAsync(UpdateRun run, List<VersionUpdate> updates, PullRequestItem? openPullRequest)
    {
        var (entry, settings, workDir, outputs, clock) = run;
        var runUrl = $"{GetEnvironmentVariable("GITHUB_SERVER_URL")}/{GetEnvironmentVariable("GITHUB_REPOSITORY")}/actions/runs/{GetEnvironmentVariable("GITHUB_RUN_ID")}";
        if (updates is not [] && await CommitAndPushAsync(updates, settings.UpdateBranch, entry.Repository, runUrl, openPullRequest is null).ConfigureAwait(false) is not 0 and var pushFailure)
        {
            return pushFailure;
        }

        var body = Path.Combine(workDir, "pull-request.md");
        await File.WriteAllTextAsync(body, $"The [release train]({runUrl}) updates these package versions before it releases {entry.Name}.\n\n{Table(updates)}").ConfigureAwait(false);
        string pullRequest;
        if (openPullRequest is null)
        {
            string[] create =
            [
                "pr", "create", "--repo", entry.Repository, "--base", entry.Branch, "--head", settings.UpdateBranch,
                "--title", "chore(deps): update ReactiveUI package versions", "--body-file", body,
            ];
            if (await GhAsync(create).ConfigureAwait(false) is not { ExitStatus.ExitCode: 0, StandardOutput: var created })
            {
                Console.WriteLine($"::error::Could not open a pull request in {entry.Repository}.");
                return Failure;
            }

            pullRequest = created.Trim();
        }
        else
        {
            pullRequest = openPullRequest.Url;
            if (updates is not [])
            {
                _ = await GhAsync("pr", "comment", pullRequest, "--body-file", body).ConfigureAwait(false);
            }
        }

        await File.AppendAllTextAsync(outputs, $"blocked=false\nchanged=true\npull-request={pullRequest}\n").ConfigureAwait(false);
        Console.WriteLine($"Waiting for the checks on {pullRequest}");
        return await MergeWhenGreenAsync(entry, settings.ChecksMinutes, pullRequest, clock).ConfigureAwait(false);
    }

    /// <summary>Commits the updates as the app's bot user and pushes them to the update branch.</summary>
    /// <param name="updates">The updates made.</param>
    /// <param name="updateBranch">The pull request branch.</param>
    /// <param name="repository">The repository, as owner/name.</param>
    /// <param name="runUrl">This train run.</param>
    /// <param name="replace">Whether the branch starts again from the base branch.</param>
    /// <returns>Zero, or the failing exit code.</returns>
    private static async Task<int> CommitAndPushAsync(List<VersionUpdate> updates, string updateBranch, string repository, string runUrl, bool replace)
    {
        var bot = $"{await AppToken.SlugAsync().ConfigureAwait(false)}[bot]";
        var botId = (await GhAsync("api", $"users/{bot}", "--jq", ".id").ConfigureAwait(false)).StandardOutput.Trim();
        HashSet<string> items = [with(StringComparer.Ordinal)];
        foreach (var update in updates)
        {
            _ = items.Add(update.Item);
        }

        string[] commit =
        [
            "-c", $"user.name={bot}", "-c", $"user.email={botId}+{bot}@users.noreply.github.com", "commit", "--all",
            "--message", $"chore(deps): update {items.Count} ReactiveUI package version(s)", "--message", $"Release train run {runUrl}",
        ];
        if (await Process.RunAsync("git", commit).ConfigureAwait(false) is { ExitCode: not 0 } committed)
        {
            Console.WriteLine("::error::Could not commit the version updates.");
            return committed.ExitCode;
        }

        // A new branch starts from the base branch, so it replaces any stale branch left by a closed pull request.
        string[] push = replace ? ["push", "--force", "origin", $"HEAD:refs/heads/{updateBranch}"] : ["push", "origin", $"HEAD:refs/heads/{updateBranch}"];
        await AppToken.RefreshAsync().ConfigureAwait(false);
        if (await Process.RunAsync("git", push).ConfigureAwait(false) is { ExitCode: not 0 } pushed)
        {
            Console.WriteLine($"::error::Could not push {updateBranch} to {repository}.");
            return pushed.ExitCode;
        }

        return 0;
    }

    /// <summary>Writes the pull request table of updates.</summary>
    /// <param name="updates">The updates made.</param>
    /// <returns>The Markdown table.</returns>
    private static string Table(List<VersionUpdate> updates)
    {
        StringBuilder table = new();
        _ = table.AppendLine("| File | Package or property | From | To | Source |").AppendLine("|---|---|---|---|---|");
        foreach (var update in updates)
        {
            _ = table.AppendLine(CultureInfo.InvariantCulture, $"| `{update.File}` | `{update.Item}` | {update.From} | {update.To} | {update.Source} |");
        }

        return table.ToString();
    }

    /// <summary>Waits for the pull request checks and merges it once they pass.</summary>
    /// <param name="entry">The repository being updated.</param>
    /// <param name="checksMinutes">How long the checks may take.</param>
    /// <param name="pullRequest">The pull request URL.</param>
    /// <param name="clock">The clock for the waits.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> MergeWhenGreenAsync(TrainEntry entry, int checksMinutes, string pullRequest, TimeProvider clock)
    {
        string[] admin = entry.AdminMerge ? ["--admin"] : [];
        string[] merge = ["pr", "merge", pullRequest, "--squash", "--delete-branch", .. admin];
        var started = clock.GetUtcNow();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(checksMinutes), clock);
        using var poll = new PeriodicTimer(CheckInterval, clock);
        try
        {
            while (await poll.WaitForNextTickAsync(timeout.Token).ConfigureAwait(false))
            {
                // Required checks decide the merge. A repository with no required checks waits for every check instead.
                var checks = await ChecksAsync(pullRequest, required: true, timeout.Token).ConfigureAwait(false);
                if (checks is [] && clock.GetUtcNow() - started > RequiredChecksGrace)
                {
                    checks = await ChecksAsync(pullRequest, required: false, timeout.Token).ConfigureAwait(false);
                }

                var (failed, pending) = Tally(checks);
                if (failed is not [])
                {
                    Console.WriteLine($"::error::Checks failed on {pullRequest}: {string.Join(", ", failed)}. Fix the pull request, merge it, then resume the train from {entry.Name}.");
                    return Failure;
                }

                if (checks is [] || pending)
                {
                    continue;
                }

                // Branch protection rejects the merge while a required check has not reported yet, so a failed merge waits again.
                if (await GhAsync(merge).ConfigureAwait(false) is { ExitStatus.ExitCode: not 0 } merged)
                {
                    Console.WriteLine($"Merge not possible yet: {merged.StandardError.Trim()}");
                    continue;
                }

                Console.WriteLine($"Merged {pullRequest}");
                return 0;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // The checks ran out of time; the error below reports it.
        }

        Console.WriteLine($"::error::{pullRequest} did not pass its checks within {checksMinutes} minutes.");
        return Failure;
    }

    /// <summary>Sorts the checks into failed ones and whether any is still running.</summary>
    /// <param name="checks">The checks.</param>
    /// <returns>The failed or cancelled check names, and whether any check is pending.</returns>
    private static (List<string> Failed, bool Pending) Tally(List<CheckRun> checks)
    {
        List<string> failed = [];
        var pending = false;
        foreach (var check in checks)
        {
            if (check.Bucket is "fail" or "cancel")
            {
                failed.Add(check.Name);
            }

            pending |= check.Bucket is "pending";
        }

        return (failed, pending);
    }

    /// <summary>Runs gh with a fresh installation token.</summary>
    /// <param name="arguments">The gh arguments.</param>
    /// <returns>The captured output.</returns>
    private static async Task<ProcessTextOutput> GhAsync(params IEnumerable<string> arguments)
    {
        await AppToken.RefreshAsync().ConfigureAwait(false);
        return await Process.RunAndCaptureTextAsync("gh", arguments).ConfigureAwait(false);
    }

    /// <summary>Reads the pull request checks.</summary>
    /// <param name="pullRequest">The pull request URL.</param>
    /// <param name="required">Whether to read only the required checks.</param>
    /// <param name="cancellationToken">Stops the read when the checks run out of time.</param>
    /// <returns>The checks, or none when gh reports none.</returns>
    private static async Task<List<CheckRun>> ChecksAsync(string pullRequest, bool required, CancellationToken cancellationToken)
    {
        string[] filter = required ? ["--required"] : [];
        await AppToken.RefreshAsync().ConfigureAwait(false);
        var output = await Process.RunAndCaptureTextAsync("gh", ["pr", "checks", pullRequest, "--json", "name,bucket", .. filter], cancellationToken).ConfigureAwait(false);
        return output.StandardOutput.AsSpan().TrimStart().StartsWith('[')
            ? JsonSerializer.Deserialize(output.StandardOutput, TrainJson.Default.ListCheckRun)!
            : [];
    }

    /// <summary>Finds the release version and the ids of the packages attached to it.</summary>
    /// <param name="release">The GitHub release.</param>
    /// <returns>The version and package ids, or no version and no ids when no asset matches.</returns>
    private static (string? Version, List<string> Ids) ReleasePackages(GitHubRelease release)
    {
        const string PackageExtension = ".nupkg";
        string[] candidates = [release.Name ?? string.Empty, release.TagName, release.TagName.TrimStart('v')];
        foreach (var version in candidates)
        {
            if (version is { Length: 0 })
            {
                continue;
            }

            var suffix = $".{version}{PackageExtension}";
            List<string> ids = [];
            foreach (var asset in release.Assets)
            {
                // Every package asset ends in .nupkg, so the version suffix alone picks them out.
                if (asset.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(asset.Name[..^suffix.Length]);
                }
            }

            if (ids is not [])
            {
                return (version, ids);
            }
        }

        return (null, []);
    }

    /// <summary>Matches a package item element.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"<(?:PackageVersion|PackageReference|GlobalPackageReference)\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex PackageItem();

    /// <summary>Matches the package id attribute.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"\b(?:Include|Update)\s*=\s*""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex IncludeAttribute();

    /// <summary>Matches a version attribute, capturing the name, the opening, the value and the closing quote.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"\b(Version|VersionOverride)(\s*=\s*"")([^""]*)("")", RegexOptions.CultureInvariant)]
    private static partial Regex VersionAttribute();

    /// <summary>Matches a version that is only an MSBuild property reference.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"^\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyReference();

    /// <summary>Matches two to four numeric parts with optional pre-release and build metadata.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"^\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainVersion();

    /// <summary>Matches an element holding only text, capturing the opening tag, the name, the text and the closing tag.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"(<([A-Za-z_][A-Za-z0-9_.-]*)(?:\s[^>]*)?>)([^<]*)(</\2>)", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyDefinition();

    /// <summary>The version a package moves to.</summary>
    /// <param name="Id">The package id.</param>
    /// <param name="Version">The version.</param>
    /// <param name="Source">Where the version came from.</param>
    internal readonly record struct PackagePin(string Id, string Version, string Source);

    /// <summary>The JSON contract for the train files and the GitHub responses.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(TrainEntry))]
    [JsonSerializable(typeof(TrainSettings))]
    [JsonSerializable(typeof(TrainResult))]
    [JsonSerializable(typeof(UpdateLog))]
    [JsonSerializable(typeof(GitHubRelease))]
    [JsonSerializable(typeof(List<PullRequestItem>))]
    [JsonSerializable(typeof(List<CheckRun>))]
    [JsonSerializable(typeof(Installation))]
    [JsonSerializable(typeof(AccessToken))]
    [JsonSerializable(typeof(GitHubApp))]
    [JsonSerializable(typeof(JwtClaims))]
    internal sealed partial class TrainJson : JsonSerializerContext;

    /// <summary>Holds a GitHub App installation token for the repository. Tokens last an hour and the waits here last longer, so every gh or git call refreshes it first.</summary>
    private static class AppToken
    {
        /// <summary>How long before expiry the token is replaced.</summary>
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(10);

        /// <summary>When the current token expires.</summary>
        private static DateTimeOffset _expires;

        /// <summary>Gets or sets the repository the token is for, as owner/name.</summary>
        internal static string Repository { get; set; } = string.Empty;

        /// <summary>Gets or sets the clock for the token lifetime.</summary>
        internal static TimeProvider Clock { get; set; } = TimeProvider.System;

        /// <summary>Gets the JWT header.</summary>
        private static ReadOnlySpan<byte> JwtHeader => """{"alg":"RS256","typ":"JWT"}"""u8;

        /// <summary>Replaces the token in GH_TOKEN when it is close to expiry.</summary>
        /// <returns>A task that completes when the token is fresh.</returns>
        internal static async Task RefreshAsync()
        {
            if (_expires - Clock.GetUtcNow() > RefreshMargin)
            {
                return;
            }

            using var http = Client();
            var installation = await http.GetFromJsonAsync(new Uri($"repos/{Repository}/installation", UriKind.Relative), TrainJson.Default.Installation).ConfigureAwait(false);
            using var response = await http.PostAsync(new Uri($"app/installations/{installation!.Id}/access_tokens", UriKind.Relative), null).ConfigureAwait(false);
            var token = await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync(TrainJson.Default.AccessToken).ConfigureAwait(false);
            Console.WriteLine($"::add-mask::{token!.Token}");
            SetEnvironmentVariable("GH_TOKEN", token.Token);
            _expires = token.ExpiresAt;
        }

        /// <summary>Reads the app's slug.</summary>
        /// <returns>The slug.</returns>
        internal static async Task<string> SlugAsync()
        {
            using var http = Client();
            var app = await http.GetFromJsonAsync(new Uri("app", UriKind.Relative), TrainJson.Default.GitHubApp).ConfigureAwait(false);
            return app!.Slug;
        }

        /// <summary>Makes a GitHub API client authenticated as the app.</summary>
        /// <returns>The client.</returns>
        private static HttpClient Client()
        {
            HttpClient http = new() { BaseAddress = new("https://api.github.com/") };
            http.DefaultRequestHeaders.Authorization = new("Bearer", Jwt());
            http.DefaultRequestHeaders.UserAgent.ParseAdd("reactiveui-release-train");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        /// <summary>Signs a nine-minute app JWT, backdated a minute for clock drift.</summary>
        /// <returns>The JWT.</returns>
        private static string Jwt()
        {
            const int ClockDriftSeconds = 60;
            const int LifetimeSeconds = 540;
            using var rsa = RSA.Create();
            rsa.ImportFromPem(GetEnvironmentVariable("APP_PRIVATE_KEY"));
            var now = Clock.GetUtcNow().ToUnixTimeSeconds();
            var claims = JsonSerializer.SerializeToUtf8Bytes(new(now - ClockDriftSeconds, now + LifetimeSeconds, GetEnvironmentVariable("APP_CLIENT_ID") ?? string.Empty), TrainJson.Default.JwtClaims);
            var unsigned = $"{Base64Url.EncodeToString(JwtHeader)}.{Base64Url.EncodeToString(claims)}";
            return $"{unsigned}.{Base64Url.EncodeToString(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        }
    }

    /// <summary>Rewrites package versions in project files, directly or through the MSBuild property they reference.</summary>
    /// <param name="packages">The packages by id.</param>
    private sealed class Rewriter(Dictionary<string, PackagePin> packages)
    {
        /// <summary>The properties that set package versions, with the version and the package that claimed them.</summary>
        private readonly Dictionary<string, (string Version, string Id)> _properties = [with(StringComparer.Ordinal)];

        /// <summary>The current text of each file.</summary>
        private readonly Dictionary<string, string> _texts = [with(StringComparer.Ordinal)];

        /// <summary>Gets the updates made.</summary>
        internal List<VersionUpdate> Updates { get; } = [];

        /// <summary>Gets the versions left alone and why.</summary>
        internal List<string> Notes { get; } = [];

        /// <summary>Rewrites the files and saves the ones that changed.</summary>
        /// <param name="files">The project and props files, relative to the current folder.</param>
        internal void Rewrite(string[] files)
        {
            foreach (var file in files)
            {
                _texts[file] = PackageItem().Replace(File.ReadAllText(file), element => Item(element, file));
            }

            foreach (var (property, (version, id)) in _properties)
            {
                if (version is not Conflicted)
                {
                    Definitions(files, property, version, id);
                }
            }

            foreach (var file in files)
            {
                // Each file keeps its byte order mark, or its lack of one.
                var bytes = File.ReadAllBytes(file);
                var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
                if (!string.Equals(Encoding.UTF8.GetString(bytes.AsSpan(bom ? Encoding.UTF8.Preamble.Length : 0)), _texts[file], StringComparison.Ordinal))
                {
                    File.WriteAllText(file, _texts[file], new UTF8Encoding(bom));
                }
            }
        }

        /// <summary>Rewrites the version attributes of one package item.</summary>
        /// <param name="element">The item element.</param>
        /// <param name="file">The file it is in.</param>
        /// <returns>The element text.</returns>
        private string Item(Match element, string file) =>
            IncludeAttribute().Match(element.Value) is { Success: true } match && packages.TryGetValue(match.Groups[1].Value, out var package)
                ? VersionAttribute().Replace(element.Value, attribute => Version(attribute, package, file))
                : element.Value;

        /// <summary>Rewrites one version attribute, or records the property it references.</summary>
        /// <param name="attribute">The version attribute.</param>
        /// <param name="package">The package the item is for.</param>
        /// <param name="file">The file it is in.</param>
        /// <returns>The attribute text.</returns>
        private string Version(Match attribute, PackagePin package, string file)
        {
            var current = attribute.Groups[3].Value;
            if (PropertyReference().Match(current) is { Success: true } reference)
            {
                var property = reference.Groups[1].Value;
                if (_properties.TryGetValue(property, out var claimed) && claimed.Version is not Conflicted && !string.Equals(claimed.Version, package.Version, StringComparison.Ordinal))
                {
                    Notes.Add($"`$({property})` serves {claimed.Id} {claimed.Version} and {package.Id} {package.Version}. It was left alone.");
                    _properties[property] = (Conflicted, claimed.Id);
                }
                else
                {
                    _ = _properties.TryAdd(property, (package.Version, package.Id));
                }

                return attribute.Value;
            }

            if (!ShouldUpdate(current, package.Version, $"{package.Id} in {file}"))
            {
                return attribute.Value;
            }

            Updates.Add(new(file, package.Id, current, package.Version, package.Source));
            return $"{attribute.Groups[1].Value}{attribute.Groups[2].Value}{package.Version}{attribute.Groups[4].Value}";
        }

        /// <summary>Rewrites every definition of a version property.</summary>
        /// <param name="files">The files to search.</param>
        /// <param name="property">The property name.</param>
        /// <param name="version">The version the packages that use it moved to.</param>
        /// <param name="id">The package that claimed the property.</param>
        private void Definitions(string[] files, string property, string version, string id)
        {
            foreach (var file in files)
            {
                _texts[file] = PropertyDefinition().Replace(_texts[file], match =>
                {
                    if (!string.Equals(match.Groups[2].Value, property, StringComparison.Ordinal))
                    {
                        return match.Value;
                    }

                    var current = match.Groups[3].Value.Trim();
                    if (!ShouldUpdate(current, version, $"$({property}) in {file}"))
                    {
                        return match.Value;
                    }

                    Updates.Add(new(file, $"$({property})", current, version, packages[id].Source));
                    return $"{match.Groups[1].Value}{version}{match.Groups[4].Value}";
                });
            }
        }

        /// <summary>Decides whether a version moves, noting why when it does not.</summary>
        /// <param name="current">The version in the file.</param>
        /// <param name="next">The released version.</param>
        /// <param name="where">The item and file, for the note.</param>
        /// <returns>Whether to write the released version.</returns>
        private bool ShouldUpdate(string current, string next, string where)
        {
            if (string.Equals(current, next, StringComparison.Ordinal))
            {
                return false;
            }

            if (SemanticVersion.TryParse(current) is not { } from)
            {
                Notes.Add($"{where} uses `{current}`, which is not a plain version. It was left alone.");
                return false;
            }

            if (SemanticVersion.TryParse(next) is { } to && to.CompareTo(from) < 0)
            {
                Notes.Add($"{where} pins {current}, which is newer than {next}. It was left alone.");
                return false;
            }

            return true;
        }
    }

    /// <summary>A plain version: numeric core parts, then pre-release identifiers.</summary>
    private sealed class SemanticVersion
    {
        /// <summary>The numeric core parts.</summary>
        private readonly long[] _core;

        /// <summary>The pre-release identifiers.</summary>
        private readonly string[] _preRelease;

        /// <summary>Initializes a new instance of the <see cref="SemanticVersion"/> class.</summary>
        /// <param name="core">The numeric core parts.</param>
        /// <param name="preRelease">The pre-release identifiers.</param>
        private SemanticVersion(long[] core, string[] preRelease)
        {
            _core = core;
            _preRelease = preRelease;
        }

        /// <summary>Parses two to four numeric parts with optional pre-release and build metadata.</summary>
        /// <param name="text">The version text.</param>
        /// <returns>The version, or null when the text is not a plain version.</returns>
        internal static SemanticVersion? TryParse(string text)
        {
            if (!PlainVersion().IsMatch(text))
            {
                return null;
            }

            var withoutMetadata = text.Split('+')[0];
            var dash = withoutMetadata.IndexOf('-', StringComparison.Ordinal);
            var parts = (dash < 0 ? withoutMetadata : withoutMetadata[..dash]).Split('.');
            var core = new long[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                core[index] = long.Parse(parts[index], CultureInfo.InvariantCulture);
            }

            return new(core, dash < 0 ? [] : withoutMetadata[(dash + 1)..].Split('.'));
        }

        /// <summary>Compares by precedence: core parts, then a release after its pre-releases, then the identifiers.</summary>
        /// <param name="other">The version to compare with.</param>
        /// <returns>Negative, zero or positive.</returns>
        internal int CompareTo(SemanticVersion other)
        {
            for (var index = 0; index < Math.Max(_core.Length, other._core.Length); index++)
            {
                var left = index < _core.Length ? _core[index] : 0;
                var right = index < other._core.Length ? other._core[index] : 0;
                if (left.CompareTo(right) is not 0 and var compared)
                {
                    return compared;
                }
            }

            switch (_preRelease.Length, other._preRelease.Length)
            {
                case (0, 0):
                    return 0;
                case (0, _):
                    return 1;
                case (_, 0):
                    return -1;
            }

            for (var index = 0; index < Math.Min(_preRelease.Length, other._preRelease.Length); index++)
            {
                if (CompareIdentifiers(_preRelease[index], other._preRelease[index]) is not 0 and var compared)
                {
                    return compared;
                }
            }

            return _preRelease.Length.CompareTo(other._preRelease.Length);
        }

        /// <summary>Compares pre-release identifiers: numbers by value and before words, words ordinally.</summary>
        /// <param name="left">The first identifier.</param>
        /// <param name="right">The second identifier.</param>
        /// <returns>Negative, zero or positive.</returns>
        private static int CompareIdentifiers(string left, string right) =>
            (long.TryParse(left, CultureInfo.InvariantCulture, out var leftNumber), long.TryParse(right, CultureInfo.InvariantCulture, out var rightNumber)) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left, right),
            };
    }

    /// <summary>A repository in the plan.</summary>
    /// <param name="Name">The repository name in the config.</param>
    /// <param name="Repository">The repository, as owner/name.</param>
    /// <param name="Branch">The branch to release from.</param>
    /// <param name="AdminMerge">Whether to merge with admin rights.</param>
    /// <param name="WaitsFor">The repositories that must release first.</param>
    /// <param name="IgnorePackages">Package ids to leave alone.</param>
    internal sealed record TrainEntry(string Name, string Repository, string Branch, bool AdminMerge, List<string> WaitsFor, List<string> IgnorePackages);

    /// <summary>The train settings from the plan.</summary>
    /// <param name="UpdateBranch">The pull request branch.</param>
    /// <param name="Catalog">Every repository in the config.</param>
    /// <param name="ChecksMinutes">How long the pull request checks may take.</param>
    internal sealed record TrainSettings(string UpdateBranch, List<CatalogEntry> Catalog, int ChecksMinutes);

    /// <summary>A repository in the config.</summary>
    /// <param name="Name">The repository name in the config.</param>
    /// <param name="Repository">The repository, as owner/name.</param>
    internal sealed record CatalogEntry(string Name, string Repository);

    /// <summary>An earlier repository's result.</summary>
    /// <param name="Name">The repository name in the config.</param>
    /// <param name="Status">The result status.</param>
    /// <param name="Version">The released version.</param>
    /// <param name="Packages">The released package ids.</param>
    internal sealed record TrainResult(string Name, string? Status, string? Version, List<string> Packages);

    /// <summary>The version updates and notes for the result step.</summary>
    /// <param name="Updates">The updates made.</param>
    /// <param name="Notes">The versions left alone and why.</param>
    internal sealed record UpdateLog(List<VersionUpdate> Updates, List<string> Notes);

    /// <summary>One version moved in one file.</summary>
    /// <param name="File">The file, relative to the repository.</param>
    /// <param name="Item">The package id or property reference.</param>
    /// <param name="From">The old version.</param>
    /// <param name="To">The new version.</param>
    /// <param name="Source">Where the new version came from.</param>
    internal sealed record VersionUpdate(string File, string Item, string From, string To, string Source);

    /// <summary>A GitHub release.</summary>
    /// <param name="TagName">The tag.</param>
    /// <param name="Name">The title.</param>
    /// <param name="Assets">The attached files.</param>
    internal sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string TagName, string? Name, List<ReleaseAsset> Assets);

    /// <summary>A file attached to a release.</summary>
    /// <param name="Name">The file name.</param>
    internal sealed record ReleaseAsset(string Name);

    /// <summary>An open pull request.</summary>
    /// <param name="Url">The pull request URL.</param>
    internal sealed record PullRequestItem(string Url);

    /// <summary>A pull request check.</summary>
    /// <param name="Name">The check name.</param>
    /// <param name="Bucket">The gh state bucket: pass, fail, pending, skipping or cancel.</param>
    internal sealed record CheckRun(string Name, string Bucket);

    /// <summary>An app installation.</summary>
    /// <param name="Id">The installation id.</param>
    internal sealed record Installation(long Id);

    /// <summary>An installation access token.</summary>
    /// <param name="Token">The token.</param>
    /// <param name="ExpiresAt">When it expires.</param>
    internal sealed record AccessToken(string Token, [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

    /// <summary>A GitHub App.</summary>
    /// <param name="Slug">The app slug.</param>
    internal sealed record GitHubApp(string Slug);

    /// <summary>The app JWT claims.</summary>
    /// <param name="Iat">Issued at, in Unix seconds.</param>
    /// <param name="Exp">Expires at, in Unix seconds.</param>
    /// <param name="Iss">The app client id.</param>
    internal sealed record JwtClaims(long Iat, long Exp, string Iss);

    /// <summary>The state shared by the publishing steps.</summary>
    /// <param name="Entry">The repository being updated.</param>
    /// <param name="Settings">The train settings.</param>
    /// <param name="WorkDir">The work folder.</param>
    /// <param name="Outputs">The step output file.</param>
    /// <param name="Clock">The clock for the waits.</param>
    private sealed record UpdateRun(TrainEntry Entry, TrainSettings Settings, string WorkDir, string Outputs, TimeProvider Clock);
}
