using System.Reflection;
using System.Windows;
using System.Windows.Input;
using CaptureIt.Services;

namespace CaptureIt;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        TxtVersion.Text = Loc.F("About.Version", version is null ? "2.0.0" : $"{version.Major}.{version.Minor}.{version.Build}");
    }

    private void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
