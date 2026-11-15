// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Actions.Common.GitHub;
using static System.Environment;

/// <summary>Moves a repository's ReactiveUI package versions to the train's releases through a pull request, then
/// merges it once its checks pass.</summary>
internal static partial class Program
{
    /// <summary>The version a package moves to.</summary>
    /// <param name="Id">The package id.</param>
    /// <param name="Version">The version.</param>
    /// <param name="Source">Where the version came from.</param>
    internal readonly record struct PackagePin(string Id, string Version, string Source);

    /// <summary>The JSON contract for the train files and the GitHub responses.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(TrainEntry))]
    [JsonSerializable(typeof(TrainSettings))]
    [JsonSerializable(typeof(TrainResult))]
    [JsonSerializable(typeof(UpdateLog))]
    [JsonSerializable(typeof(GitHubRelease))]
    [JsonSerializable(typeof(List<PullRequestItem>))]
    [JsonSerializable(typeof(List<CheckRun>))]
    [JsonSerializable(typeof(JwtClaims))]
    internal sealed partial class TrainJson : JsonSerializerContext;

    /// <summary>Holds a GitHub App installation token for the repository. Tokens last an hour and the waits here last
    /// longer, so every gh or git call refreshes it first.</summary>
    private static class AppToken
    {
        /// <summary>How long before expiry the token is replaced.</summary>
        private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(10);

        /// <summary>When the current token expires.</summary>
        private static DateTimeOffset _expires;

        /// <summary>Gets or sets the repository the token is for, as owner/name.</summary>
        internal static string Repository { get; set; } = string.Empty;

        /// <summary>Gets or sets the clock for the token lifetime.</summary>
        internal static TimeProvider Clock { get; set; } = TimeProvider.System;

        /// <summary>Gets the JWT header.</summary>
        private static ReadOnlySpan<byte> JwtHeader => """{"alg":"RS256","typ":"JWT"}"""u8;

        /// <summary>Replaces the token in GH_TOKEN when it is close to expiry.</summary>
        /// <param name="cancellationToken">The operation cancellation token.</param>
        /// <returns>A task that completes when the token is fresh.</returns>
        internal static async Task RefreshAsync(CancellationToken cancellationToken)
        {
            if (_expires - Clock.GetUtcNow() > RefreshMargin)
            {
                return;
            }

            using var http = GitHubClients.Create(Jwt(), Clock);
            var api = GitHubClients.Api(http);
            using var installation = await api.InstallationAsync(Repository, cancellationToken).ConfigureAwait(false);
            _ = await installation.EnsureSuccessStatusCodeAsync().ConfigureAwait(false);
            var token = await api.AccessTokenAsync(installation.Content!.Id, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"::add-mask::{token!.Token}");
            SetEnvironmentVariable("GH_TOKEN", token.Token);
            _expires = token.ExpiresAt;
        }

        /// <summary>Reads the app's slug.</summary>
        /// <param name="cancellationToken">The operation cancellation token.</param>
        /// <returns>The slug.</returns>
        internal static async Task<string> SlugAsync(CancellationToken cancellationToken)
        {
            using var http = GitHubClients.Create(Jwt(), Clock);
            var api = GitHubClients.Api(http);
            var app = await api.AppAsync(cancellationToken).ConfigureAwait(false);
            return app!.Slug;
        }

