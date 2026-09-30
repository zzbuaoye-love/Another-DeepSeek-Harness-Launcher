using System.Diagnostics;
using System.Runtime.InteropServices;
using AnotherDSHL.Installer.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.Graphics;
using Windows.UI;

namespace AnotherDSHL.Installer;

internal static class Program
{
    internal static readonly string[] Arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
    [STAThread]
    public static void Main()
    {
        // The installed uninstaller relocates only manifest-owned files so its runtime can be removed.
        if (Arguments.Contains("--uninstall") && !Arguments.Contains("--relocated"))
        {
            string source = AppContext.BaseDirectory;
            string temp = Path.Combine(Path.GetTempPath(), "AnotherDSHL", "uninstall-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                foreach (var file in PayloadManifest.Read(source).Files)
                {
                    string target = Installation.SafePath(temp, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Installation.SafePath(source, file.Path), target);
                }
                File.Copy(Path.Combine(source, PayloadManifest.FileName), Path.Combine(temp, PayloadManifest.FileName));
                var start = new ProcessStartInfo(Path.Combine(temp, "AnotherDSHL.Installer.exe")) { UseShellExecute = false, WorkingDirectory = temp };
                start.ArgumentList.Add("--uninstall"); start.ArgumentList.Add("--relocated");
                start.ArgumentList.Add("--target=" + Path.GetFullPath(source).TrimEnd('\\'));
                using var child = Process.Start(start)!;
                // Original exits immediately; child cleanup is best effort on the next setup run.
                return;
            }
            catch (Exception ex)
            {
                Installation.TryCleanup(temp);
                MessageBox(IntPtr.Zero, ex.Message, "无法启动卸载程序", 0x10);
                return;
            }
        }
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(init =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            try { _ = new InstallerApp(); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "AnotherDSHL-installer-error.txt"), ex.ToString()); throw; }
        });
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hwnd, string text, string caption, uint type);
}

public sealed partial class InstallerApp : Application
{
    private Window? _window;
    public InstallerApp()
    {
        UnhandledException += (_, e) => File.WriteAllText(Path.Combine(Path.GetTempPath(), "AnotherDSHL-installer-error.txt"), e.Exception.ToString());
        InitializeComponent();
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { _window = new InstallerWindow(); _window.Activate(); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(Path.GetTempPath(), "AnotherDSHL-installer-error.txt"), ex.ToString()); throw; }
    }
}

