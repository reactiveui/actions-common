// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

// Process start looks in the app's own folder before PATH, so an app named minver would run itself instead of the tool.
#:property AssemblyName=minver-action

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using static System.Environment;

return Program.Run(args);

/// <summary>Computes the MinVer version for HEAD and exports it as MINVER_* variables and step outputs.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    private const int UsageError = 2;

    /// <summary>Computes the version and writes GITHUB_ENV and GITHUB_OUTPUT.</summary>
    /// <param name="args">The tag prefix, minimum major.minor, pre-release identifiers, auto-increment, verbosity and version override.</param>
    /// <returns>The process exit code.</returns>
    internal static int Run(string[] args)
    {
        if (args is not [var tagPrefix, var minimumMajorMinor, var defaultPreRelease, var autoIncrement, var verbosity, var versionOverride])
        {
            Console.WriteLine("::error::Expected the tag prefix, minimum major.minor, pre-release identifiers, auto-increment, verbosity and version override arguments.");
            return UsageError;
        }

        Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

        string semVer2;
        if (versionOverride is not "")
        {
            semVer2 = versionOverride;
            Console.WriteLine($"Version override supplied; skipping MinVer git walk. Using: {semVer2}");
        }
        else
        {
            var minver = Process.RunAndCaptureText(
                "minver",
                [
                    "--tag-prefix", tagPrefix, "--verbosity", verbosity, "--auto-increment", autoIncrement,
                    .. Option("--minimum-major-minor", minimumMajorMinor),
                    .. Option("--default-pre-release-identifiers", defaultPreRelease),
                ]);

            Console.Error.Write(minver.StandardError);
            if (minver is not { ExitStatus.ExitCode: 0, StandardOutput: var output })
            {
                return minver.ExitStatus.ExitCode;
            }

            semVer2 = output.Trim();
            Console.WriteLine($"MinVer produced: {semVer2}");
        }

        Export(semVer2, tagPrefix);
        return 0;
    }

    /// <summary>Derives the version forms from a SemVer2 version and writes GITHUB_ENV and GITHUB_OUTPUT.</summary>
    /// <param name="semVer2">The full SemVer2 version.</param>
    /// <param name="tagPrefix">The release tag prefix, for the version height.</param>
    private static void Export(string semVer2, string tagPrefix)
    {
        // SemVer2 is "<major>.<minor>.<patch>[-<pre>][+<meta>]".
        var core = semVer2.AsSpan()[..End(semVer2, '-', '+')].ToString();
        var preRelease = semVer2.AsSpan(core.Length) is ['-', .. var rest] ? rest[..End(rest, '+', '+')].ToString() : string.Empty;
        var height = Process.RunAndCaptureText("git", ["describe", "--tags", "--abbrev=0", "--match", $"{tagPrefix}*"]) switch
        {
            { ExitStatus.ExitCode: 0, StandardOutput: var lastTag } => Git("rev-list", "--count", $"{lastTag.Trim()}..HEAD"),
            _ => Git("rev-list", "--count", "HEAD"),
        };

        (string Name, string Value)[] values =
        [
            ("SemVer2", semVer2),
            ("SimpleVersion", core),
            ("AssemblyVersion", $"{core.AsSpan()[..Major(core)]}.0.0.0"),
            ("AssemblyFileVersion", $"{core}.0"),
            ("AssemblyInformationalVersion", semVer2),

            // Package feeds reject build metadata, so the NuGet version drops it.
            ("NuGetPackageVersion", preRelease is "" ? core : $"{core}-{preRelease}"),
            ("GitCommitId", Git("rev-parse", "HEAD")),
            ("GitCommitIdShort", Git("rev-parse", "--short=7", "HEAD")),
            ("VersionHeight", height),
        ];

        var environment = new StringBuilder();
        var outputs = new StringBuilder();
        foreach (var (name, value) in values)
        {
            _ = environment.Append("MINVER_").Append(name).Append('=').Append(value).Append('\n');
            _ = outputs.Append(name).Append('=').Append(value).Append('\n');
        }

        // MinVer's MSBuild task stamps MINVERVERSIONOVERRIDE verbatim instead of walking git per project.
        _ = environment.Append("MINVERVERSIONOVERRIDE=").Append(semVer2).Append('\n');
        File.AppendAllText(GetEnvironmentVariable("GITHUB_ENV")!, environment.ToString());
        File.AppendAllText(GetEnvironmentVariable("GITHUB_OUTPUT")!, outputs.ToString());
    }

    /// <summary>Finds the length of the text before the first of two separators.</summary>
    /// <param name="value">The version text.</param>
    /// <param name="first">The first separator.</param>
    /// <param name="second">The second separator.</param>
    /// <returns>The length of the leading part, or the whole length when neither separator occurs.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int End(ReadOnlySpan<char> value, char first, char second) => value.IndexOfAny(first, second) is var end and >= 0 ? end : value.Length;

    /// <summary>Finds the length of the major segment.</summary>
    /// <param name="core">The major.minor.patch text.</param>
    /// <returns>The length of the text before the first '.'.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Major(string core) => End(core, '.', '.');

    /// <summary>Builds a command-line option, or none when the value is empty.</summary>
    /// <param name="name">The option name.</param>
    /// <param name="value">The option value.</param>
    /// <returns>The option and its value, or nothing.</returns>
    private static string[] Option(string name, string value) => value is "" ? [] : [name, value];

    /// <summary>Runs git and returns its trimmed standard output.</summary>
    /// <param name="arguments">The git arguments.</param>
    /// <returns>The trimmed output.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Git(params IEnumerable<string> arguments) => Process.RunAndCaptureText("git", arguments).StandardOutput.Trim();
}
