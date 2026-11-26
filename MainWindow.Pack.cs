using System.IO;
using System.Windows;
using NuGet.Versioning;
using NuGetCollector;

namespace RepoSync;

public partial class MainWindow
{
    // ---- Pack: library projects that are ahead of the NuGet feed -------------------------------------------

    IReadOnlyDictionary<string, NuGetVersion>? _feed; // highest version of each package in the feed
    string _feedPath = "";
    readonly SemaphoreSlim _scanGate = new(2); // a few repos scanned at once

    void BrowseFeed_Click(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog();
        if (Directory.Exists(FeedBox.Text)) d.InitialDirectory = FeedBox.Text;
        if (d.ShowDialog() == true) { FeedBox.Text = d.FolderName; _ = ReloadFeed(); }
    }

    void Feed_LostFocus(object s, RoutedEventArgs e)
    {
        if (FeedBox.Text.Trim() != _feedPath) _ = ReloadFeed();
    }

    void IncludeNew_Click(object s, RoutedEventArgs e) { SaveSettings(); ScanAll(); }

    async Task ReloadFeed()
    {
        var path = FeedBox.Text.Trim();
        SaveSettings();
        _feedPath = path;
        if (path == "" || !Directory.Exists(path))
        {
            _feed = null;
            foreach (var r in _repos) { r.ScanGen++; r.SetPack(PackPhase.Unknown, null, "Set the NuGet feed folder to enable Pack"); _ = RefreshSolutions(r); }
            return;
        }
        foreach (var r in _repos) r.SetPack(PackPhase.Scanning);
        var feed = await PackageCollector.LoadFeedAsync(path);
        if (path != _feedPath) return; // changed again while loading
        _feed = feed;
        ScanAll();
    }

    string _scanRoot = "";

    // The Pack buttons depend on where the repos are, so a different local folder means rescanning.
    void RescanIfFolderChanged()
    {
        if (FolderBox.Text.Trim() == _scanRoot) return;
        _scanRoot = FolderBox.Text.Trim();
        if (_feed != null) ScanAll();
        else foreach (var r in _repos.ToList()) _ = RefreshSolutions(r);
    }

    void ScanAll()
    {
        _scanRoot = FolderBox.Text.Trim();
        foreach (var r in _repos.ToList()) _ = ScanRepo(r);
    }

    async Task ScanRepo(RepoItem r)
    {
        _ = RefreshSolutions(r);
        var gen = ++r.ScanGen;
        var feed = _feed;
        var feedPath = _feedPath;
        var dir = Path.Combine(FolderBox.Text.Trim(), r.Name);
        if (feed == null) { r.SetPack(PackPhase.Unknown, null, "Set the NuGet feed folder to enable Pack"); return; }
        if (!Directory.Exists(Path.Combine(dir, ".git"))) { r.SetPack(PackPhase.Unknown, null, "Not cloned"); return; }

        r.SetPack(PackPhase.Scanning);
        var includeNew = IncludeNewBox.IsChecked == true;
        await _scanGate.WaitAsync();
        try
        {
            if (gen != r.ScanGen) return;
            var scan = await Task.Run(() => PackageCollector.ScanRepoAsync(dir, feedPath, feed, includeNew));
            if (gen != r.ScanGen) return; // the repo changed while we were scanning; a newer scan is on its way
            r.SetPack(scan.Candidates.Count > 0 ? PackPhase.Ready : PackPhase.None, scan.Candidates);
        }
        catch (Exception ex)
        {
            if (gen == r.ScanGen) r.SetPack(PackPhase.Unknown, null, "Scan failed: " + ex.Message);
        }
        finally { _scanGate.Release(); }
    }

    void RowPack_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is RepoItem r) _ = PackRepo(r);
    }

    async Task PackRepo(RepoItem r)
    {
        if (_busy || r.Pack != PackPhase.Ready || _feedPath == "") return;
        _busy = true;
        Grid.IsEnabled = UpdateBtn.IsEnabled = CheckBtn.IsEnabled = false;
        var candidates = r.PackCandidates.ToList();
        r.ScanGen++; // ignore any scan that is still running for this repo
        r.SetPack(PackPhase.Packing, candidates);
        r.Status = "Working…";
        try
        {
            var results = await Task.Run(() => PackageCollector.PackAsync(candidates, _feedPath));
            var ok = results.Where(x => x.Status.StartsWith("Packed")).ToList();
            var failed = results.Where(x => !x.Status.StartsWith("Packed")).ToList();

            if (ok.Count > 0 && _feed != null) // keep the feed index current
            {
                var feed = new Dictionary<string, NuGetVersion>(_feed, StringComparer.OrdinalIgnoreCase);
                foreach (var p in ok) feed[p.Id] = NuGetVersion.Parse(p.Version);
                _feed = feed;
            }
            r.Status = failed.Count == 0
                ? $"OK — packed {string.Join(", ", ok.Select(x => $"{x.Id} {x.Version}"))}"
                : $"Pack failed: {failed[0].Id} {failed[0].Version}: {failed[0].Status}";
        }
        catch (Exception ex) { r.Status = "Pack failed: " + ex.Message; }
        finally
        {
            Grid.IsEnabled = UpdateBtn.IsEnabled = CheckBtn.IsEnabled = true;
            _busy = false;
            SaveSettings();
            _ = ScanRepo(r); // whatever is still ahead (e.g. after a failure) stays enabled
        }
    }
}
