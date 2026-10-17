// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

if (args is not [var projectFile, var executableName, var publishFolder])
{
    Console.WriteLine("::error::Expected the project file, executable name and publish folder arguments.");
    return Program.UsageError;
}

var workspace = GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory();

var name = executableName is "" ? Path.GetFileNameWithoutExtension(projectFile) : executableName;

var binary = Path.Combine(workspace, publishFolder, OperatingSystem.IsWindows() ? $"{name}.exe" : name);

Directory.SetCurrentDirectory(workspace);

Console.WriteLine($"Running native AOT smoke binary: {binary}");

return Process.Run(binary).ExitCode;

/// <summary>Runs a published native AOT smoke-test executable and returns its exit code.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;
}
