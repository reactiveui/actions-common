// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

if (args is not [var timestampUrl, var packagesGlob])
{
    Console.WriteLine("::error::Expected the timestamp URL and packages glob arguments.");
    return 2;
}

if ((GetEnvironmentVariable("CERTUM_USER_ID"), GetEnvironmentVariable("CERTUM_OTP_URI")) is not ({ Length: > 0 }, { Length: > 0 }))
{
    Console.WriteLine("::error::Map the CERTUM_USER_ID and CERTUM_OTP_URI secrets into the job env.");
    return 1;
}

Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

var folder = Path.GetDirectoryName(packagesGlob) is { Length: > 0 } directory ? directory : ".";
string[] packages = Directory.Exists(folder) ? [.. Directory.GetFiles(folder, Path.GetFileName(packagesGlob)).Order(StringComparer.Ordinal)] : [];
if (packages is [])
{
    Console.WriteLine($"::error::no packages matched: {packagesGlob}");
    return 1;
}

// One run signs every package, so SafeSign logs in to SimplySign once. It reads the credentials from the environment.
Console.WriteLine("::group::sign packages");
var sign = Process.Run(
    "safesign",
    ["sign", .. packages, "--provider", "certum", "--hash", "SHA256", "--timestamp-url", timestampUrl]);
Console.WriteLine("::endgroup::");
if (sign is { ExitCode: not 0 })
{
    return sign.ExitCode;
}

foreach (var package in packages)
{
    Console.WriteLine($"::group::verify {package}");
    var verify = Process.Run("dotnet", ["nuget", "verify", package, "--all", "--verbosity", "detailed"]);
    Console.WriteLine("::endgroup::");
    if (verify is { ExitCode: not 0 })
    {
        return verify.ExitCode;
    }
}

return 0;
