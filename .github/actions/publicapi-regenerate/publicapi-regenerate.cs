// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

// Regenerates the PublicApiSharp baselines for every (project, target framework) pair this host can build.
// Usage: dotnet run --file publicapi-regenerate.cs -- <src-dir> [tfm-filter] [project-filter]
// The target frameworks a project offers are host-dependent - the Apple ones only appear where the
// workload exists - so each host is asked what it can build rather than told. `tfm-filter` narrows that
// further: a comma-separated list of substrings, so `-windows,net4` runs only the Windows-side set.
// `project-filter` is a comma-separated list of exact project names, for when a type moved and only a
// couple of projects need rewriting.
using System.Diagnostics;
using System.Globalization;

var srcDir = args is [var first, ..] ? first : ".";

var filters = Program.List(args, Program.TfmFilterPosition);

var projectFilters = Program.List(args, Program.ProjectFilterPosition);

var projects = Program.Projects(srcDir, projectFilters);

Console.WriteLine($"projects: {projects.Count}");

var pairs = Program.Pairs(projects, srcDir, filters);

Console.WriteLine($"pairs to regenerate: {pairs.Count}");

List<string> failures = [];

var index = 0;

foreach (var (project, tfm) in pairs)
{
    index++;
    var name = Path.GetFileNameWithoutExtension(project);
    var baseline = Path.Combine(Path.GetDirectoryName(project)!, "PublicAPI", tfm, "PublicAPI.txt");

    // Start from an empty baseline so the surface is rendered from scratch. The fix only adds and
    // updates entries - a member that has since been removed is PAS0002, which has no fix - so
    // regenerating in place would leave a stale entry behind and fail the next build.
    _ = Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
    File.WriteAllText(baseline, string.Empty);

    var started = Stopwatch.GetTimestamp();
    var (output, code) = Program.Run(
        ["format", "analyzers", project, "-f", tfm, "--diagnostics", "PAS0001", "PAS0003", "PAS0004", "--severity", "info", "-v", "m"],
        srcDir);
    var elapsed = Stopwatch.GetElapsedTime(started);

    var wrote = File.Exists(baseline) && new FileInfo(baseline).Length > 0;
    var failed = code != 0 || !wrote;
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{index}/{pairs.Count}] {(failed ? "FAIL" : "ok")} {name} {tfm} ({elapsed.TotalSeconds:F0}s)"));
    Console.Out.Flush();

    if (!failed)
    {
        continue;
    }

    failures.Add($"{name} {tfm} (exit {code}, baseline {(wrote ? "written" : "empty or missing")})");
    if (failures.Count >= Program.MaximumFailures)
    {
        Console.WriteLine($"giving up after {Program.MaximumFailures} failures");
        return 1;
    }

    foreach (var line in Program.Tail(output))
    {
        Console.WriteLine($"    {line}");
    }
}

Console.WriteLine();

Console.WriteLine(failures is [] ? $"ALL {pairs.Count} BASELINES REGENERATED" : $"FAILED {failures.Count} of {pairs.Count}:");

foreach (var failure in failures)
{
    Console.WriteLine($"  {failure}");
}

return failures is [] ? 0 : 1;

/// <summary>Regenerates the PublicApiSharp baselines with the PAS0001 code fix.</summary>
internal static partial class Program
{
    /// <summary>The failures after which the run stops.</summary>
    internal const int MaximumFailures = 3;

    /// <summary>The position of the target framework filter argument.</summary>
    internal const int TfmFilterPosition = 1;

    /// <summary>The position of the project filter argument.</summary>
    internal const int ProjectFilterPosition = 2;

    /// <summary>The output lines shown for a failure.</summary>
    private const int TailLines = 8;

    /// <summary>Splits a comma-separated argument into trimmed, non-empty items.</summary>
    /// <param name="args">The arguments.</param>
    /// <param name="position">The argument position.</param>
    /// <returns>The items, or empty when the argument is missing or empty.</returns>
    internal static string[] List(string[] args, int position) =>
        args.Length > position && args[position].Length > 0
            ? args[position].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>Finds the projects that already carry a PublicAPI folder.</summary>
    /// <param name="srcDir">The source folder.</param>
    /// <param name="projectFilters">Exact project names to keep, or empty for all.</param>
    /// <returns>The project files in ordinal order.</returns>
    /// <exception cref="InvalidOperationException">A folder with a PublicAPI folder does not hold exactly one project file.</exception>
    internal static List<string> Projects(string srcDir, string[] projectFilters)
    {
        List<string> projects = [];
        foreach (var directory in Directory.EnumerateDirectories(srcDir))
        {
            // Test and sample projects switch tracking off in their own Directory.Build.props and must not gain a baseline here.
            if (!Directory.Exists(Path.Combine(directory, "PublicAPI")))
            {
                continue;
            }

            // A tracked folder holds exactly one project.
            if (Directory.GetFiles(directory, "*.csproj") is not [var project])
            {
                throw new InvalidOperationException($"{directory} must hold exactly one project file.");
            }

            if (projectFilters is [] || projectFilters.Contains(Path.GetFileNameWithoutExtension(project), StringComparer.Ordinal))
            {
                projects.Add(project);
            }
        }

        projects.Sort(StringComparer.Ordinal);
        return projects;
    }

    /// <summary>Asks each project which target frameworks this host builds, keeping those that match a filter.</summary>
    /// <param name="projects">The project files.</param>
    /// <param name="srcDir">The source folder.</param>
    /// <param name="filters">Target framework substrings, or empty for all.</param>
    /// <returns>The project and target framework pairs.</returns>
    internal static List<(string Project, string Tfm)> Pairs(List<string> projects, string srcDir, string[] filters)
    {
        List<(string Project, string Tfm)> pairs = [];
        foreach (var project in projects)
        {
            var (output, code) = Run(["msbuild", project, "-getProperty:TargetFrameworks", "-nologo"], srcDir);
            if (code != 0)
            {
                Console.WriteLine($"!! could not evaluate {Path.GetFileName(project)}: {output.Trim()}");
                continue;
            }

            foreach (var tfm in output.Trim().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (filters is [] || Matches(tfm, filters))
                {
                    pairs.Add((project, tfm));
                }
            }
        }

        return pairs;
    }

    /// <summary>Runs the dotnet host and captures its output.</summary>
    /// <param name="arguments">The arguments.</param>
    /// <param name="workingDirectory">The working directory.</param>
    /// <returns>Standard output followed by standard error, and the exit code.</returns>
    internal static (string Output, int ExitCode) Run(string[] arguments, string workingDirectory)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST") ?? "dotnet";
        var result = Process.RunAndCaptureText(new ProcessStartInfo(dotnet, arguments) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true });
        return (result.StandardOutput + result.StandardError, result.ExitStatus.ExitCode);
    }

    /// <summary>Gets the last non-blank lines of the output, without trailing white space.</summary>
    /// <param name="output">The output.</param>
    /// <returns>The lines.</returns>
    internal static IEnumerable<string> Tail(string output)
    {
        List<string> lines = [];
        foreach (var line in output.Split('\n'))
        {
            if (!line.AsSpan().Trim().IsEmpty)
            {
                lines.Add(line.TrimEnd());
            }
        }

        return lines[Math.Max(0, lines.Count - TailLines)..];
    }

    /// <summary>Checks whether a target framework contains any filter.</summary>
    /// <param name="tfm">The target framework.</param>
    /// <param name="filters">The substrings.</param>
    /// <returns>Whether one matches.</returns>
    private static bool Matches(string tfm, string[] filters)
    {
        foreach (var filter in filters)
        {
            if (tfm.Contains(filter, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
