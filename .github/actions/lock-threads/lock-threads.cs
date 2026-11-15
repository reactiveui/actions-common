#!/usr/bin/env dotnet run
// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:property TargetFramework=net11.0
#:package Refit@16.4.0

using System.Globalization;
using Actions.Common.GitHub;
using static System.Environment;

const int OperationMinutes = 9;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(OperationMinutes));

var cancellationToken = timeout.Token;

var repository = GetEnvironmentVariable("GITHUB_REPOSITORY")
?? throw new InvalidOperationException("GITHUB_REPOSITORY is required.");

var token = GetEnvironmentVariable("GH_TOKEN") ?? throw new InvalidOperationException("GH_TOKEN is required.");

using var client = GitHubClients.Create(token, TimeProvider.System);

var api = GitHubClients.Api(client);

var days = int.Parse(GetEnvironmentVariable("INACTIVE_DAYS") ?? "14", CultureInfo.InvariantCulture);

// GitHub accepts an omitted reason, but rejects the workflow's empty default.
var lockReason = GetEnvironmentVariable("LOCK_REASON") is { Length: > 0 } reason ? reason : null;

var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

var query = FormattableString.Invariant(
    $"repo:{repository} is:closed is:unlocked updated:<{cutoff:yyyy-MM-ddTHH:mm:ssZ}");

var results = await api.SearchAsync(query, cancellationToken);

foreach (var item in results.Items)
{
    var number = item.Number;
    var thread = await api.ThreadAsync(repository, number, cancellationToken);

    // Search is indexed asynchronously. Recheck the current state before posting or locking.
    if (thread.Locked || thread.State is not "closed" || thread.UpdatedAt >= cutoff)
    {
        continue;
    }

    var isPullRequest = thread.PullRequest.HasValue;
    var excludedLabel = GetEnvironmentVariable("EXCLUDE_ISSUE_LABEL");
    if (!isPullRequest && !string.IsNullOrEmpty(excludedLabel) && Array.Exists(
        thread.Labels,
        label => string.Equals(label.Name, excludedLabel, StringComparison.OrdinalIgnoreCase)))
    {
        continue;
    }

    var kind = isPullRequest ? "pull request" : "issue";
    if (GetEnvironmentVariable("POST_COMMENT") is not "false")
    {
        var body = $"This {kind} has been automatically locked since there has not been any recent activity after it was closed. Please open a new issue for related bugs.";
        await api.CommentAsync(repository, number, new(body), cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }

    if (!isPullRequest && GetEnvironmentVariable("ADD_ISSUE_LABEL") is { Length: > 0 } label)
    {
        await api.LabelAsync(repository, number, new([label]), cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }

    await api.LockAsync(repository, number, new(lockReason), cancellationToken);
    Console.WriteLine(FormattableString.Invariant($"Locked {kind} #{number}."));
    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
}
