using System.Net.Http;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnotherDSHL.Services;

internal sealed record CatalogPlugin(
    string Name, string Version, string Description, string Source,
    string InstallSpec, string? Homepage, bool IsOfficial,
    string Publisher = "", string Category = "other", int? Stars = null,
    DateTimeOffset? PublishedAt = null, string? IconUrl = null,
    string? RepositoryUrl = null, string PublisherKind = "")
{
    private static readonly HashSet<string> GenericSubfolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dsh", "dsh-plugin", "plugin", "bundle", "deepseek-harness",
        "dsh-runtime", "typescript", "coding-agents", "entry", "agent", "packages"
    };

    public string DisplayName
    {
        get
        {
            var raw = Name.Contains('/') ? Name[(Name.LastIndexOf('/') + 1)..] : Name;
            if (!GenericSubfolderNames.Contains(raw)) return raw;
            if (!InstallSpec.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
            {
                var pkg = InstallSpec.Contains('/') ? InstallSpec[(InstallSpec.LastIndexOf('/') + 1)..] : InstallSpec;
                if (!string.IsNullOrWhiteSpace(pkg) && !GenericSubfolderNames.Contains(pkg))
                    return pkg;
            }
            if (RepositoryLabel is { } repo && repo.Split('/') is [_, var repoName] && !string.IsNullOrWhiteSpace(repoName))
                return $"{repoName} ({raw})";
            return raw;
        }
    }

    public string? NpmScope => InstallSpec.StartsWith('@') ? InstallSpec[1..].Split('/')[0] : null;
    public string? RepositoryLabel => RepositoryUrl is null ? null :
        string.Join('/', new Uri(RepositoryUrl).AbsolutePath.Trim('/').Split('/').Take(2));

    public string PublisherLine => PublisherKind == "discussion" && !string.IsNullOrWhiteSpace(Publisher)
        ? $"帖子作者 @{Publisher.TrimStart('@')}  ·  {(RepositoryLabel is { } postRepo ? $"仓库 {postRepo}" : $"安装包 {InstallSpec}")}"
        : RepositoryLabel is { } repo
            ? !InstallSpec.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
                ? $"安装包 {InstallSpec}  ·  仓库 {repo}"
                : $"仓库 {repo}  ·  {CategoryLabel}"
            : !string.IsNullOrWhiteSpace(Publisher)
                ? $"作者 @{Publisher.TrimStart('@')}  ·  {Source}"
                : $"{CategoryLabel}  ·  {Source}";

    public string DetailSourceLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(InstallSpec))
                parts.Add($"安装包：{InstallSpec}");
            if (RepositoryLabel is { } repo)
                parts.Add($"仓库：{repo}");
            if (PublisherKind == "discussion" && !string.IsNullOrWhiteSpace(Publisher))
                parts.Add($"帖子作者：@{Publisher.TrimStart('@')}");
            else if (RepositoryLabel is null && !string.IsNullOrWhiteSpace(Publisher))
                parts.Add($"作者：@{Publisher.TrimStart('@')}");
            parts.Add($"来源：{Source}");
            return string.Join("  ·  ", parts);
        }
    }

    public string CategoryLabel => Category switch
    {
        "tools" => "工具", "ui" => "界面", "session" => "会话", "skill" => "技能",
        "theme" => "主题", "workflow" => "工作流", "model" => "模型", "dev" => "开发",
        "fun" => "趣味", "memory" => "记忆", "notify" => "通知", "core" => "核心",
        _ => "其他"
    };
    public string PopularityLabel => Stars is > 0 ? $"★ {Stars:N0}" : "";
    public ImageSource IconImage => IconUrl is { Length: > 0 } url &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        ? uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? new SvgImageSource(uri) : new BitmapImage(uri)
        : IsOfficial ? new SvgImageSource(new Uri("ms-appx:///Assets/DeepSeekHarness.svg"))
        : new SvgImageSource(new Uri("ms-appx:///Assets/Icons/puzzle.svg"));
}

