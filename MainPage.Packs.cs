using System.Diagnostics;
using AnotherDSHL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AnotherDSHL;

public sealed partial class MainPage
{
    private IReadOnlyList<MarketPack> _marketPacks = Array.Empty<MarketPack>();
    private MarketPack? _selectedPack;
    private bool _packsLoaded;
    private bool _packsLoading;
    private bool _packDownloading;
    private string? _packProfilePath;
    private string? _packOutputPath;
    private bool _packExporting;
    private bool _packOpening;
    private string _packManagerPath = LauncherSettings.LoadPackForgePath();
    private string? _packFilePath;

    private async Task RefreshPacksAsync()
    {
        if (_packsLoading || _demoMode) return;
        _packsLoading = true;
        RefreshPacksButton.IsEnabled = false;
        PacksStatusText.Text = "正在读取官方整合包市场…";
        try
        {
            _marketPacks = await PackForgeMarketService.LoadAsync();
            _packsLoaded = true;
            PacksStatusText.Text = $"已收录 {_marketPacks.Count} 个整合包 · 数据来自 DSH PackForge 市场";
            FilterPacks();
        }
        catch (Exception ex)
        {
            PacksStatusText.Text = $"市场加载失败：{ex.Message}";
            AppendLog($"[ADL] 整合包市场加载失败：{ex.Message}");
        }
        finally
        {
            _packsLoading = false;
            RefreshPacksButton.IsEnabled = true;
        }
    }

    private async void RefreshPacksButton_Click(object sender, RoutedEventArgs e) => await RefreshPacksAsync();

    private void PacksSearchTextBox_TextChanged(object sender, TextChangedEventArgs e) => FilterPacks();

    private void FilterPacks()
    {
        if (PacksList is null || PacksSearchTextBox is null || PacksEmptyText is null) return;
        var query = PacksSearchTextBox.Text.Trim();
        var filtered = _marketPacks.Where(pack => query.Length == 0 ||
            pack.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            pack.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            pack.Author.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            pack.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        PacksList.ItemsSource = filtered;
        PacksEmptyText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PacksResultText.Text = $"显示 {filtered.Length} / {_marketPacks.Count} 个整合包";
    }

    private void PacksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedPack = PacksList.SelectedItem as MarketPack;
        PackDetailCard.Visibility = _selectedPack is null ? Visibility.Collapsed : Visibility.Visible;
        if (_selectedPack is not { } pack)
        {
            PackCatalogView.Visibility = Visibility.Visible;
            return;
        }
        PackCatalogView.Visibility = Visibility.Collapsed;
        PacksView.ChangeView(null, 0, null, true);
        PackDetailTitle.Text = $"{pack.Title}  v{pack.Version}";
        PackDetailMeta.Text = $"{pack.Author} · {pack.Owner}/{pack.Repo} · manifest v{pack.ManifestVersion} · " +
                              (pack.Type == "dshhome" ? "DSH_HOME" : "Profile") +
                              (pack.DshVersion.Length > 0 ? $" · DSH {pack.DshVersion}" : "");
        PackDetailDescription.Text = pack.Description.Length > 0 ? pack.Description : "作者未提供描述。";
        PackDetailContents.Text = pack.Type == "dshhome"
            ? $"{pack.ProfileCount} 个 Profile · {pack.BundleCount} 个 Bundle · {pack.DepCount} 个依赖"
            : $"{pack.BundleCount} 个 Bundle · {pack.DepCount} 个依赖";
        DownloadPackButton.IsEnabled = pack.CanDownload && !_packDownloading && !_demoMode;
        UpdatePackFileUi();
        PackActionStatusText.Text = pack.CanDownload
            ? "下载并校验后打开 PackForge 管理器；已下载的包可直接重新打开。"
            : pack.IsDspack ? "此条目缺少有效的大小或 SHA-256 校验信息，请到仓库查看安装方式。"
                : "此条目是历史格式，请到仓库查看安装方式。";
    }

    private void UpdatePackFileUi()
    {
        var path = _selectedPack is { } pack ? PackForgeMarketService.GetLocalPath(pack) : null;
        _packFilePath = path is not null && File.Exists(path) ? path : null;
        ShowPackFileButton.Visibility = _packFilePath is null ? Visibility.Collapsed : Visibility.Visible;
        PackDownloadedPathText.Visibility = ShowPackFileButton.Visibility;
        PackDownloadedPathText.Text = _packFilePath is null ? "" : $"已下载文件：{_packFilePath}";
        DownloadPackButton.Content = _packFilePath is null ? "下载并交给管理器安装" : "打开已下载包";
    }

