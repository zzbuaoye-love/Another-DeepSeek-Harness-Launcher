using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using AnotherDSHL.Services;

var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
// The test root is isolated from the application's settings, packs and instances.
var root = Path.Combine(Path.GetTempPath(), "adl-pack-engine-qa-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Console.WriteLine($"QA root: {root}");
var node = args.FirstOrDefault(arg => arg.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase)) ?? @"C:\Program Files\nodejs\node.exe";
var engine = Path.Combine(repo, "Resources", "PackForge", "engine.mjs");
var service = new PackForgeEngineService(Path.Combine(root, "instances"), engine);
var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine($"PASS {name}"); }
async Task Reject(Func<Task> action, string name) { try { await action(); } catch { Check(true, name); return; } throw new Exception($"Unexpected success: {name}"); }
void WritePack(string output, object manifest, params (string Path, string Content)[] entries)
{
    using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
    void Add(string path, string value) { using var writer = new StreamWriter(zip.CreateEntry(path).Open()); writer.Write(value); }
    Add("dspack.json", "{\"format\":\"dspack\",\"version\":3}");
    Add("manifest.json", JsonSerializer.Serialize(manifest));
    foreach (var entry in entries) Add(entry.Path, entry.Content);
}
var tools = PackForgeEngineService.ResolveTools(node);
if (args.Contains("--real"))
{
    var source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnotherDSHL", "Packs", "hxh230802.smoother-deepseek-harness-1.0.1.dspack");
    var info = await service.InspectAsync(source, tools);
    var version = info.DshVersion.Length > 0 ? info.DshVersion : (await DshVersionService.GetAvailableAsync()).Latest;
    var recent = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var progress = new Progress<PackEngineProgress>(message =>
    {
        if (message.Stage != "log") Console.WriteLine($"{message.Stage}: {message.Detail}");
        else { recent.Enqueue(message.Detail); while (recent.Count > 10) recent.TryDequeue(out _); }
    });
    try
    {
        var installed = await service.InstallAsync(source, info, service.CreateInstanceId(info), version,
            info.DefaultProfile, root, tools, progress);
        Check(service.LoadInstalled().Single().Id == installed.Id, "real market pack installed and discoverable after reload");
        var start = service.CreateLaunchInfo(installed, node, 39189);
        Console.WriteLine($"Installed: {installed.DisplayName} · DSH {version}");
        if (args.Contains("--launch"))
        {
            using var process = new Process { StartInfo = start };
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            var ready = false;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            for (var attempt = 0; attempt < 60 && !process.HasExited; attempt++)
            {
                try
                {
                    var response = await http.GetAsync("http://127.0.0.1:39189/");
                    var body = await response.Content.ReadAsStringAsync();
                    ready = body.Contains("DeepSeek Harness") || body.Contains("dsh web authentication required");
                    if (ready) break;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(1000);
            }
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            var log = await output + await errors;
            if (!ready) Console.WriteLine(log.Length > 4000 ? log[^4000..] : log);
            Check(ready, "real installed custom profile serves Web on isolated port");
        }
    }
    catch { Console.WriteLine(string.Join("\n", recent)); throw; }
    return;
}

var npmFixture = Path.Combine(root, "npm fixture.cjs");
await File.WriteAllTextAsync(npmFixture, """
const fs=require('fs'),path=require('path');
const a=process.argv.slice(2),r=a[a.indexOf('--prefix')+1],v=a.at(-1).split('@').at(-1);
const p=path.join(r,'node_modules','@deepseek-ai','dsh');fs.mkdirSync(path.join(p,'lib'),{recursive:true});
fs.writeFileSync(path.join(p,'package.json'),JSON.stringify({version:v,bin:{dsh:'lib/bin.js'}}));
fs.writeFileSync(path.join(p,'lib','bin.js'),"console.log(JSON.stringify({home:process.env.DSH_HOME,args:process.argv.slice(2),cwd:process.cwd()}))");
""");
var pnpmFixture = Path.Combine(root, "pnpm fixture.cjs");
await File.WriteAllTextAsync(pnpmFixture, "console.log('fixture dependency installation complete');");
var fixtureTools = new PackEngineTools(node, npmFixture, pnpmFixture);
var pack = Path.Combine(root, "测试 包.dspack");
var manifest = new { manifestVersion = 5, type = "profile", name = "fixture", version = "1.0.0", profileName = "fixture", bundles = Array.Empty<string>(), dependencies = new { }, patch = "" };
WritePack(pack, manifest, ("overrides/hello.txt", "payload"), ("home/skills/example.md", "skill"));
var preview = await service.InspectAsync(pack, fixtureTools);
Check(preview.DefaultProfile == "fixture" && preview.Size > 0, "bundled engine previews profile pack");
var id = service.CreateInstanceId(preview);
var instance = await service.InstallAsync(pack, preview, id, "1.0.0", preview.DefaultProfile, root, fixtureTools);
Check(File.ReadAllText(Path.Combine(service.InstancesRoot, id, "home", "profiles", "fixture", "hello.txt")) == "payload", "profile payload installed by actual core");
Check(File.ReadAllText(Path.Combine(service.InstancesRoot, id, "home", "skills", "example.md")) == "skill", "home content stays inside instance");
Check(new PackForgeEngineService(service.InstancesRoot, engine).LoadInstalled().Single().Id == id, "instance persists across service reload");
var profileRoot = Path.Combine(service.InstancesRoot, id, "home", "profiles", "fixture");
await File.WriteAllTextAsync(Path.Combine(profileRoot, ".env"), "SAMPLE_SECRET=fixture");
var exportPreview = await service.InspectProfileAsync(profileRoot, node);
Check(exportPreview.GetProperty("excluded").GetInt32() > 0, "export preview reports excluded files");
var exported = await service.ExportAsync(profileRoot, Path.Combine(root, "export"), "1.0.0", node, null);
var exportPath = exported.GetProperty("output").GetString()!;
Check((await service.InspectAsync(exportPath, fixtureTools)).DefaultProfile == "fixture", "embedded export produces a readable package");
using (var exportedZip = ZipFile.OpenRead(exportPath))
    Check(!exportedZip.Entries.Any(entry => entry.FullName.Contains(".env")), "export filters credentials before packaging");
var launch = service.CreateLaunchInfo(instance, node, 39001);
using (var process = Process.Start(launch)!)
{
    var output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();
    using var result = JsonDocument.Parse(output);
    Check(result.RootElement.GetProperty("home").GetString() == Path.Combine(service.InstancesRoot, id, "home"), "launch receives private DSH_HOME");
    Check(result.RootElement.GetProperty("args")[1].GetString() == "fixture", "launch selects installed custom profile");
    Check(result.RootElement.GetProperty("cwd").GetString() == root, "launch preserves requested work directory");
}
await Reject(() => service.InstallAsync(pack, preview, id, "1.0.0", "fixture", root, fixtureTools), "existing instance cannot be replaced");
var changed = Path.Combine(root, "changed.dspack"); WritePack(changed, manifest, ("overrides/different.txt", "different"));
await Reject(() => service.InstallAsync(changed, preview, service.CreateInstanceId(preview), "1.0.0", "fixture", root, fixtureTools), "package changed since preview is rejected");
var unsafePack = Path.Combine(root, "unsafe.dspack"); WritePack(unsafePack, manifest, ("overrides/../escape.txt", "bad"));
await Reject(() => service.InspectAsync(unsafePack, fixtureTools), "ZIP traversal rejected before engine execution");
var duplicate = Path.Combine(root, "duplicate.dspack"); WritePack(duplicate, manifest, ("overrides/A.txt", "one"), ("overrides/a.txt", "two"));
await Reject(() => service.InspectAsync(duplicate, fixtureTools), "case collision rejected on Windows");
var homePack = Path.Combine(root, "home.dspack");
WritePack(homePack, new { manifestVersion = 5, type = "dshhome", name = "home-fixture", version = "1.0.0", defaultProfile = "alpha", profiles = new { alpha = new { bundles = Array.Empty<string>(), dependencies = new { }, patch = "" } } }, ("overrides/profiles/alpha/example.txt", "alpha"));
var homePreview = await service.InspectAsync(homePack, fixtureTools);
var homeInstance = await service.InstallAsync(homePack, homePreview, service.CreateInstanceId(homePreview), "1.0.0", "alpha", root, fixtureTools);
Check(service.LoadInstalled().Count == 2 && File.Exists(Path.Combine(service.InstancesRoot, homeInstance.Id, "home", "profiles", "alpha", "example.txt")), "dshhome package installed separately");
var failure = Path.Combine(root, "failure.cjs"); await File.WriteAllTextAsync(failure, "process.exit(9)");
await Reject(() => service.InstallAsync(pack, preview, service.CreateInstanceId(preview), "1.0.0", "fixture", root, fixtureTools with { PnpmCli = failure }), "dependency failure is reported");
Check(!Directory.EnumerateDirectories(service.InstancesRoot, ".staging-*").Any() && service.LoadInstalled().Count == 2, "failure cleans staging and preserves prior instances");
var wait = Path.Combine(root, "wait.cjs"); await File.WriteAllTextAsync(wait, "setInterval(()=>{},1000)");
using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
    await Reject(() => service.InstallAsync(pack, preview, service.CreateInstanceId(preview), "1.0.0", "fixture", root, fixtureTools with { PnpmCli = wait }, token: cancel.Token), "installation can be cancelled while dependency process runs");
Check(!Directory.EnumerateDirectories(service.InstancesRoot, ".staging-*").Any(), "cancelled process is stopped before staging cleanup");
Console.WriteLine($"{checks} checks passed.");
