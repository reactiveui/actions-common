// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using static System.Environment;

if (args is not [var filter, var budgetKb, var artifactName])
{
    Console.WriteLine("::error::Expected the filter, summary budget and artifact name arguments.");
    return;
}

var budget = long.Parse(budgetKb, CultureInfo.InvariantCulture) * Program.Kibibyte;

var summary = GetEnvironmentVariable("GITHUB_STEP_SUMMARY") ?? string.Empty;

var root = Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory(), "benchmark-artifacts");

// With a revision input the triggering commit is not the measured one, so the checked-out commit MinVer read comes first.
var commit = GetEnvironmentVariable("MINVER_GitCommitId") ?? GetEnvironmentVariable("GITHUB_SHA");

Program.AppendLines(
    summary,
    [
        "# Benchmark results",
        string.Empty,
        $"Filter `{filter}`, commit `{commit}`, measured on `{GetEnvironmentVariable("RUNNER_OS")}`.",
        string.Empty,
        "Each section is one benchmark class. The runtimes measured are whichever jobs that class",
        "declares, so a class with no NativeAOT row cannot run ahead-of-time at all. A hosted runner",
        "is shared, so read these for trend and regression rather than as absolute figures.",
        $"Full JSON, HTML and CSV exports are attached to this run as `{artifactName}`.",
        string.Empty,
    ]);

var reports = Program.Reports(root);

if (reports is [])
{
    Program.AppendLines(summary, ["No benchmark reports were exported."]);
    return;
}

List<string> skipped = [];

foreach (var report in reports)
{
    var title = Program.Title(report);
    if (new FileInfo(summary).Length + new FileInfo(report).Length > budget)
    {
        skipped.Add(title);
        continue;
    }

    Program.AppendLines(summary, [$"<details><summary>{title}</summary>", string.Empty, .. File.ReadLines(report), string.Empty, "</details>", string.Empty]);
}

if (skipped is not [])
{
    Program.AppendLines(summary, [string.Empty, $"Omitted here to stay inside the summary limit: {string.Join(", ", skipped)}. Read them in the {artifactName} artifact.", string.Empty]);
}

/// <summary>Appends the benchmark reports to the run summary, within a size budget.</summary>
internal static partial class Program
{
    /// <summary>The bytes in a kibibyte.</summary>
    internal const long Kibibyte = 1024;

    /// <summary>The file name suffix of a GitHub markdown report.</summary>
    private const string ReportSuffix = "-report-github.md";

    /// <summary>Appends lines to a file, each ending with the platform newline.</summary>
    /// <param name="path">The file.</param>
    /// <param name="lines">The lines.</param>
    internal static void AppendLines(string path, ReadOnlySpan<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            _ = text.Append(line).Append(NewLine);
        }

        File.AppendAllText(path, text.ToString());
    }

    /// <summary>Finds the GitHub markdown reports under the artifacts folder.</summary>
    /// <param name="root">The artifacts folder.</param>
    /// <returns>The report paths in ordinal order, or empty when the folder is missing.</returns>
    internal static string[] Reports(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var reports = Directory.GetFiles(root, $"*{ReportSuffix}", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 });
        Array.Sort(reports, StringComparer.Ordinal);
        return reports;
    }

    /// <summary>Gets the report title: its file name without the report suffix.</summary>
    /// <param name="report">The report path.</param>
    /// <returns>The title.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string Title(string report) => Path.GetFileName(report).Replace(ReportSuffix, string.Empty, StringComparison.Ordinal);
}
