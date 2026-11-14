// Copyright (c) 2019-2026 ReactiveUI and Contributors. All rights reserved.
// ReactiveUI and Contributors licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

#:property TargetFramework=net11.0

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static System.Environment;

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(9));
var cancellationToken = timeout.Token;
var repository = GetEnvironmentVariable("GITHUB_REPOSITORY") ?? throw new InvalidOperationException("GITHUB_REPOSITORY is required.");
using var client = new HttpClient { BaseAddress = new Uri(GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com") };
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GetEnvironmentVariable("GH_TOKEN"));
client.DefaultRequestHeaders.UserAgent.ParseAdd("reactiveui-actions-common");
client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

var cutoff = DateTimeOffset.UtcNow.AddDays(-14);
var query = Uri.EscapeDataString(FormattableString.Invariant($"repo:{repository} is:closed is:unlocked updated:<{cutoff:yyyy-MM-ddTHH:mm:ssZ}"));
using var search = await client.GetAsync($"/search/issues?q={query}&sort=updated&order=asc&per_page=50", cancellationToken);
search.EnsureSuccessStatusCode();
await using var searchStream = await search.Content.ReadAsStreamAsync(cancellationToken);
using var results = await JsonDocument.ParseAsync(searchStream, cancellationToken: cancellationToken);
foreach (var item in results.RootElement.GetProperty("items").EnumerateArray())
{
    var number = item.GetProperty("number").GetInt32();
    var path = FormattableString.Invariant($"/repos/{repository}/issues/{number}");
    using var response = await client.GetAsync(path, cancellationToken);
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    var thread = document.RootElement;
    // Search is indexed asynchronously. Recheck the current state before posting or locking.
    if (thread.GetProperty("locked").GetBoolean() || thread.GetProperty("state").GetString() != "closed"
        || thread.GetProperty("updated_at").GetDateTimeOffset() >= cutoff)
    {
        continue;
    }

    var kind = thread.TryGetProperty("pull_request", out _) ? "pull request" : "issue";
    var body = $"This {kind} has been automatically locked since there has not been any recent activity after it was closed. Please open a new issue for related bugs.";
    using var content = new StringContent(new JsonObject { ["body"] = body }.ToJsonString(), Encoding.UTF8, "application/json");
    using var comment = await client.PostAsync($"{path}/comments", content, cancellationToken);
    comment.EnsureSuccessStatusCode();
    using var locked = await client.PutAsync($"{path}/lock", null, cancellationToken);
    locked.EnsureSuccessStatusCode();
    Console.WriteLine(FormattableString.Invariant($"Locked {kind} #{number}."));
}
