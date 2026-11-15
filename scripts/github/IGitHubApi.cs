// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using Refit;

namespace Actions.Common.GitHub;

/// <summary>The GitHub endpoints used by the maintenance actions.</summary>
internal interface IGitHubApi
{
    /// <summary>Finds inactive closed threads.</summary>
    /// <param name="query">The GitHub search query.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The first page of matching threads.</returns>
    [Get("/search/issues?sort=updated&order=asc&per_page=50")]
    Task<GitHubSearch> SearchAsync([AliasAs("q")] string query, CancellationToken cancellationToken);

    /// <summary>Reads a thread's current state.</summary>
    /// <param name="repository">The owner and repository.</param>
    /// <param name="number">The thread number.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The current thread.</returns>
    [Get("/repos/{**repository}/issues/{number}")]
    Task<GitHubThread> ThreadAsync(string repository, int number, CancellationToken cancellationToken);

    /// <summary>Posts the inactivity notice.</summary>
    /// <param name="repository">The owner and repository.</param>
    /// <param name="number">The thread number.</param>
    /// <param name="comment">The notice body.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing the request.</returns>
    [Post("/repos/{**repository}/issues/{number}/comments")]
    Task CommentAsync(string repository, int number, [Body] GitHubComment comment, CancellationToken cancellationToken);

    /// <summary>Adds the configured issue label.</summary>
    /// <param name="repository">The owner and repository.</param>
    /// <param name="number">The issue number.</param>
    /// <param name="labels">The labels to add.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing the request.</returns>
    [Post("/repos/{**repository}/issues/{number}/labels")]
    Task LabelAsync(string repository, int number, [Body] GitHubLabels labels, CancellationToken cancellationToken);

    /// <summary>Locks a closed thread.</summary>
    /// <param name="repository">The owner and repository.</param>
    /// <param name="number">The thread number.</param>
    /// <param name="reason">The configured reason.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task representing the request.</returns>
    [Put("/repos/{**repository}/issues/{number}/lock")]
    Task LockAsync(string repository, int number, [Body] GitHubLock reason, CancellationToken cancellationToken);

    /// <summary>Finds the app installation for a repository.</summary>
    /// <param name="repository">The owner and repository.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The installation response, including a missing installation.</returns>
    [Get("/repos/{**repository}/installation")]
    Task<ApiResponse<GitHubInstallation>> InstallationAsync(string repository, CancellationToken cancellationToken);

    /// <summary>Creates an installation access token.</summary>
    /// <param name="installation">The installation identifier.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The access token and expiry.</returns>
    [Post("/app/installations/{installation}/access_tokens")]
    Task<GitHubAccessToken> AccessTokenAsync(long installation, CancellationToken cancellationToken);

    /// <summary>Reads the authenticated app.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The app name.</returns>
    [Get("/app")]
    Task<GitHubApp> AppAsync(CancellationToken cancellationToken);
}
