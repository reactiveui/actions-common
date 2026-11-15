// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Text.Json.Serialization;

namespace Actions.Common.GitHub;

/// <summary>Serializes the GitHub API contracts without reflection.</summary>
[JsonSourceGenerationOptions(
PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubSearch))]
[JsonSerializable(typeof(GitHubThread))]
[JsonSerializable(typeof(GitHubComment))]
[JsonSerializable(typeof(GitHubLabels))]
[JsonSerializable(typeof(GitHubLock))]
[JsonSerializable(typeof(GitHubInstallation))]
[JsonSerializable(typeof(GitHubAccessToken))]
[JsonSerializable(typeof(GitHubApp))]
internal sealed partial class GitHubJson : JsonSerializerContext;
