// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

if (args is not [var projects, var srcFolder, var filter, var configuration, var hostFramework, var exporters, var buildOutputPath, var raisePriority])
{
    Console.WriteLine("::error::Expected the eight benchmark run arguments.");
    return Program.UsageError;
}

var workspace = GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();

// An empty build output path picks a drive root on Windows, where the NativeAOT build otherwise passes 260 characters.
string[] artifactsPath = (buildOutputPath, OperatingSystem.IsWindows()) switch
{
    ({ Length: > 0 }, _) => ["--artifacts-path", buildOutputPath],
    (_, true) => ["--artifacts-path", @"C:\a"],
    _ => [],
};

if (raisePriority is "true")
{
    Program.RaisePriority();
}

Directory.SetCurrentDirectory(Path.Combine(workspace, srcFolder));

var exporterArguments = exporters.Split(' ', StringSplitOptions.RemoveEmptyEntries);

foreach (var project in projects.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
{
    var name = Path.GetFileNameWithoutExtension(project);
    Console.WriteLine($"::group::{name}");

    var status = Process.Run(
        "dotnet",
        [
            "run", "--project", project, "-c", configuration, "-f", hostFramework, .. artifactsPath, "--",
            "--filter", filter, "--artifacts", Path.Combine(workspace, "benchmark-artifacts", name),
            "--exporters", .. exporterArguments,
        ]);

    Console.WriteLine("::endgroup::");
    if (status is { ExitCode: 0 })
    {
        continue;
    }

    Console.WriteLine($"::error::{name} exited with {status.ExitCode}");
    return status.ExitCode;
}

return 0;

/// <summary>Runs each benchmark project with BenchmarkDotNet, exporting into benchmark-artifacts.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>Raises this process to high priority. Processes started from here inherit it on Unix; on Windows BenchmarkDotNet raises its own benchmark processes.</summary>
    internal static void RaisePriority()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            current.PriorityClass = ProcessPriorityClass.High;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Console.WriteLine($"::warning::Could not raise the benchmark priority: {ex.Message}");
        }
    }
}
