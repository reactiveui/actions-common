// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static System.Environment;

if (args is not [var planJson, var resultsDir])
{
    Console.WriteLine("::error::Expected the plan and results folder arguments.");
    return UsageError;
}

var plan = JsonSerializer.Deserialize(planJson, SummaryJson.Default.ListPlanEntry) ?? [];

var results = await ReadResultsAsync(resultsDir).ConfigureAwait(false);

var remaining = Remaining(plan, results);

var summary = Summary(plan, results, remaining);

await File.AppendAllTextAsync(GetEnvironmentVariable("GITHUB_STEP_SUMMARY")!, summary).ConfigureAwait(false);

await File.AppendAllTextAsync(GetEnvironmentVariable("GITHUB_OUTPUT")!, $"results={AllResults(plan, results).ToJsonString()}\nremaining={string.Join(",", remaining)}\n").ConfigureAwait(false);

Console.Write(summary);

return remaining.Count > 0 ? 1 : 0;

/// <summary>Summarises the release train from the plan and each repository's result.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    private const int UsageError = 2;

    /// <summary>The result key holding the repository name.</summary>
    private const string NameKey = "name";

    /// <summary>The result key holding the status.</summary>
    private const string StatusKey = "status";

    /// <summary>The status of a repository with no result.</summary>
    private const string NotStarted = "not started";

    /// <summary>The result key listing the dependency updates.</summary>
    private const string UpdatesKey = "updates";

    /// <summary>Reads every result file, keyed by repository name.</summary>
    /// <param name="resultsDir">The folder holding the downloaded results.</param>
    /// <returns>The results.</returns>
    internal static async Task<Dictionary<string, JsonObject>> ReadResultsAsync(string resultsDir)
    {
        Dictionary<string, JsonObject> results = [with(StringComparer.OrdinalIgnoreCase)];
        if (!Directory.Exists(resultsDir))
        {
            return results;
        }

        var files = Directory.GetFiles(resultsDir, "*.json");
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var result = JsonNode.Parse(await File.ReadAllTextAsync(file).ConfigureAwait(false))!.AsObject();
            results[result[NameKey]!.GetValue<string>()] = result;
        }

        return results;
    }

    /// <summary>Lists the planned repositories that were not released.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="results">The results.</param>
    /// <returns>The repository names, in plan order.</returns>
    internal static List<string> Remaining(List<PlanEntry> plan, Dictionary<string, JsonObject> results)
    {
        List<string> remaining = [];
        foreach (var step in plan)
        {
            if (Status(results, step.Name) is not "released")
            {
                remaining.Add(step.Name);
            }
        }

        return remaining;
    }

    /// <summary>Renders the train summary.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="results">The results.</param>
    /// <param name="remaining">The repositories not released.</param>
    /// <returns>The Markdown.</returns>
    internal static string Summary(List<PlanEntry> plan, Dictionary<string, JsonObject> results, List<string> remaining)
    {
        StringBuilder summary = new();
        _ = summary.AppendLine("## Release train").AppendLine();
        _ = summary.AppendLine("| Level | Repository | Result | Version | Dependency updates | Pull request | Release |").AppendLine("|---|---|---|---|---|---|---|");
        foreach (var step in plan)
        {
            AppendRow(summary, step, results);
        }

        foreach (var step in plan)
        {
            if (results.GetValueOrDefault(step.Name) is { } result)
            {
                AppendDetails(summary, result);
            }
        }

        if (remaining.Count > 0)
        {
            _ = summary.AppendLine().AppendLine("### Resume").AppendLine();
            _ = summary.AppendLine("Fix the failure, then run the release train again with these inputs. It picks up the versions this run already released.").AppendLine();
            _ = summary.AppendLine("```text").AppendLine(CultureInfo.InvariantCulture, $"targets: {string.Join(", ", remaining)}").AppendLine("includeDownstream: false").AppendLine("```");
        }

        return summary.ToString();
    }

    /// <summary>Lists every planned repository's result, with "not started" for those without one.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="results">The results.</param>
    /// <returns>The results in plan order.</returns>
    internal static JsonArray AllResults(List<PlanEntry> plan, Dictionary<string, JsonObject> results)
    {
        JsonArray all = [];
        foreach (var step in plan)
        {
            all.Add(results.GetValueOrDefault(step.Name)?.DeepClone() ?? new JsonObject { [NameKey] = step.Name, [StatusKey] = NotStarted });
        }

        return all;
    }

    /// <summary>Gets a repository's status.</summary>
    /// <param name="results">The results.</param>
    /// <param name="name">The repository.</param>
    /// <returns>The status, or "not started" when it has no result.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Status(Dictionary<string, JsonObject> results, string name) =>
        results.GetValueOrDefault(name)?[StatusKey]?.GetValue<string>() ?? NotStarted;

    /// <summary>Gets the result column: "already released" for a reused release, otherwise the status.</summary>
    /// <param name="result">The repository's result, if any.</param>
    /// <param name="results">The results.</param>
    /// <param name="name">The repository.</param>
    /// <returns>The result text.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Outcome(JsonObject? result, Dictionary<string, JsonObject> results, string name) =>
        result?["reused"]?.GetValue<bool>() is true ? "already released" : Status(results, name);

    /// <summary>Formats a Markdown link, or a dash when there is no URL.</summary>
    /// <param name="url">The URL.</param>
    /// <param name="text">The link text.</param>
    /// <returns>The link.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string Link(JsonNode? url, string text) => url is null ? "-" : $"[{text}]({url})";

    /// <summary>Appends one repository's row of the results table.</summary>
    /// <param name="summary">The summary.</param>
    /// <param name="step">The planned repository.</param>
    /// <param name="results">The results.</param>
    private static void AppendRow(StringBuilder summary, PlanEntry step, Dictionary<string, JsonObject> results)
    {
        var result = results.GetValueOrDefault(step.Name);
        var version = result?["version"]?.ToString() ?? "-";
        var updates = result?[UpdatesKey]?.AsArray().Count ?? 0;
        var pullRequest = Link(result?["pullRequest"], "pull request");
        var release = Link(result?["releaseUrl"], "release");
        _ = summary.AppendLine(CultureInfo.InvariantCulture, $"| {step.Level} | {step.Name} | {Outcome(result, results, step.Name)} | {version} | {updates} | {pullRequest} | {release} |");
    }

    /// <summary>Appends one repository's message, updates, notes and packages.</summary>
    /// <param name="summary">The summary.</param>
    /// <param name="result">The repository's result.</param>
    private static void AppendDetails(StringBuilder summary, JsonObject result)
    {
        var updates = result[UpdatesKey]!.AsArray();
        _ = summary.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"### {result[NameKey]}").AppendLine();
        _ = summary.AppendLine(result["message"]!.GetValue<string>()).AppendLine();
        if (result["runUrl"] is { } runUrl)
        {
            _ = summary.AppendLine(CultureInfo.InvariantCulture, $"Release run: {runUrl}").AppendLine();
        }

        if (updates.Count > 0)
        {
            _ = summary.AppendLine("| File | Package or property | From | To | Source |").AppendLine("|---|---|---|---|---|");
            foreach (var update in updates)
            {
                _ = summary.AppendLine(CultureInfo.InvariantCulture, $"| `{update!["file"]}` | `{update["item"]}` | {update["from"]} | {update["to"]} | {update["source"]} |");
            }

            _ = summary.AppendLine();
        }

        foreach (var note in result["notes"]!.AsArray())
        {
            _ = summary.AppendLine(CultureInfo.InvariantCulture, $"- {note}");
        }

        AppendPackages(summary, result["packages"]!.AsArray());
    }

    /// <summary>Appends the released package ids, when there are any.</summary>
    /// <param name="summary">The summary.</param>
    /// <param name="packages">The package ids.</param>
    private static void AppendPackages(StringBuilder summary, JsonArray packages)
    {
        if (packages.Count is 0)
        {
            return;
        }

        _ = summary.AppendLine().Append("Packages: ");
        for (var index = 0; index < packages.Count; index++)
        {
            _ = summary.Append(index is 0 ? "`" : ", `").Append(packages[index]).Append('`');
        }

        _ = summary.AppendLine();
    }

    /// <summary>Reads the plan without reflection.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(List<PlanEntry>))]
    internal sealed partial class SummaryJson : JsonSerializerContext;

    /// <summary>A planned repository and its level.</summary>
    /// <param name="Name">The config name.</param>
    /// <param name="Level">The level, from zero.</param>
    internal sealed record PlanEntry(string Name, int Level);
}
