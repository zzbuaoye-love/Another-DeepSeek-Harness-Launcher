using System.Security.Cryptography;
using System.Text.Json;
using AnotherDSHL.Installer.Core;
using Microsoft.Win32;

string root = Path.Combine(Path.GetTempPath(), "ADL-installer-test-" + Guid.NewGuid().ToString("N"));
string identity = "ADL-Test-" + Guid.NewGuid().ToString("N");
string source = Path.Combine(root, "source"), target = Path.Combine(root, "installed");
string desktop = Path.Combine(root, "desktop"), menu = Path.Combine(root, "menu");
var service = new Installation(identity, menu, desktop);
int checks = 0;
void Check(bool condition, string label) { if (!condition) throw new Exception(label); checks++; Console.WriteLine("PASS " + label); }
void Reject(Action action, string label) { try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { checks++; Console.WriteLine("PASS " + label); return; } throw new Exception("Expected rejection: " + label); }
void Manifest(string version)
{
    var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(p => Path.GetFileName(p) != PayloadManifest.FileName)
        .Select(p => new PayloadFile(Path.GetRelativePath(source, p).Replace('\\', '/'), new FileInfo(p).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))))).ToList();
    File.WriteAllText(Path.Combine(source, PayloadManifest.FileName), JsonSerializer.Serialize(new PayloadManifest("AnotherDSHL", version, files)));
}
try
{
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "AnotherDSHL.exe"), "fake executable for file installation tests");
    File.WriteAllText(Path.Combine(source, "AnotherDSHL.Installer.exe"), "fake uninstaller");
    Directory.CreateDirectory(Path.Combine(source, "Assets"));
    File.WriteAllText(Path.Combine(source, "Assets", "sample.txt"), "asset v1"); Manifest("1.0");
    Reject(() => Installation.NormalizeDirectory("C:\\"), "reject disk root");
    Reject(() => Installation.SafePath(root, "../escape"), "reject traversal");
    Reject(() => Installation.SafePath(root, "file:stream"), "reject alternate streams");
    Reject(() => service.Install(source, source, false, false), "reject overlapping source and destination");
    string foreign = Path.Combine(root, "foreign"); Directory.CreateDirectory(foreign); File.WriteAllText(Path.Combine(foreign, "keep.txt"), "keep");
    Reject(() => service.Install(source, foreign, false, false), "reject nonempty unregistered directory");
    File.WriteAllText(Path.Combine(source, "Assets", "sample.txt"), "corrupt");
    Reject(() => service.Install(source, target, false, false), "reject corrupt payload");
    Check(!Directory.Exists(target) && service.InstalledDirectory == null, "failed install leaves no registration/files");
    File.WriteAllText(Path.Combine(source, "Assets", "sample.txt"), "asset v1");
    service.Install(source, target, true, true);
    Check(service.InstalledDirectory == target && File.Exists(Path.Combine(target, "AnotherDSHL.exe")), "install and registration");
    Check(File.Exists(Path.Combine(desktop, "AnotherDSHL.lnk")) && File.Exists(Path.Combine(menu, "AnotherDSHL.lnk")), "real Windows COM shortcuts");
    string unknown = Path.Combine(target, "user-workspace.txt"); File.WriteAllText(unknown, "preserve me");
    File.WriteAllText(Path.Combine(source, "Assets", "sample.txt"), "asset v2"); Manifest("2.0");
    // Force failure after committing staged files: the shortcut parent is a file.
    string blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "not a directory");
    var failing = new Installation(identity, blocked, desktop);
    try { failing.Install(source, target, true, true); throw new Exception("Expected shortcut failure"); }
    catch (IOException) { checks++; Console.WriteLine("PASS rollback after shortcut failure"); }
    Check(File.ReadAllText(Path.Combine(target, "Assets", "sample.txt")) == "asset v1", "rollback restores previous files");
    Check(PayloadManifest.Read(target).Version == "1.0" && service.InstalledDirectory == target, "rollback restores metadata");
    service.Install(source, target, false, true);
    Check(File.ReadAllText(unknown) == "preserve me" && PayloadManifest.Read(target).Version == "2.0", "upgrade preserves foreign files");
    Check(!File.Exists(Path.Combine(desktop, "AnotherDSHL.lnk")), "remove owned optional shortcut");
    var manifest = PayloadManifest.Read(target);
    manifest.Files.Add(new PayloadFile("../escape", 0, new string('0', 64)));
    File.WriteAllText(Path.Combine(target, PayloadManifest.FileName), JsonSerializer.Serialize(manifest));
    Reject(() => service.Uninstall(target), "reject malformed uninstall manifest before deletion");
    manifest.Files.RemoveAt(manifest.Files.Count - 1);
    File.WriteAllText(Path.Combine(target, PayloadManifest.FileName), JsonSerializer.Serialize(manifest));
    service.Uninstall(target);
    Check(!File.Exists(Path.Combine(target, "AnotherDSHL.exe")) && File.Exists(unknown), "uninstall removes only owned files");
    Check(service.InstalledDirectory == null && !File.Exists(Path.Combine(menu, "AnotherDSHL.lnk")), "uninstall registration and shortcuts");
    Check(!Directory.GetDirectories(root).Any(p => p.Contains(".adl-")), "no staging or backup leftovers");
    Console.WriteLine($"{checks} checks passed.");
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + identity, false);
    Installation.TryCleanup(root);
}
