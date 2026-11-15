// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

namespace Actions.Common.GitHub;

/// <summary>An installation access token.</summary>
/// <param name="Token">The token value.</param>
/// <param name="ExpiresAt">The expiry time.</param>
internal sealed record GitHubAccessToken(string Token, DateTimeOffset ExpiresAt);
