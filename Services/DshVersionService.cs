using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnotherDSHL.Services;

internal sealed record DshVersionCatalog(string Latest, IReadOnlyList<string> Versions);

internal static class DshVersionService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Regex VersionPattern = new(
        @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled);

    public static bool IsSafeVersion(string? version) =>
        version is { Length: > 0 } && VersionPattern.IsMatch(version);

    public static async Task<DshVersionCatalog> GetAvailableAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://registry.npmjs.org/@deepseek-ai%2Fdsh");
        request.Headers.Accept.ParseAdd("application/vnd.npm.install-v1+json");
        using var response = await Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var latest = root.GetProperty("dist-tags").GetProperty("latest").GetString();
        if (!IsSafeVersion(latest) || !root.TryGetProperty("versions", out var versions) ||
            versions.ValueKind != JsonValueKind.Object)
            throw new FormatException("npm 未返回可用的 DSH 版本信息。");
        var recent = versions.EnumerateObject().Select(item => item.Name)
            .Where(IsSafeVersion).Reverse().Take(25).ToList();
        if (!recent.Contains(latest!, StringComparer.Ordinal)) recent.Insert(0, latest!);
        return new DshVersionCatalog(latest!, recent);
    }
}
