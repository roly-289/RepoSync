using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace RepoSync;

public partial class MainWindow : Window
{
    static readonly string Ver = (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)) ?? "?";
    readonly ObservableCollection<RepoItem> _repos = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = $"Repo Sync v{Ver}";
        Grid.ItemsSource = _repos;
        var st = Settings.Load();
        UserBox.Text = st.Profile;
        TokenBox.Password = st.Token;
        FolderBox.Text = st.LocalFolder != "" ? st.LocalFolder
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        FeedBox.Text = st.NuGetFeed;
        ThemeBox.ItemsSource = new[] { "System", "Light", "Dark" };
        ThemeBox.SelectedItem = st.Theme is "System" or "Light" ? st.Theme : "Dark";
        ApplyTheme();
        ThemeBox.SelectionChanged += (_, _) => { ApplyTheme(); SaveSettings(); };
        IncludeNewBox.IsChecked = st.IncludeNewLibraries;
        Loaded += (_, _) => _ = ReloadFeed();
        FolderBox.LostFocus += (_, _) => RescanIfFolderChanged();
        _repos.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (RepoItem i in e.NewItems)
                {
                    if (_feed != null) _ = ScanRepo(i); // a repo added after startup
                    else _ = RefreshSolutions(i);
                    i.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(RepoItem.IsSelected)) SaveSettings(); };
                }
        };
        foreach (var r in st.Repos)
        {
            if (r.Status == "Working…") r.Status = "Interrupted";
            _repos.Add(r);
        }
        ShowSaved(st.LastSaved);
        Closing += (_, _) => SaveSettings();
    }

    void ShowSaved(DateTime? t) => SavedText.Text = t == null ? "Not saved yet" : $"Last saved: {t:dd MMM yyyy HH:mm:ss}";

    string ProfileName()
    {
        var u = UserBox.Text.Trim().TrimEnd('/');
        return u[(u.LastIndexOf('/') + 1)..]; // accepts a full profile URL
    }

    void SaveSettings()
    {
        try
        {
            var st = new Settings { Profile = UserBox.Text.Trim(), LocalFolder = FolderBox.Text.Trim(), NuGetFeed = FeedBox.Text.Trim(), Theme = ThemeBox.SelectedItem as string ?? "Dark", IncludeNewLibraries = IncludeNewBox.IsChecked == true, Token = TokenBox.Password, Repos = _repos.ToList() };
            st.Save();
            ShowSaved(st.LastSaved);
        }
        catch { /* non-fatal */ }
    }

    void Browse_Click(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog();
        if (d.ShowDialog() == true) { FolderBox.Text = d.FolderName; RescanIfFolderChanged(); }
    }

    void SelectAll_Click(object s, RoutedEventArgs e) { foreach (var r in _repos) r.IsSelected = true; }
    void SelectNone_Click(object s, RoutedEventArgs e) { foreach (var r in _repos) r.IsSelected = false; }

    async void Load_Click(object s, RoutedEventArgs e)
    {
        var user = ProfileName();
        var token = TokenBox.Password;
        if (user == "" && token == "") { MessageBox.Show("Enter a profile name or token."); return; }
        SaveSettings();
        LoadBtn.IsEnabled = false;
        try
        {
            var fetched = await GitHubClient.ListReposAsync(user, token);
            MergeRepos(fetched);

            var who = token == "" ? "no token" : await GitHubClient.DescribeTokenAsync(token);
            Title = $"Repo Sync v{Ver} - {_repos.Count} repos ({_repos.Count(r => r.Private)} private), {who}";
            if (fetched.Count == 0)
                MessageBox.Show(token != ""
                    ? "GitHub returned no repos for this token. For a fine-grained token, set Repository access to 'All repositories' (or select repos) and the right Resource owner."
                    : $"No public repos found for '{user}'. Private repos need a token.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Load failed"); }
        finally
        {
            LoadBtn.IsEnabled = true;
            SaveSettings();
        }
    }

    // Merge into the existing list so nothing already known (branch, commit, status, dates…) is lost.
    // Only repos GitHub no longer returns are removed; nothing changes at all if GitHub returned nothing.
    void MergeRepos(List<GitHubRepo> fetched)
    {
        if (fetched.Count == 0) return;
        var names = fetched.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _repos.Where(r => !names.Contains(r.Name)).ToList()) _repos.Remove(gone);
        for (int i = 0; i < fetched.Count; i++)
        {
            var f = fetched[i];
            var idx = _repos.ToList().FindIndex(r => string.Equals(r.Name, f.Name, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { _repos.Insert(Math.Min(i, _repos.Count), new RepoItem { Name = f.Name, CloneUrl = f.CloneUrl, Private = f.Private }); continue; }
            _repos[idx].CloneUrl = f.CloneUrl;
            _repos[idx].Private = f.Private;
            if (idx != i) _repos.Move(idx, i); // keep GitHub's order
        }
    }

    void Update_Click(object s, RoutedEventArgs e) => _ = UpdateRepos(_repos.Where(r => r.IsSelected).ToList());

    void RowUpdate_Click(object s, RoutedEventArgs e)
    {
        if (((FrameworkElement)s).DataContext is RepoItem r) _ = UpdateRepos([r]);
    }

    async void Check_Click(object s, RoutedEventArgs e)
    {
        var root = FolderBox.Text.Trim();
        if (_busy || _repos.Count == 0 || root == "") return;
        _busy = true;
        GitService.User = ProfileName();
        CheckBtn.IsEnabled = UpdateBtn.IsEnabled = false;
        foreach (var r in _repos) r.RemoteState = "Checking…";
        var gate = new SemaphoreSlim(4); // a few fetches at a time
        await Task.WhenAll(_repos.ToList().Select(async r =>
        {
            await gate.WaitAsync();
            try { r.RemoteState = await Task.Run(() => GitService.CheckRemote(root, r)); }
            catch (Exception ex) { r.RemoteState = "Error: " + ex.Message; }
            finally { gate.Release(); }
        }));
        var behind = _repos.Count(r => r.RemoteState.Contains("ehind") || r.RemoteState.Contains("newer branch"));
        CheckText.Text = $"{behind} of {_repos.Count} need updating (checked {DateTime.Now:HH:mm:ss})";
        CheckBtn.IsEnabled = UpdateBtn.IsEnabled = true;
        _busy = false;
        SaveSettings();
    }

    bool _busy;

    async Task UpdateRepos(List<RepoItem> sel)
    {
        var root = FolderBox.Text.Trim();
        if (_busy || sel.Count == 0 || root == "") return;
        _busy = true;
        GitService.User = ProfileName();
        Directory.CreateDirectory(root);
        UpdateBtn.IsEnabled = false;
        Grid.IsEnabled = false; // disables row buttons while running
        foreach (var r in sel)
        {
            var (oldBranch, oldCommit) = (r.Branch, r.LastCommit);
            r.Hold = true; // keep showing the old text until we animate the swap
            r.Outcome = "";
            r.Status = "Working…";
            try { r.Status = await Task.Run(() => GitService.Sync(root, r)); }
            catch (Exception ex) { r.Status = "Error: " + ex.Message; }
            if (r.Status.StartsWith("OK")) r.LastUpdated = DateTime.Now; // only successful syncs
            try { r.RemoteState = await Task.Run(() => GitService.CheckRemote(root, r)); } // refresh Remote column
            catch (Exception ex) { r.RemoteState = "Error: " + ex.Message; }
            var branchChanged = oldBranch != "" && r.Branch != oldBranch;
            var commitChanged = oldCommit != "" && r.LastCommit != oldCommit;
            try
            {
                await Task.WhenAll(
                    branchChanged ? TypeSwap(oldBranch, r.Branch, v => r.BranchShown = v) : Task.CompletedTask,
                    commitChanged ? TypeSwap(oldCommit, r.LastCommit, v => r.CommitShown = v) : Task.CompletedTask);
            }
            finally { r.Hold = false; r.BranchShown = r.Branch; r.CommitShown = r.LastCommit; }

            if (r.Status == "OK" && oldCommit != "") // we know what it was before, so we can say what happened
            {
                var changed = branchChanged || commitChanged;
                r.Status = changed ? "OK — updated" : "OK — no changes";
                r.Outcome = changed ? "updated" : "unchanged";
                // strong colour, settling to the faint tint the cell style keeps afterwards
                var strong = changed ? Colors.LightGreen : Colors.LightSteelBlue;
                var settle = Color.FromArgb(0x40, strong.R, strong.G, strong.B);
                Flash(r, "Status", strong, settle);
                Flash(r, "Message", strong, settle);
            }
        }
        Grid.IsEnabled = true;
        UpdateBtn.IsEnabled = true;
        _busy = false;
        SaveSettings();
        _ = ShowGraph(Grid.SelectedItem as RepoItem);
        foreach (var r in sel) _ = ScanRepo(r); // the checked-out code changed
    }
}
