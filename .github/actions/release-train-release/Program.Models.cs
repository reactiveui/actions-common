// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Actions.Common.GitHub;
using static System.Environment;

/// <summary>Starts a repository's release workflow, waits for it, and records the release it made.</summary>
internal static partial class Program
{
    /// <summary>The JSON contract for the plan entry, the GitHub responses and release.json.</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReleaseEntry))]
    [JsonSerializable(typeof(List<GitHubRelease>))]
    [JsonSerializable(typeof(List<WorkflowRun>))]
    [JsonSerializable(typeof(RunView))]
    [JsonSerializable(typeof(ReleaseRecord))]
    [JsonSerializable(typeof(JwtClaims))]
    internal sealed partial class ReleaseJson : JsonSerializerContext;

    /// <summary>Holds a GitHub App installation token for the repository. Tokens last an hour and the waits here last
    /// longer, so every gh call refreshes it first.</summary>
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

        /// <summary>Signs a nine-minute app JWT, backdated a minute for clock drift.</summary>
        /// <returns>The JWT.</returns>
        private static string Jwt()
        {
            const int ClockDriftSeconds = 60;
            const int LifetimeSeconds = 540;
            using var rsa = RSA.Create();
            rsa.ImportFromPem(GetEnvironmentVariable("APP_PRIVATE_KEY"));
            var now = Clock.GetUtcNow().ToUnixTimeSeconds();
            var issuer = GetEnvironmentVariable("APP_CLIENT_ID") ?? string.Empty;
            var claims = JsonSerializer.SerializeToUtf8Bytes(
                new(
    now
                    - ClockDriftSeconds,
    now
                    + LifetimeSeconds,
    issuer),
                ReleaseJson.Default.JwtClaims);
            var unsigned = $"{Base64Url.EncodeToString(JwtHeader)}.{Base64Url.EncodeToString(claims)}";
            return $"{unsigned}.{Base64Url.EncodeToString(rsa.SignData(
                Encoding.ASCII.GetBytes(unsigned),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))}";
        }
    }

    /// <summary>A repository in the plan.</summary>
    /// <param name="Repository">The repository, as owner/name.</param>
    /// <param name="Branch">The branch to release from.</param>
    /// <param name="Workflow">The release workflow file.</param>
    /// <param name="Inputs">The release workflow inputs.</param>
    internal sealed record ReleaseEntry(
        string Repository,
        string Branch,
        string Workflow,
        Dictionary<string, string> Inputs);

    /// <summary>A GitHub release.</summary>
    /// <param name="TagName">The tag.</param>
    /// <param name="Name">The title.</param>
    /// <param name="TargetCommitish">The commit or branch the tag points at.</param>
    /// <param name="CreatedAt">When the release was made.</param>
    /// <param name="Draft">Whether it is a draft.</param>
    /// <param name="HtmlUrl">The release page.</param>
    /// <param name="Assets">The attached files.</param>
    internal sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        string? Name,
        [property: JsonPropertyName("target_commitish")] string? TargetCommitish,
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        bool? Draft,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        List<ReleaseAsset> Assets);

    /// <summary>A file attached to a release.</summary>
    /// <param name="Name">The file name.</param>
    internal sealed record ReleaseAsset(string Name);

    /// <summary>A listed workflow run.</summary>
    /// <param name="DatabaseId">The run id.</param>
    /// <param name="CreatedAt">When the run was created.</param>
    internal sealed record WorkflowRun(long DatabaseId, DateTimeOffset CreatedAt);

    /// <summary>The state of a workflow run.</summary>
    /// <param name="Status">The run status.</param>
    /// <param name="Conclusion">The run conclusion once completed.</param>
    /// <param name="HeadSha">The commit the run built.</param>
    internal sealed record RunView(string Status, string? Conclusion, string HeadSha);

    /// <summary>The release.json record the later steps read.</summary>
    /// <param name="Version">The released version.</param>
    /// <param name="Tag">The release tag.</param>
    /// <param name="ReleaseUrl">The release page.</param>
    /// <param name="RunUrl">The release run, or null for a reused release.</param>
    /// <param name="Packages">The released package ids.</param>
    internal sealed record ReleaseRecord(
        string Version,
        string Tag,
        string ReleaseUrl,
        string? RunUrl,
        List<string> Packages);

    /// <summary>The app JWT claims.</summary>
    /// <param name="Iat">Issued at, in Unix seconds.</param>
    /// <param name="Exp">Expires at, in Unix seconds.</param>
    /// <param name="Iss">The app client id.</param>
    internal sealed record JwtClaims(long Iat, long Exp, string Iss);
}
