// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

return Program.Run(args);

/// <summary>Restores an UNO solution with the .NET CLI, then builds and packs it with Visual Studio's MSBuild.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    private const int UsageError = 2;

    /// <summary>Runs the restore or build command in the source folder.</summary>
    /// <param name="args">restore with the source folder, configuration and solution file; or build with those and the package output folder. Empty values are left out.</param>
    /// <returns>The exit code of dotnet restore or MSBuild.</returns>
    internal static int Run(string[] args)
    {
        if (args is ["restore", var restoreFolder, var restoreConfiguration, var restoreSolution])
        {
            Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, restoreFolder));
            return Process.Run("dotnet", ["restore", $"-p:Configuration={restoreConfiguration}", .. Option(string.Empty, restoreSolution)]).ExitCode;
        }

        if (args is not ["build", var srcFolder, var configuration, var solutionFile, var packageOutput])
        {
            Console.WriteLine("::error::Expected restore with the source folder, configuration and solution file, or build with those and the package output folder.");
            return UsageError;
        }

        Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, srcFolder));
        return Process.Run(
            "msbuild",
            [
                "/t:build,pack", "/nowarn:MSB4011", "/maxcpucount", "/p:NoPackageAnalysis=true", "/verbosity:minimal",
                $"/p:Configuration={configuration}",
                .. Option("/p:PackageOutputPath=", packageOutput),
                .. Option(string.Empty, solutionFile),
            ]).ExitCode;
    }

    /// <summary>Builds an argument, leaving it out when the value is empty.</summary>
    /// <param name="prefix">The text before the value.</param>
    /// <param name="value">The value.</param>
    /// <returns>The argument, or nothing.</returns>
    private static string[] Option(string prefix, string value) => value is "" ? [] : [prefix + value];
}
