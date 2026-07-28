using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DeskHush.Core.Interfaces;
using DeskHush.Core.Models;

namespace DeskHush.Core.Services;

public sealed class GitHubReleaseUpdateChecker : IUpdateChecker
{
    private static readonly Uri LatestReleaseEndpoint = new(
        "https://api.github.com/repos/sanrokamlan-prog/DeskHush/releases/latest");

    private readonly HttpClient httpClient;

    public GitHubReleaseUpdateChecker(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<UpdateInfo?> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd($"DeskHush/{NormalizeVersion(currentVersion)}");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagElement) ||
            !root.TryGetProperty("html_url", out var urlElement))
        {
            return null;
        }

        var tagName = tagElement.GetString()?.Trim();
        var releaseUrl = urlElement.GetString()?.Trim();
        if (!TryParseReleaseVersion(tagName, out var releaseVersion) ||
            releaseVersion <= currentVersion ||
            !Uri.TryCreate(releaseUrl, UriKind.Absolute, out var releaseUri) ||
            !releaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !releaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new UpdateInfo(releaseVersion, tagName!, releaseUri);
    }

    public static bool TryParseReleaseVersion(string? tagName, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return false;
        }

        var candidate = tagName.Trim();
        if (candidate.StartsWith('v') || candidate.StartsWith('V'))
        {
            candidate = candidate[1..];
        }

        var suffixIndex = candidate.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            candidate = candidate[..suffixIndex];
        }

        return Version.TryParse(candidate, out version!);
    }

    private static string NormalizeVersion(Version version) =>
        $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
}
