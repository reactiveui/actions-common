// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Net;
using Refit;

namespace Actions.Common.GitHub;

/// <summary>Creates GitHub clients with bounded, server-directed retries.</summary>
internal static class GitHubClients
{
    /// <summary>The maximum number of retries after the first request.</summary>
    private const int RetryCount = 3;

    /// <summary>The longest server-directed wait within the action timeout.</summary>
    private const int MaximumDelayMinutes = 8;

    /// <summary>Creates a client whose owner must dispose it.</summary>
    /// <param name="token">The app JWT or installation token.</param>
    /// <param name="clock">The clock used for retry waits.</param>
    /// <param name="transport">The transport, or the default HTTP handler.</param>
    /// <returns>The authenticated HTTP client.</returns>
    internal static HttpClient Create(string token, TimeProvider clock, HttpMessageHandler? transport = null)
    {
        var retries = new HttpRetryOptions
        {
            TimeProvider = clock,
            MaxRetries = RetryCount,
            BaseDelay = TimeSpan.FromMinutes(1),
            MaxDelay = TimeSpan.FromMinutes(MaximumDelayMinutes),
            UseJitter = false,
            RetryUnsafeMethods = true,

            // A disconnected POST might have succeeded. Only explicit responses can authorize replay.
            RetryHttpRequestExceptions = false,
            DelayProvider = RetryDelayProviders.GitHub,
            ShouldRetry = ShouldRetry,
        };
        var retryHandler = new HttpRetryHandler(retries) { InnerHandler = new GitHubSecondaryLimitHandler { InnerHandler = transport ?? new HttpClientHandler() }, };
        var client = new HttpClient(retryHandler) { BaseAddress = new(Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com/"), Timeout = Timeout.InfiniteTimeSpan, };
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("reactiveui-actions-common");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    /// <summary>Creates the generated API without reflection-based JSON serialization.</summary>
    /// <param name="client">The authenticated HTTP client.</param>
    /// <returns>The generated GitHub client.</returns>
    internal static IGitHubApi Api(
        HttpClient client) => RestService.ForGenerated<IGitHubApi>(client, GitHubJson.Default);

    /// <summary>Retries rate limits and safe requests that received transient server failures.</summary>
    /// <param name="response">The server response.</param>
    /// <returns>Whether the retry handler may send the request again.</returns>
    private static bool ShouldRetry(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            return response.Headers.RetryAfter is not null
                || response.Headers.Contains("X-Actions-Secondary-Limit")
                || HasExhaustedQuota(response);
        }

        var method = response.RequestMessage?.Method;
        return (method == HttpMethod.Get || method == HttpMethod.Put)
            && (response.StatusCode is HttpStatusCode.RequestTimeout
            || response.StatusCode >= HttpStatusCode.InternalServerError);
    }

    /// <summary>Checks GitHub's remaining-request quota.</summary>
    /// <param name="response">The server response.</param>
    /// <returns>Whether the reported quota is exhausted.</returns>
    private static bool HasExhaustedQuota(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ratelimit-remaining", out var values))
        {
            foreach (var value in values)
            {
                if (value is "0")
                {
                    return true;
                }
            }
        }

        return false;
    }
}
