using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AnotherDSHL;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(WindowDragRegion);
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 520));
        RootFrame.Navigate(typeof(MainPage));
    }

    public void SetBackdropMode(string mode)
    {
        SystemBackdrop = mode == "Mica"
            ? new MicaBackdrop()
            : new DesktopAcrylicBackdrop();
    }

    public void SetCornerBrandVisible(bool visible)
        => CornerAppLogo.Visibility = Visibility.Collapsed;
}