        /// <summary>Signs a nine-minute app JWT, backdated a minute for clock drift.</summary>
        /// <returns>The JWT.</returns>
        private static string Jwt()
        {
            const int ClockDriftSeconds = 60;
            const int LifetimeSeconds = 540;
            using var rsa = RSA.Create();
            rsa.ImportFromPem(GetEnvironmentVariable("APP_PRIVATE_KEY"));
            var now = Clock.GetUtcNow().ToUnixTimeSeconds();
            var claims = JsonSerializer.SerializeToUtf8Bytes(
                new(
    now
                    - ClockDriftSeconds,
    now
                    + LifetimeSeconds,
    GetEnvironmentVariable("APP_CLIENT_ID") ?? string.Empty),
                TrainJson.Default.JwtClaims);
            var unsigned = $"{Base64Url.EncodeToString(JwtHeader)}.{Base64Url.EncodeToString(claims)}";
            return $"{unsigned}.{Base64Url.EncodeToString(rsa.SignData(
                Encoding.ASCII.GetBytes(unsigned),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))}";
        }
    }

    /// <summary>Rewrites package versions in project files, directly or through the MSBuild property they reference.</summary>
    /// <param name="packages">The packages by id.</param>
    private sealed class Rewriter(Dictionary<string, PackagePin> packages)
    {
        /// <summary>The properties that set package versions, with the version and the package that claimed them.</summary>
        private readonly Dictionary<string, (string Version, string Id)> _properties = [with(StringComparer.Ordinal)];

        /// <summary>The current text of each file.</summary>
        private readonly Dictionary<string, string> _texts = [with(StringComparer.Ordinal)];

        /// <summary>Gets the updates made.</summary>
        internal List<VersionUpdate> Updates { get; } = [];

        /// <summary>Gets the versions left alone and why.</summary>
        internal List<string> Notes { get; } = [];

        /// <summary>Rewrites the files and saves the ones that changed.</summary>
        /// <param name="files">The project and props files, relative to the current folder.</param>
        internal void Rewrite(string[] files)
        {
            foreach (var file in files)
            {
                _texts[file] = PackageItem().Replace(File.ReadAllText(file), element => Item(element, file));
            }

            foreach (var (property, (version, id)) in _properties)
            {
                if (version is not Conflicted)
                {
                    Definitions(files, property, version, id);
                }
            }

            foreach (var file in files)
            {
                // Each file keeps its byte order mark, or its lack of one.
                var bytes = File.ReadAllBytes(file);
                var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
                if (!string.Equals(
                    Encoding.UTF8.GetString(
    bytes.AsSpan(bom ? Encoding.UTF8.Preamble.Length : 0)),
                    _texts[file],
                    StringComparison.Ordinal))
                {
                    File.WriteAllText(file, _texts[file], new UTF8Encoding(bom));
                }
            }
        }

        /// <summary>Rewrites the version attributes of one package item.</summary>
        /// <param name="element">The item element.</param>
        /// <param name="file">The file it is in.</param>
        /// <returns>The element text.</returns>
        private string Item(Match element, string file) => IncludeAttribute().Match(
            element.Value) is { Success: true } match

            && packages.TryGetValue(match.Groups[1].Value, out var package)
            ? VersionAttribute().Replace(element.Value, attribute => Version(attribute, package, file))
            : element.Value;

        /// <summary>Rewrites one version attribute, or records the property it references.</summary>
        /// <param name="attribute">The version attribute.</param>
        /// <param name="package">The package the item is for.</param>
        /// <param name="file">The file it is in.</param>
        /// <returns>The attribute text.</returns>
        private string Version(Match attribute, PackagePin package, string file)
        {
            var current = attribute.Groups[3].Value;
            if (PropertyReference().Match(current) is { Success: true } reference)
            {
                var property = reference.Groups[1].Value;
                if (_properties.TryGetValue(property, out var claimed) && claimed.Version is not Conflicted
                    && !string.Equals(claimed.Version, package.Version, StringComparison.Ordinal))
                {
                    Notes.Add(
                        $"`$({property})` serves {claimed.Id} {claimed.Version} and {package.Id} {package.Version}. It was left alone.");
                    _properties[property] = (Conflicted, claimed.Id);
                }
                else
                {
                    _ = _properties.TryAdd(property, (package.Version, package.Id));
                }

                return attribute.Value;
            }

            if (!ShouldUpdate(current, package.Version, $"{package.Id} in {file}"))
            {
                return attribute.Value;
            }

            Updates.Add(new(file, package.Id, current, package.Version, package.Source));
            return attribute.Groups[1].Value + attribute.Groups[2].Value
            + package.Version + attribute.Groups[4].Value;
        }

        /// <summary>Rewrites every definition of a version property.</summary>
        /// <param name="files">The files to search.</param>
        /// <param name="property">The property name.</param>
        /// <param name="version">The version the packages that use it moved to.</param>
        /// <param name="id">The package that claimed the property.</param>
        private void Definitions(string[] files, string property, string version, string id)
        {
            foreach (var file in files)
            {
                _texts[file] = PropertyDefinition().Replace(
                    _texts[file],
                    match =>
                    {
                        if (!string.Equals(match.Groups[2].Value, property, StringComparison.Ordinal))
                        {
                            return match.Value;
                        }

                        var current = match.Groups[3].Value.Trim();
                        if (!ShouldUpdate(current, version, $"$({property}) in {file}"))
                        {
                            return match.Value;
                        }

                        Updates.Add(new(file, $"$({property})", current, version, packages[id].Source));
                        return $"{match.Groups[1].Value}{version}{match.Groups[4].Value}";
                    });
            }
        }

        /// <summary>Decides whether a version moves, noting why when it does not.</summary>
        /// <param name="current">The version in the file.</param>
        /// <param name="next">The released version.</param>
        /// <param name="where">The item and file, for the note.</param>
        /// <returns>Whether to write the released version.</returns>
        private bool ShouldUpdate(string current, string next, string where)
        {
            if (string.Equals(current, next, StringComparison.Ordinal))
            {
                return false;
            }

            if (SemanticVersion.TryParse(current) is not { } from)
            {
                Notes.Add($"{where} uses `{current}`, which is not a plain version. It was left alone.");
                return false;
            }

            if (SemanticVersion.TryParse(next) is { } to && to.CompareTo(from) < 0)
            {
                Notes.Add($"{where} pins {current}, which is newer than {next}. It was left alone.");
                return false;
            }

            return true;
        }
    }

    /// <summary>A plain version: numeric core parts, then pre-release identifiers.</summary>
    private sealed class SemanticVersion
    {
        /// <summary>The numeric core parts.</summary>
        private readonly long[] _core;

        /// <summary>The pre-release identifiers.</summary>
        private readonly string[] _preRelease;

        /// <summary>Initializes a new instance of the <see cref="SemanticVersion"/> class.</summary>
        /// <param name="core">The numeric core parts.</param>
        /// <param name="preRelease">The pre-release identifiers.</param>
        private SemanticVersion(long[] core, string[] preRelease)
        {
            _core = core;
            _preRelease = preRelease;
        }

        /// <summary>Parses two to four numeric parts with optional pre-release and build metadata.</summary>
        /// <param name="text">The version text.</param>
        /// <returns>The version, or null when the text is not a plain version.</returns>
        internal static SemanticVersion? TryParse(string text)
        {
            if (!PlainVersion().IsMatch(text))
            {
                return null;
            }

            var withoutMetadata = text.Split('+')[0];
            var dash = withoutMetadata.IndexOf('-', StringComparison.Ordinal);
            var parts = (dash < 0 ? withoutMetadata : withoutMetadata[..dash]).Split('.');
            var core = new long[parts.Length];
            for (var index = 0; index < parts.Length; index++)
            {
                core[index] = long.Parse(parts[index], CultureInfo.InvariantCulture);
            }

            return new(core, dash < 0 ? [] : withoutMetadata[(dash + 1)..].Split('.'));
        }

        /// <summary>Compares by precedence: core parts, then a release after its pre-releases, then the identifiers.</summary>
        /// <param name="other">The version to compare with.</param>
        /// <returns>Negative, zero or positive.</returns>
        internal int CompareTo(SemanticVersion other)
        {
            for (var index = 0; index < Math.Max(_core.Length, other._core.Length); index++)
            {
                var left = index < _core.Length ? _core[index] : 0;
                var right = index < other._core.Length ? other._core[index] : 0;
                if (left.CompareTo(right) is not 0 and var compared)
                {
                    return compared;
                }
            }

            switch (_preRelease.Length, other._preRelease.Length)
            {
                case (0, 0):
                    return 0;
                case (0, _):
                    return 1;
                case (_, 0):
                    return -1;
            }

            for (var index = 0; index < Math.Min(_preRelease.Length, other._preRelease.Length); index++)
            {
                if (CompareIdentifiers(_preRelease[index], other._preRelease[index]) is not 0 and var compared)
                {
                    return compared;
                }
            }

            return _preRelease.Length.CompareTo(other._preRelease.Length);
        }

        /// <summary>Compares pre-release identifiers: numbers by value and before words, words ordinally.</summary>
        /// <param name="left">The first identifier.</param>
        /// <param name="right">The second identifier.</param>
        /// <returns>Negative, zero or positive.</returns>
        private static int CompareIdentifiers(string left, string right) => (long.TryParse(
            left,
            CultureInfo.InvariantCulture,
            out var leftNumber), long.TryParse(
    right,
    CultureInfo.InvariantCulture,
    out var rightNumber)) switch
        {
            (true, true) => leftNumber.CompareTo(rightNumber),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    /// <summary>A repository in the plan.</summary>
    /// <param name="Name">The repository name in the config.</param>
    /// <param name="Repository">The repository, as owner/name.</param>
    /// <param name="Branch">The branch to release from.</param>
    /// <param name="AdminMerge">Whether to merge with admin rights.</param>
    /// <param name="WaitsFor">The repositories that must release first.</param>
    /// <param name="IgnorePackages">Package ids to leave alone.</param>
    internal sealed record TrainEntry(
        string Name,
        string Repository,
        string Branch,
        bool AdminMerge,
        List<string> WaitsFor,
        List<string> IgnorePackages);

    /// <summary>The train settings from the plan.</summary>
    /// <param name="UpdateBranch">The pull request branch.</param>
    /// <param name="Catalog">Every repository in the config.</param>
    /// <param name="ChecksMinutes">How long the pull request checks may take.</param>
    internal sealed record TrainSettings(string UpdateBranch, List<CatalogEntry> Catalog, int ChecksMinutes);

    /// <summary>A repository in the config.</summary>
    /// <param name="Name">The repository name in the config.</param>
    /// <param name="Repository">The repository, as owner/name.</param>
    internal sealed record CatalogEntry(string Name, string Repository);

    /// <summary>An earlier repository's result.</summary>
    /// <param name="Name">The repository name in the config.</param>
    /// <param name="Status">The result status.</param>
    /// <param name="Version">The released version.</param>
    /// <param name="Packages">The released package ids.</param>
    internal sealed record TrainResult(string Name, string? Status, string? Version, List<string> Packages);

    /// <summary>The version updates and notes for the result step.</summary>
    /// <param name="Updates">The updates made.</param>
    /// <param name="Notes">The versions left alone and why.</param>
    internal sealed record UpdateLog(List<VersionUpdate> Updates, List<string> Notes);

    /// <summary>One version moved in one file.</summary>
    /// <param name="File">The file, relative to the repository.</param>
    /// <param name="Item">The package id or property reference.</param>
    /// <param name="From">The old version.</param>
    /// <param name="To">The new version.</param>
    /// <param name="Source">Where the new version came from.</param>
    internal sealed record VersionUpdate(string File, string Item, string From, string To, string Source);

    /// <summary>A GitHub release.</summary>
    /// <param name="TagName">The tag.</param>
    /// <param name="Name">The title.</param>
    /// <param name="Assets">The attached files.</param>
    internal sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        string? Name,
        List<ReleaseAsset> Assets);

    /// <summary>A file attached to a release.</summary>
    /// <param name="Name">The file name.</param>
    internal sealed record ReleaseAsset(string Name);

    /// <summary>An open pull request.</summary>
    /// <param name="Url">The pull request URL.</param>
    internal sealed record PullRequestItem(string Url);

    /// <summary>A pull request check.</summary>
    /// <param name="Name">The check name.</param>
    /// <param name="Bucket">The gh state bucket: pass, fail, pending, skipping or cancel.</param>
    internal sealed record CheckRun(string Name, string Bucket);

    /// <summary>The app JWT claims.</summary>
    /// <param name="Iat">Issued at, in Unix seconds.</param>
    /// <param name="Exp">Expires at, in Unix seconds.</param>
    /// <param name="Iss">The app client id.</param>
    internal sealed record JwtClaims(long Iat, long Exp, string Iss);

    /// <summary>The state shared by the publishing steps.</summary>
    /// <param name="Entry">The repository being updated.</param>
    /// <param name="Settings">The train settings.</param>
    /// <param name="WorkDir">The work folder.</param>
    /// <param name="Outputs">The step output file.</param>
    /// <param name="Clock">The clock for the waits.</param>
    private sealed record UpdateRun(
        TrainEntry Entry,
        TrainSettings Settings,
        string WorkDir,
        string Outputs,
        TimeProvider Clock);
}
