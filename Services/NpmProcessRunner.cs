using System.Diagnostics;
using System.Text;

namespace AnotherDSHL.Services;

internal static class NpmProcessRunner
{
    public static async Task RunAsync(string node, string cli, IEnumerable<string> arguments, string directory,
        NpmRegistry registry, Action<string>? log, CancellationToken token, string? dshHome = null, string? npmCache = null)
    {
        var start = new ProcessStartInfo(node) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(cli);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = Path.GetDirectoryName(node) + Path.PathSeparator + start.Environment["PATH"];
        NpmRegistryService.Apply(start, registry);
        if (dshHome is not null) start.Environment["DSH_HOME"] = dshHome;
        if (npmCache is not null)
        {
            start.Environment["npm_config_cache"] = npmCache;
            // npm itself rejects pnpm's store-dir setting; DSH/pnpm children can use it.
            if (!Path.GetFileName(cli).Equals("npm-cli.js", StringComparison.OrdinalIgnoreCase))
                start.Environment["npm_config_store_dir"] = Path.Combine(npmCache, "pnpm-store");
        }
        using var process = new Process { StartInfo = start };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = DrainAsync(process.StandardOutput, line => log?.Invoke("[npm] " + line), timeout.Token);
        var error = DrainAsync(process.StandardError, line => log?.Invoke("[npm] " + line), timeout.Token);
        var watch = HeartbeatAsync(process, log, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error);
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException($"npm 退出代码 {process.ExitCode}，请查看运行日志。");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
            token.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("npm 准备资源超过 10 分钟，已停止；可更换源后重试。");
            throw;
        }
        finally
        {
            timeout.Cancel();
            try { await watch; } catch (OperationCanceledException) { }
        }
    }

    private static async Task HeartbeatAsync(Process process, Action<string>? log, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (!process.HasExited)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            if (!process.HasExited) log?.Invoke($"[ADL] npm 正在准备资源，已等待 {watch.Elapsed.TotalSeconds:F0} 秒；可取消启动。");
        }
    }

    // npm/pnpm progress can end with CR rather than LF. Read both without waiting for a whole line.
    public static async Task DrainAsync(StreamReader reader, Action<string> log, CancellationToken token = default)
    {
        var buffer = new char[2048];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] is '\r' or '\n' || line.Length >= 4000)
                {
                    if (line.Length > 0) { log(line.ToString()); line.Clear(); }
                }
                if (buffer[i] is not ('\r' or '\n')) line.Append(buffer[i]);
            }
        }
        if (line.Length > 0) log(line.ToString());
    }
}
