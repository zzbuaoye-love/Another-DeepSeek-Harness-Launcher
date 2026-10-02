using System.Security.Cryptography;
using System.Text.Json;
using AnotherDSHL.Installer.Core;
using Microsoft.Win32;
using System.IO.Compression;
using AnotherDSHL.Services;

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
    if (args is ["--verify-delta", var baseline, var delta])
    {
        string realTarget = Path.Combine(root, "real-upgraded");
        service.Install(baseline, realTarget, false, false);
        string preserved = Path.Combine(realTarget, "user-workspace.txt");
        File.WriteAllText(preserved, "preserve user data");
        var update = UpdatePackageService.ReadManifest(delta);
        Check(PayloadManifest.Read(realTarget).Version == update.From, "actual release delta matches published baseline");
        new UpdatePackageService(service).Apply(delta, realTarget);
        var upgraded = PayloadManifest.Read(realTarget);
        Check(upgraded.Version == "0.0.3-alpha", "actual release delta upgrades to Alpha");
        Check(upgraded.Files.All(file =>
        {
            string path = Installation.SafePath(realTarget, file.Path);
            return new FileInfo(path).Length == file.Size &&
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
        }), "all upgraded release files match target checksums");
        Check(File.ReadAllText(preserved) == "preserve user data", "actual release delta preserves user data");
        service.Uninstall(realTarget);
        Check(service.InstalledDirectory == null && File.ReadAllText(preserved) == "preserve user data", "actual updated release uninstalls without removing user data");
        Console.WriteLine($"{checks} release delta checks passed.");
        return;
    }
    if (args is ["--verify-release"])
    {
        var updates = new AppUpdateService(version: "0.0.2-beta", installed: false, cacheRoot: Path.Combine(root, "release-cache"));
        var release = await updates.CheckAsync() ?? throw new Exception("Expected Alpha release update");
        Check(release.Tag == "v0.0.3-alpha", "public GitHub API discovers Alpha release");
        var prepared = await updates.PrepareAsync(release);
        await AppUpdateService.VerifyPreparedAsync(prepared);
        Check(!prepared.Differential && new FileInfo(prepared.Path).Length > 1_000_000, "public release setup downloads and passes update verification");
        Console.WriteLine($"{checks} public release checks passed.");
        return;
    }
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "AnotherDSHL.exe"), "fake executable for file installation tests");
    File.WriteAllText(Path.Combine(source, "AnotherDSHL.Installer.exe"), "fake uninstaller");
    Directory.CreateDirectory(Path.Combine(source, "Assets"));
    File.WriteAllText(Path.Combine(source, "Assets", "sample.txt"), "asset v1"); Manifest("1.0.0");
    Reject(() => Installation.NormalizeDirectory("C:\\"), "reject disk root");
    Reject(() => Installation.SafePath(root, "../escape"), "reject traversal");
    Reject(() => Installation.SafePath(root, "file:stream"), "reject alternate streams");
    Reject(() => Installation.SafePath(root, "Assets/file.txt."), "reject Windows path alias");
    Reject(() => Installation.SafePath(root, "Assets/CON.txt"), "reject Windows device name");
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
    File.WriteAllText(Path.Combine(source, "Assets", "sample.txt"), "asset v2"); Manifest("2.0.0");
    // Force failure after committing staged files: the shortcut parent is a file.
    string blocked = Path.Combine(root, "blocked"); File.WriteAllText(blocked, "not a directory");
    var failing = new Installation(identity, blocked, desktop);
    try { failing.Install(source, target, true, true); throw new Exception("Expected shortcut failure"); }
    catch (IOException) { checks++; Console.WriteLine("PASS rollback after shortcut failure"); }
    Check(File.ReadAllText(Path.Combine(target, "Assets", "sample.txt")) == "asset v1", "rollback restores previous files");
    Check(PayloadManifest.Read(target).Version == "1.0.0" && service.InstalledDirectory == target, "rollback restores metadata");
    service.Install(source, target, false, true);
    Check(File.ReadAllText(unknown) == "preserve me" && PayloadManifest.Read(target).Version == "2.0.0", "upgrade preserves foreign files");
    Check(!File.Exists(Path.Combine(desktop, "AnotherDSHL.lnk")), "remove owned optional shortcut");
    string package = Path.Combine(root, "update.adup");
    File.Delete(Path.Combine(source, "Assets", "sample.txt"));
    File.WriteAllText(Path.Combine(source, "Assets", "new.txt"), "new version asset"); Manifest("3.0.0");
    UpdatePackageService.Create(target, source, package);
    Check(UpdatePackageService.ReadManifest(package).Delete.Contains("Assets/sample.txt"), "delta contains obsolete-file deletion");
    var updater = new UpdatePackageService(service);
    string corrupt = Path.Combine(root, "corrupt.adup"); File.Copy(package, corrupt);
    using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update))
    {
        var entry = zip.GetEntry("files/Assets/new.txt")!;
        long size = entry.Length; entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("files/Assets/new.txt").Open()); writer.Write(new string('x', (int)size));
    }
    Reject(() => updater.Apply(corrupt, target), "reject corrupt differential payload");
    Check(PayloadManifest.Read(target).Version == "2.0.0" && File.Exists(Path.Combine(target, "Assets", "sample.txt")), "failed delta preserves original installation");
    Reject(() => updater.Apply(package, target, new FailAtCompletion()), "rollback differential commit on failure");
    Check(PayloadManifest.Read(target).Version == "2.0.0" && !File.Exists(Path.Combine(target, "Assets", "new.txt")), "delta rollback restores files and version");
    updater.Apply(package, target);
    Check(PayloadManifest.Read(target).Version == "3.0.0" && !File.Exists(Path.Combine(target, "Assets", "sample.txt")) && File.Exists(Path.Combine(target, "Assets", "new.txt")), "delta replaces files and removes obsolete files");
    Check(File.Exists(Path.Combine(menu, "AnotherDSHL.lnk")) && File.Exists(unknown), "delta preserves shortcuts and foreign files");
    Reject(() => updater.Apply(package, target), "reject wrong base version");
    Check(AppUpdateService.CompareVersions("v1.2.0", "1.2.0-beta.10") > 0 && AppUpdateService.CompareVersions("1.2.0-beta.10", "1.2.0-beta.2") > 0 && AppUpdateService.CompareVersions("1.10.0", "1.9.0") > 0, "semantic version ordering");
    Check(AppUpdateService.ParseChecksums(new string('A', 64) + "  release.adup")["release.adup"] == new string('A', 64), "release checksums parsing");
    await UpdateChecks.Run(root, package, Check);
    Reject(() => UpdatePackageService.Create(target, source, package), "reject equal-version update generation");
    Manifest("2.0.0");
    Reject(() => UpdatePackageService.Create(target, source, package), "reject downgrade update generation");
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
    if (args.Length == 1)
    {
        string realTarget = Path.Combine(root, "real-installed");
        service.Install(args[0], realTarget, false, false);
        Check(File.Exists(Path.Combine(realTarget, "AnotherDSHL.Installer.exe")) && PayloadManifest.Read(realTarget).InstalledBytes > 1000000, "install actual self-contained release payload");
        service.Uninstall(realTarget);
        Check(!Directory.Exists(realTarget) && service.InstalledDirectory == null, "uninstall actual release payload cleanly");
    }
    Console.WriteLine($"{checks} checks passed.");
}
finally
{
    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + identity, false);
    Installation.TryCleanup(root);
}
sealed class FailAtCompletion : IProgress<double>
{
    public void Report(double value) { if (value >= 100) throw new IOException("Simulated failure after file and metadata commit"); }
}
