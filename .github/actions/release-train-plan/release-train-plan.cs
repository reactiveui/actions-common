#!/usr/bin/env dotnet run
// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:property TargetFramework=net11.0
#:package Refit@17.0.0

using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Actions.Common.GitHub;
using static System.Environment;

if (args is not [var configPath, var targets, var includeDownstream, var exclude, var releaseLevel,
    var channel, var onFailure, var dryRun])
{
    Console.WriteLine(
        "::error::Expected config, targets, downstream, exclude, bump, channel, on failure, and dry run.");
    return Program.UsageError;
}

var request = new PlanRequest(
    configPath,
    targets,
    includeDownstream
    is Program.True,
    exclude,
    releaseLevel,
    channel,
    onFailure,
    dryRun
    is Program.True);

return await Program.RunAsync(request, TimeProvider.System).ConfigureAwait(false);

/// <summary>Plans the release train: the order and level of every repository to release.</summary>
internal static partial class Program
{
    /// <summary>The exit code for missing arguments.</summary>
    internal const int UsageError = 2;

    /// <summary>The value a boolean argument carries when set.</summary>
    internal const string True = "true";

    /// <summary>The most levels release-train.yml runs.</summary>
    private const int MaxLevels = 10;

    /// <summary>The exit code for a plan that cannot run.</summary>
    private const int Failure = 1;

    /// <summary>The config key that overrides a repository's owner/name.</summary>
    private const string RepositoryKey = "repository";

    /// <summary>The config key that lists release workflow inputs.</summary>
    private const string ReleaseInputsKey = "releaseInputs";

    /// <summary>The release input used when the config sets none.</summary>
    private const string DefaultInputKey = "bump";

    /// <summary>The placeholder a release input template uses for the channel.</summary>
    private const string ChannelPlaceholder = "{channel}";

    /// <summary>The placeholder a release input template uses for the release level.</summary>
    private const string BumpPlaceholder = "{bump}";

    /// <summary>The default minutes to wait for checks and NuGet indexing.</summary>
    private const int DefaultWaitMinutes = 120;

    /// <summary>The default minutes to wait for a release run.</summary>
    private const int DefaultReleaseMinutes = 180;

    /// <summary>The characters that separate repository and group names.</summary>
    private static readonly char[] Separators = [',', ' ', '\n', '\r', '\t'];

    /// <summary>Validates the config and writes the plan to the step outputs and summary.</summary>
    /// <param name="request">The workflow inputs.</param>
    /// <param name="clock">The clock for the app JWT.</param>
    /// <returns>The exit code.</returns>
    internal static async Task<int> RunAsync(PlanRequest request, TimeProvider clock)
    {
        if (!request.DryRun && !HasAppSecrets())
        {
            Console.WriteLine(
                "::error::Set RELEASE_TRAIN_APP_CLIENT_ID and RELEASE_TRAIN_APP_PRIVATE_KEY. See README.md.");
            return Failure;
        }

        Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());
        if (Catalog.Read(
            request.ConfigPath,
            await File.ReadAllTextAsync(request.ConfigPath).ConfigureAwait(false)) is not { } catalog

            || Select(request, catalog) is not { } selected)
        {
            return Failure;
        }

        var levels = Levels(catalog, selected);
        var planned = Planned(catalog, selected, levels);
        var depth = levels[planned[^1]] + 1;
        if (depth > MaxLevels)
        {
            Console.WriteLine($"::error::The plan needs {depth} levels. release-train.yml runs at most {MaxLevels}.");
            return Failure;
        }

        if (!request.DryRun
            && await MissingInstallationsAsync(planned, catalog, clock).ConfigureAwait(false) is { Count: > 0 } missing)
        {
            Console.WriteLine(
                $"::error::The release train GitHub App is not installed on {string.Join(", ", missing)}.");
            return Failure;
        }

        if (LevelEntries(planned, levels, catalog, request) is not { } levelEntries)
        {
            return Failure;
        }

