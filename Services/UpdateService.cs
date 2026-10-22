using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GetSoundBoard.Models;

namespace GetSoundBoard.Services;

public sealed class UpdateService
{
    private static readonly HttpClient Client = CreateClient();
    private readonly Version _currentVersion;

    public string CurrentVersionText => $"Versão instalada: v{_currentVersion}";

    public UpdateService()
    {
        var informationalVersion = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var versionText = informationalVersion?.Split('+', 2)[0];
        _currentVersion = Version.TryParse(versionText, out var parsed)
            ? parsed
            : new Version(0, 1, 5);
    }

    public async Task<IReadOnlyList<AppRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(
            "https://api.github.com/repos/gwennocas/getSoundBoard/releases?per_page=30",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<GitHubRelease>>(stream, JsonOptions, cancellationToken)
            ?? [];

        return releases
            .Where(release => !release.Draft && !release.Prerelease && TryParseVersion(release.TagName, out _))
            .Select(ToAppRelease)
            .OrderByDescending(release => ParseVersion(release.Version))
            .ToList();
    }

    private AppRelease ToAppRelease(GitHubRelease release)
    {
        var version = ParseVersion(release.TagName);
        var status = version == _currentVersion
            ? "Versão instalada"
            : version > _currentVersion
                ? "Atualização disponível"
                : "Versão anterior";

        var installer = release.Assets.FirstOrDefault(asset =>
            asset.Name.StartsWith("getSoundBoard-", StringComparison.OrdinalIgnoreCase)
            && asset.Name.EndsWith("-win-x64-setup.exe", StringComparison.OrdinalIgnoreCase)
            && IsTrustedDownload(asset.BrowserDownloadUrl));

        var downloadUrl = installer?.BrowserDownloadUrl;
        if (downloadUrl is null && !IsTrustedReleasePage(release.HtmlUrl))
            downloadUrl = "https://github.com/gwennocas/getSoundBoard/releases";
        else if (downloadUrl is null)
            downloadUrl = release.HtmlUrl;

        var notes = string.IsNullOrWhiteSpace(release.Body)
            ? "Esta versão não tem notas de lançamento."
            : release.Body.Trim();
        if (notes.Length > 1200)
            notes = notes[..1200].TrimEnd() + "…";

        var publishedAt = DateTimeOffset.TryParse(release.PublishedAt, out var published)
            ? published.ToLocalTime().ToString("d")
            : "Data indisponível";

        return new AppRelease
        {
            Version = release.TagName,
            PublishedAt = $"Publicada em {publishedAt}",
            StatusLabel = status,
            Notes = notes,
            DownloadUrl = downloadUrl,
            DownloadButtonText = installer is null ? "Ver lançamento" : "Baixar instalador"
        };
    }

    private static bool IsTrustedDownload(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/gwennocas/getSoundBoard/releases/download/", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrustedReleasePage(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith("/gwennocas/getSoundBoard/releases/", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseVersion(string? value, out Version version)
    {
        if (Version.TryParse(value?.TrimStart('v', 'V'), out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0);
        return false;
    }

    private static Version ParseVersion(string value)
    {
        _ = Version.TryParse(value.TrimStart('v', 'V'), out var version);
        return version ?? new Version(0, 0);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var version = CurrentAppVersion().Split('+', 2)[0];
        if (!Version.TryParse(version, out _))
            version = "0.1";
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("getSoundBoard", version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string CurrentAppVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.1.5";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = "";

        [JsonPropertyName("published_at")]
        public string? PublishedAt { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; init; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = "";
    }
}
