using AnotherDSHL.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnotherDSHL;

public sealed partial class MainPage
{
    // An isolated catalog lets native UI QA exercise persistence without changing the user's selections.
    private readonly string? _workspaceCatalogOverride = Environment.GetCommandLineArgs()
        .FirstOrDefault(arg => arg.StartsWith("--workspace-catalog=", StringComparison.OrdinalIgnoreCase))?[20..];
    private WorkspaceCatalogService? _workspaces;
    private bool _workspaceApplying;
    private bool _workspaceListRefreshing;
    private bool _workspaceAutomationReady;
    private bool _workspaceDiscovering;
    private bool _missingWorkspacePack;
    private bool _workspaceIsolationUiUpdating;
    private DateTime _lastWorkspaceDiscovery;

    private bool WorkspaceChangeBlocked => HasRunningHarness || _stoppingService || _installingPlugin ||
        _packInstalling || _packRemoving || _packExporting || _removalConfirming;

    private bool IsWorkspaceIsolated => _workspaces?.Selected?.IsolatedHarness == true;
    private WorkspaceHarnessEnvironment? GetWorkspaceEnvironment() => IsWorkspaceIsolated
        ? new(WorkspaceTextBox.Text.Trim(), _workspaceCatalogOverride is null ? null : Path.Combine(Path.GetDirectoryName(_workspaceCatalogOverride)!, "environments")) : null;
    private DshRuntimeService GetWorkspaceRuntimeService() => GetWorkspaceEnvironment()?.CreateRuntimeService() ?? _runtimes;
    private PackForgeEngineService GetWorkspacePackEngine() => GetWorkspaceEnvironment() is { } environment
        ? new(environment.InstancesRoot) : _packEngine;

    private async Task<InstalledPack> PrepareWorkspacePackAsync(InstalledPack pack, PackEngineTools tools,
        NpmRegistry registry, CancellationToken token = default)
    {
        if (GetWorkspaceEnvironment() is not { } environment) return pack;
        var progress = new Progress<PackEngineProgress>(value => AppendLog($"[ADL] 隔离整合包 · {value.Detail}"));
        AppendLog($"[ADL] 工作区专用整合包实例：{environment.InstancesRoot}");
        return await environment.PreparePackAsync(pack, _packEngine, GetWorkspacePackEngine(), WorkspaceTextBox.Text.Trim(), tools, registry, progress, token);
    }

    private void InitializeWorkspaces()
    {
        if (_demoMode)
        {
            _workspaceAutomationReady = false;
            WorkspaceDiscoverToggle.IsOn = WorkspaceRestoreToggle.IsOn = true;
            _workspaceAutomationReady = true;
            WorkspacesList.ItemsSource = new[] { new SavedWorkspace(@"C:\Demo\SampleProject", "0.1.5-rc.3"),
                new SavedWorkspace(@"C:\Demo\Website"), new SavedWorkspace(@"D:\Projects\Tools", "0.2.0-rc.2") };
            _workspaceListRefreshing = true;
            WorkspacesList.SelectedIndex = 0;
            _workspaceListRefreshing = false;
            UpdateWorkspaceConfigurationText();
            return;
        }
        var file = _workspaceCatalogOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnotherDSHL", "workspaces.json");
        _workspaces = new WorkspaceCatalogService(file, _activePack?.Workspace ?? LauncherSettings.LoadWorkspace(),
            _selectedDshVersion, _activePack?.Id ?? "");
        WorkspaceDiscoverToggle.IsOn = _workspaces.Current.AutoDiscover;
        WorkspaceRestoreToggle.IsOn = _workspaces.Current.RestoreConfiguration;
        _workspaceAutomationReady = true;
        ApplySelectedWorkspace();
        if (_workspaces.LoadError is { } error) WorkspaceManagerStatusText.Text = error;
    }

