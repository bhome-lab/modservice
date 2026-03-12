using System.Net;
using System.Net.Http;
using System.Text;
using ModService.Core.Configuration;
using ModService.Core.Updates;
using ModService.GitHub.Gh;

namespace ModService.Tests;

public sealed class GhReleaseClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModServiceGhClientTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetReleaseAssetsAsync_AndDownloadAssetAsync_UseGitHubApi()
    {
        Directory.CreateDirectory(_root);

        const string assetApiUrl = "https://api.github.com/repos/owner/repo/releases/assets/42";
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/owner/repo/releases/tags/latest")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "assets": [
                            {
                              "id": 42,
                              "name": "bundle.dll",
                              "size": 5,
                              "updated_at": "2026-03-10T10:00:00Z",
                              "url": "https://api.github.com/repos/owner/repo/releases/assets/42",
                              "browser_download_url": "https://github.com/owner/repo/releases/download/latest/bundle.dll"
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json")
                };
            }

            if (request.RequestUri?.AbsoluteUri == assetApiUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var client = new GhReleaseClient(httpClient);
        var source = new SourceConfiguration
        {
            Id = "repo",
            Repo = "owner/repo",
            Tag = "latest"
        };

        var assets = await client.GetReleaseAssetsAsync(source, CancellationToken.None);
        var asset = Assert.Single(assets);
        Assert.Equal("42", asset.Id);
        Assert.Equal("bundle.dll", asset.Name);
        Assert.Equal(assetApiUrl, asset.ApiUrl);

        var downloadedPath = await client.DownloadAssetAsync(source, asset, _root, CancellationToken.None);
        Assert.Equal("hello", await File.ReadAllTextAsync(downloadedPath, CancellationToken.None));
    }

    [Fact]
    public async Task GetReleaseAssetsAsync_ThrowsGitHubRateLimitException_WhenGitHubReturnsRateLimit()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/owner/repo/releases/tags/latest")
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"message\":\"rate limit exceeded\"}", Encoding.UTF8, "application/json")
                };
                response.Headers.Add("X-RateLimit-Limit", "60");
                response.Headers.Add("X-RateLimit-Remaining", "0");
                response.Headers.Add("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds().ToString());
                response.Headers.Add("X-RateLimit-Resource", "core");
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var client = new GhReleaseClient(httpClient);
        var source = new SourceConfiguration
        {
            Id = "repo",
            Repo = "owner/repo",
            Tag = "latest"
        };

        var exception = await Assert.ThrowsAsync<GitHubRateLimitException>(() => client.GetReleaseAssetsAsync(source, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal(60, exception.Limit);
        Assert.Equal(0, exception.Remaining);
        Assert.Equal("core", exception.Scope);
        Assert.NotNull(exception.ResetAtUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
