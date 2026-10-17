// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using static System.Environment;

return Program.Run(args);

/// <summary>Restores the workloads a solution's projects need.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    private const int UsageError = 2;

    /// <summary>Finds solution files in the solution folder, hidden ones included.</summary>
    private static readonly EnumerationOptions SolutionSearch = new() { AttributesToSkip = 0 };

    /// <summary>Restores the workloads for the solution, its filter's base solution, or the solution found in the folder.</summary>
    /// <param name="args">The source folder, relative to the workspace, and the solution file, relative to it; an empty solution file finds one.</param>
    /// <returns>The exit code of dotnet workload restore.</returns>
    internal static int Run(string[] args)
    {
        if (args is not [var srcFolder, var solutionFile])
        {
            Console.WriteLine("::error::Expected the source folder and solution file arguments.");
            return UsageError;
        }

        Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, srcFolder));

        // dotnet workload restore does not read a solution filter, so a filter restores its base solution's workloads.
        if (solutionFile.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            var filter = JsonSerializer.Deserialize(File.ReadAllBytes(solutionFile), SolutionFilterContext.Default.SolutionFilter)!;
            solutionFile = Path.Combine(Path.GetDirectoryName(solutionFile) ?? string.Empty, filter.Solution.Path);
        }

        var found = solutionFile is not "" ? solutionFile : FindSolution();
        string[] solution = found is null ? [] : [found];
        return Process.Run("dotnet", ["workload", "restore", .. solution, "--from-previous-sdk", "--skip-manifest-update"]).ExitCode;
    }

    /// <summary>Finds the solution in the current folder, preferring .slnx over .sln.</summary>
    /// <returns>The first solution in ordinal order, or null when there is none.</returns>
    private static string? FindSolution()
    {
        foreach (var pattern in (ReadOnlySpan<string>)["*.slnx", "*.sln"])
        {
            string? first = null;
            foreach (var file in Directory.EnumerateFiles(".", pattern, SolutionSearch))
            {
                if (first is null || string.CompareOrdinal(file, first) < 0)
                {
                    first = file;
                }
            }

            if (first is not null)
            {
                return first;
            }
        }

        return null;
    }

    /// <summary>Source-generated JSON metadata for solution filters.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(SolutionFilter))]
    private sealed partial class SolutionFilterContext : JsonSerializerContext;

    /// <summary>A solution filter (.slnf) file.</summary>
    /// <param name="Solution">The base solution.</param>
    private sealed record SolutionFilter([property: JsonRequired] SolutionFilterSolution Solution);

    /// <summary>The solution section of a solution filter.</summary>
    /// <param name="Path">The base solution, relative to the filter.</param>
    private sealed record SolutionFilterSolution([property: JsonRequired] string Path);
}
