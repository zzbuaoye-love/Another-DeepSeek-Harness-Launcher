using AnotherDSHL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnotherDSHL;

public sealed partial class MainPage
{
    private bool _packRemoving;
    private bool _removalConfirming;
    private bool HasRunningHarness => _starting || _serviceReady || _service is { HasExited: false };

    private string GetDefaultDshHome() => GetWorkspaceEnvironment()?.Home ??
        (Environment.GetEnvironmentVariable("DSH_HOME") is { Length: > 0 } home ? Path.GetFullPath(home) :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"));

    private string GetCurrentPluginDirectory() => _activePack is { } pack
        ? GetWorkspacePackEngine().GetProfileDirectory(pack) : Path.Combine(GetDefaultDshHome(), "profiles", "web");

    private void UpdateRemovalUi()
    {
        if (RemovePluginButton is null || RemoveInstalledPackButton is null) return;
        var available = !_demoMode && !_installingPlugin && !_packInstalling && !_packRemoving && !_packExporting && !_removalConfirming;
        RemovePluginButton.IsEnabled = available && PluginsList.SelectedItem is InstalledPlugin plugin && PluginProfileService.IsPackageName(plugin.Name);
        RemoveInstalledPackButton.IsEnabled = available && (InstalledPacksComboBox.SelectedItem as ComboBoxItem)?.Tag is InstalledPack;
        RemoveDetailPackButton.Visibility = _detailInstalledPack is null ? Visibility.Collapsed : Visibility.Visible;
        RemoveDetailPackButton.IsEnabled = available && _detailInstalledPack is not null;
        RemoveDetailPluginButton.IsEnabled = available && _selectedPlugin is { } selected && IsPluginInstalled(selected);
    }

    private void PluginsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRemovalUi();
    private void InstalledPacksComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRemovalUi();

    private async Task<bool> ConfirmRemovalAsync(ContentDialog dialog, TextBlock status)
    {
        if (_removalConfirming) return false;
        _removalConfirming = true;
        UpdateRemovalUi();
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        catch (Exception ex) { status.Text = $"无法打开删除确认：{ex.Message}"; return false; }
        finally { _removalConfirming = false; UpdateRemovalUi(); }
    }

    private async void RemovePlugin_Click(object sender, RoutedEventArgs e)
    {
        if (PluginsList.SelectedItem is InstalledPlugin plugin) await RemovePluginAsync(plugin, PluginRemovalStatusText);
    }

    private async void RemoveDetailPlugin_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPlugin is not { } selected) return;
        var plugin = PluginsList.Items.OfType<InstalledPlugin>().FirstOrDefault(item =>
            item.Name.Equals(selected.Name, StringComparison.OrdinalIgnoreCase) ||
            item.Name.Equals(selected.InstallSpec, StringComparison.OrdinalIgnoreCase) || item.InstallSpec == selected.InstallSpec);
        if (plugin is not null) await RemovePluginAsync(plugin, DetailStatusText);
    }

    private async Task RemovePluginAsync(InstalledPlugin plugin, TextBlock status)
    {
        if (_demoMode || _installingPlugin || _packInstalling || _packRemoving || _packExporting || _removalConfirming) return;
        if (HasRunningHarness) { status.Text = "请先停止当前 Web 服务，再删除插件。"; return; }
        if (!PluginProfileService.IsPackageName(plugin.Name)) { status.Text = "插件包名无效，无法删除。"; return; }
        var directory = GetCurrentPluginDirectory();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "删除插件",
            Content = $"插件：{plugin.Name}\nProfile：{directory}\n\n" +
                (plugin.IsDependency ? "将卸载插件依赖并移除启用配置。" : "此插件由运行时提供，将移除当前 Profile 的启用配置。") +
                "\n删除核心插件可能影响此 Profile 的启动。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (!await ConfirmRemovalAsync(dialog, status)) return;
        if (HasRunningHarness || GetCurrentPluginDirectory() != directory || _installingPlugin || _packInstalling || _packRemoving) return;
        _installingPlugin = true;
        UpdatePluginDetailState();
        UpdateRemovalUi();
        status.Text = $"正在删除 {plugin.Name}…";
        try
        {
            var current = PluginProfileService.Load(directory).FirstOrDefault(item => item.Name == plugin.Name)
                ?? throw new InvalidOperationException("插件配置已改变，请刷新后重试。");
            if (current.IsDependency) await RunPluginOperationAsync(_manualNodeMode ? _manualNodePath : _nodePath, current.Name, "remove");
            PluginProfileService.RemoveEnabledBundle(directory, current.Name);
            RefreshPlugins();
            if (PluginProfileService.Load(directory).Any(item => item.Name == current.Name))
                throw new InvalidOperationException("删除命令已完成，但插件仍在配置中，请查看日志。");
            status.Text = $"已删除插件：{current.Name}。";
            AppendLog($"[ADL] 已删除插件：{current.Name} · Profile：{directory}");
        }
        catch (Exception ex) { status.Text = $"删除未完成：{ex.Message}"; AppendLog($"[ADL] 插件删除失败：{ex.Message}"); }
        finally
        {
            var message = status.Text;
            _installingPlugin = false;
            RefreshPlugins();
            UpdateRemovalUi();
            status.Text = message;
        }
    }

    private async void RemoveInstalledPack_Click(object sender, RoutedEventArgs e)
    {
        if ((InstalledPacksComboBox.SelectedItem as ComboBoxItem)?.Tag is InstalledPack pack)
            await RemovePackAsync(pack, InstalledPackStatusText);
    }

    private async void RemoveDetailPack_Click(object sender, RoutedEventArgs e)
    {
        if (_detailInstalledPack is { } pack) await RemovePackAsync(pack, PackActionStatusText);
    }

    private async Task RemovePackAsync(InstalledPack pack, TextBlock status)
    {
        if (_demoMode || _packRemoving || _packInstalling || _packExporting || _installingPlugin || _removalConfirming) return;
        if (HasRunningHarness) { status.Text = "请先停止当前 Web 服务，再删除整合包。"; return; }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "删除整合包",
            Content = $"整合包：{pack.DisplayName}\n\n将删除此独立实例的运行时、插件、Profile 和实例内数据。\n工作目录中的项目文件、默认工作区以及下载的 .dspack 文件会保留。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (!await ConfirmRemovalAsync(dialog, status)) return;
        if (HasRunningHarness || _packInstalling || _packRemoving || _packExporting || _installingPlugin) return;
        _packRemoving = true;
        UpdateRemovalUi();
        status.Text = $"正在删除 {pack.Title}…";
        try
        {
            await Task.Run(() => _packEngine.Uninstall(pack));
            if (_activePack?.Id == pack.Id || LauncherSettings.LoadActivePackId() == pack.Id)
            {
                _activePack = null;
                LauncherSettings.SaveActivePackId("");
            }
            RefreshInstalledPacks();
            RefreshPlugins();
            WorkspaceNameText.Text = Directory.Exists(WorkspaceTextBox.Text.Trim())
                ? new DirectoryInfo(WorkspaceTextBox.Text.Trim()).Name : "选择工作区";
            UpdateBranding();
            status.Text = $"已删除整合包：{pack.Title}。";
            AppendLog($"[ADL] 已删除整合包实例：{pack.Id}");
        }
        catch (Exception ex) { status.Text = $"删除未完成：{ex.Message}"; AppendLog($"[ADL] 整合包删除失败：{ex.Message}"); }
        finally { _packRemoving = false; RefreshInstalledPacks(); UpdateRemovalUi(); }
    }
}
