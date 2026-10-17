// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using static System.Environment;

if (args is not [var configPath, var targets, var includeDownstream, var exclude, var releaseLevel, var channel, var onFailure, var dryRun])
{
    Console.WriteLine("::error::Expected the config path, targets, include downstream, exclude, bump, channel, on failure and dry run arguments.");
    return Program.UsageError;
}

var request = new PlanRequest(configPath, targets, includeDownstream is Program.True, exclude, releaseLevel, channel, onFailure, dryRun is Program.True);

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
            Console.WriteLine("::error::The RELEASE_TRAIN_APP_CLIENT_ID and RELEASE_TRAIN_APP_PRIVATE_KEY secrets must both be set. README.md describes the GitHub App.");
            return Failure;
        }

        Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Directory.GetCurrentDirectory());

        if (Catalog.Read(request.ConfigPath, await File.ReadAllTextAsync(request.ConfigPath).ConfigureAwait(false)) is not { } catalog
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

        if (!request.DryRun && await MissingInstallationsAsync(planned, catalog, clock).ConfigureAwait(false) is { Count: > 0 } missing)
        {
            Console.WriteLine($"::error::The release train GitHub App is not installed on {string.Join(", ", missing)}.");
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
    private static bool HasAppSecrets() =>
        !string.IsNullOrEmpty(GetEnvironmentVariable("APP_CLIENT_ID")) && !string.IsNullOrEmpty(GetEnvironmentVariable("APP_PRIVATE_KEY"));

    /// <summary>Selects the targets, their downstream repositories when asked, less the exclusions.</summary>
    /// <param name="request">The workflow inputs.</param>
    /// <param name="catalog">The config.</param>
    /// <returns>The selected repositories, or null after reporting an error.</returns>
    private static HashSet<string>? Select(PlanRequest request, Catalog catalog)
    {
        if (Expand(request.Targets, catalog) is not { Count: > 0 } targeted || Expand(request.Exclude, catalog) is not { } excluded)
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
    private static int LevelOf(string name, Dictionary<string, int> levels, Dictionary<string, HashSet<string>> ancestors, HashSet<string> selected)
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
    private static async Task<List<string>> MissingInstallationsAsync(List<string> planned, Catalog catalog, TimeProvider clock)
    {
        const int TimeoutMinutes = 5;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(TimeoutMinutes), clock);
        using var http = AppClient(clock);
        List<string> missing = [];
        foreach (var name in planned)
        {
            var repository = catalog.Setting(name, RepositoryKey, $"{catalog.Owner}/{name}");
            using var response = await http.GetAsync(new Uri($"repos/{repository}/installation", UriKind.Relative), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                missing.Add(repository);
            }
        }

        return missing;
    }

    /// <summary>Makes a GitHub API client that authenticates as the app.</summary>
    /// <param name="clock">The clock for the JWT.</param>
    /// <returns>The client.</returns>
    private static HttpClient AppClient(TimeProvider clock)
    {
        HttpClient http = new() { BaseAddress = new("https://api.github.com/") };
        http.DefaultRequestHeaders.Authorization = new("Bearer", Jwt(clock));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("reactiveui-release-train");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
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
        var claims = string.Create(CultureInfo.InvariantCulture, $"{{\"iat\":{now - Backdate},\"exp\":{now + Lifetime},\"iss\":\"{GetEnvironmentVariable("APP_CLIENT_ID")}\"}}");
        var unsigned = $"{Base64Url.EncodeToString("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"u8)}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims))}";
        return $"{unsigned}.{Base64Url.EncodeToString(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))}";
    }

    /// <summary>Builds the entries of every level.</summary>
    /// <param name="planned">The planned repositories, in order.</param>
    /// <param name="levels">The level of each repository.</param>
    /// <param name="catalog">The config.</param>
    /// <param name="request">The workflow inputs.</param>
    /// <returns>The entries for each level, or null after reporting a missing channel input.</returns>
    private static List<LevelEntry>[]? LevelEntries(List<string> planned, Dictionary<string, int> levels, Catalog catalog, PlanRequest request)
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
        if (Inputs(repository[ReleaseInputsKey] as JsonObject ?? catalog.Defaults[ReleaseInputsKey] as JsonObject, request) is not { } inputs)
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
            inputs[key] = text.Replace(BumpPlaceholder, request.Bump, StringComparison.Ordinal).Replace(ChannelPlaceholder, request.Channel, StringComparison.Ordinal);
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
    private static Task WriteOutputsAsync(Catalog catalog, List<LevelEntry>[] levelEntries, int depth, List<string> planned, Dictionary<string, int> levels)
    {
        List<PlanEntry> plan = [];
        foreach (var name in planned)
        {
            plan.Add(new(name, levels[name]));
        }

        var output = new StringBuilder();
        for (var level = 0; level < levelEntries.Length; level++)
        {
            _ = output.Append(CultureInfo.InvariantCulture, $"level{level}=").Append(JsonSerializer.Serialize(levelEntries[level], PlanJson.Default.ListLevelEntry)).Append('\n');
        }

        _ = output.Append(CultureInfo.InvariantCulture, $"depth={depth}\n");
        _ = output.Append("settings=").Append(JsonSerializer.Serialize(Settings(catalog), PlanJson.Default.TrainSettings)).Append('\n');
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
    private static int Minutes(JsonObject timeouts, string key, int fallback) => timeouts[key]?.GetValue<int>() ?? fallback;

    /// <summary>Renders the plan as a Markdown table.</summary>
    /// <param name="request">The workflow inputs.</param>
    /// <param name="catalog">The config.</param>
    /// <param name="levelEntries">The entries for each level.</param>
    /// <param name="selected">The repositories in the plan.</param>
    /// <returns>The Markdown.</returns>
    private static string Summary(PlanRequest request, Catalog catalog, List<LevelEntry>[] levelEntries, HashSet<string> selected)
    {
        StringBuilder summary = new();
        _ = summary.AppendLine("## Release train plan").AppendLine();
        _ = summary.AppendLine(CultureInfo.InvariantCulture, $"Bump `{request.Bump}`, channel `{request.Channel}`, on failure `{request.OnFailure}`.").AppendLine();
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

                _ = summary.AppendLine(CultureInfo.InvariantCulture, $"| {level} | {entry.Repository} | {(waits.Count is 0 ? "-" : string.Join(", ", waits))} | {string.Join(", ", inputs)} |");
            }
        }

        return summary.ToString();
    }

    /// <summary>The repositories in the config, their dependencies and defaults.</summary>
    internal sealed class Catalog
    {
        /// <summary>The config key that lists a repository's dependencies.</summary>
        private const string DependsOnKey = "dependsOn";

        /// <summary>Gets the whole config.</summary>
        internal required JsonObject Config { get; init; }

        /// <summary>Gets the default repository owner.</summary>
        internal required string Owner { get; init; }

        /// <summary>Gets the repository names in config order.</summary>
        internal required List<string> Order { get; init; }

        /// <summary>Gets the repository entries by name.</summary>
        internal required Dictionary<string, JsonObject> Repositories { get; init; }

        /// <summary>Gets the direct dependencies of each repository, spelled as the config names them.</summary>
        internal required Dictionary<string, List<string>> DirectDependencies { get; init; }

        /// <summary>Gets every repository each repository depends on, directly or not.</summary>
        internal required Dictionary<string, HashSet<string>> Ancestors { get; init; }

        /// <summary>Gets the defaults for every repository.</summary>
        internal JsonObject Defaults => Config["defaults"] as JsonObject ?? [];

        /// <summary>Reads the repositories and checks their dependencies.</summary>
        /// <param name="path">The config path, for messages.</param>
        /// <param name="json">The config text.</param>
        /// <returns>The catalog, or null after reporting an error.</returns>
        internal static Catalog? Read(string path, string json)
        {
            if (JsonNode.Parse(json) is not JsonObject config
                || config["owner"]?.GetValue<string>() is not { Length: > 0 } owner
                || config["repositories"] is not JsonArray repositoryNodes)
            {
                Console.WriteLine($"::error::{path} needs an owner and a repositories list.");
                return null;
            }

            return Names(repositoryNodes) is { } repositories
                && Dependencies(repositories.Order, repositories.Entries) is { } direct
                && AllAncestors(repositories.Order, direct) is { } ancestors
                ? new() { Config = config, Owner = owner, Order = repositories.Order, Repositories = repositories.Entries, DirectDependencies = direct, Ancestors = ancestors }
                : null;
        }

        /// <summary>Gets the config spelling of a repository name.</summary>
        /// <param name="name">The name in any case.</param>
        /// <returns>The name as the config writes it.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal string Canonical(string name) => Find(name)!;

        /// <summary>Finds a repository by name, ignoring case.</summary>
        /// <param name="name">The name.</param>
        /// <returns>The name as the config writes it, or null.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal string? Find(string name) => FindIn(Order, name);

        /// <summary>Finds a group by name, ignoring case.</summary>
        /// <param name="name">The group name.</param>
        /// <returns>The members, or null when no group has that name.</returns>
        internal JsonArray? Group(string name)
        {
            if (Config["groups"] is not JsonObject groups)
            {
                return null;
            }

            foreach (var (key, members) in groups)
            {
                if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return members as JsonArray;
                }
            }

            return null;
        }

        /// <summary>Reads a repository setting, falling back to the defaults.</summary>
        /// <param name="name">The repository.</param>
        /// <param name="key">The setting.</param>
        /// <param name="fallback">The value when neither sets it.</param>
        /// <returns>The value.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal string Setting(string name, string key, string fallback) =>
            Repositories[name][key]?.GetValue<string>() ?? Defaults[key]?.GetValue<string>() ?? fallback;

        /// <summary>Finds a name in a list, ignoring case.</summary>
        /// <param name="names">The names.</param>
        /// <param name="name">The name to find.</param>
        /// <returns>The name as the list writes it, or null.</returns>
        private static string? FindIn(List<string> names, string name)
        {
            foreach (var known in names)
            {
                if (known.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            return null;
        }

        /// <summary>Reads the repository names, rejecting blank and repeated names.</summary>
        /// <param name="repositoryNodes">The repositories list.</param>
        /// <returns>The names in order and the entries by name, or null after reporting an error.</returns>
        private static (List<string> Order, Dictionary<string, JsonObject> Entries)? Names(JsonArray repositoryNodes)
        {
            Dictionary<string, JsonObject> repositories = [with(StringComparer.OrdinalIgnoreCase)];
            List<string> order = [];
            foreach (var node in repositoryNodes)
            {
                if (node?["name"]?.GetValue<string>() is not { Length: > 0 } name)
                {
                    Console.WriteLine("::error::Every repository needs a name.");
                    return null;
                }

                if (!repositories.TryAdd(name, node.AsObject()))
                {
                    Console.WriteLine($"::error::{name} is listed twice.");
                    return null;
                }

                order.Add(name);
            }

            return (order, repositories);
        }

        /// <summary>Reads each repository's direct dependencies, rejecting unknown names.</summary>
        /// <param name="order">The repository names.</param>
        /// <param name="repositories">The repository entries.</param>
        /// <returns>The dependencies, spelled as the config names them, or null after reporting an error.</returns>
        private static Dictionary<string, List<string>>? Dependencies(List<string> order, Dictionary<string, JsonObject> repositories)
        {
            Dictionary<string, List<string>> direct = [with(StringComparer.OrdinalIgnoreCase)];
            foreach (var name in order)
            {
                List<string> dependencies = [];
                foreach (var dependency in repositories[name][DependsOnKey] as JsonArray ?? [])
                {
                    var dependencyName = dependency!.GetValue<string>();
                    if (FindIn(order, dependencyName) is not { } known)
                    {
                        Console.WriteLine($"::error::{name} depends on {dependencyName}, which is not in the config.");
                        return null;
                    }

                    dependencies.Add(known);
                }

                direct[name] = dependencies;
            }

            return direct;
        }

        /// <summary>Finds every repository each repository depends on, rejecting cycles.</summary>
        /// <param name="order">The repository names.</param>
        /// <param name="direct">The direct dependencies.</param>
        /// <returns>The ancestors, or null after reporting a cycle.</returns>
        private static Dictionary<string, HashSet<string>>? AllAncestors(List<string> order, Dictionary<string, List<string>> direct)
        {
            Dictionary<string, HashSet<string>> ancestors = [with(StringComparer.OrdinalIgnoreCase)];
            foreach (var name in order)
            {
                if (Walk(name, order.Count, direct) is not { } found)
                {
                    Console.WriteLine($"::error::{name} depends on itself through dependsOn.");
                    return null;
                }

                ancestors[name] = found;
            }

            return ancestors;
        }

        /// <summary>Walks one repository's dependencies.</summary>
        /// <param name="name">The repository.</param>
        /// <param name="maxDepth">The deepest a chain can go without a cycle.</param>
        /// <param name="direct">The direct dependencies.</param>
        /// <returns>The ancestors, or null on a cycle.</returns>
        private static HashSet<string>? Walk(string name, int maxDepth, Dictionary<string, List<string>> direct)
        {
            HashSet<string> found = [with(StringComparer.OrdinalIgnoreCase)];
            Stack<(string Name, int Depth)> pending = [];
            foreach (var dependency in direct[name])
            {
                pending.Push((dependency, 1));
            }

            while (pending.TryPop(out var current))
            {
                if (current.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || current.Depth > maxDepth)
                {
                    return null;
                }

                if (found.Add(current.Name))
                {
                    foreach (var dependency in direct[current.Name])
                    {
                        pending.Push((dependency, current.Depth + 1));
                    }
                }
            }

            return found;
        }
    }

    /// <summary>Serializes the plan outputs without reflection.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(List<LevelEntry>))]
    [JsonSerializable(typeof(TrainSettings))]
    [JsonSerializable(typeof(List<PlanEntry>))]
    internal sealed partial class PlanJson : JsonSerializerContext;

    /// <summary>The workflow inputs.</summary>
    /// <param name="ConfigPath">The release train config, relative to the workspace.</param>
    /// <param name="Targets">The repositories or groups to release.</param>
    /// <param name="IncludeDownstream">Whether to release everything that depends on the targets.</param>
    /// <param name="Exclude">The repositories or groups to leave out.</param>
    /// <param name="Bump">The release level.</param>
    /// <param name="Channel">The release channel, or none.</param>
    /// <param name="OnFailure">stop or continue.</param>
    /// <param name="DryRun">Whether only the plan is shown.</param>
    internal sealed record PlanRequest(string ConfigPath, string Targets, bool IncludeDownstream, string Exclude, string Bump, string Channel, string OnFailure, bool DryRun);

    /// <summary>One repository to release in a level.</summary>
    /// <param name="Name">The config name.</param>
    /// <param name="Repository">The owner/name.</param>
    /// <param name="Branch">The branch to update and release.</param>
    /// <param name="Workflow">The release workflow file.</param>
    /// <param name="AdminMerge">Whether to merge the update pull request as an admin.</param>
    /// <param name="Inputs">The release workflow inputs.</param>
    /// <param name="WaitsFor">The planned repositories it depends on.</param>
    /// <param name="IgnorePackages">Package ids to leave as they are.</param>
    internal sealed record LevelEntry(
        string Name,
        string Repository,
        string Branch,
        string Workflow,
        bool AdminMerge,
        Dictionary<string, string> Inputs,
        List<string> WaitsFor,
        JsonNode IgnorePackages);

    /// <summary>A config repository and its owner/name.</summary>
    /// <param name="Name">The config name.</param>
    /// <param name="Repository">The owner/name.</param>
    internal sealed record CatalogEntry(string Name, string Repository);

    /// <summary>The settings every level reads.</summary>
    /// <param name="Catalog">Every config repository.</param>
    /// <param name="UpdateBranch">The branch dependency updates are pushed to.</param>
    /// <param name="ChecksMinutes">Minutes to wait for pull request checks.</param>
    /// <param name="ReleaseMinutes">Minutes to wait for a release run.</param>
    /// <param name="NugetMinutes">Minutes to wait for NuGet to list a package.</param>
    internal sealed record TrainSettings(List<CatalogEntry> Catalog, string UpdateBranch, int ChecksMinutes, int ReleaseMinutes, int NugetMinutes);

    /// <summary>A planned repository and its level.</summary>
    /// <param name="Name">The config name.</param>
    /// <param name="Level">The level, from zero.</param>
    internal sealed record PlanEntry(string Name, int Level);
}
