// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

namespace Actions.Common.GitHub;

/// <summary>A comment request.</summary>
/// <param name="Body">The notice text.</param>
internal sealed record GitHubComment(string Body);
