using AnotherDSHL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AnotherDSHL;

public sealed partial class MainPage
{
    private CancellationTokenSource? _appUpdateCancellation;
    private PreparedUpdate? _preparedAppUpdate;

    private void SetAppUpdateBusy(bool busy)
    {
        CheckAppUpdateButton.IsEnabled = LocalAppUpdateButton.IsEnabled = InstallPreparedAppUpdateButton.IsEnabled = !busy;
        AppUpdatePrereleaseCheckBox.IsEnabled = !busy;
        CancelAppUpdateButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelAppUpdateButton.IsEnabled = busy;
        AppUpdateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AppUpdateProgress.IsIndeterminate = busy;
        InstallPreparedAppUpdateButton.Visibility = _preparedAppUpdate is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void CheckAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) { AppUpdateStatus.Text = "演示模式下不会下载或安装更新。"; return; }
        if (_appUpdateCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _appUpdateCancellation = cancellation;
        SetAppUpdateBusy(true);
        try
        {
            AppUpdateStatus.Text = "正在检查 GitHub 发布版本…";
            var service = new AppUpdateService();
            var release = await service.CheckAsync(cancellation.Token, AppUpdatePrereleaseCheckBox.IsChecked == true);
            if (release is null) { AppUpdateStatus.Text = $"当前版本 {AppUpdateService.CurrentVersion}，暂无可用更新。"; return; }
            var confirm = new ContentDialog
            {
                Title = "发现新版本 " + release.Tag, XamlRoot = XamlRoot,
                PrimaryButtonText = "下载更新", CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Close,
                Content = new ScrollViewer { MaxHeight = 220, Content = new TextBlock { Text = release.Notes, TextWrapping = TextWrapping.Wrap } }
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) { AppUpdateStatus.Text = "可用更新：" + release.Tag; return; }
            cancellation.Token.ThrowIfCancellationRequested();
            AppUpdateStatus.Text = "正在下载并校验更新…";
            _preparedAppUpdate = await service.PrepareAsync(release, new Progress<double>(value =>
            {
                AppUpdateProgress.IsIndeterminate = false;
                AppUpdateProgress.Value = value;
                AppUpdateStatus.Text = $"下载更新 {value:F0}%";
            }), cancellation.Token);
            AppUpdateProgress.IsIndeterminate = true;
            await ConfirmPreparedAppUpdateAsync(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { AppUpdateStatus.Text = "更新操作已取消，可重新检查或下载。"; }
        catch (OperationCanceledException) { AppUpdateStatus.Text = "检查或下载超时，请稍后重试。"; }
        catch (Exception ex) { AppUpdateStatus.Text = "更新未完成：" + ex.Message; AppendLog("[ADL] 更新失败：" + ex.Message); }
        finally { _appUpdateCancellation = null; SetAppUpdateBusy(false); }
    }

    private async void LocalAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _appUpdateCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _appUpdateCancellation = cancellation;
        SetAppUpdateBusy(true);
        try
        {
            if (!AppUpdateService.IsInstalled) { AppUpdateStatus.Text = "差分更新需要安装版；当前版本请使用完整安装包。"; return; }
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".adup");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            AppUpdateStatus.Text = "正在校验本地更新包…";
            _preparedAppUpdate = await new AppUpdateService().PrepareLocalAsync(file.Path, cancellation.Token);
            await ConfirmPreparedAppUpdateAsync(cancellation.Token);
        }
        catch (OperationCanceledException) { AppUpdateStatus.Text = "本地更新准备已取消。"; }
        catch (Exception ex) { AppUpdateStatus.Text = "无法安装更新：" + ex.Message; }
        finally { _appUpdateCancellation = null; SetAppUpdateBusy(false); }
    }

    private async Task ConfirmPreparedAppUpdateAsync(CancellationToken token)
    {
        if (_preparedAppUpdate is not { } prepared) return;
        AppUpdateStatus.Text = $"{prepared.Version} 已校验，可稍后安装。";
        var dialog = new ContentDialog
        {
            Title = "更新已准备好", XamlRoot = XamlRoot, PrimaryButtonText = "退出并更新",
            CloseButtonText = "稍后", DefaultButton = ContentDialogButton.Close,
            Content = prepared.Differential ? "将打开差分更新程序。启动器会退出，设置和工作区会保留。"
                : "将打开完整安装程序。启动器会退出；安装版会使用原安装目录。"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        token.ThrowIfCancellationRequested();
        if (_packInstalling || _packExporting || _installingPlugin || _repairingNode || _desktopBusy)
            throw new InvalidOperationException("请先完成或取消当前安装任务，再安装启动器更新。");
        AppUpdateStatus.Text = "正在重新校验已下载更新…";
        await AppUpdateService.VerifyPreparedAsync(prepared, token);
        AppUpdateService.Launch(prepared);
        ((App)Application.Current).MainWindow.Close();
    }

    private async void InstallPreparedAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _appUpdateCancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _appUpdateCancellation = cancellation;
        SetAppUpdateBusy(true);
        try { await ConfirmPreparedAppUpdateAsync(cancellation.Token); }
        catch (OperationCanceledException) { AppUpdateStatus.Text = "更新操作已取消。"; }
        catch (Exception ex) { AppUpdateStatus.Text = "无法安装已下载更新：" + ex.Message; }
        finally { _appUpdateCancellation = null; SetAppUpdateBusy(false); }
    }

    private void CancelAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        CancelAppUpdateButton.IsEnabled = false;
        AppUpdateStatus.Text = "正在取消更新操作…";
        _appUpdateCancellation?.Cancel();
    }

    private async void AppReleases_Click(object sender, RoutedEventArgs e) =>
        await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/zzbuaoye-love/Another-DeepSeek-Harness-Launcher/releases"));
}
