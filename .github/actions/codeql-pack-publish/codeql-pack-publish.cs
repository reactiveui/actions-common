// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

if (args is not [var packDir])
{
    Console.WriteLine("::error::Expected the pack directory argument.");
    return Program.UsageError;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

var publish = Process.RunAndCaptureText("gh", ["codeql", "pack", "publish", packDir]);

Console.Write(publish.StandardOutput);

Console.Error.Write(publish.StandardError);

// A re-run of an already-published commit carries the same version.
if (publish.ExitStatus.ExitCode != 0
    && (publish.StandardOutput.Contains(Program.AlreadyExists, StringComparison.Ordinal) || publish.StandardError.Contains(Program.AlreadyExists, StringComparison.Ordinal)))
{
    Console.WriteLine("::notice::This pack version is already published.");
    return 0;
}

return publish.ExitStatus.ExitCode;

/// <summary>Publishes a CodeQL pack, treating an already-published version as success.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The text the CLI prints when the pack version is already published.</summary>
    internal const string AlreadyExists = "already exists";
}
