// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

namespace Actions.Common.GitHub;

/// <summary>A thread lock request.</summary>
/// <param name="LockReason">The optional lock reason.</param>
internal sealed record GitHubLock(string? LockReason);
