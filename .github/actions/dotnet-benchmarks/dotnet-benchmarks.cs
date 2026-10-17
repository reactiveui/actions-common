// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.ComponentModel;
using System.Diagnostics;
using static System.Environment;

return Program.Run(args);

/// <summary>Runs BenchmarkDotNet projects and writes each one's reports to its own results folder.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    private const int UsageError = 2;

    /// <summary>Runs each benchmark project in turn, stopping at the first failure.</summary>
    /// <param name="args">The newline-separated projects, relative to src, and the results folder.</param>
    /// <returns>The process exit code.</returns>
    internal static int Run(string[] args)
    {
        if (args is not [var projects, var results])
        {
            Console.WriteLine("::error::Expected the projects and results folder arguments.");
            return UsageError;
        }

        // A short build path on Windows keeps the NativeAOT build under 260 characters.
        var output = OperatingSystem.IsWindows() ? @"C:\a" : Path.Combine(GetEnvironmentVariable("RUNNER_TEMP")!, "a");

        RaisePriority();
        Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, "src"));

        foreach (var project in projects.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var status = Process.Run(
                "dotnet",
                [
                    "run", "--project", project, "-c", "Release", "-f", "net10.0", "--artifacts-path", output, "--",
                    "--filter", "*", "--artifacts", Path.Combine(results, name), "--exporters", "github", "fulljson",
                ]);

            if (status is { ExitCode: not 0 })
            {
                return status.ExitCode;
            }
        }

        return 0;
    }

    /// <summary>Raises this process's priority; processes started from here inherit it on Unix, and on Windows BenchmarkDotNet raises its own.</summary>
    private static void RaisePriority()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            current.PriorityClass = ProcessPriorityClass.High;
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException or InvalidOperationException)
        {
            Console.WriteLine($"::warning::Could not raise the benchmark priority: {ex.Message}");
        }
    }
}
