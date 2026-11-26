using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace NuGetCollector;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private bool _isRunning;

    public MainWindow()
    {
        InitializeComponent();
        SourceBox.Text = _settings.SourceDirectory;
        TargetBox.Text = _settings.TargetDirectory;
        PackBox.IsChecked = _settings.PackProjects;
        IncludeNewBox.IsChecked = _settings.IncludeNewProjects;
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e) => Browse(SourceBox);

    private void BrowseTarget_Click(object sender, RoutedEventArgs e) => Browse(TargetBox);

    private void Browse(TextBox box)
    {
        var dialog = new OpenFolderDialog();
        if (Directory.Exists(box.Text))
            dialog.InitialDirectory = box.Text;

        if (dialog.ShowDialog(this) == true)
        {
            box.Text = dialog.FolderName;
            SaveSettings();
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        var source = SourceBox.Text.Trim();
        var target = TargetBox.Text.Trim();
        if (source.Length == 0 || target.Length == 0)
        {
            StatusText.Text = "Choose both folders first.";
            return;
        }

        SaveSettings();
        var packProjects = PackBox.IsChecked == true;
        var includeNew = IncludeNewBox.IsChecked == true;
        var progress = new Progress<string>(message =>
        {
            // Progress is delivered asynchronously; don't overwrite the final summary.
            if (_isRunning) StatusText.Text = message;
        });
        _isRunning = true;
        InputPanel.IsEnabled = false;
        ResultsGrid.ItemsSource = null;
        WarningsPanel.Visibility = Visibility.Collapsed;
        StatusText.Text = "Scanning...";

        try
        {
            var result = await Task.Run(() => PackageCollector.RunAsync(
                source, target, packProjects, includeNew, progress));

            ResultsGrid.ItemsSource = result.Packages;
            WarningsList.ItemsSource = result.Warnings;
            WarningsPanel.Header = $"Warnings ({result.Warnings.Count})";
            WarningsPanel.Visibility = result.Warnings.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            var failed = result.Packages.Count(p => p.Status.StartsWith("Failed"));
            var scanned = $"Scanned {result.ScannedCount} package file(s)" +
                (packProjects
                    ? $" and {result.ProjectCount} project(s), " +
                      $"{result.MsBuildReadCount} checked with MSBuild."
                    : ".");
            var outcome = result.Packages.Count == 0
                ? "Nothing was moved."
                : $"Succeeded {result.Packages.Count - failed}, failed {failed}.";
            var notes = result.Warnings.Count > 0 ? " See warnings." : "";
            var timing = $" (scan {result.ScanElapsed.TotalSeconds:0.0} s, total {result.TotalElapsed.TotalSeconds:0.0} s)";
            StatusText.Text = $"{scanned} {outcome}{notes}{timing}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
        finally
        {
            _isRunning = false;
            InputPanel.IsEnabled = true;
        }
    }

    private void SaveSettings()
    {
        _settings.SourceDirectory = SourceBox.Text.Trim();
        _settings.TargetDirectory = TargetBox.Text.Trim();
        _settings.PackProjects = PackBox.IsChecked == true;
        _settings.IncludeNewProjects = IncludeNewBox.IsChecked == true;
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Could not save settings: {ex.Message}";
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isRunning)
        {
            // Closing mid-move could leave a partial file in the target folder.
            e.Cancel = true;
            StatusText.Text = "Please wait for the update to finish before closing.";
            return;
        }
        SaveSettings();
    }
}
