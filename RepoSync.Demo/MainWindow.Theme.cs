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
        ApplyPalette(dark);
    }

    // Selected-row and row-button colours (the brushes are DynamicResources in MainWindow.xaml).
    void ApplyPalette(bool dark)
    {
        static SolidColorBrush B(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        (string Key, string Dark, string Light)[] palette =
        [
            ("RowSelectedBackground",         "#264F78", "#CCE4F7"),
            ("RowSelectedForeground",         "#FFFFFF", "#000000"),
            ("RowButtonBackground",           "#3A3F44", "#F3F3F3"),
            ("RowButtonBorder",               "#6B737B", "#9A9A9A"),
            ("RowButtonForeground",           "#F0F0F0", "#1A1A1A"),
            ("RowButtonHoverBackground",      "#566069", "#DDE8F5"),
            ("RowButtonHoverBorder",          "#9CC2FF", "#2F7BD6"),
            ("RowButtonPressedBackground",    "#2B2F33", "#C5D6EA"),
            ("RowButtonDisabledBackground",   "#2A2D30", "#EDEDED"),
            ("RowButtonDisabledBorder",       "#3C4044", "#D6D6D6"),
            ("RowButtonDisabledForeground",   "#7C8288", "#A3A3A3"),
        ];
        foreach (var (key, d, l) in palette)
            Resources[key] = B(dark ? d : l);
    }

    static bool SystemUsesDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is 0;
    }
}
