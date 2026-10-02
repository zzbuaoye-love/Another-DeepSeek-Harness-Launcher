using System.Diagnostics;
using System.Net;
using AnotherDSHL.Services;

var root = args.FirstOrDefault(arg => arg.StartsWith("--qa-root="))?[10..]
    ?? Path.Combine(Path.GetTempPath(), "adl-runtime-qa-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine("QA root: " + root);
var checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
var node = @"C:\Program Files\nodejs\node.exe";
var service = new DshRuntimeService(Path.Combine(root, "runtimes"), reuseExisting: false);
if (args.Contains("--real-isolation"))
{
    var tools = PackForgeEngineService.ResolveTools(node);
    var registry = await new NpmRegistryService().SelectAsync("Auto", "", Console.WriteLine);
    var version = await DshVersionService.GetLatestAsync();
    var projects = new[] { Path.Combine(root, "RealA"), Path.Combine(root, "RealB") };
    foreach (var project in projects) Directory.CreateDirectory(project);
    var environments = projects.Select(project => new WorkspaceHarnessEnvironment(project, Path.Combine(root, "environments"))).ToArray();
    var runtimes = await Task.WhenAll(environments.Select((environment, index) => environment.CreateRuntimeService().PrepareAsync(version, tools, registry,
        line => { if (!line.Contains("[npm] npm http")) Console.WriteLine($"[{index}] {LaunchLog.Sanitize(line)}"); })));
    Check(runtimes[0].Entry != runtimes[1].Entry && runtimes.Select((runtime, index) => runtime.Entry.StartsWith(environments[index].RuntimeRoot)).All(value => value),
        "real same-version installs use separate workspace runtime entries");
    await Task.WhenAll(environments.Select(async (environment, index) =>
    {
        var port = 39193 + index;
        var start = DshRuntimeService.CreateLaunchInfo(runtimes[index], node, projects[index], port, registry, environment.Home, environment.NpmCache);
        using var process = new Process { StartInfo = start };
        process.Start();
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var output = NpmProcessRunner.DrainAsync(process.StandardOutput, line => logs.Enqueue(LaunchLog.Sanitize(line)));
        var errors = NpmProcessRunner.DrainAsync(process.StandardError, line => logs.Enqueue(LaunchLog.Sanitize(line)));
        var ready = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (var attempt = 0; attempt < 70 && !process.HasExited; attempt++)
            {
                try
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{port}/");
                    var body = await response.Content.ReadAsStringAsync();
                    if (body.Contains("DeepSeek Harness") || body.Contains("dsh web authentication required")) { ready = true; break; }
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(1000);
            }
            if (!ready) Console.WriteLine(string.Join("\n", logs.TakeLast(15)));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, errors);
        }
        if (!ready) throw new Exception($"Workspace {index} Web did not become ready.");
        Console.WriteLine($"PASS real workspace {index} Web ready · DSH {version} · isolated home {environment.Home}");
    }));
    Check(environments.All(environment => Directory.Exists(Path.Combine(environment.Home, "profiles")) &&
        File.Exists(Path.Combine(environment.Home, ".anonymous-user-id"))), "both real Web sessions initialize their own home and identity");
    await File.WriteAllTextAsync(Path.Combine(environments[0].Home, "isolation-marker"), "only A");
    Check(!File.Exists(Path.Combine(environments[1].Home, "isolation-marker")), "real workspace data stays separate");
    var cached = await environments[0].CreateRuntimeService().PrepareAsync(version, tools, registry, Console.WriteLine);
    Check(cached == runtimes[0], "real same workspace restarts from its own cache");
    Console.WriteLine($"Real isolation checks passed. QA root: {root}");
    return;
}
if (args.Contains("--real"))
{
    var log = new List<string>();
    var watch = Stopwatch.StartNew();
    var registry = await new NpmRegistryService().SelectAsync("Auto", "", Console.WriteLine);
    var version = await DshVersionService.GetLatestAsync();
    Console.WriteLine("Official latest: " + version);
    var tools = PackForgeEngineService.ResolveTools(node);
    var runtime = await service.PrepareAsync(version, tools, registry, line => { lock (log) log.Add(line); Console.WriteLine(LaunchLog.Sanitize(line)); });
    Console.WriteLine($"Cold prepare: {watch.Elapsed.TotalSeconds:F1}s");
    watch.Restart();
    var cached = await service.PrepareAsync(version, tools, registry, Console.WriteLine);
    Check(cached == runtime, "real latest runtime is reused");
    Console.WriteLine($"Cached prepare: {watch.Elapsed.TotalMilliseconds:F0}ms");
    var home = Path.Combine(root, "home");
    Directory.CreateDirectory(home);
    var start = DshRuntimeService.CreateLaunchInfo(runtime, node, root, 39187, registry);
    start.Environment["DSH_HOME"] = home;
    using var process = new Process { StartInfo = start };
    process.Start();
    var output = NpmProcessRunner.DrainAsync(process.StandardOutput, line => Console.WriteLine(LaunchLog.Sanitize(line)));
    var errors = NpmProcessRunner.DrainAsync(process.StandardError, line => Console.WriteLine(LaunchLog.Sanitize(line)));
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var ready = false;
    watch.Restart();
    try
    {
        for (var attempt = 0; attempt < 120 && !process.HasExited; attempt++)
        {
            try
            {
                using var response = await http.GetAsync("http://127.0.0.1:39187/");
                var body = await response.Content.ReadAsStringAsync();
                ready = body.Contains("DeepSeek Harness") || body.Contains("dsh web authentication required");
                if (ready) break;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(1000);
        }
        Check(ready, "real latest serves Web using isolated home and port");
        Console.WriteLine($"Web readiness: {watch.Elapsed.TotalSeconds:F1}s");
    }
    finally
    {
        if (!process.HasExited) process.Kill(true);
        await process.WaitForExitAsync();
        await Task.WhenAll(output, errors);
    }
    return;
}

var npm = Path.Combine(root, "npm fixture.cjs");
await File.WriteAllTextAsync(npm, """
const fs=require('fs'),path=require('path');
const a=process.argv.slice(2),r=a[a.indexOf('--prefix')+1],v=a.at(-1).split('@').at(-1);
fs.appendFileSync(path.join(path.dirname(r),'calls.txt'),process.env.npm_config_registry+'\n');
if(process.env.npm_config_registry.includes('bad')) process.exit(7);
const p=path.join(r,'node_modules','@deepseek-ai','dsh');fs.mkdirSync(path.join(p,'lib'),{recursive:true});
fs.writeFileSync(path.join(p,'package.json'),JSON.stringify({version:v,bin:{dsh:'lib/bin.js'}}));
fs.writeFileSync(path.join(p,'lib','bin.js'),"console.log('fixture runtime')");
process.stdout.write('download 10%\rdownload 80%\r'); process.stderr.write('npm fixture error stream\n');
""");
var logs = new List<string>();
void Log(string line) { lock (logs) logs.Add(line); }
var toolsFixture = new PackEngineTools(node, npm, null);
var runtime1 = await service.PrepareAsync("1.0.0", toolsFixture, NpmRegistryService.Official, Log);
var workspaceA = Path.Combine(root, "ProjectA");
var workspaceB = Path.Combine(root, "ProjectB");
Directory.CreateDirectory(workspaceA);
Directory.CreateDirectory(workspaceB);
var isolatedA = new WorkspaceHarnessEnvironment(workspaceA, Path.Combine(root, "environments"));
var isolatedB = new WorkspaceHarnessEnvironment(workspaceB, Path.Combine(root, "environments"));
Check(isolatedA.Root == new WorkspaceHarnessEnvironment(workspaceA.ToUpperInvariant() + Path.DirectorySeparatorChar,
    Path.Combine(root, "environments")).Root && isolatedA.Root != isolatedB.Root, "stable path identity and separate workspaces");
var aRuntime = await isolatedA.CreateRuntimeService().PrepareAsync("1.0.0", toolsFixture, NpmRegistryService.Official, Log);
var bRuntime = await isolatedB.CreateRuntimeService().PrepareAsync("1.0.0", toolsFixture, NpmRegistryService.Official, Log);
Check(aRuntime.Entry != bRuntime.Entry && aRuntime.Entry != runtime1.Entry && File.Exists(aRuntime.Entry) && File.Exists(bRuntime.Entry),
    "same version in different workspaces installs independent runtimes");
var aCached = await isolatedA.CreateRuntimeService().PrepareAsync("1.0.0", toolsFixture, NpmRegistryService.Official, Log);
Check(aCached == aRuntime && File.ReadAllLines(Path.Combine(isolatedA.RuntimeRoot, "calls.txt")).Length == 1,
    "same workspace reuses only its own complete installation");
Check(isolatedA.CreateRuntimeService().FindCachedVersion() == "1.0.0" &&
    new WorkspaceHarnessEnvironment(Path.Combine(root, "NeverStarted"), Path.Combine(root, "environments")).CreateRuntimeService().FindCachedVersion() is null,
    "isolated environment status ignores shared runtime cache");
var aLaunch = DshRuntimeService.CreateLaunchInfo(aRuntime, node, workspaceA, 39992, NpmRegistryService.Official, isolatedA.Home, isolatedA.NpmCache);
var bLaunch = DshRuntimeService.CreateLaunchInfo(bRuntime, node, workspaceB, 39993, NpmRegistryService.Official, isolatedB.Home, isolatedB.NpmCache);
Check(aLaunch.Environment["DSH_HOME"] == isolatedA.Home && bLaunch.Environment["DSH_HOME"] == isolatedB.Home &&
    aLaunch.Environment["npm_config_cache"] != bLaunch.Environment["npm_config_cache"], "launch isolates profile and npm cache");
await File.WriteAllTextAsync(Path.Combine(isolatedA.Home, "profile-marker"), "A plugin config");
Check(!File.Exists(Path.Combine(isolatedB.Home, "profile-marker")), "workspace configuration does not leak");
var sharedLaunch = DshRuntimeService.CreateLaunchInfo(runtime1, node, workspaceA, 39994, NpmRegistryService.Official);
sharedLaunch.Environment.TryGetValue("DSH_HOME", out var sharedHome);
Check(sharedHome == Environment.GetEnvironmentVariable("DSH_HOME"), "shared mode preserves inherited DSH_HOME");
var callsFile = Path.Combine(root, "runtimes", "calls.txt");
Check(File.ReadAllLines(callsFile).Length == 1, "cold pinned version installs once");
var cached1 = await service.PrepareAsync("1.0.0", toolsFixture, NpmRegistryService.Official, Log);
Check(cached1 == runtime1 && File.ReadAllLines(callsFile).Length == 1, "pinned cache skips npm and version updates");
var runtime2 = await service.PrepareAsync("2.0.0", toolsFixture, new("Bad", "坏源", "https://bad.example/"), Log);
Check(File.ReadAllLines(callsFile).Length == 3 && File.Exists(runtime1.Entry), "mirror failure retries official and preserves previous version");
Check(logs.Any(line => line.Contains("download 10%")) && logs.Any(line => line.Contains("download 80%")) &&
    logs.Any(line => line.Contains("error stream")), "CR progress and stderr both stream to log");
var launch = DshRuntimeService.CreateLaunchInfo(runtime2, node, root, 39991, NpmRegistryService.Official);
Check(launch.FileName == node && launch.ArgumentList[0] == runtime2.Entry && launch.ArgumentList[1] == "web",
    "launch bypasses npx and uses exact local entry");
var slow = Path.Combine(root, "slow.cjs");
await File.WriteAllTextAsync(slow, "setInterval(()=>{},1000)");
using (var cancellation = new CancellationTokenSource(500))
{
    try { await service.PrepareAsync("3.0.0", toolsFixture with { NpmCli = slow }, NpmRegistryService.Official, Log, cancellation.Token); throw new Exception("Cancellation did not occur"); }
    catch (OperationCanceledException) { Check(true, "cancel stops npm preparation"); }
}
Check(!Directory.EnumerateDirectories(Path.Combine(root, "runtimes"), ".staging-*").Any() && File.Exists(runtime1.Entry),
    "cancel removes staging and preserves installed versions");
var handler = new RegistryFixture();
var registries = new NpmRegistryService(new HttpClient(handler));
Check(NpmRegistryService.Presets.Length == 2 && NpmRegistryService.Presets.All(source => !source.Url.Contains("tsinghua")), "presets contain only official and working npm mirror");
Check(NpmRegistryService.NormalizeMode("Tsinghua") == "Auto", "legacy Tsinghua setting migrates to automatic mode");
Check(NpmRegistryService.NormalizeMode("Custom") == "Custom", "custom registry mode is preserved");
Check((await registries.SelectAsync("Auto", "", Log)).Id == "Aliyun", "auto chooses fastest valid package response, excludes HTML and failures");
Check((await registries.SelectAsync("Tsinghua", "", Log)).Id == "Aliyun", "legacy mirror selection uses cached automatic detection");
Check(handler.Hosts.Count == 2 && handler.Hosts.All(host => !host.Contains("tsinghua")), "automatic and legacy modes never probe removed mirror");
Check((await registries.SelectAsync("Custom", "https://bad.example/", Log)).Id == "Official", "invalid custom registry response falls back to official");
handler.AllFail = true;
Check((await registries.SelectAsync("Aliyun", "", Log, force: true)).Id == "Official", "unavailable manual mirror falls back to official");
Check((await registries.SelectAsync("Auto", "", Log, force: true)).Id == "Official", "all failed probes retain official fallback");
Check(NpmRegistryService.NormalizeCustomUrl("https://example.com/npm") == "https://example.com/npm/", "custom registry path is preserved");
try { NpmRegistryService.NormalizeCustomUrl("https://user:password@example.com/"); throw new Exception("Credentials accepted"); }
catch (ArgumentException) { Check(true, "registry credentials rejected"); }
Check(!LaunchLog.Sanitize("https://127.0.0.1:3080/?token=secret token=secret").Contains("secret"), "session parameters redacted from persistent log");
Check(!LaunchLog.Sanitize("Authorization: Bearer secret").Contains("secret") &&
    !LaunchLog.Sanitize("{\"api_key\":\"secret\"}").Contains("secret"), "header and JSON credentials redacted");
Console.WriteLine($"{checks} runtime checks passed.");

sealed class RegistryFixture : HttpMessageHandler
{
    public bool AllFail;
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Hosts = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var host = request.RequestUri!.Host;
        Hosts.Enqueue(host);
        if (host == "registry.npmjs.org") await Task.Delay(100, token);
        if (AllFail || host.Contains("tsinghua")) return new(HttpStatusCode.NotFound);
        return new(HttpStatusCode.OK) { Content = new StringContent(host.Contains("bad") ? "<html>not npm</html>" : "{\"version\":\"1.0.0\"}") };
    }
}
