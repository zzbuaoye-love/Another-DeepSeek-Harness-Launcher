using System.Net;
using System.Security.Cryptography;
using System.Text;
using AnotherDSHL.Services;
using AnotherDSHL.Installer.Core;

internal static class UpdateChecks
{
    internal static async Task Run(string root, string deltaPath, Action<bool, string> check)
    {
        async Task Reject(Func<Task> action, string label)
        {
            try { await action(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException)
            { check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }
        const string setupName = "AnotherDSHL-v3.0.0-win-x64-Setup.exe";
        const string deltaName = "AnotherDSHL-v3.0.0-win-x64-Update.adup";
        string cache = Path.Combine(root, "download-cache");
        byte[] setup = Encoding.UTF8.GetBytes("fixture setup data; never executed");
        byte[] delta = await File.ReadAllBytesAsync(deltaPath);
        string sums = Convert.ToHexString(SHA256.HashData(setup)) + "  " + setupName + "\n" +
            Convert.ToHexString(SHA256.HashData(delta)) + "  " + deltaName + "\n";
        var responses = new Dictionary<string, byte[]> { [setupName] = setup, [deltaName] = delta, ["SHA256SUMS.txt"] = Encoding.UTF8.GetBytes(sums) };
        var handler = new FixtureHandler(responses);
        using var client = new HttpClient(handler);
        AppReleaseAsset Asset(string name) => new(name, responses[name].Length,
            "https://github.com/zzbuaoye-love/Another-DeepSeek-Harness-Launcher/releases/download/v3.0.0/" + name);
        var release = new AppRelease("v3.0.0", "fixture", false, false, [Asset(setupName), Asset(deltaName), Asset("SHA256SUMS.txt")]);
        var stable = release with { Tag = "v3.1.0" };
        var beta = release with { Tag = "v4.0.0-beta.1", Prerelease = true };
        var draft = release with { Tag = "v9.0.0", Draft = true };
        check(AppUpdateService.SelectRelease([stable, beta, draft], "2.0.0", false) == stable, "stable update channel excludes beta and draft");
        check(AppUpdateService.SelectRelease([stable, beta, draft], "2.0.0", true) == beta, "beta update channel chooses newest eligible release");
        check(AppUpdateService.SelectRelease([release with { Assets = [] }, release with { Tag = "bad" }], "2.0.0", true) is null,
            "incomplete releases and invalid tags are ignored");

        var portable = new AppUpdateService(client, "2.0.0", false, cache);
        var prepared = await portable.PrepareAsync(release);
        check(!prepared.Differential && File.ReadAllBytes(prepared.Path).SequenceEqual(setup), "portable build prepares verified full setup");
        await AppUpdateService.VerifyPreparedAsync(prepared);
        check(new AppUpdateService(client, "2.0.0", false, cache).LoadPrepared() == prepared, "downloaded update survives service restart");
        await File.WriteAllTextAsync(prepared.Path, "modified");
        await Reject(() => AppUpdateService.VerifyPreparedAsync(prepared), "prepared update tampering rejected before launch");
        check(new AppUpdateService(client, "3.0.0", false, cache).LoadPrepared() is null, "completed update is not offered again");

        var installed = new AppUpdateService(client, "2.0.0", true, cache);
        prepared = await installed.PrepareAsync(release);
        check(prepared.Differential && UpdatePackageService.ValidatePackage(prepared.Path).From == "2.0.0", "matching installed version selects verified differential");
        var fallback = await new AppUpdateService(client, "1.0.0", true, cache).PrepareAsync(release);
        check(!fallback.Differential, "wrong differential base falls back to full setup");
        var local = await installed.PrepareLocalAsync(deltaPath);
        check(local.Differential && local.Path != deltaPath && File.ReadAllBytes(local.Path).SequenceEqual(delta), "local update is copied to a verified private snapshot");
        await Reject(() => new AppUpdateService(client, "1.0.0", true, cache).PrepareLocalAsync(deltaPath), "local update rejects wrong installed version");

        string failedCache = Path.Combine(root, "failed-cache");
        handler.CorruptSetup = true;
        await Reject(() => new AppUpdateService(client, "2.0.0", false, failedCache).PrepareAsync(release), "downloaded update checksum mismatch rejected");
        check(!Directory.EnumerateFileSystemEntries(failedCache).Any(), "failed download cleans its private cache");
        handler.CorruptSetup = false;
        handler.DelaySetup = true;
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            await Reject(() => new AppUpdateService(client, "2.0.0", false, failedCache).PrepareAsync(release, token: cancellation.Token), "update download cancellation is observed");
        check(!Directory.EnumerateFileSystemEntries(failedCache).Any(), "cancelled download leaves no partial cache");
        handler.DelaySetup = false;
        var unsafeRelease = release with { Assets = [Asset("SHA256SUMS.txt"), Asset(setupName) with { Url = "https://example.com/setup.exe" }] };
        await Reject(() => portable.PrepareAsync(unsafeRelease), "update refuses asset outside repository release URL");
    }

    private sealed class FixtureHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public bool CorruptSetup { get; set; }
        public bool DelaySetup { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string name = Path.GetFileName(request.RequestUri!.AbsolutePath);
            if (name.EndsWith("Setup.exe") && DelaySetup) await Task.Delay(TimeSpan.FromSeconds(10), token);
            var data = files[name];
            if (name.EndsWith("Setup.exe") && CorruptSetup) data = Enumerable.Repeat((byte)'x', data.Length).ToArray();
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
        }
    }
}