    private void BackToPacks_Click(object sender, RoutedEventArgs e)
    {
        PackDetailCard.Visibility = Visibility.Collapsed;
        PackCatalogView.Visibility = Visibility.Visible;
        PacksList.SelectedItem = null;
        PacksView.ChangeView(null, 0, null, true);
    }

    private void PackTab_Click(object sender, RoutedEventArgs e)
    {
        var browse = (sender as Button)?.Tag as string == "browse";
        PacksBrowsePanel.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;
        PacksLocalPanel.Visibility = browse ? Visibility.Collapsed : Visibility.Visible;
        BrowsePacksTab.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(browse ? (byte)0x24 : (byte)0, 255, 255, 255));
        LocalPacksTab.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(browse ? (byte)0 : (byte)0x24, 255, 255, 255));
        BrowsePacksTab.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(browse ? (byte)0xFF : (byte)0xAF, 255, 255, 255));
        LocalPacksTab.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(browse ? (byte)0xAF : (byte)0xFF, 255, 255, 255));
        PacksView.ChangeView(null, 0, null, true);
    }

    private async void DownloadPackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _packDownloading || _packOpening || _selectedPack is not { } pack || !pack.CanDownload) return;
        _packDownloading = true;
        DownloadPackButton.IsEnabled = false;
        try
        {
            PackActionStatusText.Text = "正在校验本地整合包…";
            var path = await PackForgeMarketService.TryGetVerifiedLocalPathAsync(pack);
            if (path is null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "下载整合包",
                    Content = $"{pack.Title} v{pack.Version}\n作者：{pack.Author}\n大小：{pack.Size:N0} 字节\n\n下载并校验后，打开 DSH PackForge 管理器查看和安装。未安装管理器时，可获取安装版或选择已有的便携版程序。",
                    PrimaryButtonText = "下载",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                {
                    PackActionStatusText.Text = "下载已取消。";
                    return;
                }
                PackActionStatusText.Text = "正在下载并校验整合包…";
                path = await PackForgeMarketService.DownloadVerifiedAsync(pack);
            }
            UpdatePackFileUi();
            AppendLog($"[ADL] 整合包校验完成：{pack.Owner}/{pack.Repo} v{pack.Version}，文件：{path}");
            await OpenPackWithRecoveryAsync(path, PackActionStatusText, verified: true);
        }
        catch (Exception ex)
        {
            PackActionStatusText.Text = $"整合包下载或校验失败：{ex.Message}";
            AppendLog($"[ADL] 整合包下载或校验失败：{ex.Message}");
        }
        finally
        {
            _packDownloading = false;
            UpdatePackFileUi();
            DownloadPackButton.IsEnabled = _selectedPack?.CanDownload == true;
        }
    }

    private async Task OpenPackWithRecoveryAsync(string path, TextBlock status, bool verified = false)
    {
        if (_packOpening || _demoMode) return;
        _packOpening = true;
        var prefix = verified ? "整合包已下载并通过大小与 SHA-256 校验。" : "整合包文件已选择。";
        try
        {
            var result = await Task.Run(() => PackForgeLauncherService.Open(path, _packManagerPath));
            if (result == PackOpenResult.Opened)
            {
                status.Text = prefix + "已发送打开管理器的请求，请在管理器中确认安装。";
                AppendLog($"[ADL] 已发送整合包打开请求：{path}");
                return;
            }
            status.Text = prefix + "尚未找到可打开此包的管理器。请获取 PackForge 管理器或选择已有程序后重试；文件已保留。";
            AppendLog($"[ADL] 整合包文件已保留，未找到 PackForge 管理器或有效的 .dspack 文件关联：{path}");
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "整合包已准备好，需要 PackForge 管理器",
                Content = $"{prefix}\n{path}\n\n可下载并安装 DSH PackForge Setup，或选择已有的 DSH PackForge 程序（包括便携版）。完成后重新打开此包即可，无需重复下载。",
                PrimaryButtonText = "获取管理器",
                SecondaryButtonText = "选择已有程序",
                CloseButtonText = "稍后",
                DefaultButton = ContentDialogButton.Close
            };
            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Primary) OpenUrl(PackForgeLauncherService.ReleasesUrl);
            else if (choice == ContentDialogResult.Secondary && await PickPackManagerAsync(status) is { } manager)
            {
                if (await Task.Run(() => PackForgeLauncherService.Open(path, manager)) == PackOpenResult.Opened)
                {
                    status.Text = prefix + "已发送打开管理器的请求，请在管理器中确认安装。";
                    AppendLog($"[ADL] 已通过所选 PackForge 程序打开整合包：{path}");
                }
                else status.Text = prefix + "所选管理器未能启动，文件已保留，可重新选择程序。";
            }
        }
        catch (Exception ex)
        {
            status.Text = prefix + $"打开管理器失败：{ex.Message} 文件已保留，可稍后重试。";
            AppendLog($"[ADL] 打开整合包管理器失败（文件已保留）：{ex.Message}");
        }
        finally { _packOpening = false; }
    }

    private void OpenPackRepositoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPack is { } pack) OpenUrl(pack.RepositoryUrl);
    }

    private void ShowPackFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _packFilePath is null) return;
        try { PackForgeLauncherService.ShowFile(_packFilePath); }
        catch (Exception ex) { PackActionStatusText.Text = $"无法定位文件：{ex.Message}"; }
    }

    private async void OpenLocalPackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _packDownloading || _packOpening) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".dspack");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await OpenPackWithRecoveryAsync(file.Path, PackManagerStatusText);
    }

    private async Task<string?> PickPackManagerAsync(TextBlock status)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return null;
        if (!PackForgeLauncherService.IsManagerExecutable(file.Path))
        {
            status.Text = "请选择 DSH PackForge.exe 或 DSH PackForge 版本号.exe 便携版程序；Setup 是安装程序。";
            return null;
        }
        _packManagerPath = file.Path;
        LauncherSettings.SavePackForgePath(file.Path);
        status.Text = $"已保存管理器：{file.Path}，可以重新打开整合包。";
        PackManagerStatusText.Text = status.Text;
        return file.Path;
    }

    private async void SelectPackManagerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _packDownloading || _packOpening) return;
        var status = PackDetailCard.Visibility == Visibility.Visible ? PackActionStatusText : PackManagerStatusText;
        try { await PickPackManagerAsync(status); }
        catch (Exception ex) { status.Text = $"无法选择管理器：{ex.Message}"; }
    }

    private void OpenPackForgeButton_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(PackForgeLauncherService.ReleasesUrl);

    private async void SelectPackProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        var folder = await PickPackFolderAsync();
        if (folder is null) return;
        _packProfilePath = folder;
        PackProfilePathText.Text = folder;
    }

    private async void SelectPackOutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode) return;
        var folder = await PickPackFolderAsync();
        if (folder is null) return;
        _packOutputPath = folder;
        PackOutputPathText.Text = folder;
    }

    private async Task<string?> PickPackFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(((App)Application.Current).MainWindow));
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    private async void PreviewPackButton_Click(object sender, RoutedEventArgs e) =>
        await RunPackCommandAsync(preview: true);

    private async void ExportPackButton_Click(object sender, RoutedEventArgs e) =>
        await RunPackCommandAsync(preview: false);

    private async Task RunPackCommandAsync(bool preview)
    {
        if (_demoMode || _packExporting) return;
        if (_packProfilePath is null || (!preview && _packOutputPath is null))
        {
            PackExportStatusText.Text = preview ? "请先选择 Profile 目录。" : "请先选择 Profile 和输出目录。";
            return;
        }
        RefreshProcessPath();
        var cli = FindOnPath("dspack.exe");
        if (cli is null)
        {
            PackExportStatusText.Text = "未找到 dspack CLI。请安装 DSH PackForge Setup，然后重新打开启动器。";
            return;
        }
        if (!preview)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "导出 Profile 整合包",
                Content = $"来源：{_packProfilePath}\n输出：{_packOutputPath}\n\nPackForge 将扫描并过滤敏感文件，生成 manifest v5 的 .dspack。",
                PrimaryButtonText = "导出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        _packExporting = true;
        PreviewPackButton.IsEnabled = ExportPackButton.IsEnabled = false;
        PackExportStatusText.Text = preview ? "正在扫描 Profile…" : "正在导出整合包…";
        try
        {
            var start = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(preview ? "inspect" : "pack");
            start.ArgumentList.Add(_packProfilePath);
            if (!preview)
            {
                start.ArgumentList.Add("--out");
                start.ArgumentList.Add(_packOutputPath!);
            }
            using var process = new Process { StartInfo = start };
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PackForge 命令超过 10 分钟，已停止。");
            }
            var message = (await output + "\n" + await errors).Trim();
            PackExportStatusText.Text = (process.ExitCode == 0 ? "完成。\n" : $"未完成（退出代码 {process.ExitCode}）。\n") +
                                        (message.Length > 2400 ? message[..2400] + "\n…" : message);
            AppendLog($"[ADL] PackForge {(preview ? "预览" : "导出")}退出代码：{process.ExitCode}");
        }
        catch (Exception ex)
        {
            PackExportStatusText.Text = $"PackForge 命令失败：{ex.Message}";
            AppendLog($"[ADL] PackForge 命令失败：{ex.Message}");
        }
        finally
        {
            _packExporting = false;
            PreviewPackButton.IsEnabled = ExportPackButton.IsEnabled = true;
        }
    }
}
