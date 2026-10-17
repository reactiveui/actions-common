// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using static System.Environment;

return Program.Run(args);

/// <summary>Computes the next release version from the repository's tags and optionally pushes its tag.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    private const int UsageError = 2;

    /// <summary>The exit code for a rejected input or an existing tag.</summary>
    private const int Failure = 1;

    /// <summary>Computes the version, pushes the tag when asked and writes the step outputs.</summary>
    /// <param name="args">The bump, tag prefix, push-tag flag and pre-release identifier.</param>
    /// <returns>The process exit code.</returns>
    internal static int Run(string[] args)
    {
        if (args is not [var bump, var tagPrefix, var pushTag, var preReleaseId])
        {
            Console.WriteLine("::error::Expected the bump, tag prefix, push-tag and pre-release id arguments.");
            return UsageError;
        }

        if (bump is "")
        {
            Console.WriteLine("No bump requested; skipping version/tag computation (CI pre-release path).");
            return 0;
        }

        if (!IsValid(bump, preReleaseId))
        {
            return Failure;
        }

        Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

        var latest = LatestRelease(tagPrefix);
        if (latest is null)
        {
            Console.WriteLine($"No RTM tag ({tagPrefix}X.Y.Z) found in history; seeding first release from 0.0.0.");
        }

        var last = latest ?? new Version(0, 0, 0);
        var core = Bump(last, bump);
        var version = preReleaseId is "" ? core.ToString() : $"{core}-{preReleaseId}.{NextCounter(tagPrefix, core, preReleaseId)}";
        var tag = $"{tagPrefix}{version}";
        Console.WriteLine($"Latest RTM tag: {tagPrefix}{last} -> next ({bump}{(preReleaseId is "" ? string.Empty : $", {preReleaseId}")}): {tag}");

        if (Process.Run("git", ["rev-parse", "-q", "--verify", $"refs/tags/{tag}"], silent: true) is { ExitCode: 0 })
        {
            Console.WriteLine($"::error::Tag {tag} already exists. Refusing to re-release an existing version.");
            return Failure;
        }

        CreateTag(tag, pushTag is "true");

        // MinVer's MSBuild task stamps this verbatim instead of walking git.
        File.AppendAllText(GetEnvironmentVariable("GITHUB_ENV")!, $"MINVERVERSIONOVERRIDE={version}\n");
        File.AppendAllText(GetEnvironmentVariable("GITHUB_OUTPUT")!, $"version={version}\ntag={tag}\nprerelease={(preReleaseId is "" ? "false" : "true")}\n");
        return 0;
    }

    /// <summary>Checks the bump level and pre-release identifier, reporting the first problem.</summary>
    /// <param name="bump">The bump level.</param>
    /// <param name="preReleaseId">The pre-release channel, or empty.</param>
    /// <returns>True when both are valid.</returns>
    private static bool IsValid(string bump, string preReleaseId)
    {
        if (bump is not ("major" or "minor" or "patch"))
        {
            Console.WriteLine($"::error::bump must be one of major|minor|patch (got '{bump}').");
            return false;
        }

        // No dots, so "<core>-<id>.<N>" has exactly one numeric counter to parse.
        if (preReleaseId is not "" && !PreReleaseIdentifier().IsMatch(preReleaseId))
        {
            Console.WriteLine($"::error::pre-release-id '{preReleaseId}' must be a SemVer 2.0 alphanumeric/hyphen identifier (no dots).");
            return false;
        }

        return true;
    }

    /// <summary>Increments one segment of a release and resets the lower segments.</summary>
    /// <param name="last">The latest release.</param>
    /// <param name="bump">The validated bump level.</param>
    /// <returns>The next core version.</returns>
    private static Version Bump(Version last, string bump) => bump switch
    {
        "major" => new(last.Major + 1, 0, 0),
        "minor" => new(last.Major, last.Minor + 1, 0),
        _ => new(last.Major, last.Minor, last.Build + 1),
    };

    /// <summary>Creates and pushes the tag, or reports that a later step will create it.</summary>
    /// <param name="tag">The tag name.</param>
    /// <param name="push">True to create and push the tag now.</param>
    private static void CreateTag(string tag, bool push)
    {
        if (push)
        {
            _ = Git("config", "user.name", "github-actions[bot]");
            _ = Git("config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com");
            _ = Git("tag", tag);
            _ = Git("push", "origin", tag);
            Console.WriteLine($"Pushed tag {tag}.");
        }
        else
        {
            Console.WriteLine($"push-tag=false: computed {tag} but not creating it now (deferred, e.g. to GitHub release creation).");
        }
    }

    /// <summary>Finds the highest stable release tag of the form prefix + X.Y.Z.</summary>
    /// <param name="tagPrefix">The release tag prefix.</param>
    /// <returns>The highest release, or null when there is none.</returns>
    private static Version? LatestRelease(string tagPrefix)
    {
        Version? latest = null;
        foreach (var tag in Lines(Git("tag", "--list", $"{tagPrefix}[0-9]*.[0-9]*.[0-9]*")))
        {
            if (TryParseRelease(tag, tagPrefix, out var release) && (latest is null || release > latest))
            {
                latest = release;
            }
        }

        return latest;
    }

    /// <summary>Parses a stable release tag of the form prefix + X.Y.Z.</summary>
    /// <param name="tag">The tag name.</param>
    /// <param name="tagPrefix">The release tag prefix.</param>
    /// <param name="release">The parsed version.</param>
    /// <returns>True when the tag is a stable release tag.</returns>
    private static bool TryParseRelease(string tag, string tagPrefix, [NotNullWhen(true)] out Version? release)
    {
        release = null;
        if (!tag.StartsWith(tagPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var match = StableVersion().Match(tag, tagPrefix.Length);
        if (!match.Success)
        {
            return false;
        }

        release = new(Number(match.Groups[1]), Number(match.Groups[2]), Number(match.Groups[3]));
        return true;
    }

    /// <summary>Finds the next pre-release counter for a core version and channel.</summary>
    /// <param name="tagPrefix">The release tag prefix.</param>
    /// <param name="core">The bumped core version.</param>
    /// <param name="preReleaseId">The pre-release channel.</param>
    /// <returns>One more than the highest existing counter, or 1.</returns>
    private static int NextCounter(string tagPrefix, Version core, string preReleaseId)
    {
        var highest = 0;
        foreach (var tag in Lines(Git("tag", "--list", $"{tagPrefix}{core}-{preReleaseId}.[0-9]*")))
        {
            if (int.TryParse(tag.AsSpan(tag.LastIndexOf('.') + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var counter))
            {
                highest = Math.Max(highest, counter);
            }
        }

        return highest + 1;
    }

    /// <summary>Runs git, echoing its errors and exiting with its exit code when it fails.</summary>
    /// <param name="arguments">The git arguments.</param>
    /// <returns>The standard output.</returns>
    private static string Git(params IEnumerable<string> arguments)
    {
        var result = Process.RunAndCaptureText("git", arguments);
        Console.Error.Write(result.StandardError);
        if (result is not { ExitStatus.ExitCode: 0 })
        {
            Exit(result.ExitStatus.ExitCode);
        }

        return result.StandardOutput;
    }

    /// <summary>Splits command output into trimmed, non-empty lines.</summary>
    /// <param name="output">The command output.</param>
    /// <returns>The lines.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string[] Lines(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Parses a captured version segment.</summary>
    /// <param name="group">The captured digits.</param>
    /// <returns>The number.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Number(Group group) => int.Parse(group.ValueSpan, CultureInfo.InvariantCulture);

    /// <summary>Matches a SemVer 2.0 pre-release identifier with no dots.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex PreReleaseIdentifier();

    /// <summary>Matches X.Y.Z from the start position through the end of the tag.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"\G(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex StableVersion();
}
