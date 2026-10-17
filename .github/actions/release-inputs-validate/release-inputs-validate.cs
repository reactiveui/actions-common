// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

if (args is not [var bump, var preRelease, var versionOverride])
{
    Console.WriteLine("::error::Expected the bump, pre-release and version override arguments.");
    return Program.UsageError;
}

var error = Program.Validate(bump, preRelease, versionOverride);

if (error is not null)
{
    Console.WriteLine($"::error::{error}");
    return 1;
}

return 0;

/// <summary>Rejects release inputs that cannot be combined.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>Finds the first rule the release inputs break.</summary>
    /// <param name="bump">The release level, or empty.</param>
    /// <param name="preRelease">The pre-release channel, or empty.</param>
    /// <param name="versionOverride">The exact version, or empty.</param>
    /// <returns>The error message, or null when the inputs are valid.</returns>
    internal static string? Validate(string bump, string preRelease, string versionOverride) => (bump, preRelease, versionOverride) switch
    {
        (not "", _, not "") => "'bump' and 'versionOverride' are mutually exclusive — set exactly one.",
        (_, not "", not "") => "'preRelease' cannot be combined with 'versionOverride' (the override is stamped verbatim — put any pre-release suffix in the override itself).",
        ("", not "", _) => "'preRelease' requires 'bump' (major|minor|patch) so the channel has a core version to attach to.",
        _ => null,
    };
}
