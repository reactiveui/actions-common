#!/usr/bin/env dotnet run
// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:property TargetFramework=net11.0
#:package Refit@16.4.0

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using static System.Environment;

if (args is not [var entryJson, var timeoutMinutes, var workDir])
{
    Console.WriteLine("::error::Expected the repository entry, timeout and work folder arguments.");
    return Program.UsageError;
}

const int OperationHours = 6;

using var operationTimeout = new CancellationTokenSource(TimeSpan.FromHours(OperationHours));

return await Program.RunAsync(entryJson, timeoutMinutes, workDir, TimeProvider.System, operationTimeout.Token)
    .ConfigureAwait(false);

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
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(
        string entryJson,
        string timeoutMinutes,
        string workDir,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var entry = JsonSerializer.Deserialize(entryJson, ReleaseJson.Default.ReleaseEntry)!;
        var outputs = GetEnvironmentVariable("GITHUB_OUTPUT")!;
        AppToken.Repository = entry.Repository;
        AppToken.Clock = clock;
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromMinutes(
    int.Parse(timeoutMinutes, CultureInfo.InvariantCulture)),
            clock);
        var dispatched = clock.GetUtcNow() - DispatchSlack;
        _ = Directory.CreateDirectory(workDir);

        // A branch whose head is already the latest release has nothing new to ship, so the train reuses that release.
        if (await GhAsync(
            cancellationToken,
            "api",
            $"repos/{entry.Repository}/commits/{entry.Branch}",
            "--jq",
            ".sha").ConfigureAwait(false) is not { ExitStatus.ExitCode: 0, StandardOutput: var headOutput })
        {
            Console.WriteLine($"::error::Could not read the head of {entry.Branch} in {entry.Repository}.");
            return Failure;
        }

        var branchHead = headOutput.Trim();
        if (await ReleasesAsync(entry.Repository, cancellationToken).ConfigureAwait(false) is [var newest, ..]
            && string.Equals(newest.TargetCommitish, branchHead, StringComparison.Ordinal))
        {
            Console.WriteLine($"{entry.Repository} already released {newest.TagName} at {branchHead}. Reusing it.");
            await File.AppendAllTextAsync(outputs, "reused=true\n", cancellationToken).ConfigureAwait(false);
            return await WriteReleaseAsync(newest, entry.Repository, null, workDir).ConfigureAwait(false);
        }

        if (await DispatchAsync(entry, dispatched, timeout.Token).ConfigureAwait(false) is not { } runId)
        {
            return Failure;
        }

        var runUrl = $"https://github.com/{entry.Repository}/actions/runs/{runId}";
        await File.AppendAllTextAsync(outputs, $"run-url={runUrl}\n", cancellationToken).ConfigureAwait(false);
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

        return await WriteCompletedReleaseAsync(entry.Repository, run, dispatched, workDir, runUrl, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Records the release created by a completed workflow.</summary>
    /// <param name="repository">The owner and repository.</param>
    /// <param name="run">The completed release run.</param>
    /// <param name="dispatched">The earliest release timestamp.</param>
    /// <param name="workDir">The output directory.</param>
    /// <param name="runUrl">The release workflow URL.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>Zero when the release was recorded; otherwise, a failure code.</returns>
    private static async Task<int> WriteCompletedReleaseAsync(
        string repository,
        RunView run,
        DateTimeOffset dispatched,
        string workDir,
        string runUrl,
        CancellationToken cancellationToken)
    {
        var releases = await ReleasesAsync(repository, cancellationToken).ConfigureAwait(false);
        if (FindRelease(releases, run.HeadSha, dispatched) is not { } release)
        {
            Console.WriteLine(
                $"::error::{runUrl} succeeded, but {repository} has no release for commit {run.HeadSha}.");
            return Failure;
        }

        return await WriteReleaseAsync(release, repository, runUrl, workDir).ConfigureAwait(false);
    }

    /// <summary>Starts the release workflow and finds the run it started.</summary>
    /// <param name="entry">The repository entry.</param>
    /// <param name="dispatched">The earliest time the run can have started.</param>
    /// <param name="cancellationToken">Stops the search when the release runs out of time.</param>
    /// <returns>The run id, or null after reporting an error.</returns>
    private static async Task<string?> DispatchAsync(
        ReleaseEntry entry,
        DateTimeOffset dispatched,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["workflow", "run", entry.Workflow, RepoOption, entry.Repository, "--ref",
            entry.Branch];
        foreach (var (key, value) in entry.Inputs)
        {
            arguments.Add("--raw-field");
            arguments.Add($"{key}={value}");
        }

        var dispatch = await GhAsync(cancellationToken, arguments).ConfigureAwait(false);
        if (dispatch is { ExitStatus.ExitCode: not 0 })
        {
            Console.WriteLine(
                $"::error::Could not start {entry.Workflow} in {entry.Repository}: {dispatch.StandardError.Trim()}");
            return null;
        }

        Console.Write(dispatch.StandardOutput);
        var runId = RunLink().Match(dispatch.StandardOutput
            + dispatch.StandardError) is { Success: true } link
            ? link.Groups[1].Value
            : null;
        try
        {
            for (var attempt = 0; runId is null && attempt < RunSearchAttempts; attempt++)
            {
                await Task.Delay(RunSearchInterval, cancellationToken).ConfigureAwait(false);
                runId = await NewestDispatchedRunAsync(entry, dispatched, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The release ran out of time while looking; the error below reports the missing run.
        }

        if (runId is null)
        {
            Console.WriteLine(
                $"::error::{entry.Workflow} in {entry.Repository} started, but its run could not be found.");
        }

        return runId;
    }

    /// <summary>Finds the last listed dispatch run that started after the dispatch.</summary>
    /// <param name="entry">The repository entry.</param>
    /// <param name="dispatched">The earliest time the run can have started.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The run id, or null when none matches yet.</returns>
    private static async Task<string?> NewestDispatchedRunAsync(
        ReleaseEntry entry,
        DateTimeOffset dispatched,
        CancellationToken cancellationToken)
    {
        string[] list =
        ["run", "list", RepoOption, entry.Repository, "--workflow", entry.Workflow, "--branch", entry.Branch,
            "--event", "workflow_dispatch", "--limit", "10", "--json", "databaseId,createdAt",];
        var listed = await GhAsync(cancellationToken, list).ConfigureAwait(false);
        var runs = listed.StandardOutput is "" ? [] : JsonSerializer.Deserialize(
            listed.StandardOutput,
            ReleaseJson.Default.ListWorkflowRun)!;
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
    private static async Task<RunView?> CompletedRunAsync(
        string repository,
        string runId,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        using var poll = new PeriodicTimer(RunPollInterval, clock);
        try
        {
            while (await poll.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await GhAsync(
                    cancellationToken,
                    "run",
                    "view",
                    runId,
                    RepoOption,
                    repository,
                    "--json",
                    "status,conclusion,headSha").ConfigureAwait(
    false) is { ExitStatus.ExitCode: 0, StandardOutput: var view }
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
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The releases, newest first.</returns>
    private static async Task<List<GitHubRelease>> ReleasesAsync(string repository, CancellationToken cancellationToken)
    {
        var output = await GhAsync(cancellationToken, "api", $"repos/{repository}/releases?per_page=20")
            .ConfigureAwait(false);
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
    private static async Task<int> WriteReleaseAsync(
        GitHubRelease release,
        string repository,
        string? runUrl,
        string workDir)
    {
        if (ReleasePackages(release) is not ({ } version, [_, ..] packages))
        {
            Console.WriteLine($"::error::Release {release.TagName} in {repository} has no .nupkg assets.");
            return Failure;
        }

        var record = JsonSerializer.SerializeToUtf8Bytes(
            new(
    version,
    release.TagName,
    release.HtmlUrl,
    runUrl,
    packages),
            ReleaseJson.Default.ReleaseRecord);
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

    /// <summary>Matches the run link gh prints after a dispatch.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"/actions/runs/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex RunLink();
}
