using System.Net;
using System.Net.Http;
using System.Text;
using DeskHush.Core.Services;

namespace DeskHush.Tests;

internal static class UpdateCheckerTests
{
    internal static Task TestVersionParsing()
    {
        Assert(GitHubReleaseUpdateChecker.TryParseReleaseVersion("v1.2.3", out var stable) &&
               stable == new Version(1, 2, 3), "Stable release tags should parse.");
        Assert(GitHubReleaseUpdateChecker.TryParseReleaseVersion("V2.0.1-beta.1", out var preview) &&
               preview == new Version(2, 0, 1), "Release suffixes should not affect numeric comparison.");
        Assert(!GitHubReleaseUpdateChecker.TryParseReleaseVersion("latest", out _),
            "Non-version tags should be rejected.");
        return Task.CompletedTask;
    }

    internal static async Task TestNewerReleaseDetection()
    {
        var handler = new StubHandler(request =>
        {
            Assert(request.Headers.UserAgent.ToString().StartsWith("DeskHush/1.0.0", StringComparison.Ordinal),
                "Update requests should identify the application version.");
            return JsonResponse("""
                {"tag_name":"v1.1.0","html_url":"https://github.com/sanrokamlan-prog/DeskHush/releases/tag/v1.1.0"}
                """);
        });
        var checker = new GitHubReleaseUpdateChecker(new HttpClient(handler));

        var update = await checker.CheckAsync(new Version(1, 0, 0));

        Assert(update?.Version == new Version(1, 1, 0), "A newer stable release should be returned.");
        Assert(update?.ReleaseUri.Host == "github.com", "The release link should remain on github.com.");
    }

    internal static async Task TestCurrentAndInvalidReleaseRejection()
    {
        var equalChecker = new GitHubReleaseUpdateChecker(new HttpClient(new StubHandler(_ => JsonResponse("""
            {"tag_name":"v1.0.0","html_url":"https://github.com/sanrokamlan-prog/DeskHush/releases/tag/v1.0.0"}
            """))));
        Assert(await equalChecker.CheckAsync(new Version(1, 0, 0)) is null,
            "The installed version should not be reported as an update.");

        var invalidChecker = new GitHubReleaseUpdateChecker(new HttpClient(new StubHandler(_ => JsonResponse("""
            {"tag_name":"v9.0.0","html_url":"https://example.com/not-a-release"}
            """))));
        Assert(await invalidChecker.CheckAsync(new Version(1, 0, 0)) is null,
            "Untrusted release links should be rejected.");
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }
}
