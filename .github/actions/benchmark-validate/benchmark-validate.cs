// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using static System.Environment;

var root = Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory(), "benchmark-artifacts");

// BenchmarkDotNet exits zero on a validation error, so the exported reports are the evidence it ran.
var reports = Program.Reports(root);

if (reports is [])
{
    Console.WriteLine("::error::No benchmark reports were produced. Check the validation errors above.");
    return 1;
}

// A job whose build fails still exports its rows, with every measurement as NA.
List<string> unmeasured = [];

foreach (var report in reports)
{
    if (Program.UnmeasuredRows(report) is > 0 and var rows)
    {
        unmeasured.Add($"{Program.Title(report)} ({rows} rows)");
    }
}

if (unmeasured is not [])
{
    Console.WriteLine($"::error::These benchmarks measured nothing: {string.Join("; ", unmeasured)}");
    return 1;
}

return 0;

/// <summary>Fails when BenchmarkDotNet exported no reports, or a report holds a row measured as NA.</summary>
internal static partial class Program
{
    /// <summary>The file name suffix of a GitHub markdown report.</summary>
    private const string ReportSuffix = "-report-github.md";

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

    /// <summary>Counts the table rows that hold a cell of exactly NA.</summary>
    /// <param name="report">The report path.</param>
    /// <returns>The number of rows.</returns>
    internal static int UnmeasuredRows(string report)
    {
        var rows = 0;
        foreach (var line in File.ReadLines(report))
        {
            if (line.StartsWith('|') && HasNotAvailableCell(line))
            {
                rows++;
            }
        }

        return rows;
    }

    /// <summary>Checks whether a table row holds a cell that trims to NA.</summary>
    /// <param name="line">The row.</param>
    /// <returns>Whether any cell is NA.</returns>
    private static bool HasNotAvailableCell(ReadOnlySpan<char> line)
    {
        foreach (var range in line.Split('|'))
        {
            if (line[range].Trim() is "NA")
            {
                return true;
            }
        }

        return false;
    }
}
