#!/usr/bin/env dotnet run
// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:property TargetFramework=net11.0

using System.Diagnostics;
using static System.Environment;

const int TimeoutMinutes = 30;

const int UsageError = 2;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(TimeoutMinutes));

var cancellationToken = timeout.Token;

var workspace = GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();

if (args is ["changes", var eventName, var before])
{
    var changed = true;
    var baseline = eventName switch
    {
        "pull_request" => "HEAD^1",
        "push" when before.Trim('0') is not "" => before,
        _ => null,
    };

    if (baseline is not null)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workspace, RedirectStandardOutput = true, RedirectStandardError = true, };
        foreach (var argument in new[] { "diff", "--name-only", "--no-renames", baseline, "HEAD" })
        {
            start.ArgumentList.Add(argument);
        }

        var result = await Process.RunAndCaptureTextAsync(start, cancellationToken);

        // A missing baseline validates everything rather than skipping a check.
        if (result.ExitStatus.ExitCode is 0)
        {
            changed = false;
            foreach (var path in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                changed |= path.EndsWith(".cs", StringComparison.Ordinal)
                    || path.EndsWith(".props", StringComparison.Ordinal)
                    || path.EndsWith(".targets", StringComparison.Ordinal)
                    || path.EndsWith("/action.yml", StringComparison.Ordinal)
                    || path is ".editorconfig" or "global.json" or "NuGet.Config" or "nuget.config"
                        or ".github/workflows/validate-scripts.yml";
            }
        }
    }

    await File.AppendAllTextAsync(
        GetEnvironmentVariable("GITHUB_OUTPUT")!,
        $"scripts={(changed ? "true" : "false")}\n",
        cancellationToken);
    Console.WriteLine($"Script validation required: {changed}.");
    return 0;
}

if (args is not ["build"])
{
    Console.WriteLine("::error::Expected build, or changes followed by the event name and before commit.");
    return UsageError;
}

var failures = 0;

var count = 0;

foreach (var folder in Directory.GetDirectories(Path.Combine(workspace, ".github", "actions")).Order(StringComparer.Ordinal))
{
    var script = Path.Combine(folder, $"{Path.GetFileName(folder)}.cs");
    if (!File.Exists(script))
    {
        continue;
    }

    count++;
    Console.WriteLine($"::group::Build {Path.GetRelativePath(workspace, script)}");
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = workspace };
    foreach (var argument in new[]
    {
        "build", script, "-f", "net11.0", "--no-incremental", "-v", "q", "-nologo", "-clp:ErrorsOnly", "-p:TreatWarningsAsErrors=true",
        $"-p:ActionLocalSources={Path.Combine(folder, "Program.*.cs")}",
    })
    {
        start.ArgumentList.Add(argument);
    }

    var action = await File.ReadAllTextAsync(Path.Combine(folder, "action.yml"), cancellationToken);
    if (action.Contains("-p:ActionSharedSources=", StringComparison.Ordinal))
    {
        start.ArgumentList.Add($"-p:ActionSharedSources={Path.Combine(workspace, "scripts", "github", "*.cs")}");
    }

    var result = await Process.RunAsync(start, cancellationToken);
    if (result.ExitCode is not 0)
    {
        Console.WriteLine($"::error file={Path.GetRelativePath(workspace, script)}::Script build failed with exit code {result.ExitCode}.");
        failures++;
    }

    Console.WriteLine("::endgroup::");
}

Console.WriteLine($"Built {count} scripts; {failures} failed.");

return failures is 0 && count is not 0 ? 0 : 1;
