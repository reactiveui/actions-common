// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:package Microsoft.Extensions.FileSystemGlobbing@*

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.FileSystemGlobbing;
using static System.Environment;

return Program.Run(args);

/// <summary>Runs the solution's tests on Microsoft Testing Platform with native coverage.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    private const int UsageError = 2;

    /// <summary>The exit code for a missing test configuration or an empty project selection.</summary>
    private const int Failure = 1;

    /// <summary>The dotnet test option that names the solution.</summary>
    private const string SolutionOption = "--solution";

    /// <summary>The attribute holding a project's path in a .slnx file.</summary>
    private const string ProjectPathAttribute = "Path";

    /// <summary>Finds solution files in the solution folder, hidden ones included.</summary>
    private static readonly EnumerationOptions SolutionSearch = new() { AttributesToSkip = 0 };

    /// <summary>Runs dotnet test over the solution or the chosen projects.</summary>
    /// <param name="args">The source folder, solution file, configuration, detailed logging flag, test timeout and test project globs.</param>
    /// <returns>The process exit code.</returns>
    internal static int Run(string[] args)
    {
        if (args is not [var srcFolder, var solutionFile, var configuration, var detailedLogging, var testTimeout, var testProjects])
        {
            Console.WriteLine("::error::Expected the source folder, solution file, configuration, detailed logging, test timeout and test projects arguments.");
            return UsageError;
        }

        Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, srcFolder));

        if (!File.Exists("testconfig.json"))
        {
            Console.WriteLine(
                $"::error::MTP requires '{srcFolder}/testconfig.json' (manages coverage and execution). "
                + "See https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-extensions-code-coverage");
            return Failure;
        }

        var solutionPath = solutionFile is not "" ? solutionFile : FindSolution();
        string[] solution = solutionPath is null ? [] : [SolutionOption, solutionPath];

        var patterns = testProjects.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (patterns is not [])
        {
            if (solutionPath is null)
            {
                Console.WriteLine("::error::test-projects needs a solution to choose projects from; set solution-file.");
                return Failure;
            }

            if (WriteProjectFilter(solutionPath, patterns) is not { } filterPath)
            {
                return Failure;
            }

            solution = [SolutionOption, filterPath];
        }

        // --verbosity is VSTest-only and ignored by MTP. Synchronous writes keep the log when the runner kills a hung test.
        string[] diagnostics = detailedLogging is "true"
            ? ["--diagnostic", "--diagnostic-verbosity", "Trace", "--diagnostic-synchronous-write", "--log-level", "Trace", "--output", "Detailed"]
            : [];

        string[] timeout = testTimeout is "" ? [] : ["--timeout", testTimeout];

        // No --results-directory: parallel test hosts sharing one directory collide on log_<ts>.diag names.
        return Process.Run(
            "dotnet",
            ["test", .. solution, "--no-build", "--configuration", configuration, .. diagnostics, .. timeout, "--coverage", "--coverage-output-format", "cobertura"]).ExitCode;
    }

    /// <summary>Writes a solution filter holding the solution's projects that match the globs.</summary>
    /// <param name="solutionPath">The solution or solution filter to choose from.</param>
    /// <param name="patterns">The globs; a leading '!' excludes.</param>
    /// <returns>The written filter's path, or null when no project matches.</returns>
    private static string? WriteProjectFilter(string solutionPath, string[] patterns)
    {
        var (baseSolution, listed) = ListProjects(solutionPath);
        var root = Path.GetDirectoryName(Path.GetFullPath(baseSolution))!;
        List<string> candidates = [with(listed.Count)];
        foreach (var path in listed.Keys)
        {
            candidates.Add(Path.Combine(root, path));
        }

        // Match the solution's own project list in memory; nothing is read from disk.
        List<string> projects = [];
        foreach (var match in CreateMatcher(patterns).Match(root, candidates).Files)
        {
            projects.Add(listed[match.Path]);
        }

        if (projects is [])
        {
            Console.WriteLine($"::error::No project in {baseSolution} matches test-projects: {string.Join(", ", patterns)}");
            return null;
        }

        // A solution filter keeps dotnet test running the chosen projects in parallel, as it does for the whole solution.
        var filterPath = Path.Combine(root, "ci-test-projects.slnf");
        File.WriteAllBytes(filterPath, JsonSerializer.SerializeToUtf8Bytes(new(new(Path.GetFileName(baseSolution), [.. projects])), SolutionFilterContext.Default.SolutionFilter));

        Console.WriteLine($"Testing {projects.Count} project(s) matching test-projects:");
        foreach (var project in projects)
        {
            Console.WriteLine($"  {project}");
        }

        return filterPath;
    }

    /// <summary>Builds a case-insensitive glob matcher.</summary>
    /// <param name="patterns">The globs; a leading '!' excludes.</param>
    /// <returns>The matcher.</returns>
    private static Matcher CreateMatcher(string[] patterns)
    {
        Matcher matcher = new(StringComparison.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            _ = pattern.StartsWith('!') ? matcher.AddExclude(pattern[1..]) : matcher.AddInclude(pattern);
        }

        return matcher;
    }

    /// <summary>Lists the projects to choose from, keyed by their forward-slash path.</summary>
    /// <param name="solutionPath">The solution or solution filter.</param>
    /// <returns>The solution the project paths are relative to, and the projects as listed.</returns>
    private static (string BaseSolution, Dictionary<string, string> Projects) ListProjects(string solutionPath)
    {
        // A solution filter lists its projects relative to its base solution, so match against that solution.
        IReadOnlyList<string>? filtered = null;
        if (solutionPath.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            var filter = JsonSerializer.Deserialize(File.ReadAllBytes(solutionPath), SolutionFilterContext.Default.SolutionFilter)!;
            filtered = filter.Solution.Projects;
            solutionPath = Path.Combine(Path.GetDirectoryName(solutionPath) ?? string.Empty, filter.Solution.Path);
        }

        Dictionary<string, string> listed = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var project in filtered ?? SolutionProjects(solutionPath))
        {
            listed[project.Replace('\\', '/')] = project;
        }

        return (solutionPath, listed);
    }

    /// <summary>Lists the project paths as the solution lists them, relative to the solution's folder.</summary>
    /// <param name="solutionPath">The .slnx or .sln file.</param>
    /// <returns>The project paths.</returns>
    private static List<string> SolutionProjects(string solutionPath)
    {
        List<string> projects = [];
        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var project in XDocument.Load(solutionPath).Descendants("Project"))
            {
                if ((string?)project.Attribute(ProjectPathAttribute) is { } path)
                {
                    projects.Add(path);
                }
            }

            return projects;
        }

        for (var match = SlnProject().Match(File.ReadAllText(solutionPath)); match.Success; match = match.NextMatch())
        {
            projects.Add(match.Groups[1].Value);
        }

        return projects;
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

    /// <summary>Matches a project line in a .sln file, capturing the project path.</summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex("^Project\\(\"[^\"]*\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+\\.[a-z]+proj)\"", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex SlnProject();

    /// <summary>Source-generated JSON metadata for solution filters.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
    [JsonSerializable(typeof(SolutionFilter))]
    private sealed partial class SolutionFilterContext : JsonSerializerContext;

    /// <summary>A solution filter (.slnf) file.</summary>
    /// <param name="Solution">The base solution and the projects it keeps.</param>
    private sealed record SolutionFilter([property: JsonRequired] SolutionFilterSolution Solution);

    /// <summary>The solution section of a solution filter.</summary>
    /// <param name="Path">The base solution, relative to the filter.</param>
    /// <param name="Projects">The kept projects, relative to the base solution.</param>
    private sealed record SolutionFilterSolution([property: JsonRequired] string Path, [property: JsonRequired] string[] Projects);
}
