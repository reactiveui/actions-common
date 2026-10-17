// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

if (args is not [var bumpTag, var bumpPrerelease, var versionOverride, var tagPrefix])
{
    Console.WriteLine("::error::Expected the bump tag, bump pre-release, version override and tag prefix arguments.");
    return Program.UsageError;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

if (Process.RunAndCaptureText("git", ["rev-parse", "HEAD"]) is not { ExitStatus.ExitCode: 0, StandardOutput: var head })
{
    Console.WriteLine("::error::Could not resolve the built commit SHA.");
    return 1;
}

var outputs = GetEnvironmentVariable("GITHUB_OUTPUT") ?? throw new InvalidOperationException("GITHUB_OUTPUT is not set.");

File.AppendAllLines(outputs, [$"sha={head.Trim()}"]);

if (versionOverride is "")
{
    File.AppendAllLines(outputs, [$"tag={bumpTag}", $"prerelease={(bumpPrerelease is "" ? "false" : bumpPrerelease)}"]);
    return 0;
}

var tag = $"{tagPrefix}{versionOverride}";

if (Process.Run("git", ["rev-parse", "-q", "--verify", $"refs/tags/{tag}"], silent: true) is { ExitCode: 0 })
{
    Console.WriteLine($"::error::Tag {tag} already exists. Refusing to re-release an existing version.");
    return 1;
}

File.AppendAllLines(outputs, [$"tag={tag}", $"prerelease={(versionOverride.Contains('-', StringComparison.Ordinal) ? "true" : "false")}"]);

return 0;

/// <summary>Resolves the built commit and the release tag from a bump result or a version override.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;
}
