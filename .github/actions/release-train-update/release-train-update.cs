#!/usr/bin/env dotnet run
// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:property TargetFramework=net11.0
#:package Refit@16.4.0

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static System.Environment;

if (args is not [var entryJson, var settingsJson, var resultsDir, var workDir])
{
    Console.WriteLine("::error::Expected the repository entry, settings, results folder and work folder arguments.");
    return Program.UsageError;
}

const int OperationHours = 6;

using var operationTimeout = new CancellationTokenSource(TimeSpan.FromHours(OperationHours));

return await Program.RunAsync(
    entryJson,
    settingsJson,
    resultsDir,
    workDir,
    TimeProvider.System,
    operationTimeout.Token);

/// <summary>Moves a repository's ReactiveUI package versions to the train's releases through a pull request, then
/// merges it once its checks pass.</summary>
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
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(
        string entryJson,
        string settingsJson,
        string resultsDir,
        string workDir,
        TimeProvider clock,
        CancellationToken cancellationToken)
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
            await File.AppendAllTextAsync(outputs, $"blocked=true\nreason={reason}\n", cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        var packages = await CollectPackagesAsync(entry, settings, results, cancellationToken).ConfigureAwait(false);
        var (failure, openPullRequest, files) = await CloneAsync(entry, settings, workDir, cancellationToken)
            .ConfigureAwait(false);
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

        var log = JsonSerializer.SerializeToUtf8Bytes(
            new(rewriter.Updates, rewriter.Notes),
            TrainJson.Default.UpdateLog);
        await File.WriteAllBytesAsync(Path.Combine(workDir, "updates.json"), log, cancellationToken)
            .ConfigureAwait(false);
        Console.WriteLine($"{rewriter.Updates.Count} version(s) to update in {entry.Repository}.");
        if (rewriter.Updates is [] && openPullRequest is null)
        {
            await File.AppendAllTextAsync(outputs, "blocked=false\nchanged=false\n", cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }

        return await PublishAsync(
            new(entry, settings, workDir, outputs, clock),
            rewriter.Updates,
            openPullRequest,
            cancellationToken).ConfigureAwait(false);
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

        return unreleased is []
            ? string.Empty
            : $"Blocked because {string.Join(", ", unreleased)} did not release in this run.";
    }

    /// <summary>Finds the version of every package the other repositories supply: this run's release when it has one,
    /// else the latest release.</summary>
    /// <param name="entry">The repository being updated.</param>
    /// <param name="settings">The train settings.</param>
    /// <param name="results">The earlier levels' results.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The packages by id.</returns>
    private static async Task<Dictionary<string, PackagePin>> CollectPackagesAsync(
        TrainEntry entry,
        TrainSettings settings,
        Dictionary<string, TrainResult> results,
        CancellationToken cancellationToken)
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

            if (await GhAsync(cancellationToken, "api", $"repos/{source.Repository}/releases/latest")
                .ConfigureAwait(false) is not { ExitStatus.ExitCode: 0, StandardOutput: var latestJson })
            {
                Console.WriteLine($"{source.Repository} has no latest release. Its packages stay as they are.");
                continue;
            }

            var (version, ids) = ReleasePackages(
                JsonSerializer.Deserialize(latestJson, TrainJson.Default.GitHubRelease)!);
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
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Zero or the failing exit code, the open pull request, and the project files.</returns>
    private static async Task<(int ExitCode, PullRequestItem? OpenPullRequest, string[] Files)> CloneAsync(
        TrainEntry entry,
        TrainSettings settings,
        string workDir,
        CancellationToken cancellationToken)
    {
        if (await GhAsync(cancellationToken, "auth", "setup-git").ConfigureAwait(
            false) is { ExitStatus.ExitCode: not 0 } setup)
        {
            Console.WriteLine($"::error::Could not set up git credentials: {setup.StandardError.Trim()}");
            return (Failure, null, []);
        }

        var pullRequests = await GhAsync(
            cancellationToken,
            "pr",
            "list",
            "--repo",
            entry.Repository,
            "--head",
            settings.UpdateBranch,
            "--base",
            entry.Branch,
            "--state",
            "open",
            "--json",
            "number,url").ConfigureAwait(false);
        if (pullRequests is not { ExitStatus.ExitCode: 0 })
        {
            Console.WriteLine(
                $"::error::Could not list pull requests in {entry.Repository}: {pullRequests.StandardError.Trim()}");
            return (Failure, null, []);
        }

        var listedPullRequests = JsonSerializer.Deserialize(
            pullRequests.StandardOutput,
            TrainJson.Default.ListPullRequestItem)!;
        var open = listedPullRequests.Count > 0 ? listedPullRequests[0] : null;
        var clone = Path.Combine(workDir, "repository");
        var cloneBranch = open is null ? entry.Branch : settings.UpdateBranch;
        await AppToken.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (await Process.RunAsync(
            "git",
            ["clone", "--depth", "1", "--branch", cloneBranch, $"https://github.com/{entry.Repository}.git", clone],
            cancellationToken: cancellationToken).ConfigureAwait(false) is { ExitCode: not 0 } cloned)
        {
            Console.WriteLine($"::error::Could not clone {entry.Repository} at {cloneBranch}.");
            return (cloned.ExitCode, open, []);
        }

        Directory.SetCurrentDirectory(clone);
        if (await Process.RunAndCaptureTextAsync(
            "git",
            ["ls-files", "-z", "--", "*.props", "*.targets", "*.csproj", "*.fsproj", "*.vbproj"],
            cancellationToken: cancellationToken).ConfigureAwait(
    false) is not { ExitStatus.ExitCode: 0, StandardOutput: var listed })
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
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> PublishAsync(
        UpdateRun run,
        List<VersionUpdate> updates,
        PullRequestItem? openPullRequest,
        CancellationToken cancellationToken)
    {
        var (entry, settings, workDir, outputs, clock) = run;
        var runUrl = $"{GetEnvironmentVariable("GITHUB_SERVER_URL")}/{GetEnvironmentVariable(
            "GITHUB_REPOSITORY")}/actions/runs/{GetEnvironmentVariable("GITHUB_RUN_ID")}";
        if (updates is not [] && await CommitAndPushAsync(
            updates,
            settings.UpdateBranch,
            entry.Repository,
            runUrl,
            openPullRequest is null,
            cancellationToken).ConfigureAwait(false) is not 0 and var pushFailure)
        {
            return pushFailure;
        }

        var body = Path.Combine(workDir, "pull-request.md");
        await File.WriteAllTextAsync(
            body,
            $"The [release train]({runUrl}) updates these package versions before it releases {entry.Name}.\n\n{Table(
    updates)}",
            cancellationToken).ConfigureAwait(false);
        string pullRequest;
        if (openPullRequest is null)
        {
            string[] create =
            ["pr", "create", "--repo", entry.Repository, "--base", entry.Branch, "--head",
                settings.UpdateBranch, "--title", "chore(deps): update ReactiveUI package versions",
                "--body-file", body,];
            if (await GhAsync(cancellationToken, create).ConfigureAwait(
                false) is not { ExitStatus.ExitCode: 0, StandardOutput: var created })
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
                _ = await GhAsync(cancellationToken, "pr", "comment", pullRequest, "--body-file", body)
                    .ConfigureAwait(false);
            }
        }

        await File.AppendAllTextAsync(
            outputs,
            $"blocked=false\nchanged=true\npull-request={pullRequest}\n",
            cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Waiting for the checks on {pullRequest}");
        return await MergeWhenGreenAsync(entry, settings.ChecksMinutes, pullRequest, clock, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Commits the updates as the app's bot user and pushes them to the update branch.</summary>
    /// <param name="updates">The updates made.</param>
    /// <param name="updateBranch">The pull request branch.</param>
    /// <param name="repository">The repository, as owner/name.</param>
    /// <param name="runUrl">This train run.</param>
    /// <param name="replace">Whether the branch starts again from the base branch.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Zero, or the failing exit code.</returns>
    private static async Task<int> CommitAndPushAsync(
        List<VersionUpdate> updates,
        string updateBranch,
        string repository,
        string runUrl,
        bool replace,
        CancellationToken cancellationToken)
    {
        var bot = $"{await AppToken.SlugAsync(cancellationToken).ConfigureAwait(false)}[bot]";
        var botId = (await GhAsync(cancellationToken, "api", $"users/{bot}", "--jq", ".id")
            .ConfigureAwait(false)).StandardOutput.Trim();
        HashSet<string> items = [with(StringComparer.Ordinal)];
        foreach (var update in updates)
        {
            _ = items.Add(update.Item);
        }

        string[] commit =
        ["-c", $"user.name={bot}", "-c", $"user.email={botId}+{bot}@users.noreply.github.com", "commit",
            "--all", "--message", $"chore(deps): update {items.Count} ReactiveUI package version(s)",
            "--message", $"Release train run {runUrl}",];
        if (await Process.RunAsync("git", commit, cancellationToken: cancellationToken)
            .ConfigureAwait(false) is { ExitCode: not 0 } committed)
        {
            Console.WriteLine("::error::Could not commit the version updates.");
            return committed.ExitCode;
        }

        // A new branch starts from the base branch, so it replaces any stale branch left by a closed pull request.
        string[] push = replace ? ["push", "--force", "origin", $"HEAD:refs/heads/{updateBranch}"] : ["push",
            "origin", $"HEAD:refs/heads/{updateBranch}"];
        await AppToken.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (await Process.RunAsync("git", push, cancellationToken: cancellationToken)
            .ConfigureAwait(false) is { ExitCode: not 0 } pushed)
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
            _ = table.AppendLine(
                CultureInfo.InvariantCulture,
                $"| `{update.File}` | `{update.Item}` | {update.From} | {update.To} | {update.Source} |");
        }

        return table.ToString();
    }

    /// <summary>Waits for the pull request checks and merges it once they pass.</summary>
    /// <param name="entry">The repository being updated.</param>
    /// <param name="checksMinutes">How long the checks may take.</param>
    /// <param name="pullRequest">The pull request URL.</param>
    /// <param name="clock">The clock for the waits.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The exit code.</returns>
    private static async Task<int> MergeWhenGreenAsync(
        TrainEntry entry,
        int checksMinutes,
        string pullRequest,
        TimeProvider clock,
        CancellationToken cancellationToken)
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
                    Console.WriteLine($"::error::Checks failed on {pullRequest}: {string.Join(
                        ", ",
                        failed)}. Fix the pull request, merge it, then resume the train from {entry.Name}.");
                    return Failure;
                }

                if (checks is [] || pending)
                {
                    continue;
                }

                // Branch protection rejects the merge while a required check has not reported yet, so a failed merge
                // waits again.
                if (await GhAsync(cancellationToken, merge).ConfigureAwait(
                    false) is { ExitStatus.ExitCode: not 0 } merged)
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
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <param name="arguments">The gh arguments.</param>
    /// <returns>The captured output.</returns>
    private static async Task<ProcessTextOutput> GhAsync(
        CancellationToken cancellationToken,
        params IEnumerable<string> arguments)
    {
        await AppToken.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return await Process.RunAndCaptureTextAsync("gh", arguments, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the pull request checks.</summary>
    /// <param name="pullRequest">The pull request URL.</param>
    /// <param name="required">Whether to read only the required checks.</param>
    /// <param name="cancellationToken">Stops the read when the checks run out of time.</param>
    /// <returns>The checks, or none when gh reports none.</returns>
    private static async Task<List<CheckRun>> ChecksAsync(
        string pullRequest,
        bool required,
        CancellationToken cancellationToken)
    {
        string[] filter = required ? ["--required"] : [];
        await AppToken.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var output = await Process.RunAndCaptureTextAsync(
            "gh",
            ["pr", "checks", pullRequest, "--json",
                "name,bucket", .. filter],
            cancellationToken).ConfigureAwait(false);
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
    [GeneratedRegex(
    @"<(?:PackageVersion|PackageReference|GlobalPackageReference)\b[^>]*>",
    RegexOptions.CultureInvariant)]
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
}
