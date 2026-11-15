// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>Plans the release train: the order and level of every repository to release.</summary>
internal static partial class Program
{
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
                && AllAncestors(repositories.Order, direct) is { } ancestors ? new()
                {
                    Config = config,
                    Owner = owner,
                    Order = repositories.Order,
                    Repositories = repositories.Entries,
                    DirectDependencies = direct,
                    Ancestors = ancestors,
                } : null;
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
        internal string Setting(
            string name,
            string key,
            string fallback) => Repositories[name][key]?.GetValue<string>()
        ?? Defaults[key]?.GetValue<string>()
        ?? fallback;

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
        private static Dictionary<string, List<string>>? Dependencies(
            List<string> order,
            Dictionary<string, JsonObject> repositories)
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
        private static Dictionary<string, HashSet<string>>? AllAncestors(
            List<string> order,
            Dictionary<string, List<string>> direct)
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
    internal sealed record PlanRequest(
        string ConfigPath,
        string Targets,
        bool IncludeDownstream,
        string Exclude,
        string Bump,
        string Channel,
        string OnFailure,
        bool DryRun);

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
    internal sealed record TrainSettings(
        List<CatalogEntry> Catalog,
        string UpdateBranch,
        int ChecksMinutes,
        int ReleaseMinutes,
        int NugetMinutes);

    /// <summary>A planned repository and its level.</summary>
    /// <param name="Name">The config name.</param>
    /// <param name="Level">The level, from zero.</param>
    internal sealed record PlanEntry(string Name, int Level);
}
