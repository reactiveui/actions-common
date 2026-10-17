// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

if (args is not [var packDir])
{
    Console.WriteLine("::error::Expected the pack directory argument.");
    return Program.UsageError;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

var qlpack = Path.Combine(packDir, "qlpack.yml");

var lines = File.ReadAllLines(qlpack);

if (Array.FindIndex(lines, static line => line.StartsWith(Program.VersionKey, StringComparison.Ordinal)) is not (>= 0 and var index)
    || lines[index][Program.VersionKey.Length..].Trim().Split('.') is not [var major, var minor, _])
{
    Console.WriteLine($"::error::{qlpack} needs a top-level major.minor.patch version.");
    return 1;
}

if (Process.RunAndCaptureText("git", ["rev-list", "--count", "HEAD", "--", packDir]) is not { ExitStatus.ExitCode: 0, StandardOutput: var commits })
{
    Console.WriteLine("::error::Could not count the commits that touched the pack.");
    return 1;
}

lines[index] = $"version: {major}.{minor}.{commits.Trim()}";

File.WriteAllLines(qlpack, lines);

Console.WriteLine($"Publishing {lines[index]}");

return 0;

/// <summary>Sets the pack's patch version to the number of commits that touched the pack.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The key of the top-level version line.</summary>
    internal const string VersionKey = "version:";
}
