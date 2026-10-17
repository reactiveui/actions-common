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

if (args is not [var entryJson, var timeoutMinutes, var workDir])
{
    Console.WriteLine("::error::Expected the repository entry, timeout and work folder arguments.");
    return Program.UsageError;
}

return await Program.RunAsync(entryJson, timeoutMinutes, workDir, TimeProvider.System).ConfigureAwait(false);

/// <summary>Starts a repository's release workflow, waits for it, and records the release it made.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The exit code for a failed step.</summary>
    private const int Failure = 1;

    /// <summary>The gh option that names the repository.</summary>
    private const string RepoOption = "--repo";

    /// <summary>The most looks for the dispatched run.</summary>
    private const int RunSearchAttempts = 12;

    /// <summary>How far before the dispatch a run may start and still count as the dispatched one.</summary>
    private static readonly TimeSpan DispatchSlack = TimeSpan.FromSeconds(10);

    /// <summary>The time between two looks for the dispatched run.</summary>
    private static readonly TimeSpan RunSearchInterval = TimeSpan.FromSeconds(10);

    /// <summary>The time between two looks at the release run.</summary>
    private static readonly TimeSpan RunPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>Runs the release.</summary>
    /// <param name="entryJson">The repository entry from the plan.</param>
    /// <param name="timeoutMinutes">How long the release run may take.</param>
    /// <param name="workDir">The folder for release.json.</param>
    /// <param name="clock">The clock for the waits and the app token.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(string entryJson, string timeoutMinutes, string workDir, TimeProvider clock)
    {
        var entry = JsonSerializer.Deserialize(entryJson, ReleaseJson.Default.ReleaseEntry)!;
        var outputs = GetEnvironmentVariable("GITHUB_OUTPUT")!;
        AppToken.Repository = entry.Repository;
        AppToken.Clock = clock;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(int.Parse(timeoutMinutes, CultureInfo.InvariantCulture)), clock);
        var dispatched = clock.GetUtcNow() - DispatchSlack;
        _ = Directory.CreateDirectory(workDir);

        // A branch whose head is already the latest release has nothing new to ship, so the train reuses that release.
        if (await GhAsync("api", $"repos/{entry.Repository}/commits/{entry.Branch}", "--jq", ".sha").ConfigureAwait(false) is not { ExitStatus.ExitCode: 0, StandardOutput: var headOutput })
        {
            Console.WriteLine($"::error::Could not read the head of {entry.Branch} in {entry.Repository}.");
            return Failure;
        }

        var branchHead = headOutput.Trim();
        if (await ReleasesAsync(entry.Repository).ConfigureAwait(false) is [var newest, ..] && string.Equals(newest.TargetCommitish, branchHead, StringComparison.Ordinal))
        {
            Console.WriteLine($"{entry.Repository} already released {newest.TagName} at {branchHead}. Reusing it.");
            await File.AppendAllTextAsync(outputs, "reused=true\n").ConfigureAwait(false);
            return await WriteReleaseAsync(newest, entry.Repository, null, workDir).ConfigureAwait(false);
        }

        if (await DispatchAsync(entry, dispatched, timeout.Token).ConfigureAwait(false) is not { } runId)
        {
            return Failure;
        }

        var runUrl = $"https://github.com/{entry.Repository}/actions/runs/{runId}";
        await File.AppendAllTextAsync(outputs, $"run-url={runUrl}\n").ConfigureAwait(false);
        Console.WriteLine($"Waiting for {runUrl}");

        if (await CompletedRunAsync(entry.Repository, runId, clock, timeout.Token).ConfigureAwait(false) is not { } run)
        {
            Console.WriteLine($"::error::{runUrl} did not finish within {timeoutMinutes} minutes.");
            return Failure;
        }

        if (run.Conclusion is not "success")
        {
            Console.WriteLine($"::error::The release run ended with {run.Conclusion}: {runUrl}");
            return Failure;
        }

        var releases = await ReleasesAsync(entry.Repository).ConfigureAwait(false);
        if (FindRelease(releases, run.HeadSha, dispatched) is not { } release)
        {
            Console.WriteLine($"::error::{runUrl} succeeded, but {entry.Repository} has no release for commit {run.HeadSha}.");
            return Failure;
        }

        return await WriteReleaseAsync(release, entry.Repository, runUrl, workDir).ConfigureAwait(false);
    }

    /// <summary>Starts the release workflow and finds the run it started.</summary>
    /// <param name="entry">The repository entry.</param>
    /// <param name="dispatched">The earliest time the run can have started.</param>
    /// <param name="cancellationToken">Stops the search when the release runs out of time.</param>
    /// <returns>The run id, or null after reporting an error.</returns>
    private static async Task<string?> DispatchAsync(ReleaseEntry entry, DateTimeOffset dispatched, CancellationToken cancellationToken)
    {
        List<string> arguments = ["workflow", "run", entry.Workflow, RepoOption, entry.Repository, "--ref", entry.Branch];
        foreach (var (key, value) in entry.Inputs)
        {
            arguments.Add("--raw-field");
            arguments.Add($"{key}={value}");
        }

        var dispatch = await GhAsync(arguments).ConfigureAwait(false);
        if (dispatch is { ExitStatus.ExitCode: not 0 })
        {
            Console.WriteLine($"::error::Could not start {entry.Workflow} in {entry.Repository}: {dispatch.StandardError.Trim()}");
            return null;
        }

        Console.Write(dispatch.StandardOutput);
        var runId = RunLink().Match(dispatch.StandardOutput + dispatch.StandardError) is { Success: true } link ? link.Groups[1].Value : null;
        try
        {
            for (var attempt = 0; runId is null && attempt < RunSearchAttempts; attempt++)
            {
                await Task.Delay(RunSearchInterval, cancellationToken).ConfigureAwait(false);
                runId = await NewestDispatchedRunAsync(entry, dispatched).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The release ran out of time while looking; the error below reports the missing run.
        }

        if (runId is null)
        {
            Console.WriteLine($"::error::{entry.Workflow} in {entry.Repository} started, but its run could not be found.");
        }

        return runId;
    }

    /// <summary>Finds the last listed dispatch run that started after the dispatch.</summary>
    /// <param name="entry">The repository entry.</param>
    /// <param name="dispatched">The earliest time the run can have started.</param>
    /// <returns>The run id, or null when none matches yet.</returns>
    private static async Task<string?> NewestDispatchedRunAsync(ReleaseEntry entry, DateTimeOffset dispatched)
    {
        string[] list =
        [
            "run", "list", RepoOption, entry.Repository, "--workflow", entry.Workflow, "--branch", entry.Branch,
            "--event", "workflow_dispatch", "--limit", "10", "--json", "databaseId,createdAt",
        ];
        var listed = await GhAsync(list).ConfigureAwait(false);
        var runs = listed.StandardOutput is "" ? [] : JsonSerializer.Deserialize(listed.StandardOutput, ReleaseJson.Default.ListWorkflowRun)!;
        string? runId = null;
        foreach (var run in runs)
        {
            if (run.CreatedAt >= dispatched)
            {
                runId = run.DatabaseId.ToString(CultureInfo.InvariantCulture);
            }
        }

        return runId;
    }

    /// <summary>Waits for the release run to complete.</summary>
    /// <param name="repository">The repository, as owner/name.</param>
    /// <param name="runId">The run id.</param>
    /// <param name="clock">The clock for the poll timer.</param>
    /// <param name="cancellationToken">Stops the wait when the release runs out of time.</param>
    /// <returns>The completed run, or null when it did not finish in time.</returns>
    private static async Task<RunView?> CompletedRunAsync(string repository, string runId, TimeProvider clock, CancellationToken cancellationToken)
    {
        using var poll = new PeriodicTimer(RunPollInterval, clock);
        try
        {
            while (await poll.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await GhAsync("run", "view", runId, RepoOption, repository, "--json", "status,conclusion,headSha").ConfigureAwait(false) is { ExitStatus.ExitCode: 0, StandardOutput: var view }
                    && JsonSerializer.Deserialize(view, ReleaseJson.Default.RunView) is { Status: "completed" } run)
                {
                    return run;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The run did not finish in time; the caller reports it.
        }

        return null;
    }

    /// <summary>Picks the release the run made: the one for its commit, else the first created since the dispatch.</summary>
    /// <param name="releases">The published releases, newest first.</param>
    /// <param name="headSha">The commit the run built.</param>
    /// <param name="dispatched">The earliest time the release can have been made.</param>
    /// <returns>The release, or null when there is none.</returns>
    private static GitHubRelease? FindRelease(List<GitHubRelease> releases, string headSha, DateTimeOffset dispatched)
    {
        foreach (var candidate in releases)
        {
            if (string.Equals(candidate.TargetCommitish, headSha, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        foreach (var candidate in releases)
        {
            if (candidate.CreatedAt >= dispatched)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Lists the repository's newest releases, without drafts.</summary>
    /// <param name="repository">The repository, as owner/name.</param>
    /// <returns>The releases, newest first.</returns>
    private static async Task<List<GitHubRelease>> ReleasesAsync(string repository)
    {
        var output = await GhAsync("api", $"repos/{repository}/releases?per_page=20").ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize(output.StandardOutput, ReleaseJson.Default.ListGitHubRelease)!;
        _ = releases.RemoveAll(static release => release.Draft is true);
        return releases;
    }

    /// <summary>Writes release.json with the version, tag, URLs and package ids of the release.</summary>
    /// <param name="release">The release.</param>
    /// <param name="repository">The repository, as owner/name.</param>
    /// <param name="runUrl">The release run, or null for a reused release.</param>
    /// <param name="workDir">The folder for release.json.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> WriteReleaseAsync(GitHubRelease release, string repository, string? runUrl, string workDir)
    {
        if (ReleasePackages(release) is not ({ } version, [_, ..] packages))
        {
            Console.WriteLine($"::error::Release {release.TagName} in {repository} has no .nupkg assets.");
            return Failure;
        }

        var record = JsonSerializer.SerializeToUtf8Bytes(new(version, release.TagName, release.HtmlUrl, runUrl, packages), ReleaseJson.Default.ReleaseRecord);
        await File.WriteAllBytesAsync(Path.Combine(workDir, "release.json"), record).ConfigureAwait(false);
        Console.WriteLine($"Using {version}: {string.Join(", ", packages)}");
        return 0;
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

    /// <summary>Runs gh with a fresh installation token.</summary>
    /// <param name="arguments">The gh arguments.</param>
    /// <returns>The captured output.</returns>
    private static async Task<ProcessTextOutput> GhAsync(params IEnumerable<string> arguments)
    {
        await AppToken.RefreshAsync().ConfigureAwait(false);
        return await Process.RunAndCaptureTextAsync("gh", arguments).ConfigureAwait(false);
    }

    /// <summary>Matches the run link gh prints after a dispatch.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"/actions/runs/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex RunLink();

    /// <summary>The JSON contract for the plan entry, the GitHub responses and release.json.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReleaseEntry))]
    [JsonSerializable(typeof(List<GitHubRelease>))]
    [JsonSerializable(typeof(List<WorkflowRun>))]
    [JsonSerializable(typeof(RunView))]
    [JsonSerializable(typeof(ReleaseRecord))]
    [JsonSerializable(typeof(Installation))]
    [JsonSerializable(typeof(AccessToken))]
    [JsonSerializable(typeof(JwtClaims))]
    internal sealed partial class ReleaseJson : JsonSerializerContext;

    /// <summary>Holds a GitHub App installation token for the repository. Tokens last an hour and the waits here last longer, so every gh call refreshes it first.</summary>
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
            var installation = await http.GetFromJsonAsync(new Uri($"repos/{Repository}/installation", UriKind.Relative), ReleaseJson.Default.Installation).ConfigureAwait(false);
            using var response = await http.PostAsync(new Uri($"app/installations/{installation!.Id}/access_tokens", UriKind.Relative), null).ConfigureAwait(false);
            var token = await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync(ReleaseJson.Default.AccessToken).ConfigureAwait(false);
            Console.WriteLine($"::add-mask::{token!.Token}");
            SetEnvironmentVariable("GH_TOKEN", token.Token);
            _expires = token.ExpiresAt;
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
            var issuer = GetEnvironmentVariable("APP_CLIENT_ID") ?? string.Empty;
            var claims = JsonSerializer.SerializeToUtf8Bytes(new(now - ClockDriftSeconds, now + LifetimeSeconds, issuer), ReleaseJson.Default.JwtClaims);
            var unsigned = $"{Base64Url.EncodeToString(JwtHeader)}.{Base64Url.EncodeToString(claims)}";
            return $"{unsigned}.{Base64Url.EncodeToString(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
        }
    }

    /// <summary>A repository in the plan.</summary>
    /// <param name="Repository">The repository, as owner/name.</param>
    /// <param name="Branch">The branch to release from.</param>
    /// <param name="Workflow">The release workflow file.</param>
    /// <param name="Inputs">The release workflow inputs.</param>
    internal sealed record ReleaseEntry(string Repository, string Branch, string Workflow, Dictionary<string, string> Inputs);

    /// <summary>A GitHub release.</summary>
    /// <param name="TagName">The tag.</param>
    /// <param name="Name">The title.</param>
    /// <param name="TargetCommitish">The commit or branch the tag points at.</param>
    /// <param name="CreatedAt">When the release was made.</param>
    /// <param name="Draft">Whether it is a draft.</param>
    /// <param name="HtmlUrl">The release page.</param>
    /// <param name="Assets">The attached files.</param>
    internal sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        string? Name,
        [property: JsonPropertyName("target_commitish")] string? TargetCommitish,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        bool? Draft,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        List<ReleaseAsset> Assets);

    /// <summary>A file attached to a release.</summary>
    /// <param name="Name">The file name.</param>
    internal sealed record ReleaseAsset(string Name);

    /// <summary>A listed workflow run.</summary>
    /// <param name="DatabaseId">The run id.</param>
    /// <param name="CreatedAt">When the run was created.</param>
    internal sealed record WorkflowRun(long DatabaseId, DateTimeOffset CreatedAt);

    /// <summary>The state of a workflow run.</summary>
    /// <param name="Status">The run status.</param>
    /// <param name="Conclusion">The run conclusion once completed.</param>
    /// <param name="HeadSha">The commit the run built.</param>
    internal sealed record RunView(string Status, string? Conclusion, string HeadSha);

    /// <summary>The release.json record the later steps read.</summary>
    /// <param name="Version">The released version.</param>
    /// <param name="Tag">The release tag.</param>
    /// <param name="ReleaseUrl">The release page.</param>
    /// <param name="RunUrl">The release run, or null for a reused release.</param>
    /// <param name="Packages">The released package ids.</param>
    internal sealed record ReleaseRecord(string Version, string Tag, string ReleaseUrl, string? RunUrl, List<string> Packages);

    /// <summary>An app installation.</summary>
    /// <param name="Id">The installation id.</param>
    internal sealed record Installation(long Id);

    /// <summary>An installation access token.</summary>
    /// <param name="Token">The token.</param>
    /// <param name="ExpiresAt">When it expires.</param>
    internal sealed record AccessToken(string Token, [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);

    /// <summary>The app JWT claims.</summary>
    /// <param name="Iat">Issued at, in Unix seconds.</param>
    /// <param name="Exp">Expires at, in Unix seconds.</param>
    /// <param name="Iss">The app client id.</param>
    internal sealed record JwtClaims(long Iat, long Exp, string Iss);
}
