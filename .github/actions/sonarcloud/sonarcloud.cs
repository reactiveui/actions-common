// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Diagnostics;
using static System.Environment;

return Program.Run(args);

/// <summary>Checks the SonarCloud token and starts a SonarScanner for .NET analysis with the action's settings.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    private const int UsageError = 2;

    /// <summary>The exit code when SONAR_TOKEN is not set.</summary>
    private const int MissingToken = 1;

    /// <summary>Runs the validate or begin command.</summary>
    /// <param name="args">validate, or begin followed by the eleven begin arguments.</param>
    /// <returns>The process exit code.</returns>
    internal static int Run(string[] args)
    {
        if (args is ["validate"])
        {
            return Validate();
        }

        if (args is not
            [
                "begin", var workingDirectory, var projectKey, var organization, var hostUrl, var version, var coverageGlob,
                var extraBeginArgs, var exclusions, var coverageExclusions, var cpdExclusions, var testExclusions,
            ])
        {
            Console.WriteLine("::error::Expected validate, or begin and the eleven SonarScanner begin arguments.");
            return UsageError;
        }

        Directory.SetCurrentDirectory(Path.Combine(GetEnvironmentVariable("GITHUB_WORKSPACE")!, workingDirectory));

        return Process.Run(
            "dotnet",
            MergeProperties([
                "sonarscanner", "begin",
                $"/k:{projectKey}",
                .. Switch("/o:", organization),
                $"/d:sonar.host.url={hostUrl}",
                $"/d:sonar.token={GetEnvironmentVariable("SONAR_TOKEN")}",
                "/d:sonar.scanner.skipJreProvisioning=true",
                $"/d:sonar.cs.cobertura.reportsPaths={coverageGlob}",
                "/d:sonar.issue.ignore.multicriteria=s8969",
                "/d:sonar.issue.ignore.multicriteria.s8969.ruleKey=csharpsquid:S8969",
                "/d:sonar.issue.ignore.multicriteria.s8969.resourceKey=**/*.cs",
                .. Switch("/d:sonar.exclusions=", exclusions),
                .. Switch("/d:sonar.coverage.exclusions=", coverageExclusions),
                .. Switch("/d:sonar.cpd.exclusions=", cpdExclusions),
                .. Switch("/d:sonar.test.exclusions=", testExclusions),
                .. Switch("/v:", version),
                .. extraBeginArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ])).ExitCode;
    }

    /// <summary>Applies extra property values over defaults without repeating scanner switches.</summary>
    /// <param name="arguments">The scanner arguments in precedence order.</param>
    /// <returns>The arguments with each property supplied once.</returns>
    private static string[] MergeProperties(string[] arguments)
    {
        const string propertyPrefix = "/d:";
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var otherArguments = new List<string>(arguments.Length);
        foreach (var argument in arguments)
        {
            if (argument.StartsWith(propertyPrefix, StringComparison.OrdinalIgnoreCase) && argument.IndexOf('=') is var separator && separator > propertyPrefix.Length)
            {
                properties[argument[..separator]] = argument;
            }
            else
            {
                otherArguments.Add(argument);
            }
        }

        return [.. otherArguments, .. properties.Values];
    }

    /// <summary>Checks that the caller mapped the SONAR_TOKEN secret into the environment.</summary>
    /// <returns>Zero when the token is set.</returns>
    private static int Validate()
    {
        if (GetEnvironmentVariable("SONAR_TOKEN") is { Length: > 0 })
        {
            return 0;
        }

        Console.WriteLine("::error::SONAR_TOKEN is not set in the job/step env. Map the SONAR_TOKEN secret into env on the calling job/step (see this action's description).");
        return MissingToken;
    }

    /// <summary>Builds a scanner switch, leaving it out when the value is empty.</summary>
    /// <param name="name">The switch prefix.</param>
    /// <param name="value">The switch value.</param>
    /// <returns>The switch, or nothing.</returns>
    private static string[] Switch(string name, string value) => value is "" ? [] : [name + value];
}
