// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static System.Environment;

if (args is not [var baseFolder, var headFolder])
{
    Console.WriteLine("::error::Expected the base and head results folder arguments.");
    return Program.UsageError;
}

// A benchmark is faster or slower only when the 99.9% confidence intervals of its two means do not overlap.
var baseResults = Program.Load(baseFolder);

var headResults = Program.Load(headFolder);

File.AppendAllText(GetEnvironmentVariable("GITHUB_STEP_SUMMARY") ?? string.Empty, Program.Table(baseResults, headResults));

return 0;

/// <summary>Compares two BenchmarkDotNet runs and writes the result table to the run summary.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The cell for a missing value.</summary>
    private const string Missing = "-";

    /// <summary>Loads every full JSON report under a folder, keyed by benchmark name and job characteristics.</summary>
    /// <param name="folder">The results folder.</param>
    /// <returns>The measurements.</returns>
    internal static Dictionary<string, Measurement> Load(string folder)
    {
        var results = new Dictionary<string, Measurement>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(folder, "*full.json", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            using var stream = File.OpenRead(path);
            var report = JsonSerializer.Deserialize(stream, ReportJsonContext.Default.Report);
            foreach (var benchmark in report?.Benchmarks ?? [])
            {
                if (benchmark.Statistics is not { ConfidenceInterval: { } interval } statistics)
                {
                    continue;
                }

                results[Key(benchmark)] = new(statistics.Mean, interval.Lower, interval.Upper);
            }
        }

        return results;
    }

    /// <summary>Builds the markdown result table.</summary>
    /// <param name="baseResults">The baseline measurements.</param>
    /// <param name="headResults">The head measurements.</param>
    /// <returns>The table.</returns>
    internal static string Table(Dictionary<string, Measurement> baseResults, Dictionary<string, Measurement> headResults)
    {
        var keys = new HashSet<string>(baseResults.Keys, StringComparer.Ordinal);
        keys.UnionWith(headResults.Keys);
        string[] ordered = [.. keys];
        Array.Sort(ordered, StringComparer.Ordinal);

        var table = new StringBuilder();
        _ = table.Append("# A/B benchmark results").Append(NewLine).Append(NewLine);
        _ = table.Append("| Benchmark | Base | Head | Head/Base | Result |").Append(NewLine);
        _ = table.Append("|---|---:|---:|---:|---|").Append(NewLine);
        foreach (var key in ordered)
        {
            AppendRow(table, key, baseResults, headResults);
        }

        return table.ToString();
    }

    /// <summary>Appends the row for one benchmark.</summary>
    /// <param name="table">The table.</param>
    /// <param name="key">The benchmark key.</param>
    /// <param name="baseResults">The baseline measurements.</param>
    /// <param name="headResults">The head measurements.</param>
    private static void AppendRow(StringBuilder table, string key, Dictionary<string, Measurement> baseResults, Dictionary<string, Measurement> headResults)
    {
        var hasBase = baseResults.TryGetValue(key, out var before);
        var hasHead = headResults.TryGetValue(key, out var after);
        if (hasBase && hasHead)
        {
            _ = table.Append(CultureInfo.InvariantCulture, $"| {key} | {Format(before)} | {Format(after)} | {after.Mean / before.Mean:0.000} | {Verdict(before, after)} |").Append(NewLine);
            return;
        }

        var baseCell = hasBase ? Format(before) : Missing;
        var headCell = hasHead ? Format(after) : Missing;
        _ = table.Append(CultureInfo.InvariantCulture, $"| {key} | {baseCell} | {headCell} | {Missing} | {(hasBase ? "base only" : "head only")} |").Append(NewLine);
    }

    /// <summary>Judges the head against the base by whether their confidence intervals overlap.</summary>
    /// <param name="before">The baseline.</param>
    /// <param name="after">The head.</param>
    /// <returns>slower, faster or unresolved.</returns>
    private static string Verdict(Measurement before, Measurement after)
    {
        if (after.Lower > before.Upper)
        {
            return "slower";
        }

        return after.Upper < before.Lower ? "faster" : "unresolved";
    }

    /// <summary>Keys a benchmark by its name and job characteristics, because BenchmarkDotNet job ids differ between runs.</summary>
    /// <param name="benchmark">The benchmark.</param>
    /// <returns>The key.</returns>
    private static string Key(Benchmark benchmark)
    {
        var characteristics = Characteristics().Match(benchmark.DisplayInfo ?? string.Empty);
        return $"{benchmark.FullName} ({characteristics.Groups[1].Value})";
    }

    /// <summary>Formats a mean and its confidence margin in the largest fitting time unit.</summary>
    /// <param name="measurement">The measurement in nanoseconds.</param>
    /// <returns>The text.</returns>
    private static string Format(Measurement measurement)
    {
        const double Second = 1_000_000_000;
        const double Millisecond = 1_000_000;
        const double Microsecond = 1_000;
        const double Nanosecond = 1;
        var (scale, unit) = measurement.Mean switch
        {
            >= Second => (Second, "s"),
            >= Millisecond => (Millisecond, "ms"),
            >= Microsecond => (Microsecond, "us"),
            _ => (Nanosecond, "ns"),
        };

        var margin = (measurement.Upper - measurement.Mean) / scale;
        return string.Create(CultureInfo.InvariantCulture, $"{measurement.Mean / scale:0.00} ± {margin:0.00} {unit}");
    }

    /// <summary>Matches the trailing parenthesised job characteristics of a display name.</summary>
    /// <returns>The regex.</returns>
    [GeneratedRegex(@"\(([^()]*)\)\s*$")]
    private static partial Regex Characteristics();

    /// <summary>A benchmark mean and its confidence interval, in nanoseconds.</summary>
    /// <param name="Mean">The mean.</param>
    /// <param name="Lower">The lower confidence bound.</param>
    /// <param name="Upper">The upper confidence bound.</param>
    internal readonly record struct Measurement(double Mean, double Lower, double Upper);

    /// <summary>The source-generated JSON metadata for the report.</summary>
    [JsonSerializable(typeof(Report))]
    internal sealed partial class ReportJsonContext : JsonSerializerContext;

    /// <summary>A BenchmarkDotNet full JSON report.</summary>
    /// <param name="Benchmarks">The benchmarks.</param>
    internal sealed record Report(Benchmark[]? Benchmarks);

    /// <summary>One benchmark in a full JSON report.</summary>
    /// <param name="FullName">The benchmark name.</param>
    /// <param name="DisplayInfo">The display name, ending with the job characteristics.</param>
    /// <param name="Statistics">The statistics, or null when the benchmark did not run.</param>
    internal sealed record Benchmark(string? FullName, string? DisplayInfo, Statistics? Statistics);

    /// <summary>The statistics of a benchmark.</summary>
    /// <param name="Mean">The mean.</param>
    /// <param name="ConfidenceInterval">The confidence interval of the mean.</param>
    internal sealed record Statistics(double Mean, ConfidenceInterval? ConfidenceInterval);

    /// <summary>A confidence interval.</summary>
    /// <param name="Lower">The lower bound.</param>
    /// <param name="Upper">The upper bound.</param>
    internal sealed record ConfidenceInterval(double Lower, double Upper);
}