internal sealed class InstallerWindow : Window
{
    private readonly Installation _installation = new();
    private readonly bool _uninstall = Program.Arguments.Contains("--uninstall");
    private readonly bool _preview = Program.Arguments.Contains("--preview");
    private readonly Grid _root = new();
    private readonly StackPanel _options = new() { Spacing = 10 };
    private readonly StackPanel _activity = new() { Spacing = 14, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _path = new() { MinWidth = 0, Height = 32, FontSize = 12, Padding = new Thickness(10, 4, 10, 4) };
    private readonly CheckBox _desktop = new() { Content = "桌面快捷方式", IsChecked = true, FontSize = 12 };
    private readonly CheckBox _startMenu = new() { Content = "添加到开始菜单", IsChecked = true, FontSize = 12 };
    private readonly TextBlock _heading = new() { Text = "安装 AnotherDSHL", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _subtitle = new() { Text = "为 DeepSeek Harness 准备一个便捷的入口。", FontSize = 12, Margin = new Thickness(0, 5, 0, 0) };
    private readonly TextBlock _size = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Text = "正在校验并安装文件…", FontSize = 14 };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
    private readonly Button _primary = new() { Content = "安装", MinWidth = 100 };
    private readonly Button _cancel = new() { Content = "取消", MinWidth = 72 };
    private readonly HyperlinkButton _license = new() { Content = "GPLv3 许可", Padding = new Thickness(0), FontSize = 11 };
    private bool _busy, _complete;
    private string _target = "";
    private PayloadManifest? _manifest;

    public InstallerWindow()
    {
        Title = _uninstall ? "卸载 AnotherDSHL" : "AnotherDSHL 安装程序";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        _root.Background = Brush("ApplicationPageBackgroundThemeBrush");
        if (Program.Arguments.Contains("--light")) _root.RequestedTheme = ElementTheme.Light;
        if (Program.Arguments.Contains("--dark")) _root.RequestedTheme = ElementTheme.Dark;
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(86) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
        var title = new Grid { Margin = new Thickness(16, 0, 140, 0) };
        title.Children.Add(new TextBlock { Text = "AnotherDSHL · " + (_uninstall ? "卸载" : "安装"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        _root.Children.Add(title); SetTitleBar(title);
        var hero = new Grid { Margin = new Thickness(24, 9, 24, 8), ColumnSpacing = 14 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        hero.ColumnDefinitions.Add(new ColumnDefinition());
        var logo = new Image { Width = 48, Height = 48, Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.targetsize-48_altform-lightunplated.png"))) };
        hero.Children.Add(logo);
        var brand = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(_heading); brand.Children.Add(_subtitle); Grid.SetColumn(brand, 1); hero.Children.Add(brand);
        Grid.SetRow(hero, 1); _root.Children.Add(hero);
        var body = new Grid { Margin = new Thickness(24, 10, 24, 10) };
        _options.Children.Add(new TextBlock { Text = "安装位置", FontSize = 12 });
        var pathRow = new Grid { ColumnSpacing = 8 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition()); pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(_path);
        var browse = new Button { Content = new FontIcon { Glyph = "\uE8B7", FontSize = 14 }, Width = 36, Height = 32, Padding = new Thickness(0) };
        ToolTipService.SetToolTip(browse, "选择安装目录");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(browse, "选择安装目录");
        browse.Click += Browse; Grid.SetColumn(browse, 1); pathRow.Children.Add(browse); _options.Children.Add(pathRow);
        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        checks.Children.Add(_desktop); checks.Children.Add(_startMenu); _options.Children.Add(checks);
        var legal = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        legal.Children.Add(new TextBlock { Text = "点击安装即表示接受", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        legal.Children.Add(_license); _options.Children.Add(legal);
        body.Children.Add(_options);
        _activity.Children.Add(_status); _activity.Children.Add(_progress); body.Children.Add(_activity);
        Grid.SetRow(body, 2); _root.Children.Add(body);
        var footer = new Grid { Padding = new Thickness(24, 12, 24, 12), ColumnSpacing = 8, Background = Brush("LayerFillColorDefaultBrush"), BorderBrush = Brush("DividerStrokeColorDefaultBrush"), BorderThickness = new Thickness(0, 1, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _size.Foreground = Brush("TextFillColorSecondaryBrush");
        footer.Children.Add(_size); Grid.SetColumn(_cancel, 1); footer.Children.Add(_cancel); Grid.SetColumn(_primary, 2); footer.Children.Add(_primary);
        _primary.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        Grid.SetRow(footer, 3); _root.Children.Add(footer);
        Content = _root;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96d;
        AppWindow.Resize(new SizeInt32((int)(529 * scale), (int)(352 * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsResizable = false; presenter.IsMaximizable = false; }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + (area.Width - AppWindow.Size.Width) / 2, area.Y + (area.Height - AppWindow.Size.Height) / 2));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _path.Text = _installation.InstalledDirectory ?? Installation.DefaultDirectory;
        _cancel.Click += (_, _) => Close();
        _primary.Click += InstallClick;
        _license.Click += async (_, _) => await ShowDialog("GPLv3 许可", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "LICENSE")));
        AppWindow.Closing += (_, e) => { if (_busy) e.Cancel = true; };
        if (_uninstall)
        {
            _target = Program.Arguments.FirstOrDefault(s => s.StartsWith("--target="))?[9..] ?? _installation.InstalledDirectory ?? "";
            _heading.Text = "卸载 AnotherDSHL"; _subtitle.Text = "移除程序文件，保留你的个人设置。";
            _options.Visibility = Visibility.Collapsed; _activity.Visibility = Visibility.Visible;
            _status.Text = "确定要卸载吗？"; _progress.Visibility = Visibility.Collapsed;
            _activity.Children.Add(new TextBlock { Text = _target, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            _primary.Content = "卸载";
        }
        else
        {
            try { _manifest = PayloadManifest.Read(AppContext.BaseDirectory); _size.Text = $"v{_manifest.Version} · 需要 {_manifest.InstalledBytes / 1048576d:F0} MB"; }
            catch (Exception ex) { _size.Text = _preview ? "界面预览 · 不会安装" : "安装文件不完整"; _primary.IsEnabled = _preview; _status.Text = ex.Message; }
        }
    }
    private static SolidColorBrush Brush(string key) => (SolidColorBrush)Application.Current.Resources[key];
    private async void Browse(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder != null) _path.Text = Path.Combine(folder.Path, Installation.ProductName);
    }
    private async void InstallClick(object sender, RoutedEventArgs e)
    {
        if (_complete)
        {
            if (!_uninstall && !_preview)
            {
                try { Process.Start(new ProcessStartInfo(Path.Combine(_target, Installation.ExecutableName)) { UseShellExecute = true, WorkingDirectory = _target }); }
                catch (Exception ex) { await ShowDialog("无法启动程序", ex.Message); return; }
            }
            Close(); return;
        }
        try
        {
            if (!_uninstall) _target = Installation.NormalizeDirectory(_path.Text);
            _busy = true; _primary.IsEnabled = _cancel.IsEnabled = false;
            _options.Visibility = Visibility.Collapsed; _activity.Visibility = Visibility.Visible;
            _progress.Visibility = Visibility.Visible; _progress.IsIndeterminate = _uninstall;
            _status.Text = _uninstall ? "正在移除程序文件…" : "正在校验并安装文件…";
            bool desktop = _desktop.IsChecked == true, startMenu = _startMenu.IsChecked == true;
            var progress = new Progress<double>(value => _progress.Value = value);
            if (_preview) { _progress.Value = 100; }
            else await Task.Run(() => { if (_uninstall) _installation.Uninstall(_target); else _installation.Install(AppContext.BaseDirectory, _target, desktop, startMenu, progress); });
            _complete = true; _heading.Text = _uninstall ? "已卸载" : "安装完成";
            _subtitle.Text = _uninstall ? "AnotherDSHL 已从这台电脑移除! 期待您的反馈" : "一切就绪，可以开始使用了。";
            _status.Text = _uninstall ? "个人设置和工作区已保留" : "AnotherDSHL 已成功安装。";
            _progress.Visibility = Visibility.Collapsed; _primary.Content = _uninstall ? "完成" : "启动";
            _cancel.Content = "关闭";
        }
        catch (Exception ex)
        {
            _options.Visibility = _uninstall ? Visibility.Collapsed : Visibility.Visible;
            _activity.Visibility = _uninstall ? Visibility.Visible : Visibility.Collapsed;
            _status.Text = "卸载未完成，可关闭程序后重试";
            await ShowDialog(_uninstall ? "卸载未完成" : "安装未完成", ex.Message);
        }
        finally { _busy = false; _primary.IsEnabled = _cancel.IsEnabled = true; }
    }
    private async Task ShowDialog(string title, string text)
    {
        var dialog = new ContentDialog { Title = title, CloseButtonText = "关闭", XamlRoot = _root.XamlRoot,
            Content = new ScrollViewer { MaxHeight = 160, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 12 } } };
        await dialog.ShowAsync();
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
