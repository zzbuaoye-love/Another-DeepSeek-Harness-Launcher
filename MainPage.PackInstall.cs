using System.Text.Json;
using AnotherDSHL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnotherDSHL;

public sealed partial class MainPage
{
    private readonly PackForgeEngineService _packEngine = new();
    private bool _packInstalling;
    private bool _packChoicesReady;
    private CancellationTokenSource? _packInstallCancellation;
    private InstalledPack? _activePack;
    private InstalledPack? _detailInstalledPack;

    private void RefreshInstalledPacks()
    {
        _packChoicesReady = false;
        var id = _activePack?.Id ?? LauncherSettings.LoadActivePackId();
        var packs = _demoMode ? Array.Empty<InstalledPack>() : _packEngine.LoadInstalled();
        HomePackComboBox.Items.Clear();
        HomePackComboBox.Items.Add(new ComboBoxItem { Content = "标准 DSH（不使用整合包）", Tag = "" });
        InstalledPacksComboBox.Items.Clear();
        foreach (var pack in packs)
        {
            HomePackComboBox.Items.Add(new ComboBoxItem { Content = pack.DisplayName, Tag = pack });
            InstalledPacksComboBox.Items.Add(new ComboBoxItem { Content = pack.DisplayName, Tag = pack });
        }
        HomePackComboBox.SelectedItem = HomePackComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (item.Tag as InstalledPack)?.Id == id) ?? HomePackComboBox.Items[0];
        _activePack = (HomePackComboBox.SelectedItem as ComboBoxItem)?.Tag as InstalledPack;
        if (packs.Count > 0) InstalledPacksComboBox.SelectedIndex = 0;
        _detailInstalledPack = _selectedPack is { } selected
            ? packs.FirstOrDefault(pack => pack.SourceSha256.Equals(selected.Sha256, StringComparison.OrdinalIgnoreCase)) : null;
        LaunchInstalledPackButton.Visibility = _detailInstalledPack is null ? Visibility.Collapsed : Visibility.Visible;
        _packChoicesReady = true;
        UpdateVersionSelectionUi();
        UpdateRemovalUi();
        if (_workspaces is not null && !_workspaceApplying && !WorkspaceChangeBlocked) ApplySelectedWorkspace();
    }

    private void HomePackComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_packChoicesReady) return;
        if (_starting || _serviceReady || _service is { HasExited: false } || _installingPlugin || _packRemoving || _removalConfirming)
        {
            _packChoicesReady = false;
            HomePackComboBox.SelectedItem = HomePackComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (item.Tag as InstalledPack)?.Id == _activePack?.Id) ?? HomePackComboBox.Items[0];
            _packChoicesReady = true;
            ServiceStatusText.Text = "请先停止当前服务，再切换启动实例。";
            return;
        }
        _activePack = (HomePackComboBox.SelectedItem as ComboBoxItem)?.Tag as InstalledPack;
        if (!_demoMode && _workspaceCatalogOverride is null && !LauncherSettings.SaveActivePackId(_activePack?.Id ?? ""))
            ServiceStatusText.Text = "已切换实例，但设置保存失败，下次打开可能需要重新选择。";
        else ServiceStatusText.Text = _activePack is { } pack ? $"准备启动 {pack.Title} · {pack.Profile}" : "本地服务未启动";
        var workspace = WorkspaceTextBox.Text.Trim();
        WorkspaceNameText.Text = Directory.Exists(workspace) ? new DirectoryInfo(workspace).Name : "选择工作区";
        PackActivationText.Text = _activePack is null ? "" : "尚未启动，可先检查实例完整性。";
        UpdateVersionSelectionUi();
        if (_initialized) UpdateBranding();
        SaveWorkspaceConfiguration();
        RefreshPlugins();
    }

    private void SelectInstalledPack(InstalledPack pack)
    {
        if (_starting || _serviceReady || _service is { HasExited: false })
        {
            InstalledPackStatusText.Text = "已安装，请先停止当前服务，再从工作区选择整合包实例。";
            return;
        }
        LaunchModeComboBox.SelectedIndex = 0;
        HomePackComboBox.SelectedItem = HomePackComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => (item.Tag as InstalledPack)?.Id == pack.Id);
        ShellNavigation.SelectedItem = HomeItem;
    }

    private void UseInstalledPack_Click(object sender, RoutedEventArgs e)
    {
        if ((InstalledPacksComboBox.SelectedItem as ComboBoxItem)?.Tag is InstalledPack pack) SelectInstalledPack(pack);
        else InstalledPackStatusText.Text = "请先安装或选择一个整合包实例。";
    }

    private void LaunchInstalledPackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailInstalledPack is { } pack) SelectInstalledPack(pack);
    }

    private async Task<PackEngineTools> GetPackToolsAsync()
    {
        RefreshProcessPath();
        var node = _manualNodeMode ? _manualNodePath : _nodePath ?? FindOnPath("node.exe");
        var tools = PackForgeEngineService.ResolveTools(node);
        var text = await ReadNodeVersionAsync(tools.Node);
        if (!Version.TryParse(text.TrimStart('v', 'V'), out var version) ||
            !((version.Major == 22 && version >= new Version(22, 19)) || version.Major >= 24))
            throw new InvalidOperationException("请在工作区配置 Node.js 22.19+ 或 24+ 后再安装整合包。");
        return tools;
    }

    private async Task InstallPackInLauncherAsync(Func<CancellationToken, Task<string>> getSource, TextBlock status)
    {
        if (_demoMode || _packInstalling || _packExporting || _packRemoving || _installingPlugin || _removalConfirming) return;
        _packInstalling = true;
        UpdateRemovalUi();
        using var cancellation = new CancellationTokenSource();
        _packInstallCancellation = cancellation;
        PackInstallProgressPanel.Visibility = Visibility.Visible;
        PackInstallProgressBar.IsIndeterminate = true;
        CancelPackInstallButton.Visibility = Visibility.Visible;
        CancelPackInstallButton.IsEnabled = true;
        DismissPackInstallButton.Visibility = Visibility.Collapsed;
        DownloadPackButton.IsEnabled = false;
        LaunchInstalledPackButton.Visibility = Visibility.Collapsed;
        var installedSuccessfully = false;
        var progress = new Progress<PackEngineProgress>(message =>
        {
            AppendLog($"[整合包/{message.Stage}] {message.Detail}");
            if (message.Stage != "log")
            {
                PackInstallProgressText.Text = message.Detail;
                status.Text = message.Detail;
            }
        });
        try
        {
            PackInstallProgressText.Text = "正在准备整合包…";
            var tools = await GetPackToolsAsync();
            var path = await getSource(cancellation.Token);
            PackInstallProgressText.Text = "正在解析整合包安装内容…";
            var info = await _packEngine.InspectAsync(path, tools, cancellation.Token);
            var dshVersion = info.DshVersion.Length > 0 ? info.DshVersion : _selectedDshVersion;
            if (dshVersion.Length == 0)
                dshVersion = (await DshVersionService.GetAvailableAsync()).Latest;
            cancellation.Token.ThrowIfCancellationRequested();
            var id = _packEngine.CreateInstanceId(info);
            PackInstallSummaryText.Text = $"{info.Title} v{info.Version}\nDSH {dshVersion} · {info.Profiles.Length} 个 Profile · {info.DependencyCount} 个依赖\n包内 {info.FileCount} 个文件 · 已完成格式检查";
            PackInstallTargetText.Text = $"独立实例：{Path.Combine(_packEngine.InstancesRoot, id)}\n工作目录：{WorkspaceTextBox.Text.Trim()}";
            PackInstallProfileComboBox.Items.Clear();
            foreach (var profile in info.Profiles.Where(profile => profile != "desktop")) PackInstallProfileComboBox.Items.Add(profile);
            if (PackInstallProfileComboBox.Items.Count == 0) throw new InvalidOperationException("此包只有 desktop Profile，请通过官方桌面客户端管理。");
            PackInstallProfileComboBox.SelectedItem = info.DefaultProfile;
            if (PackInstallProfileComboBox.SelectedItem is null) PackInstallProfileComboBox.SelectedIndex = 0;
            PackInstallDialog.XamlRoot = XamlRoot;
            if (await PackInstallDialog.ShowAsync() != ContentDialogResult.Primary)
            {
                status.Text = PackInstallProgressText.Text = "安装已取消，下载的包文件已保留。";
                return;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            var selectedProfile = PackInstallProfileComboBox.SelectedItem as string
                ?? throw new InvalidOperationException("请选择启动 Profile。");
            var installedPack = await _packEngine.InstallAsync(path, info, id, dshVersion, selectedProfile,
                WorkspaceTextBox.Text.Trim(), tools, progress, cancellation.Token, (await SelectNpmRegistryAsync(cancellation.Token)).Url);
            installedSuccessfully = true;
            RefreshInstalledPacks();
            InstalledPackStatusText.Text = $"已安装 {installedPack.DisplayName}，可在首页选择并启动。";
            status.Text = PackInstallProgressText.Text = InstalledPackStatusText.Text;
            AppendLog($"[ADL] 整合包安装完成：{installedPack.Id} · DSH {dshVersion} · Profile {selectedProfile}");
        }
        catch (OperationCanceledException)
        {
            status.Text = PackInstallProgressText.Text = "安装已取消，本次临时安装内容已清理；包文件已保留。";
            AppendLog("[ADL] 整合包安装已取消。");
        }
        catch (Exception ex)
        {
            status.Text = PackInstallProgressText.Text = $"安装未完成：{ex.Message}";
            AppendLog($"[ADL] 整合包安装失败：{ex}");
        }
        finally
        {
            _packInstalling = false;
            _packInstallCancellation = null;
            PackInstallProgressBar.IsIndeterminate = false;
            PackInstallProgressBar.Value = installedSuccessfully ? 100 : 0;
            CancelPackInstallButton.Visibility = Visibility.Collapsed;
            DismissPackInstallButton.Visibility = Visibility.Visible;
            DownloadPackButton.IsEnabled = _selectedPack?.CanDownload == true && !_demoMode;
            UpdatePackFileUi();
            UpdateRemovalUi();
        }
    }

    private void CancelPackInstall_Click(object sender, RoutedEventArgs e)
    {
        CancelPackInstallButton.IsEnabled = false;
        PackInstallProgressText.Text = "正在取消并清理临时安装内容…";
        _packInstallCancellation?.Cancel();
    }

    private void DismissPackInstall_Click(object sender, RoutedEventArgs e) => PackInstallProgressPanel.Visibility = Visibility.Collapsed;

}
