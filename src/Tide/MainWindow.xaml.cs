using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Tide;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => OnStateChanged();
        SourceInitialized += (_, _) => RoundCorners();
    }

    /// <summary>Set by the app: return true to hide to the tray instead of closing.</summary>
    public Func<bool>? ShouldHideOnClose { get; set; }

    /// <summary>Raised when the window is really closing (not hiding to the tray).</summary>
    public event Action? ExitRequested;

    private void OnStateChanged()
    {
        // With a custom chrome a maximized window overhangs the screen edge; pad it back in.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (ShouldHideOnClose?.Invoke() == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
        ExitRequested?.Invoke();
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // reliably bring to front
        Topmost = false;
        Focus();
    }

    private void RoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int pref = 2; // DWMWCP_ROUND (Windows 11; ignored on Windows 10)
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
        }
        catch
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
