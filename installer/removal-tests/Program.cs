using System.Text.Json;
using System.Text.Json.Nodes;
using AnotherDSHL.Services;

var root = Path.Combine(Path.GetTempPath(), "AnotherDSHL-removal-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("Unexpected success: " + name); }
try
{
    var profile = Path.Combine(root, "default", "profiles", "web");
    Directory.CreateDirectory(profile);
    var manifest = Path.Combine(profile, "package.json");
    File.WriteAllText(manifest, """
    {"custom":{"keep":"untouched"},"dependencies":{"community-plugin":"github:owner/repo","@example/enabled":"^1.0.0"},"devDependencies":{"development-plugin":"2.0.0"},"dsh":{"profile":{"bundles":["@example/enabled","@example/builtin","@example/builtin"],"patch":"keep.json"}}}
    """);
    var plugins = PluginProfileService.Load(profile);
    Check(plugins.Count == 4, "deduplicate bundles and dependencies");
    Check(plugins.Single(p => p.Name == "@example/enabled") is { IsEnabled: true, IsDependency: true }, "identify enabled dependency");
    Check(plugins.Single(p => p.Name == "community-plugin").InstallSpec == "github:owner/repo", "retain repository spec");
    Check(plugins.Single(p => p.Name == "development-plugin").IsDependency, "include devDependencies");
    Check(!plugins.Single(p => p.Name == "@example/builtin").IsDependency, "identify runtime bundle");
    var original = File.ReadAllText(manifest);
    Reject(() => PluginProfileService.RemoveEnabledBundle(profile, "../escape"), "reject unsafe name");
    Check(File.ReadAllText(manifest) == original, "invalid removal preserves manifest");
    PluginProfileService.RemoveEnabledBundle(profile, "@example/builtin");
    Check(!PluginProfileService.Load(profile).Any(p => p.Name == "@example/builtin"), "remove duplicate enabled references");
    var after = JsonNode.Parse(File.ReadAllText(manifest))!;
    Check(after["custom"]!["keep"]!.GetValue<string>() == "untouched" && after["dsh"]!["profile"]!["patch"]!.GetValue<string>() == "keep.json", "preserve unrelated fields");
    Check(PluginProfileService.Load(profile).Count == 3, "preserve other plugins");
    PluginProfileService.RemoveEnabledBundle(profile, "@example/enabled");
    Check(PluginProfileService.Load(profile).Single(p => p.Name == "@example/enabled") is { IsEnabled: false, IsDependency: true }, "bundle edit preserves dependency");
    var custom = Path.Combine(root, "custom-profile"); Directory.CreateDirectory(custom);
    File.WriteAllText(Path.Combine(custom, "package.json"), "{\"dependencies\":{\"pack-plugin\":\"1.0.0\"}}");
    Check(PluginProfileService.Load(custom).Single().Name == "pack-plugin", "read selected pack profile");
    Check(PluginProfileService.Load(Path.Combine(root, "missing")).Count == 0, "missing profile is empty");
    Check(!Directory.EnumerateFiles(profile, "*.tmp").Any(), "clean temporary files");
    var instances = Path.Combine(root, "instances");
    var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
    var project = Path.Combine(workspace, "project.txt"); File.WriteAllText(project, "user project");
    var archive = Path.Combine(root, "download.dspack"); File.WriteAllText(archive, "download");
    var service = new PackForgeEngineService(instances);
    InstalledPack Fixture(string id)
    {
        var pack = new InstalledPack(id, id, "1.0.0", new string('a', 64), "1.0.0", "custom", "home", "runtime/entry.js", workspace, DateTimeOffset.UtcNow);
        var directory = Path.Combine(instances, id);
        Directory.CreateDirectory(Path.Combine(directory, "runtime"));
        Directory.CreateDirectory(Path.Combine(directory, "home", "profiles", "custom"));
        File.WriteAllText(Path.Combine(directory, "runtime", "entry.js"), "fixture");
        File.WriteAllText(Path.Combine(directory, "home", "profiles", "custom", "package.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "instance.json"), JsonSerializer.Serialize(pack)); return pack;
    }
    var first = Fixture("first"); var second = Fixture("second");
    Check(service.LoadInstalled().Count == 2, "fixtures registered");
    Check(service.GetProfileDirectory(first) == Path.Combine(instances, "first", "home", "profiles", "custom"), "resolve custom profile");
    Reject(() => service.Uninstall(first with { Id = "../workspace" }), "reject traversal");
    Reject(() => service.Uninstall(first with { Version = "stale" }), "reject stale record");
    Check(service.LoadInstalled().Count == 2, "invalid uninstall preserves instances");
    try { Directory.CreateSymbolicLink(Path.Combine(instances, "first", "external-link"), workspace); Console.WriteLine("Created external link fixture."); }
    catch (UnauthorizedAccessException) { Console.WriteLine("SKIP link: symbolic link privilege unavailable."); }
    service.Uninstall(first);
    Check(!Directory.Exists(Path.Combine(instances, "first")), "delete selected instance");
    Check(service.LoadInstalled().Single().Id == second.Id, "preserve second instance and persist deletion");
    Check(File.ReadAllText(project) == "user project", "preserve workspace and link target");
    Check(File.Exists(archive) && File.Exists(manifest), "preserve archive and default profile");
    var unsafeWorkspace = second with { Workspace = Path.Combine(instances, "second", "project") };
    File.WriteAllText(Path.Combine(instances, "second", "instance.json"), JsonSerializer.Serialize(unsafeWorkspace));
    Reject(() => service.Uninstall(unsafeWorkspace), "protect workspace inside instance");
    Check(Directory.Exists(Path.Combine(instances, "second")), "workspace protection preserves instance");
    Console.WriteLine($"Passed {checks} removal checks.");
}
finally { Directory.Delete(root, true); }
