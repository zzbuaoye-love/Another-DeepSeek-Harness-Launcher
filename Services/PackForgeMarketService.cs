using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnotherDSHL.Services;

public sealed record MarketPack(
    string Name, string Title, string Description, string Author, string Version,
    string Type, int ManifestVersion, string DshVersion, string Owner, string Repo,
    string DownloadUrl, string Sha256, long Size, int BundleCount, int DepCount,
    int ProfileCount)
{
    public string Summary => $"{Author} · v{Version} · {(Type == "dshhome" ? "DSH_HOME" : "Profile")}";
    public bool IsDspack => Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var uri) &&
                            uri.Scheme == Uri.UriSchemeHttps &&
                            uri.AbsolutePath.EndsWith(".dspack", StringComparison.OrdinalIgnoreCase);
    public bool CanDownload => IsDspack && Size is > 0 and <= 1_000_000_000 &&
                               Regex.IsMatch(Sha256, "^[a-fA-F0-9]{64}$");
    public string RepositoryUrl => $"https://github.com/{Owner}/{Repo}";
}

public static class PackForgeMarketService
{
    public const string MarketUrl = "https://dsh-packforge.github.io/dsh-pack-market/index.json";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly Regex GitHubName = new("^[A-Za-z0-9_.-]+$", RegexOptions.Compiled);
    private static readonly Regex Hash = new("^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    public static async Task<IReadOnlyList<MarketPack>> LoadAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await Client.GetAsync(MarketUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2_000_000)
            throw new InvalidDataException("市场索引过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream,
            new JsonDocumentOptions { MaxDepth = 32 }, timeout.Token);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 2 ||
            !root.TryGetProperty("modpacks", out var packs) || packs.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("市场索引格式不是受支持的 schemaVersion 2。");

        var result = new List<MarketPack>();
        foreach (var entry in packs.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var owner = String(entry, "owner");
            var repo = String(entry, "repo");
            var url = String(entry, "downloadUrl");
            var sha = String(entry, "sha256");
            var size = Number(entry, "size");
            if (!GitHubName.IsMatch(owner) || !GitHubName.IsMatch(repo))
                continue;
            var name = String(entry, "name");
            var title = Localized(entry, "displayName", name);
            result.Add(new MarketPack(name, title, Localized(entry, "description", ""),
                String(entry, "author"), String(entry, "version"), String(entry, "type", "profile"),
                (int)Number(entry, "manifestVersion"), String(entry, "dshVersion"), owner, repo,
                url, sha, size, (int)Number(entry, "bundleCount"), (int)Number(entry, "depCount"),
                (int)Number(entry, "profileCount")));
        }
        return result;
    }

    public static async Task<string> DownloadVerifiedAsync(MarketPack pack, CancellationToken cancellationToken = default)
    {
        if (!pack.CanDownload)
            throw new InvalidDataException("此条目不支持通过管理器打开。");
        var existing = await TryGetVerifiedLocalPathAsync(pack, cancellationToken);
        if (existing is not null) return existing;
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnotherDSHL", "Packs");
        Directory.CreateDirectory(folder);
        var destination = GetLocalPath(pack);
        var temporary = destination + ".download";
        try
        {
            using var response = await Client.GetAsync(pack.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("下载地址跳转到了非 HTTPS 页面。");
            if (response.Content.Headers.ContentLength is long length && length != pack.Size)
                throw new InvalidDataException("下载文件大小与市场索引不符。");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > pack.Size)
                    throw new InvalidDataException("下载文件大小超出市场索引记录。");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
            if (total != pack.Size ||
                !Convert.ToHexString(hash.GetHashAndReset()).Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("整合包大小或 SHA-256 与市场索引不符。");
            output.Close();
            File.Move(temporary, destination, true);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static string GetLocalPath(MarketPack pack) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnotherDSHL", "Packs",
        $"{SafeName(pack.Owner)}.{SafeName(pack.Repo)}-{SafeName(pack.Version)}.dspack");

    public static async Task<string?> TryGetVerifiedLocalPathAsync(MarketPack pack,
        CancellationToken cancellationToken = default)
    {
        if (!pack.CanDownload) return null;
        var path = GetLocalPath(pack);
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != pack.Size) return null;
        var hash = await SHA256.HashDataAsync(file, cancellationToken);
        return Convert.ToHexString(hash).Equals(pack.Sha256, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string SafeName(string value) => Regex.Replace(value, "[^A-Za-z0-9_.-]", "-");

    private static string String(JsonElement value, string name, string fallback = "") =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback : fallback;

    private static long Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt64(out var number) ? number : 0;

    private static string Localized(JsonElement value, string name, string fallback)
    {
        if (!value.TryGetProperty(name, out var property)) return fallback;
        if (property.ValueKind == JsonValueKind.String) return property.GetString() ?? fallback;
        if (property.ValueKind != JsonValueKind.Object) return fallback;
        foreach (var language in new[] { "zh-CN", "zh", "en-US", "en" })
            if (property.TryGetProperty(language, out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString() ?? fallback;
        foreach (var text in property.EnumerateObject())
            if (text.Value.ValueKind == JsonValueKind.String)
                return text.Value.GetString() ?? fallback;
        return fallback;
    }
}
