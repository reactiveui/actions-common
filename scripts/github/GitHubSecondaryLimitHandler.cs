// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Net;

namespace Actions.Common.GitHub;

/// <summary>Recognizes secondary limits that GitHub identifies only in the response body.</summary>
internal sealed class GitHubSecondaryLimitHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden && response.Headers.RetryAfter is null)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
                || body.Contains("abuse detection", StringComparison.OrdinalIgnoreCase))
            {
                // GitHub requires at least a minute when a secondary limit has no retry header.
                response.Headers.Add("X-Actions-Secondary-Limit", "true");
            }
        }

        return response;
    }
}
