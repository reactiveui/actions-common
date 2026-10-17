// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args is not [var releasePath, var timeoutMinutes])
{
    Console.WriteLine("::error::Expected the release file and timeout arguments.");
    return Program.UsageError;
}

return await Program.RunAsync(releasePath, timeoutMinutes, TimeProvider.System).ConfigureAwait(false);

/// <summary>Waits until NuGet lists every package of the release.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The exit code when NuGet does not list the packages in time.</summary>
    private const int Failure = 1;

    /// <summary>The time between two looks at NuGet.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>The NuGet client; one request may take 30 seconds.</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Polls the NuGet flat container until every package lists the version.</summary>
    /// <param name="releasePath">The release.json the release step wrote.</param>
    /// <param name="timeoutMinutes">How long NuGet may take.</param>
    /// <param name="clock">The clock for the wait.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(string releasePath, string timeoutMinutes, TimeProvider clock)
    {
        var release = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(releasePath).ConfigureAwait(false), NuGetJson.Default.ReleaseRecord)!;
        var version = release.Version.Split('+')[0].ToLowerInvariant();
        HashSet<string> pending = [with(StringComparer.OrdinalIgnoreCase), .. release.Packages];
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(int.Parse(timeoutMinutes, CultureInfo.InvariantCulture)), clock);

        // Restore reads the flat container, so a version listed there is ready for the next repository.
        while (true)
        {
            await RemoveListedAsync(pending, version).ConfigureAwait(false);
            if (pending.Count is 0)
            {
                return 0;
            }

            if (timeout.IsCancellationRequested)
            {
                Console.WriteLine($"::error::NuGet did not list {version} of {string.Join(", ", pending)} within {timeoutMinutes} minutes.");
                return Failure;
            }

            Console.WriteLine($"Waiting for NuGet to list {pending.Count} package(s).");
            await WaitAsync(clock, timeout.Token).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for the next look, cut short when the wait runs out of time so the last look happens at the deadline.</summary>
    /// <param name="clock">The clock for the delay.</param>
    /// <param name="cancellationToken">Cancelled at the deadline.</param>
    /// <returns>A task that completes when the next look is due.</returns>
    private static async Task WaitAsync(TimeProvider clock, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PollInterval, clock, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The deadline passed; the caller makes one last look and then reports the timeout.
        }
    }

    /// <summary>Removes the packages whose flat container index lists the version.</summary>
    /// <param name="pending">The package ids not yet listed.</param>
    /// <param name="version">The normalised version.</param>
    /// <returns>A task that completes after one look at every pending package.</returns>
    private static async Task RemoveListedAsync(HashSet<string> pending, string version)
    {
        string[] ids = [.. pending];
        foreach (var id in ids)
        {
            try
            {
                var index = await Http.GetFromJsonAsync(new Uri($"https://api.nuget.org/v3-flatcontainer/{id.ToLowerInvariant()}/index.json"), NuGetJson.Default.FlatContainerIndex)
                    .ConfigureAwait(false);

                // List<string>.Contains compares strings ordinally.
                if (index?.Versions?.Contains(version) is true)
                {
                    Console.WriteLine($"{id} {version} is on NuGet.");
                    _ = pending.Remove(id);
                }
            }
            catch (HttpRequestException exception)
            {
                Console.WriteLine($"{id}: {exception.Message}");
            }
        }
    }

    /// <summary>The JSON contract for release.json and the NuGet index.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReleaseRecord))]
    [JsonSerializable(typeof(FlatContainerIndex))]
    internal sealed partial class NuGetJson : JsonSerializerContext;

    /// <summary>The release.json record the release step wrote.</summary>
    /// <param name="Version">The released version.</param>
    /// <param name="Packages">The released package ids.</param>
    internal sealed record ReleaseRecord(string Version, List<string> Packages);

    /// <summary>A NuGet flat container package index.</summary>
    /// <param name="Versions">The listed versions.</param>
    internal sealed record FlatContainerIndex(List<string>? Versions);
}
