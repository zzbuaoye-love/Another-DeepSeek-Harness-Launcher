using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace AnotherDSHL.Services;

internal sealed record NpmRegistry(string Id, string Name, string Url);

internal sealed class NpmRegistryService
{
    public static readonly NpmRegistry Official = new("Official", "npm 官方", "https://registry.npmjs.org/");
    public static readonly NpmRegistry[] Presets = [Official,
        new("Aliyun", "阿里 npmmirror", "https://registry.npmmirror.com/")];
    private readonly HttpClient _client;
    private NpmRegistry? _cached;
    private DateTimeOffset _checkedAt;
    private string _cachedChoice = "";
    public NpmRegistryService(HttpClient? client = null) => _client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    public static string NormalizeMode(string mode) => mode is "Auto" or "Custom" ||
        Presets.Any(source => source.Id == mode) ? mode : "Auto";

    public async Task<NpmRegistry> SelectAsync(string mode, string customUrl, Action<string>? log = null,
        CancellationToken token = default, bool force = false)
    {
        mode = NormalizeMode(mode);
        var choice = mode + "|" + customUrl;
        if (!force && _cached is not null && choice == _cachedChoice && DateTimeOffset.UtcNow - _checkedAt < TimeSpan.FromMinutes(5))
        {
            log?.Invoke($"[ADL] 复用最近检测的 npm 源：{_cached.Name}（5 分钟内）。");
            return _cached;
        }
        if (mode == "Auto")
        {
            log?.Invoke("[ADL] 正在并行检测 npm 源（读取 DSH 包信息，最多 8 秒）…");
            var probes = await Task.WhenAll(Presets.Select(source => ProbeAsync(source, token)));
            foreach (var probe in probes)
                log?.Invoke($"[ADL] {probe.Source.Name}：{(probe.Error is null ? $"{probe.Milliseconds} ms" : probe.Error)}");
            var best = probes.Where(probe => probe.Error is null).OrderBy(probe => probe.Milliseconds).FirstOrDefault();
            var result = best.Source ?? Official;
            log?.Invoke($"[ADL] npm 源：{result.Name} · {result.Url}");
            if (best.Source is not null) Cache(result, choice);
            return result;
        }
        var selected = mode == "Custom"
            ? new NpmRegistry("Custom", "自定义源", NormalizeCustomUrl(customUrl))
            : Presets.FirstOrDefault(source => source.Id == mode) ?? Official;
        if (selected == Official) return Official;
        var check = await ProbeAsync(selected, token);
        if (check.Error is null) { Cache(selected, choice); return selected; }
        log?.Invoke($"[ADL] {selected.Name} 不可用（{check.Error}），回退 npm 官方源。");
        return Official;
    }

    private void Cache(NpmRegistry result, string choice)
    {
        _cached = result;
        _cachedChoice = choice;
        _checkedAt = DateTimeOffset.UtcNow;
    }

    private async Task<(NpmRegistry Source, long Milliseconds, string? Error)> ProbeAsync(NpmRegistry source, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var response = await _client.GetAsync(source.Url + "@deepseek-ai%2Fdsh/latest", timeout.Token);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            if (!document.RootElement.TryGetProperty("version", out var version) || !DshVersionService.IsSafeVersion(version.GetString()))
                throw new FormatException("未返回有效 npm 包信息");
            return (source, elapsed.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or FormatException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            return (source, elapsed.ElapsedMilliseconds, ex is OperationCanceledException ? "检测超时" : ex.Message);
        }
    }

    public static string NormalizeCustomUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("请输入不含账号、查询参数的 HTTPS npm registry 地址。");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }

    public static void Apply(ProcessStartInfo start, NpmRegistry registry)
    {
        start.Environment["npm_config_registry"] = registry.Url;
        start.Environment["npm_config_loglevel"] = "http";
        start.Environment["npm_config_progress"] = "false";
        start.Environment["npm_config_foreground_scripts"] = "true";
        start.Environment["npm_config_prefer_offline"] = "true";
        start.Environment["npm_config_fetch_retries"] = "1";
        start.Environment["npm_config_fetch_timeout"] = "20000";
        start.Environment["npm_config_fetch_retry_mintimeout"] = "1000";
        start.Environment["npm_config_fetch_retry_maxtimeout"] = "3000";
    }
}
