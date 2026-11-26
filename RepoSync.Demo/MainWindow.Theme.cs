using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace RepoSync;

public partial class MainWindow
{
    // ---- Theme: System / Light / Dark (WPF's built-in Fluent theme) ---------------------------------------

    void ApplyTheme()
    {
        var choice = ThemeBox.SelectedItem as string ?? "Dark";
        Application.Current.ThemeMode = choice switch
        {
            "Light" => ThemeMode.Light,
            "System" => ThemeMode.System,
            _ => ThemeMode.Dark,
        };

        // Controls and the title bar follow ThemeMode. The commit graph is drawn by BranchKit, so set its colours here.
        var dark = choice == "Dark" || (choice == "System" && SystemUsesDarkMode());
        Graph.Background = new SolidColorBrush(dark ? Color.FromRgb(0x1E, 0x1E, 0x1E) : Colors.White);
        Graph.Foreground = new SolidColorBrush(dark ? Color.FromRgb(0xE6, 0xE6, 0xE6) : Colors.Black);
    }

    static bool SystemUsesDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }
}
