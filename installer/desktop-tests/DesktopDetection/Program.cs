using AnotherDSHL.Services;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    checks++;
    Console.WriteLine($"PASS {name}");
}

foreach (var name in new[] { "DeepSeek Harness", "DeepSeek Harness 0.2.0-rc.2", "deepseek harness 1.2.3", "DeepSeek Harness v1.2.3+build.4" })
    Check(DesktopClientService.IsClientDisplayName(name), $"recognize {name}");
foreach (var name in new[] { null, "", "DeepSeek Harness Tools", "Uninstall DeepSeek Harness", "DeepSeek HarnessFake", "DeepSeek Harness 0.2.0-rc.2 Helper" })
    Check(!DesktopClientService.IsClientDisplayName(name), $"reject unrelated name: {name}");

var folder = Path.Combine(Path.GetTempPath(), $"AnotherDSHL Desktop Detection {Guid.NewGuid():N}");
var executable = Path.Combine(folder, "DeepSeek Harness.exe");
var uninstall = Path.Combine(folder, "Uninstall DeepSeek Harness.exe");
string[] Candidates(string? location, string? icon, string? command) =>
    DesktopClientService.GetRegistryExecutableCandidates(location, icon, command).ToArray();
Check(Candidates(folder, null, null).SequenceEqual(new[] { executable }), "install directory with spaces");
Check(Candidates($"\"{folder}\"", null, null).SequenceEqual(new[] { executable }), "quoted install directory");
Check(Candidates(null, $"{executable},0", null).SequenceEqual(new[] { executable }), "unquoted display icon with index");
Check(Candidates("", $"\"{executable}\",0", null).SequenceEqual(new[] { executable }), "quoted display icon without install directory");
Check(Candidates(null, null, $"\"{uninstall}\" /currentuser /S").SequenceEqual(new[] { executable }), "uninstall command fallback");
Check(Candidates(null, null, $"{uninstall} /S").SequenceEqual(new[] { executable }), "unquoted uninstall command fallback");
Check(Candidates(folder, $"{executable},0", $"\"{uninstall}\" /S").Length == 1, "deduplicate installation paths");
Check(Candidates("relative", "relative.exe,0", "relative.exe /S").Length == 0, "ignore relative paths");
Check(Candidates(null, "not an executable", null).Length == 0, "ignore invalid icon");
Check(Candidates(null, "%TEMP%\\DeepSeek Harness.exe,0", null).Single() ==
    Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "DeepSeek Harness.exe"), "expand registry environment variables");

try
{
    Directory.CreateDirectory(folder);
    File.WriteAllText(executable, "Incomplete desktop fixture");
    Check(!DesktopClientService.IsClientExecutable(executable), "reject incomplete installation");
    Directory.CreateDirectory(Path.Combine(folder, "resources"));
    File.WriteAllText(Path.Combine(folder, "resources", "app.asar"), "fixture");
    Check(DesktopClientService.IsClientExecutable(executable), "recognize complete client layout");
    Check(!DesktopClientService.IsClientExecutable(uninstall), "reject uninstaller as client");
    var manual = DesktopClientService.Detect(executable);
    Check(manual?.ExecutablePath == executable && manual.IsManual, "preserve manual path priority");
}
finally
{
    Directory.Delete(folder, true);
}

if (args.Length == 1)
{
    var expected = Path.GetFullPath(args[0]);
    var automatic = DesktopClientService.Detect();
    Check(automatic is { IsManual: false } && automatic.ExecutablePath.Equals(expected, StringComparison.OrdinalIgnoreCase),
        "detect actual signed installation independently of Web workspace");
    Console.WriteLine($"Installed: {automatic!.ExecutablePath} · {automatic.Version}");
    var fallback = DesktopClientService.Detect(Path.Combine(folder, "missing.exe"));
    Check(fallback?.ExecutablePath == automatic.ExecutablePath && !fallback.IsManual, "stale manual path falls back to automatic detection");
}

Console.WriteLine($"Passed {checks} desktop detection checks.");
