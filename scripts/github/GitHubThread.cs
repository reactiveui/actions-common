// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Text.Json;

namespace Actions.Common.GitHub;

/// <summary>A thread's current locking policy inputs.</summary>
/// <param name="Number">The issue or pull request number.</param>
/// <param name="Locked">Whether the thread is locked.</param>
/// <param name="State">The current open or closed state.</param>
/// <param name="UpdatedAt">The most recent activity.</param>
/// <param name="PullRequest">The pull request marker, when present.</param>
/// <param name="Labels">The current labels.</param>
internal sealed record GitHubThread(
    int Number,
    bool Locked,
    string State,
    DateTimeOffset UpdatedAt,
    JsonElement? PullRequest,
    GitHubLabel[] Labels);
