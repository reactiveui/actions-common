// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

return Program.Run(args);

/// <summary>Reports whether a push or pull request changes files under .github, outside it, or both.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    private const int UsageError = 2;

    /// <summary>The folder whose files count as workflow changes.</summary>
    private const string WorkflowFolder = ".github/";

    /// <summary>Compares the changed files and writes the code and workflows outputs.</summary>
    /// <param name="args">The event name and the push's before commit.</param>
    /// <returns>The process exit code.</returns>
    internal static int Run(string[] args)
    {
        if (args is not [var eventName, var before])
        {
            Console.WriteLine("::error::Expected the event name and before-commit arguments.");
            return UsageError;
        }

        Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

        // A pull request checks out its merge commit, whose first parent is the base branch. A new branch pushes an
        // all-zero before commit.
        var baseline = eventName switch
        {
            "pull_request" => "HEAD^1",
            "push" when before.Trim('0') is not "" && Process.Run("git", ["fetch", "--no-tags", "--depth=1", "origin", before]) is { ExitCode: 0 } => before,
            _ => null,
        };

        var files = baseline is null ? [] : ChangedFiles(baseline);

        // With nothing to compare, both areas count as changed so every job runs.
        var workflows = files is [];
        var code = files is [];
        foreach (var file in files)
        {
            var isWorkflow = file.StartsWith(WorkflowFolder, StringComparison.Ordinal);
            workflows |= isWorkflow;
            code |= !isWorkflow;
        }

        Console.WriteLine($"Changed files: {files.Length}. Code: {code}. Workflows: {workflows}.");
        File.AppendAllText(GetEnvironmentVariable("GITHUB_OUTPUT")!, $"code={(code ? "true" : "false")}\nworkflows={(workflows ? "true" : "false")}\n");
        return 0;
    }

    /// <summary>Lists the files changed between a baseline and HEAD.</summary>
    /// <param name="baseline">The commit to compare against.</param>
    /// <returns>The changed paths, or none when git cannot compare them.</returns>
    private static string[] ChangedFiles(string baseline) =>
        Process.RunAndCaptureText("git", ["diff", "--name-only", baseline, "HEAD"]) switch
        {
            { ExitStatus.ExitCode: 0, StandardOutput: var output } => output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => [],
        };
}