        await WriteOutputsAsync(catalog, levelEntries, depth, planned, levels).ConfigureAwait(false);
        var summary = Summary(request, catalog, levelEntries, selected);
        await File.AppendAllTextAsync(GetEnvironmentVariable("GITHUB_STEP_SUMMARY")!, summary).ConfigureAwait(false);
        Console.Write(summary);
        return 0;
    }

    /// <summary>Checks that both GitHub App secrets are set.</summary>
    /// <returns>Whether the client ID and private key are both set.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasAppSecrets() => !string.IsNullOrEmpty(GetEnvironmentVariable("APP_CLIENT_ID"))
        && !string.IsNullOrEmpty(GetEnvironmentVariable("APP_PRIVATE_KEY"));

    /// <summary>Selects the targets, their downstream repositories when asked, less the exclusions.</summary>
    /// <param name="request">The workflow inputs.</param>
    /// <param name="catalog">The config.</param>
    /// <returns>The selected repositories, or null after reporting an error.</returns>
    private static HashSet<string>? Select(PlanRequest request, Catalog catalog)
    {
        if (Expand(request.Targets, catalog) is not { Count: > 0 } targeted
            || Expand(request.Exclude, catalog) is not { } excluded)
        {
            Console.WriteLine("::error::Choose at least one repository or group to release.");
            return null;
        }

        HashSet<string> selected = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var name in targeted)
        {
            _ = selected.Add(catalog.Canonical(name));
        }

        if (request.IncludeDownstream)
        {
            foreach (var name in catalog.Order)
            {
                if (catalog.Ancestors[name].Overlaps(selected))
                {
                    _ = selected.Add(name);
                }
            }
        }

        foreach (var name in excluded)
        {
            _ = selected.Remove(catalog.Canonical(name));
        }

        if (selected.Count is 0)
        {
            Console.WriteLine("::error::The exclude list removes every repository.");
            return null;
        }

        return selected;
    }

    /// <summary>Expands a list of repositories, groups and "all" into repository names.</summary>
    /// <param name="list">The names, separated by commas or white space.</param>
    /// <param name="catalog">The config.</param>
    /// <returns>The names, or null after reporting an unknown name.</returns>
    private static List<string>? Expand(string list, Catalog catalog)
    {
        List<string> names = [];
        foreach (var token in list.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                names.AddRange(catalog.Order);
            }
            else if (catalog.Group(token) is { } members)
            {
                foreach (var member in members)
                {
                    names.Add(member!.GetValue<string>());
                }
            }
            else if (catalog.Find(token) is { } name)
            {
                names.Add(name);
            }
            else
            {
                Console.WriteLine($"::error::{token} is not a repository or group in the config.");
                return null;
            }
        }

        foreach (var name in names)
        {
            if (catalog.Repositories.ContainsKey(name))
            {
                continue;
            }

            Console.WriteLine($"::error::A group lists {name}, which is not in the config.");
            return null;
        }

        return names;
    }

    /// <summary>Gets the level of every selected repository.</summary>
    /// <param name="catalog">The config.</param>
    /// <param name="selected">The repositories in the plan.</param>
    /// <returns>The levels, from zero.</returns>
    private static Dictionary<string, int> Levels(Catalog catalog, HashSet<string> selected)
    {
        Dictionary<string, int> levels = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (var name in catalog.Order)
        {
            if (!selected.Contains(name))
            {
                continue;
            }

            _ = LevelOf(name, levels, catalog.Ancestors, selected);
        }

        return levels;
    }

    /// <summary>Gets a repository's level: one past the deepest selected repository it depends on.</summary>
    /// <param name="name">The repository.</param>
    /// <param name="levels">The levels found so far.</param>
    /// <param name="ancestors">Every repository each repository depends on, directly or not.</param>
    /// <param name="selected">The repositories in the plan.</param>
    /// <returns>The level, from zero.</returns>
    private static int LevelOf(
        string name,
        Dictionary<string, int> levels,
        Dictionary<string, HashSet<string>> ancestors,
        HashSet<string> selected)
    {
        if (levels.TryGetValue(name, out var level))
        {
            return level;
        }

        level = 0;
        foreach (var ancestor in ancestors[name])
        {
            if (selected.Contains(ancestor))
            {
                level = Math.Max(level, LevelOf(ancestor, levels, ancestors, selected) + 1);
            }
        }

        levels[name] = level;
        return level;
    }

    /// <summary>Orders the selected repositories by level, keeping config order within a level.</summary>
    /// <param name="catalog">The config.</param>
    /// <param name="selected">The repositories in the plan.</param>
    /// <param name="levels">The level of each repository.</param>
    /// <returns>The planned repositories.</returns>
    private static List<string> Planned(Catalog catalog, HashSet<string> selected, Dictionary<string, int> levels)
    {
        List<string> planned = [];
        for (var level = 0; planned.Count < selected.Count; level++)
        {
            foreach (var name in catalog.Order)
            {
                if (selected.Contains(name) && levels[name] == level)
                {
                    planned.Add(name);
                }
            }
        }

        return planned;
    }

    /// <summary>Lists the planned repositories the GitHub App is not installed on.</summary>
    /// <param name="planned">The planned repositories.</param>
    /// <param name="catalog">The config.</param>
    /// <param name="clock">The clock for the app JWT.</param>
    /// <returns>The owner/name of each repository without the app.</returns>
    private static async Task<List<string>> MissingInstallationsAsync(
        List<string> planned,
        Catalog catalog,
        TimeProvider clock)
    {
        const int TimeoutMinutes = 5;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(TimeoutMinutes), clock);
        using var http = GitHubClients.Create(Jwt(clock), clock);
        var api = GitHubClients.Api(http);
        List<string> missing = [];
        foreach (var name in planned)
        {
            var repository = catalog.Setting(name, RepositoryKey, $"{catalog.Owner}/{name}");
            using var response = await api.InstallationAsync(repository, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                missing.Add(repository);
                continue;
            }

            _ = await response.EnsureSuccessStatusCodeAsync().ConfigureAwait(false);
        }

        return missing;
    }

    /// <summary>Signs a nine-minute app JWT, backdated a minute for clock drift.</summary>
    /// <param name="clock">The clock for the issue and expiry times.</param>
    /// <returns>The JWT.</returns>
    private static string Jwt(TimeProvider clock)
    {
        const int Backdate = 60;
        const int Lifetime = 540;
        using var rsa = RSA.Create();
        rsa.ImportFromPem(GetEnvironmentVariable("APP_PRIVATE_KEY"));
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var claims = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"iat\":{now
            - Backdate},\"exp\":{now
            + Lifetime},\"iss\":\"{GetEnvironmentVariable("APP_CLIENT_ID")}\"}}");
        var unsigned = $"{Base64Url.EncodeToString(
            "{\"alg\":\"RS256\",\"typ\":\"JWT\"}"u8)}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
                claims))}";
        return $"{unsigned}.{Base64Url.EncodeToString(rsa.SignData(
            Encoding.ASCII.GetBytes(unsigned),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1))}";
    }

    /// <summary>Builds the entries of every level.</summary>
    /// <param name="planned">The planned repositories, in order.</param>
    /// <param name="levels">The level of each repository.</param>
    /// <param name="catalog">The config.</param>
    /// <param name="request">The workflow inputs.</param>
    /// <returns>The entries for each level, or null after reporting a missing channel input.</returns>
    private static List<LevelEntry>[]? LevelEntries(
        List<string> planned,
        Dictionary<string, int> levels,
        Catalog catalog,
        PlanRequest request)
    {
        var levelEntries = new List<LevelEntry>[MaxLevels];
        for (var level = 0; level < MaxLevels; level++)
        {
            levelEntries[level] = [];
        }

        foreach (var name in planned)
        {
            if (Entry(name, catalog, planned, request) is not { } entry)
            {
                Console.WriteLine($"::error::{name} has no release input for the {request.Channel} channel.");
                return null;
            }

            levelEntries[levels[name]].Add(entry);
        }

        return levelEntries;
    }

    /// <summary>Builds the level entry for one repository.</summary>
    /// <param name="name">The repository.</param>
    /// <param name="catalog">The config.</param>
    /// <param name="planned">The planned repositories, in order.</param>
    /// <param name="request">The workflow inputs.</param>
    /// <returns>The entry, or null when the channel has no release input.</returns>
    private static LevelEntry? Entry(string name, Catalog catalog, List<string> planned, PlanRequest request)
    {
        var repository = catalog.Repositories[name];
        if (Inputs(
            repository[ReleaseInputsKey]
            as JsonObject
            ?? catalog.Defaults[ReleaseInputsKey]
            as JsonObject,
            request) is not { } inputs)
        {
            return null;
        }

        List<string> waitsFor = [];
        foreach (var other in planned)
        {
            if (catalog.Ancestors[name].Contains(other))
            {
                waitsFor.Add(other);
            }
        }

        return new(
            name,
            catalog.Setting(name, RepositoryKey, $"{catalog.Owner}/{name}"),
            catalog.Setting(name, "branch", "main"),
            catalog.Setting(name, "releaseWorkflow", "release.yml"),
            repository["adminMerge"]?.GetValue<bool>() ?? false,
            inputs,
            waitsFor,
            (repository["ignorePackages"] as JsonArray)?.DeepClone() ?? new JsonArray());
    }

    /// <summary>Fills the release input templates with the release level and channel.</summary>
    /// <param name="templates">The templates, or null for the default bump input.</param>
    /// <param name="request">The workflow inputs.</param>
    /// <returns>The inputs, or null when a channel is set but no template takes it.</returns>
    private static Dictionary<string, string>? Inputs(JsonObject? templates, PlanRequest request)
    {
        templates ??= new JsonObject { [DefaultInputKey] = BumpPlaceholder };
        Dictionary<string, string> inputs = [];
        var usesChannel = false;
        foreach (var (key, template) in templates)
        {
            var text = template!.GetValue<string>();
            usesChannel |= text.Contains(ChannelPlaceholder, StringComparison.Ordinal);
            inputs[key] = text.Replace(BumpPlaceholder, request.Bump, StringComparison.Ordinal).Replace(
                ChannelPlaceholder,
                request.Channel,
                StringComparison.Ordinal);
        }

        return request.Channel is not "none" && !usesChannel ? null : inputs;
    }

    /// <summary>Writes the level, depth, settings and plan step outputs.</summary>
    /// <param name="catalog">The config.</param>
    /// <param name="levelEntries">The entries for each level.</param>
    /// <param name="depth">The number of levels used.</param>
    /// <param name="planned">The planned repositories, in order.</param>
    /// <param name="levels">The level of each repository.</param>
    /// <returns>A task that completes when the outputs are written.</returns>
    private static Task WriteOutputsAsync(
        Catalog catalog,
        List<LevelEntry>[] levelEntries,
        int depth,
        List<string> planned,
        Dictionary<string, int> levels)
    {
        List<PlanEntry> plan = [];
        foreach (var name in planned)
        {
            plan.Add(new(name, levels[name]));
        }

        var output = new StringBuilder();
        for (var level = 0; level < levelEntries.Length; level++)
        {
            _ = output.Append(CultureInfo.InvariantCulture, $"level{level}=").Append(JsonSerializer.Serialize(
                levelEntries[level],
                PlanJson.Default.ListLevelEntry)).Append('\n');
        }

        _ = output.Append(CultureInfo.InvariantCulture, $"depth={depth}\n");
        _ = output.Append("settings=").Append(JsonSerializer.Serialize(
            Settings(catalog),
            PlanJson.Default.TrainSettings)).Append('\n');
        _ = output.Append("plan=").Append(JsonSerializer.Serialize(plan, PlanJson.Default.ListPlanEntry)).Append('\n');
        return File.AppendAllTextAsync(GetEnvironmentVariable("GITHUB_OUTPUT")!, output.ToString());
    }

    /// <summary>Builds the settings every level reads.</summary>
    /// <param name="catalog">The config.</param>
    /// <returns>The settings.</returns>
    private static TrainSettings Settings(Catalog catalog)
    {
        var timeouts = catalog.Config["timeouts"] as JsonObject ?? [];
        List<CatalogEntry> entries = [];
        foreach (var name in catalog.Order)
        {
            entries.Add(new(name, catalog.Setting(name, RepositoryKey, $"{catalog.Owner}/{name}")));
        }

        return new(
            entries,
            catalog.Config["updateBranch"]?.GetValue<string>() ?? "release-train/dependencies",
            Minutes(timeouts, "checksMinutes", DefaultWaitMinutes),
            Minutes(timeouts, "releaseMinutes", DefaultReleaseMinutes),
            Minutes(timeouts, "nugetMinutes", DefaultWaitMinutes));
    }

    /// <summary>Reads a timeout from the config.</summary>
    /// <param name="timeouts">The timeouts section.</param>
    /// <param name="key">The timeout name.</param>
    /// <param name="fallback">The minutes when the config sets none.</param>
    /// <returns>The minutes.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Minutes(
        JsonObject timeouts,
        string key,
        int fallback) => timeouts[key]?.GetValue<int>() ?? fallback;

    /// <summary>Renders the plan as a Markdown table.</summary>
    /// <param name="request">The workflow inputs.</param>
    /// <param name="catalog">The config.</param>
    /// <param name="levelEntries">The entries for each level.</param>
    /// <param name="selected">The repositories in the plan.</param>
    /// <returns>The Markdown.</returns>
    private static string Summary(
        PlanRequest request,
        Catalog catalog,
        List<LevelEntry>[] levelEntries,
        HashSet<string> selected)
    {
        StringBuilder summary = new();
        _ = summary.AppendLine("## Release train plan").AppendLine();
        _ = summary.AppendLine(
            CultureInfo.InvariantCulture,
            $"Bump `{request.Bump}`, channel `{request.Channel}`, on failure `{request.OnFailure}`.").AppendLine();
        _ = summary.AppendLine("| Level | Repository | After | Release inputs |").AppendLine("|---|---|---|---|");
        List<string> waits = [];
        List<string> inputs = [];
        for (var level = 0; level < levelEntries.Length; level++)
        {
            foreach (var entry in levelEntries[level])
            {
                waits.Clear();
                foreach (var dependency in catalog.DirectDependencies[entry.Name])
                {
                    if (selected.Contains(dependency))
                    {
                        waits.Add(dependency);
                    }
                }

                inputs.Clear();
                foreach (var (key, value) in entry.Inputs)
                {
                    inputs.Add($"{key}={value}");
                }

                var dependencies = waits.Count is 0 ? "-" : string.Join(", ", waits);
                _ = summary.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"| {level} | {entry.Repository} | {dependencies} | {string.Join(", ", inputs)} |");
            }
        }

        return summary.ToString();
    }
}
