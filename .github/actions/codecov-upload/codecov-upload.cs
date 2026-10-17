// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using System.Security.Cryptography;
using static System.Environment;

if (args is not [var coverageFolder, var cliUrl, var cliPath])
{
    Console.WriteLine("::error::Expected the coverage folder, Codecov CLI URL and Codecov CLI path arguments.");
    return Program.UsageError;
}

if (!await Program.DownloadCliAsync(new(cliUrl), cliPath).ConfigureAwait(false))
{
    return 1;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

if (!Directory.Exists(coverageFolder))
{
    Console.WriteLine($"::error::Coverage folder '{coverageFolder}' does not exist.");
    return 1;
}

var reports = Directory.GetFiles(coverageFolder, "*.cobertura.xml", Program.Recursive);

Array.Sort(reports, StringComparer.Ordinal);

List<string> arguments = ["upload-coverage"];

foreach (var report in reports)
{
    arguments.Add("--file");
    arguments.Add(report);
}

return (await Process.RunAsync(cliPath, arguments).ConfigureAwait(false)).ExitCode;

/// <summary>Downloads Codecov's standalone CLI and uploads every Cobertura coverage report under a folder.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The Unix mode of the CLI: rwxr-xr-x.</summary>
    private const UnixFileMode Executable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>The client for the CLI download.</summary>
    private static readonly HttpClient Http = new();

    /// <summary>Gets the options that walk the coverage folder and its subfolders.</summary>
    internal static EnumerationOptions Recursive { get; } = new() { RecurseSubdirectories = true, AttributesToSkip = 0 };

    /// <summary>Downloads the CLI, checks it against its published SHA-256 and saves it as an executable.</summary>
    /// <param name="url">The CLI download URL; the checksum file is the same URL with .SHA256SUM appended.</param>
    /// <param name="path">Where to save the CLI.</param>
    /// <returns>Whether the CLI is ready to run.</returns>
    internal static async Task<bool> DownloadCliAsync(Uri url, string path)
    {
        var binary = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
        var checksumFile = await Http.GetStringAsync(new Uri($"{url}.SHA256SUM")).ConfigureAwait(false);

        // The checksum file holds "<hex digest>  <file name>".
        var separator = checksumFile.IndexOf(' ', StringComparison.Ordinal);
        var expected = Convert.FromHexString(separator < 0 ? checksumFile.Trim() : checksumFile[..separator]);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(binary), expected))
        {
            Console.WriteLine($"::error::The Codecov CLI from {url} does not match its published SHA-256.");
            return false;
        }

        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, PreallocationSize = binary.Length };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = Executable;
        }

        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(binary).ConfigureAwait(false);

        Console.WriteLine($"Codecov CLI from {url} matches its published SHA-256.");
        return true;
    }
}
