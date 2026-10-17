// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static System.Environment;

if (args is not [var name, var repository, var blocked, var reason, var updateOutcome, var releaseOutcome, var nugetOutcome, var pullRequest, var runUrl, var reused, var workDir, var resultDir])
{
    Console.WriteLine("::error::Expected the name, repository, blocked, reason, step outcomes, pull request, run URL, reused, work folder and result folder arguments.");
    return Program.UsageError;
}

var outcomes = new StepOutcomes(blocked, reason, updateOutcome, releaseOutcome, nugetOutcome, pullRequest, reused is Program.True);

return await Program.RunAsync(name, repository, outcomes, runUrl, workDir, resultDir).ConfigureAwait(false);

/// <summary>Records one repository's train result for the later levels and the summary.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The value a boolean argument carries when set.</summary>
    internal const string True = "true";

    /// <summary>The status of a repository that did not release.</summary>
    private const string Failed = "failed";

    /// <summary>The name of the update step in messages.</summary>
    private const string UpdateStep = "update dependencies";

    /// <summary>Writes the result file and the job summary.</summary>
    /// <param name="name">The repository name in the config.</param>
    /// <param name="repository">The repository, as owner/name.</param>
    /// <param name="outcomes">The step outputs and outcomes.</param>
    /// <param name="runUrl">The release run, or empty.</param>
    /// <param name="workDir">The folder holding release.json and updates.json.</param>
    /// <param name="resultDir">The folder for the result file.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(string name, string repository, StepOutcomes outcomes, string runUrl, string workDir, string resultDir)
    {
        // A step that did not run wrote no file, which reads as a record with no values.
        var release = await ReadAsync(Path.Combine(workDir, "release.json"), RecordJson.Default.ReleaseRecord).ConfigureAwait(false) ?? new(null, null, null, null);
        var updates = await ReadAsync(Path.Combine(workDir, "updates.json"), RecordJson.Default.UpdateLog).ConfigureAwait(false) ?? new(null, null);
        var (status, message) = Outcome(name, outcomes, release);
        var result = new TrainResult(
            name,
            repository,
            status,
            outcomes.Reused,
            message,
            release.Version,
            release.Tag,
            release.ReleaseUrl,
            NullIfEmpty(runUrl),
            NullIfEmpty(outcomes.PullRequest),
            CloneOrEmpty(release.Packages),
            CloneOrEmpty(updates.Updates),
            CloneOrEmpty(updates.Notes));

        _ = Directory.CreateDirectory(resultDir);
        await File.WriteAllBytesAsync(Path.Combine(resultDir, $"{name}.json"), JsonSerializer.SerializeToUtf8Bytes(result, RecordJson.Default.TrainResult)).ConfigureAwait(false);

        StringBuilder summary = new();
        _ = summary.AppendLine(CultureInfo.InvariantCulture, $"## {repository}: {status}").AppendLine().AppendLine(message).AppendLine();
        if (result.PullRequest is { } link)
        {
            _ = summary.AppendLine(CultureInfo.InvariantCulture, $"Pull request: {link}").AppendLine();
        }

        if (result.ReleaseUrl is { } releaseUrl)
        {
            _ = summary.AppendLine(CultureInfo.InvariantCulture, $"Release: {releaseUrl}").AppendLine();
        }

        await File.AppendAllTextAsync(GetEnvironmentVariable("GITHUB_STEP_SUMMARY")!, summary.ToString()).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Decides the status and message from the step outcomes.</summary>
    /// <param name="name">The repository name in the config.</param>
    /// <param name="outcomes">The step outputs and outcomes.</param>
    /// <param name="release">The release record.</param>
    /// <returns>The status and message.</returns>
    private static (string Status, string Message) Outcome(string name, StepOutcomes outcomes, ReleaseRecord release) =>
        (outcomes.Blocked, outcomes.Nuget, FailedStep(outcomes)) switch
        {
            (True, _, _) => ("blocked", outcomes.Reason),
            (_, "success", _) when outcomes.Reused => ("released", $"{release.Version} was already released at the head of the branch. The train reused it."),
            (_, "success", _) => ("released", $"Released {release.Version}."),
            (_, _, UpdateStep) when outcomes.PullRequest is not "" => (Failed, $"The dependency pull request did not merge. Fix {outcomes.PullRequest}, merge it, then resume the train from {name}."),
            (_, _, { } step) => (Failed, $"Failed at {step}."),
            _ => (Failed, "The job stopped before it finished."),
        };

    /// <summary>Names the first step that failed or was cancelled.</summary>
    /// <param name="outcomes">The step outcomes.</param>
    /// <returns>The step name, or null when none failed.</returns>
    private static string? FailedStep(StepOutcomes outcomes)
    {
        if (IsFailure(outcomes.Update))
        {
            return UpdateStep;
        }

        if (IsFailure(outcomes.Release))
        {
            return "release";
        }

        return IsFailure(outcomes.Nuget) ? "wait for NuGet" : null;
    }

    /// <summary>Turns an empty step output into a missing value.</summary>
    /// <param name="value">The output.</param>
    /// <returns>The output, or null when it is empty.</returns>
    private static string? NullIfEmpty(string value) => value is "" ? null : value;

    /// <summary>Copies a list from a step file, or makes an empty one.</summary>
    /// <param name="node">The list, if the file had it.</param>
    /// <returns>The copy.</returns>
    private static JsonNode CloneOrEmpty(JsonNode? node) => node?.DeepClone() ?? new JsonArray();

    /// <summary>Checks whether a step outcome is a failure.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>Whether the step failed or was cancelled.</returns>
    private static bool IsFailure(string outcome) => outcome is "failure" or "cancelled";

    /// <summary>Reads a JSON file an earlier step wrote.</summary>
    /// <typeparam name="T">The record type.</typeparam>
    /// <param name="path">The file.</param>
    /// <param name="type">The JSON contract.</param>
    /// <returns>The record, or null when the step that writes it did not run.</returns>
    private static async Task<T?> ReadAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize(await File.ReadAllBytesAsync(path).ConfigureAwait(false), type) : null;

    /// <summary>The JSON contract for the step files and the result.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReleaseRecord))]
    [JsonSerializable(typeof(UpdateLog))]
    [JsonSerializable(typeof(TrainResult))]
    internal sealed partial class RecordJson : JsonSerializerContext;

    /// <summary>The outputs and outcomes of the earlier steps.</summary>
    /// <param name="Blocked">The update step's blocked output.</param>
    /// <param name="Reason">The update step's reason output.</param>
    /// <param name="Update">The update step outcome.</param>
    /// <param name="Release">The release step outcome.</param>
    /// <param name="Nuget">The NuGet wait step outcome.</param>
    /// <param name="PullRequest">The dependency pull request, or empty.</param>
    /// <param name="Reused">Whether the train reused an earlier release.</param>
    internal sealed record StepOutcomes(string Blocked, string Reason, string Update, string Release, string Nuget, string PullRequest, bool Reused);

    /// <summary>The release.json record the release step wrote.</summary>
    /// <param name="Version">The released version.</param>
    /// <param name="Tag">The release tag.</param>
    /// <param name="ReleaseUrl">The release page.</param>
    /// <param name="Packages">The released package ids.</param>
    internal sealed record ReleaseRecord(string? Version, string? Tag, string? ReleaseUrl, JsonNode? Packages);

    /// <summary>The updates.json record the update step wrote.</summary>
    /// <param name="Updates">The version updates.</param>
    /// <param name="Notes">The versions left alone and why.</param>
    internal sealed record UpdateLog(JsonNode? Updates, JsonNode? Notes);

    /// <summary>One repository's result, which later levels and the summary read.</summary>
    /// <param name="Name">The repository name in the config.</param>
    /// <param name="Repository">The repository, as owner/name.</param>
    /// <param name="Status">released, blocked or failed.</param>
    /// <param name="Reused">Whether the train reused an earlier release.</param>
    /// <param name="Message">What happened.</param>
    /// <param name="Version">The released version.</param>
    /// <param name="Tag">The release tag.</param>
    /// <param name="ReleaseUrl">The release page.</param>
    /// <param name="RunUrl">The release run.</param>
    /// <param name="PullRequest">The dependency pull request.</param>
    /// <param name="Packages">The released package ids.</param>
    /// <param name="Updates">The version updates.</param>
    /// <param name="Notes">The versions left alone and why.</param>
    internal sealed record TrainResult(
        string Name,
        string Repository,
        string Status,
        bool Reused,
        string Message,
        string? Version,
        string? Tag,
        string? ReleaseUrl,
        string? RunUrl,
        string? PullRequest,
        JsonNode Packages,
        JsonNode Updates,
        JsonNode Notes);
}
