// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

namespace Actions.Common.GitHub;

/// <summary>The search results.</summary>
/// <param name="Items">The matching threads.</param>
internal sealed record GitHubSearch(GitHubThread[] Items);
