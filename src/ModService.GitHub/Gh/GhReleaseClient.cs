using System.Net;
using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModService.Core.Configuration;
using ModService.Core.Updates;
using ModService.GitHub.Auth;

namespace ModService.GitHub.Gh;

[SupportedOSPlatform("windows")]
public sealed class GhReleaseClient : IGitHubReleaseClient
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly GitHubTokenStore? _tokenStore;
    private readonly HttpClient _httpClient;

    public GhReleaseClient()
        : this(tokenStore: null, httpClient: null)
    {
    }

    public GhReleaseClient(GitHubTokenStore tokenStore)
        : this(tokenStore, httpClient: null)
    {
    }

    public GhReleaseClient(HttpClient httpClient)
        : this(tokenStore: null, httpClient)
    {
    }

    public GhReleaseClient(GitHubTokenStore? tokenStore, HttpClient? httpClient)
    {
        _tokenStore = tokenStore;
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public async Task<IReadOnlyList<GitHubReleaseAsset>> GetReleaseAssetsAsync(SourceConfiguration source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var request = CreateRequest(
            HttpMethod.Get,
            $"https://api.github.com/repos/{source.Repo}/releases/tags/{Uri.EscapeDataString(source.Tag)}",
            MediaTypeWithQualityHeaderValue.Parse("application/vnd.github+json"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, source, cancellationToken);

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var release = await JsonSerializer.DeserializeAsync<GitHubReleaseResponse>(contentStream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"GitHub returned an empty release document for '{source.Repo}' tag '{source.Tag}'.");

        return release.Assets
            .Select(asset => new GitHubReleaseAsset
            {
                Id = asset.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name = asset.Name ?? string.Empty,
                Size = asset.Size,
                UpdatedAt = asset.UpdatedAt,
                ApiUrl = asset.Url ?? string.Empty,
                DownloadUrl = asset.BrowserDownloadUrl ?? string.Empty
            })
            .ToArray();
    }

    public async Task<string> DownloadAssetAsync(
        SourceConfiguration source,
        GitHubReleaseAsset asset,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (string.IsNullOrWhiteSpace(asset.ApiUrl) && string.IsNullOrWhiteSpace(asset.DownloadUrl))
        {
            throw new InvalidOperationException(
                $"GitHub asset '{asset.Name}' from '{source.Repo}' tag '{source.Tag}' did not include a download URL.");
        }

        Directory.CreateDirectory(destinationDirectory);

        var filePath = Path.Combine(destinationDirectory, asset.Name);
        var tempPath = filePath + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        var requestUri = !string.IsNullOrWhiteSpace(asset.ApiUrl) ? asset.ApiUrl : asset.DownloadUrl;
        using var request = CreateRequest(
            HttpMethod.Get,
            requestUri,
            MediaTypeWithQualityHeaderValue.Parse("application/octet-stream"));

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, source, cancellationToken, asset.Name);

        await using (var remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var localStream = File.Create(tempPath))
        {
            await remoteStream.CopyToAsync(localStream, cancellationToken);
        }

        File.Move(tempPath, filePath, overwrite: true);
        return filePath;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri, MediaTypeWithQualityHeaderValue accept)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Add(accept);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ModService", "1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        var token = _tokenStore?.TryLoadToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        SourceConfiguration source,
        CancellationToken cancellationToken,
        string? assetName = null)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken);
        var trimmedDetail = string.IsNullOrWhiteSpace(detail)
            ? string.Empty
            : $"{Environment.NewLine}{detail.Trim()}";

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            var target = assetName is null ? $"release tag '{source.Tag}'" : $"asset '{assetName}'";
            throw new FileNotFoundException(
                $"GitHub {target} was not found for repository '{source.Repo}'.{trimmedDetail}");
        }

        throw new InvalidOperationException(
            $"GitHub request for '{source.Repo}' tag '{source.Tag}' failed with {(int)response.StatusCode} ({response.ReasonPhrase}).{trimmedDetail}");
    }

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    private sealed class GitHubReleaseResponse
    {
        public List<GitHubReleaseAssetResponse> Assets { get; set; } = [];
    }

    private sealed class GitHubReleaseAssetResponse
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public long Size { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }

        public string? Url { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