internal static class PluginCatalogService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly Regex PackageName = new(@"^(@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$", RegexOptions.Compiled);
    private static readonly Regex GitHubSpec = new(@"^github:[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:#[A-Za-z0-9_./:-]+)?$", RegexOptions.Compiled);

    static PluginCatalogService() => Client.DefaultRequestHeaders.UserAgent.ParseAdd("AnotherDSHL/1.0");

    public static bool IsSafeInstallSpec(string spec) => PackageName.IsMatch(spec) || GitHubSpec.IsMatch(spec);

    public static Task<IReadOnlyList<CatalogPlugin>> GetCommunityCatalogAsync() =>
        ReadCatalogAsync("https://deepseek1024.com/api/v1/registry", "社区目录 · 1024Store");

    public static async Task<IReadOnlyList<CatalogPlugin>> GetCommunityAsync()
    {
        const string feedUrl = "https://github.com/deepseek-ai/deepseek-harness/discussions/categories/show-your-plugins.atom";
        using var response = await Client.GetAsync(feedUrl);
        response.EnsureSuccessStatusCode();
        var document = XDocument.Parse(await response.Content.ReadAsStringAsync());
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var results = new List<CatalogPlugin>();
        foreach (var entry in document.Root?.Elements(atom + "entry") ?? Enumerable.Empty<XElement>())
        {
            var title = entry.Element(atom + "title")?.Value.Trim();
            var link = entry.Elements(atom + "link").Select(element => (string?)element.Attribute("href"))
                .FirstOrDefault(value => value?.StartsWith("https://github.com/deepseek-ai/deepseek-harness/discussions/", StringComparison.Ordinal) == true);
            if (string.IsNullOrWhiteSpace(title) || link is null) continue;
            var html = entry.Element(atom + "content")?.Value ?? "";
            var plain = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " "));
            plain = Regex.Replace(plain, @"\s+", " ").Trim();
            var match = Regex.Match(plain, @"\bdsh\s+plugin\s+--profile\s+web\s+add\s+(?:-w\s+)?(?<spec>github:[A-Za-z0-9_.:/#-]+|@?[a-z0-9][a-z0-9._/-]*)", RegexOptions.IgnoreCase);
            var spec = match.Success ? match.Groups["spec"].Value : "";
            if (!IsSafeInstallSpec(spec) || spec is "file" or "package" or "plugin" or "example") continue;
            var name = spec.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
                ? spec[7..].Split('#')[0].Split('/').Last() : spec;
            var publisher = entry.Element(atom + "author")?.Element(atom + "name")?.Value.Trim() ?? "";
            results.Add(new CatalogPlugin(name, "社区帖子", plain.Length > 220 ? plain[..220] + "…" : plain,
                "官方社区 · 作者发布", spec, link, false, publisher, "other",
                PublishedAt: DateTimeOffset.TryParse(entry.Element(atom + "published")?.Value, out var published) ? published : null,
                RepositoryUrl: RepositoryFromInstallSpec(spec), PublisherKind: "discussion"));
        }
        return results;
    }

    public static async Task<IReadOnlyList<CatalogPlugin>> GetOfficialAsync()
    {
        // The upstream bundle directory is the authoritative list. npm supplies published versions.
        using var response = await Client.GetAsync("https://api.github.com/repos/deepseek-ai/deepseek-harness/contents/packages/bundle");
        response.EnsureSuccessStatusCode();
        using var listing = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var directories = listing.RootElement.EnumerateArray()
            .Where(item => Read(item, "type") == "dir")
            .Select(item => Read(item, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();

        var results = await Task.WhenAll(directories.Select(LoadOfficialBundleAsync));
        var available = results.OfType<CatalogPlugin>().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (available.Length == 0 && directories.Length > 0)
            throw new HttpRequestException("官方 bundle 元数据暂时不可用。");
        return available;
    }

    private static async Task<CatalogPlugin?> LoadOfficialBundleAsync(string? directory)
    {
        if (directory is null) return null;
        try
        {
            var uri = $"https://raw.githubusercontent.com/deepseek-ai/deepseek-harness/master/packages/bundle/{Uri.EscapeDataString(directory!)}/package.json";
            using var packageResponse = await Client.GetAsync(uri);
            if (!packageResponse.IsSuccessStatusCode) return null;
            using var package = JsonDocument.Parse(await packageResponse.Content.ReadAsStringAsync());
            var root = package.RootElement;
            var name = Read(root, "name");
            if (name is null || !name.StartsWith("@deepseek-ai/dsh-", StringComparison.Ordinal) || !IsSafeInstallSpec(name) ||
                !root.TryGetProperty("dsh", out var dsh) || !dsh.TryGetProperty("bundle", out _)) return null;

            // An unpublished source package is shown only when npm confirms an installable version.
            using var npmResponse = await Client.GetAsync($"https://registry.npmjs.org/{Uri.EscapeDataString(name)}/latest");
            if (!npmResponse.IsSuccessStatusCode) return null;
            using var published = JsonDocument.Parse(await npmResponse.Content.ReadAsStringAsync());
            var version = Read(published.RootElement, "version");
            if (string.IsNullOrWhiteSpace(version)) return null;
            return new CatalogPlugin(name, version, Read(published.RootElement, "description") ??
                Read(root, "description") ?? "官方 DSH bundle", "DeepSeek 官方", name,
                $"https://github.com/deepseek-ai/deepseek-harness/tree/master/packages/bundle/{directory}", true,
                "deepseek-ai", "core", RepositoryUrl: "https://github.com/deepseek-ai/deepseek-harness");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    public static Task<IReadOnlyList<CatalogPlugin>> GetCustomAsync(string url) =>
        ReadCatalogAsync(url, "自定义目录");

    private static async Task<IReadOnlyList<CatalogPlugin>> ReadCatalogAsync(string url, string source)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("目录地址必须是 HTTPS URL。");
        using var response = await Client.GetAsync(uri);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!document.RootElement.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
            throw new FormatException("目录 JSON 需要 plugins 数组。");
        var results = new List<CatalogPlugin>();
        foreach (var entry in plugins.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var name = Read(entry, "name") ?? Read(entry, "id");
            var spec = Read(entry, "installSpec") ?? Read(entry, "target") ?? ParseInstallCommand(Read(entry, "install"));
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(spec) || !IsSafeInstallSpec(spec)) continue;
            var homepage = Read(entry, "url");
            if (homepage is not null && (!Uri.TryCreate(homepage, UriKind.Absolute, out var homeUri) || homeUri.Scheme != Uri.UriSchemeHttps))
                homepage = null;
            var icon = Read(entry, "icon") ?? Read(entry, "iconUrl") ?? Read(entry, "logo");
            if (icon is not null)
            {
                if (Uri.TryCreate(uri, icon, out var iconUri) && iconUri.Scheme == Uri.UriSchemeHttps)
                    icon = iconUri.ToString();
                else
                    icon = null;
            }
            // A github: install target identifies the installed repository even if a catalog URL differs.
            var repository = RepositoryFromInstallSpec(spec) ?? NormalizeGitHubRepositoryUrl(homepage);
            var owner = Read(entry, "owner");
            results.Add(new CatalogPlugin(name, Read(entry, "version") ?? "版本以安装时为准",
                ReadLocalized(entry, "description") ?? "暂无描述", source == "自定义目录" ? $"自定义 · {uri.Host}" : source,
                spec, homepage, false, owner ?? Read(entry, "publisher") ?? "",
                Read(entry, "category") ?? "other",
                entry.TryGetProperty("stars", out var stars) && stars.ValueKind == JsonValueKind.Number &&
                stars.TryGetInt32(out var count) ? count : null,
                DateTimeOffset.TryParse(Read(entry, "added"), out var added) ? added : null, icon,
                repository, owner is null ? "author" : "repository"));
        }
        return results;
    }

    private static string? ParseInstallCommand(string? command)
    {
        if (command is null) return null;
        var parts = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 6 && parts[0] == "dsh" && parts[1] == "plugin" && parts[2] == "--profile" &&
               parts[3] == "web" && parts[4] == "add" ? parts[5] == "-w" && parts.Length > 6 ? parts[6] : parts[5] : null;
    }

    private static string? RepositoryFromInstallSpec(string spec)
    {
        if (!GitHubSpec.IsMatch(spec)) return null;
        return NormalizeGitHubRepositoryUrl($"https://github.com/{spec[7..].Split('#')[0]}");
    }

    private static string? NormalizeGitHubRepositoryUrl(string? candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts.Take(2).Any(part =>
            !Regex.IsMatch(part, "^[A-Za-z0-9_.-]+$") || part is "." or "..")) return null;
        var repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return string.IsNullOrWhiteSpace(repo) ? null : $"https://github.com/{parts[0]}/{repo}";
    }

    private static string? ReadLocalized(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var value)) return null;
        return value.ValueKind == JsonValueKind.Object ? Read(value, "zh") ?? Read(value, "en") :
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? Read(JsonElement root, string key) => root.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
