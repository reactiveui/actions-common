// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

if (Directory.GetFiles(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory(), "*.nupkg") is not [_, ..] packages)
{
    Console.WriteLine("::error::The artifact holds no .nupkg files.");
    return 1;
}

var source = $"https://nuget.pkg.github.com/{GetEnvironmentVariable("GITHUB_REPOSITORY_OWNER")}/index.json";

var apiKey = GetEnvironmentVariable("GITHUB_TOKEN") ?? string.Empty;

foreach (var package in packages.Order(StringComparer.Ordinal))
{
    if (Process.Run("dotnet", ["nuget", "push", package, "--source", source, "--api-key", apiKey, "--skip-duplicate"]) is { ExitCode: not 0 } push)
    {
        return push.ExitCode;
    }
}

return 0;
