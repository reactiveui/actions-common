// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

if (args is not [var repository, var tag, var target, var title, var prerelease, var assetGlob])
{
    Console.WriteLine("::error::Expected the repository, tag, target, title, pre-release and asset pattern arguments.");
    return Program.UsageError;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

string[] flags = prerelease is "true" ? ["--prerelease"] : [];

string[] assets = Directory.Exists(Program.AssetFolder) ? [.. Directory.GetFiles(Program.AssetFolder, assetGlob).Order(StringComparer.Ordinal)] : [];

return Process.Run(
    "gh",
    ["release", "create", tag, "--repo", repository, "--target", target, "--title", title, "--notes-file", "release-notes.md", .. flags, .. assets]).ExitCode;

/// <summary>Creates a GitHub release, and its tag, with the matching downloaded files attached.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The workspace folder holding the files to attach.</summary>
    internal const string AssetFolder = "signed";
}