    private void RefreshWorkspaceList()
    {
        if (_workspaces is null) return;
        _workspaceListRefreshing = true;
        try
        {
            WorkspacesList.ItemsSource = _workspaces.Current.Items.ToArray();
            WorkspacesList.SelectedItem = _workspaces.Selected;
            RemoveWorkspaceButton.IsEnabled = _workspaces.Selected is not null;
        }
        finally { _workspaceListRefreshing = false; }
        UpdateWorkspaceHint();
        UpdateWorkspaceConfigurationText();
        UpdateWorkspaceIsolationUi();
    }

    private void ApplySelectedWorkspace()
    {
        if (_workspaces is null) return;
        _workspaceApplying = true;
        try
        {
            var selected = _workspaces.Selected;
            WorkspaceTextBox.Text = selected?.Path ?? "";
            if (_workspaces.Current.RestoreConfiguration || selected?.IsolatedHarness == true || selected is null)
            {
                var version = selected?.Version ?? "";
                _selectedDshVersion = DshVersionService.IsSafeVersion(version) ? version : "";
                _versionChoicesReady = false;
                var choice = DshVersionComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag as string == _selectedDshVersion);
                if (choice is null)
                {
                    choice = new ComboBoxItem { Content = $"v{_selectedDshVersion} · 已锁定", Tag = _selectedDshVersion };
                    DshVersionComboBox.Items.Add(choice);
                }
                DshVersionComboBox.SelectedItem = choice;
                _versionChoicesReady = true;
                var packId = selected?.PackId ?? "";
                var packChoice = HomePackComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (item.Tag as InstalledPack)?.Id == packId);
                if (packChoice is null && selected?.IsolatedHarness == true && packId.Length > 0)
                {
                    var privatePack = GetWorkspacePackEngine().LoadInstalled().FirstOrDefault(pack => pack.Id == packId);
                    if (privatePack is not null)
                    {
                        packChoice = new ComboBoxItem { Content = privatePack.DisplayName, Tag = privatePack };
                        HomePackComboBox.Items.Add(packChoice);
                    }
                }
                _missingWorkspacePack = packId.Length > 0 && packChoice is null;
                if (_missingWorkspacePack)
                {
                    packChoice = new ComboBoxItem { Content = "原整合包不可用 · 请重新选择", Tag = packId };
                    HomePackComboBox.Items.Add(packChoice);
                }
                HomePackComboBox.SelectedItem = packChoice ?? HomePackComboBox.Items[0];
                _activePack = (HomePackComboBox.SelectedItem as ComboBoxItem)?.Tag as InstalledPack;
            }
            else _missingWorkspacePack = false;
            if (_workspaceCatalogOverride is null)
            {
                LauncherSettings.SaveWorkspace(WorkspaceTextBox.Text);
                LauncherSettings.SaveDshVersion(_selectedDshVersion);
                LauncherSettings.SaveActivePackId(_activePack?.Id ?? "");
            }
            UpdateVersionSelectionUi();
            if (_initialized) UpdateBranding();
            if (_initialized) RefreshPlugins();
            WorkspaceManagerStatusText.Text = _missingWorkspacePack
                ? "此工作区绑定的整合包已不存在。请重新选择整合包或标准 DSH 后启动。"
                : selected is null ? "尚未添加工作区，请选择项目文件夹。"
                : $"当前工作区：{selected.Path} · {selected.Availability}";
        }
        finally { _workspaceApplying = false; }
        RefreshWorkspaceList();
    }

    private void SaveWorkspaceConfiguration()
    {
        if (_workspaceApplying || _demoMode || _workspaces?.Selected is null) return;
        try
        {
            _workspaces.SaveConfiguration(_selectedDshVersion, _activePack?.Id ?? "");
            _missingWorkspacePack = false;
            RefreshWorkspaceList();
        }
        catch (Exception ex) { WorkspaceManagerStatusText.Text = $"启动配置保存失败：{ex.Message}"; }
    }

    private void UpdateWorkspaceConfigurationText()
    {
        if (WorkspaceConfigurationText is null) return;
        WorkspaceConfigurationText.Text = _missingWorkspacePack ? "绑定的整合包不可用，请重新选择。"
            : _activePack is { } pack ? $"{pack.DisplayName} · DSH v{pack.DshVersion}（整合包锁定）"
            : _selectedDshVersion.Length == 0 ? "标准 DSH · 默认版本，每次启动检查更新"
            : $"标准 DSH · v{_selectedDshVersion}，不自动更新";
    }

    private void UpdateWorkspaceIsolationUi()
    {
        if (WorkspaceIsolationToggle is null) return;
        _workspaceIsolationUiUpdating = true;
        WorkspaceIsolationToggle.IsOn = IsWorkspaceIsolated;
        WorkspaceIsolationToggle.IsEnabled = _demoMode || _workspaces?.Selected is not null;
        _workspaceIsolationUiUpdating = false;
        WorkspaceIsolationDescription.Text = GetWorkspaceEnvironment() is { } environment
            ? $"此目录单独安装版本、插件和配置，首次启动需要准备资源。同一工作区可复用自己的缓存。环境位置：{environment.Root}"
            : "共享本机 Harness 缓存与默认配置；每个工作区仍记住自己的启动版本。开启后创建独立环境，不会导入共享环境中的账户或插件。";
    }

    private async void WorkspaceIsolation_Toggled(object sender, RoutedEventArgs e)
    {
        if (_workspaceIsolationUiUpdating || _demoMode || _workspaces?.Selected is null) return;
        if (WorkspaceChangeBlocked)
        {
            UpdateWorkspaceIsolationUi();
            WorkspaceManagerStatusText.Text = "请先停止服务并等待当前操作完成，再更改版本隔离。";
            return;
        }
        try
        {
            var isolated = WorkspaceIsolationToggle.IsOn;
            SaveWorkspaceConfiguration();
            _workspaces.SetIsolation(isolated);
            ApplySelectedWorkspace();
            WorkspaceManagerStatusText.Text = IsWorkspaceIsolated ? "已启用独立 Harness，下次启动使用此工作区的环境。" : "已切回共享环境；此前的独立环境保留。";
            if (_initialized) await CheckEnvironmentAsync();
        }
        catch (Exception ex) { UpdateWorkspaceIsolationUi(); WorkspaceManagerStatusText.Text = $"版本隔离保存失败：{ex.Message}"; }
    }

    private void AddWorkspace(string path)
    {
        if (WorkspaceChangeBlocked) { WorkspaceManagerStatusText.Text = "请先停止服务并等待当前操作完成，再添加或切换工作区。"; return; }
        if (_demoMode) { WorkspaceManagerStatusText.Text = "演示模式不保存工作区。"; return; }
        try
        {
            var item = _workspaces!.Add(path);
            _workspaces.Select(item.Path);
            ApplySelectedWorkspace();
            WorkspacePathInput.Text = "";
            DiscoveredWorkspacesList.ItemsSource = Array.Empty<SavedWorkspace>();
        }
        catch (Exception ex) { WorkspaceManagerStatusText.Text = $"添加失败：{ex.Message}"; }
    }

    private void AddWorkspace_Click(object sender, RoutedEventArgs e) => AddWorkspace(WorkspacePathInput.Text);

    private void WorkspacesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_workspaceListRefreshing || _workspaceApplying || _demoMode || _workspaces is null || WorkspacesList.SelectedItem is not SavedWorkspace item) return;
        if (WorkspaceCatalogService.SamePath(item.Path, _workspaces.Current.SelectedPath)) return;
        if (WorkspaceChangeBlocked)
        {
            RefreshWorkspaceList();
            WorkspaceManagerStatusText.Text = "请先停止服务并等待当前操作完成，再切换工作区。";
            return;
        }
        try { _workspaces.Select(item.Path); ApplySelectedWorkspace(); }
        catch (Exception ex) { RefreshWorkspaceList(); WorkspaceManagerStatusText.Text = $"切换失败：{ex.Message}"; }
    }

    private void RemoveWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_demoMode || _workspaces?.Selected is not { } selected) return;
        if (WorkspaceChangeBlocked) { WorkspaceManagerStatusText.Text = "请先停止服务并等待当前操作完成，再移除工作区。"; return; }
        try
        {
            _workspaces.Remove(selected.Path);
            ApplySelectedWorkspace();
            WorkspaceManagerStatusText.Text = $"已从列表移除 {selected.Name}，项目文件保留。";
        }
        catch (Exception ex) { WorkspaceManagerStatusText.Text = $"移除失败：{ex.Message}"; }
    }

    private void WorkspaceAutomation_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_workspaceAutomationReady || _demoMode || _workspaces is null) return;
        try { _workspaces.SetAutomation(WorkspaceDiscoverToggle.IsOn, WorkspaceRestoreToggle.IsOn); }
        catch (Exception ex) { WorkspaceManagerStatusText.Text = $"自动化设置保存失败：{ex.Message}"; }
    }

    private async Task DiscoverWorkspacesAsync(bool force = false)
    {
        if (_workspaceDiscovering || (!force && DateTime.UtcNow - _lastWorkspaceDiscovery < TimeSpan.FromMinutes(1))) return;
        if (_demoMode)
        {
            DiscoveredWorkspacesList.ItemsSource = new[] { new SavedWorkspace(@"C:\Users\Demo\source\repos\NewProject") };
            DiscoveredWorkspacesStatusText.Text = "发现 1 个候选项目（演示）。";
            return;
        }
        if (_workspaces is null) return;
        _workspaceDiscovering = true;
        DiscoverWorkspacesButton.IsEnabled = false;
        DiscoveredWorkspacesStatusText.Text = "正在检查常用项目位置…";
        try
        {
            var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new List<string> { Environment.CurrentDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Path.Combine(user, "source", "repos"), Path.Combine(user, "Projects"), Path.Combine(user, "Code") };
            roots.AddRange(_workspaces.Current.Items.Select(item => Path.GetDirectoryName(item.Path)).OfType<string>()
                .Where(path => !WorkspaceCatalogService.SamePath(Path.GetPathRoot(path), path)));
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
                foreach (var name in new[] { "Projects", "Code", "source\\repos" }) roots.Add(Path.Combine(drive.Name, name));
            var saved = _workspaces.Current.Items.Select(item => item.Path).ToArray();
            var items = await Task.Run(() => WorkspaceCatalogService.Discover(roots, saved));
            DiscoveredWorkspacesList.ItemsSource = items;
            DiscoveredWorkspacesStatusText.Text = items.Count > 0 ? $"发现 {items.Count} 个项目，点击添加并切换。" : "没有发现新的项目，可通过浏览或输入路径添加。";
            _lastWorkspaceDiscovery = DateTime.UtcNow;
        }
        catch (Exception ex) { DiscoveredWorkspacesStatusText.Text = $"查找失败：{ex.Message}"; }
        finally { _workspaceDiscovering = false; DiscoverWorkspacesButton.IsEnabled = true; }
    }

    private async void DiscoverWorkspaces_Click(object sender, RoutedEventArgs e) => await DiscoverWorkspacesAsync(force: true);
    private void DiscoveredWorkspacesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SavedWorkspace item) AddWorkspace(item.Path);
    }
    private void OpenEnvironment_Click(object sender, RoutedEventArgs e) => ShellNavigation.SelectedItem = EnvironmentItem;
    private void WorkspaceVersion_Click(object sender, RoutedEventArgs e)
    {
        LaunchModeComboBox.SelectedIndex = 0;
        ShellNavigation.SelectedItem = HomeItem;
        VersionTile.Flyout.ShowAt(VersionTile);
    }
}
