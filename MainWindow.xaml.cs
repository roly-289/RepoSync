using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BranchKit;

namespace RepoSync;

public class RepoItem : INotifyPropertyChanged
{
    string _status = "";
    DateTime? _updated;
    string _remote = "", _branch = "", _commit = "", _branchShown = "", _commitShown = "", _outcome = "";
    bool _sel;
    public string Name { get; set; } = "";
    public string CloneUrl { get; set; } = "";
    public bool Private { get; set; }
    public bool IsSelected { get => _sel; set { _sel = value; Raise(nameof(IsSelected)); } }
    public DateTime? LastUpdated { get => _updated; set { _updated = value; Raise(nameof(LastUpdated)); Raise(nameof(LastUpdatedText)); } }

    /// <summary>Today: "12:24"; yesterday: "Yesterday"; within the last week: "Monday"; older: "20 Nov 2026".</summary>
    [JsonIgnore]
    public string LastUpdatedText
    {
        get
        {
            if (_updated is not { } t) return "";
            return (DateTime.Today - t.Date).Days switch
            {
                <= 0 => t.ToString("HH:mm"),
                1 => "Yesterday",
                < 7 => t.ToString("dddd"),
                _ => t.ToString("dd MMM yyyy"),
            };
        }
    }

    /// <summary>One icon for the whole row: ✔ fine, ✘ sync failed or out of sync with remote, – not cloned, … busy.</summary>
    [JsonIgnore]
    public string StatusIcon
    {
        get
        {
            if (_status == "Working…" || _remote == "Checking…") return "…";
            if (_status != "" && !_status.StartsWith("OK")) return "✘"; // last sync failed
            if (_remote == "") return _status.StartsWith("OK") ? "✔" : "";
            return _remote switch { "Not cloned" => "–", "Up to date" => "✔", _ => "✘" };
        }
    }

    /// <summary>What is wrong, or what just happened: sync error, else remote difference, else sync result.</summary>
    [JsonIgnore]
    public string Message
    {
        get
        {
            if (_status == "Working…") return "Working…";
            if (_remote == "Checking…") return "Checking…";
            if (_status != "" && !_status.StartsWith("OK")) return _status;
            if (_remote != "" && _remote != "Up to date") return _remote; // e.g. "Behind 2 · newer branch: dev"
            if (_status.StartsWith("OK"))
            {
                var m = _status.Length > 2 ? _status[2..].TrimStart(' ', '—', '-') : "";
                return m == "" ? "Synced" : char.ToUpper(m[0]) + m[1..];
            }
            return _remote == "Up to date" ? "Up to date" : "";
        }
    }

    [JsonIgnore] public string Detail => string.Join("\n", new[] { _status, _remote }.Where(x => x != ""));
    public string RemoteState { get => _remote; set { _remote = value; Raise(nameof(RemoteState)); Raise(nameof(StatusIcon)); Raise(nameof(Message)); Raise(nameof(Detail)); } }
    public string Branch { get => _branch; set { _branch = value; Raise(nameof(Branch)); if (!Hold) BranchShown = value; } }
    public string LastCommit { get => _commit; set { _commit = value; Raise(nameof(LastCommit)); if (!Hold) CommitShown = value; } }

    // What the grid displays. Normally equal to Branch / LastCommit, but while an update is animating
    // (Hold = true) the UI types the text out itself.
    [JsonIgnore] public bool Hold { get; set; }
    [JsonIgnore] public string BranchShown { get => _branchShown; set { _branchShown = value; Raise(nameof(BranchShown)); } }
    [JsonIgnore] public string CommitShown { get => _commitShown; set { _commitShown = value; Raise(nameof(CommitShown)); } }
    /// <summary>"updated", "unchanged" or "" – drives the faint tint on the Status cell.</summary>
    [JsonIgnore] public string Outcome { get => _outcome; set { _outcome = value; Raise(nameof(Outcome)); } }
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); Raise(nameof(StatusIcon)); Raise(nameof(Message)); Raise(nameof(Detail)); } }


    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public class Settings
{
    public string Profile { get; set; } = "";
    public string LocalFolder { get; set; } = "";
    public DateTime? LastSaved { get; set; }
    public List<RepoItem> Repos { get; set; } = new();
    public string ProtectedToken { get; set; } = ""; // DPAPI-encrypted (current Windows user)

    static readonly string PathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RepoSync", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathFile)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        LastSaved = DateTime.Now;
        Directory.CreateDirectory(Path.GetDirectoryName(PathFile)!);
        File.WriteAllText(PathFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public string Token
    {
        get
        {
            try { return ProtectedToken == "" ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        set => ProtectedToken = value == "" ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }
}

public partial class MainWindow : Window
{
    static readonly string Ver = (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)) ?? "?";
    readonly ObservableCollection<RepoItem> _repos = new();
    static readonly HttpClient Http = new();

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
        _repos.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (RepoItem i in e.NewItems)
                    i.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(RepoItem.IsSelected)) SaveSettings(); };
        };
        foreach (var r in st.Repos)
        {
            if (r.Status == "Working…") r.Status = "Interrupted";
            _repos.Add(r);
        }
        ShowSaved(st.LastSaved);
        Closing += (_, _) => SaveSettings();
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("RepoSync");
    }

    void ShowSaved(DateTime? t) => SavedText.Text = t == null ? "Not saved yet" : $"Last saved: {t:dd MMM yyyy HH:mm:ss}";

    // Profile name (accepts a full URL). Passed to git so Git Credential Manager doesn't ask which account to use.
    static string? GitUser;

    string ProfileName()
    {
        var u = UserBox.Text.Trim().TrimEnd('/');
        return u[(u.LastIndexOf('/') + 1)..];
    }

    int _graphSeq;

    void Grid_SelectionChanged(object s, SelectionChangedEventArgs e) => _ = ShowGraph(Grid.SelectedItem as RepoItem);

    async Task ShowGraph(RepoItem? r)
    {
        var seq = ++_graphSeq;
        if (r == null) return;
        var dir = Path.Combine(FolderBox.Text.Trim(), r.Name);
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            Graph.Tree = null;
            GraphInfo.Text = $"{r.Name}: not cloned yet";
            return;
        }
        GraphInfo.Text = $"{r.Name}: loading…";
        var (tree, info) = await Task.Run(() => BuildTree(dir));
        if (seq != _graphSeq) return; // selection changed meanwhile
        Graph.Tree = tree;
        GraphInfo.Text = $"{r.Name}: {info}";
    }

    // Commit graph (first-parent tree, latest 300 commits across all refs). Current = HEAD.
    static (BranchTree? tree, string info) BuildTree(string dir)
    {
        const int Max = 300;
        var (code, log) = Git(dir, $"log --all --topo-order --reverse -n {Max} --format=%H%x1f%P%x1f%ct%x1f%D%x1f%s");
        if (code != 0 || log == "") return (null, "no commits");

        var map = new Dictionary<string, BranchNode>();
        BranchTree? tree = null;
        BranchNode? root = null;
        var merges = 0;
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\x1f');
            if (f.Length < 5) continue;
            var label = f[3] == "" ? f[4] : $"{f[4]}  [{f[3]}]";
            var time = DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[2])).LocalDateTime;
            var node = new BranchNode<string>(f[0], label, time);
            var parents = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tree == null) { tree = new BranchTree(node); root = node; }
            else
            {
                // First parent = the line it continues; further parents = branches merged in (needs BranchKit with merge support).
                var first = parents.Length > 0 && map.TryGetValue(parents[0], out var p) ? p : root;
                var merged = parents.Skip(1).Select(h => map.GetValueOrDefault(h))
                    .Where(m => m != null && m != first).Distinct().Select(m => m!).ToList();
                if (merged.Count > 0) merges++;
                tree.Add(node, first, merged.Count > 0 ? merged : null);
            }
            map[f[0]] = node;
        }
        if (tree == null) return (null, "no commits");

        var head = Git(dir, "rev-parse HEAD").output;
        if (map.TryGetValue(head, out var h)) tree.Select(h);
        return (tree, $"{map.Count} commits, {merges} merges" + (map.Count == Max ? " (latest shown)" : ""));
    }

    void SaveSettings()
    {
        try
        {
            var st = new Settings { Profile = UserBox.Text.Trim(), LocalFolder = FolderBox.Text.Trim(), Token = TokenBox.Password, Repos = _repos.ToList() };
            st.Save();
            ShowSaved(st.LastSaved);
        }
        catch { /* non-fatal */ }
    }

    void Browse_Click(object s, RoutedEventArgs e)
    {
        var d = new Microsoft.Win32.OpenFolderDialog();
        if (d.ShowDialog() == true) FolderBox.Text = d.FolderName;
    }

    void SelectAll_Click(object s, RoutedEventArgs e) { foreach (var r in _repos) r.IsSelected = true; }
    void SelectNone_Click(object s, RoutedEventArgs e) { foreach (var r in _repos) r.IsSelected = false; }

    async void Load_Click(object s, RoutedEventArgs e)
    {
        var user = UserBox.Text.Trim().TrimEnd('/');
        user = user[(user.LastIndexOf('/') + 1)..]; // accept full profile URL
        var token = TokenBox.Password;
        if (user == "" && token == "") { MessageBox.Show("Enter a profile name or token."); return; }
        SaveSettings();
        LoadBtn.IsEnabled = false;
        try
        {
            Http.DefaultRequestHeaders.Authorization = token == "" ? null : new AuthenticationHeaderValue("Bearer", token);
            // With a token (and no profile) list the token owner's repos incl. private; otherwise public repos of the profile.
            var baseUrl = token != "" ? "https://api.github.com/user/repos?per_page=100" : $"https://api.github.com/users/{Uri.EscapeDataString(user)}/repos?per_page=100";
            var fetched = new List<(string Name, string Url, bool Private)>();
            for (int page = 1; ; page++)
            {
                var json = await Http.GetStringAsync($"{baseUrl}&page={page}");
                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement.EnumerateArray().ToList();
                foreach (var r in arr.Where(x => token != "" || user == "" || string.Equals(x.GetProperty("owner").GetProperty("login").GetString(), user, StringComparison.OrdinalIgnoreCase)))
                    fetched.Add((r.GetProperty("name").GetString()!, r.GetProperty("clone_url").GetString()!, r.GetProperty("private").GetBoolean()));
                if (arr.Count < 100) break;
            }

            // Merge into the existing list so nothing already known (branch, commit, status, dates…) is lost.
            // Only repos GitHub no longer returns are removed; nothing changes at all if GitHub returned nothing.
            if (fetched.Count > 0)
            {
                var names = fetched.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var gone in _repos.Where(r => !names.Contains(r.Name)).ToList()) _repos.Remove(gone);
                for (int i = 0; i < fetched.Count; i++)
                {
                    var f = fetched[i];
                    var idx = _repos.ToList().FindIndex(r => string.Equals(r.Name, f.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0) { _repos.Insert(Math.Min(i, _repos.Count), new RepoItem { Name = f.Name, CloneUrl = f.Url, Private = f.Private }); continue; }
                    _repos[idx].CloneUrl = f.Url;
                    _repos[idx].Private = f.Private;
                    if (idx != i) _repos.Move(idx, i); // keep GitHub's order
                }
            }
            string who = "no token";
            if (token != "")
            {
                try { using var d = JsonDocument.Parse(await Http.GetStringAsync("https://api.github.com/user")); who = "token user: " + d.RootElement.GetProperty("login").GetString(); }
                catch (Exception ex) { who = "TOKEN REJECTED: " + ex.Message; }
            }
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
        GitUser = ProfileName();
        CheckBtn.IsEnabled = UpdateBtn.IsEnabled = false;
        foreach (var r in _repos) r.RemoteState = "Checking…";
        var gate = new SemaphoreSlim(4); // a few fetches at a time
        await Task.WhenAll(_repos.ToList().Select(async r =>
        {
            await gate.WaitAsync();
            try { r.RemoteState = await Task.Run(() => CheckRemote(root, r)); }
            catch (Exception ex) { r.RemoteState = "Error: " + ex.Message; }
            finally { gate.Release(); }
        }));
        var behind = _repos.Count(r => r.RemoteState.Contains("ehind") || r.RemoteState.Contains("newer branch"));
        CheckText.Text = $"{behind} of {_repos.Count} need updating (checked {DateTime.Now:HH:mm:ss})";
        CheckBtn.IsEnabled = UpdateBtn.IsEnabled = true;
        _busy = false;
        SaveSettings();
    }

    // Fetches, then compares the checked-out branch with its upstream.
    static string CheckRemote(string root, RepoItem r)
    {
        var state = CheckUpstream(root, r);
        if (state.StartsWith("Not cloned") || state.StartsWith("Fetch failed")) return state;

        // Also flag when another remote branch has newer commits than the one checked out
        var dir = Path.Combine(root, r.Name);
        var cur = Git(dir, "rev-parse --abbrev-ref HEAD").output;
        r.Branch = cur;
        r.LastCommit = Git(dir, "log -1 --format=%s").output;
        var latest = Git(dir, "for-each-ref --sort=-committerdate --count=3 --format=%(refname:short) refs/remotes/origin").output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())
            .FirstOrDefault(x => x != "origin" && x != "origin/HEAD");
        if (latest != null && latest["origin/".Length..] != cur)
            state += $" · newer branch: {latest["origin/".Length..]}";
        return state;
    }

    static string CheckUpstream(string root, RepoItem r)
    {
        var dir = Path.Combine(root, r.Name);
        if (!Directory.Exists(Path.Combine(dir, ".git"))) return "Not cloned";
        var f = Git(dir, "fetch --all --prune");
        if (f.code != 0) return "Fetch failed: " + f.output;
        var (c, o) = Git(dir, "rev-list --left-right --count HEAD...@{u}");
        if (c != 0) return "No upstream branch";
        var p = o.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int ahead = int.Parse(p[0]), behind = int.Parse(p[1]);
        return (ahead, behind) switch
        {
            (0, 0) => "Up to date",
            (0, _) => $"Behind {behind}",
            (_, 0) => $"Ahead {ahead}",
            _ => $"Ahead {ahead}, behind {behind}",
        };
    }

    // Highlights a cell that changed: gold flash fading out over a few seconds.
    void Flash(RepoItem item, string header, Color color, Color settle)
    {
        try
        {
            Grid.UpdateLayout();
            var col = Grid.Columns.FirstOrDefault(c => (string?)c.Header == header);
            if (col == null || Grid.ItemContainerGenerator.ContainerFromItem(item) is not DataGridRow row) return;
            row.UpdateLayout();
            var presenter = FindChild<System.Windows.Controls.Primitives.DataGridCellsPresenter>(row);
            if (presenter?.ItemContainerGenerator.ContainerFromIndex(col.DisplayIndex) is not DataGridCell cell) return;

            var brush = new SolidColorBrush(color);
            cell.Background = brush;
            var anim = new ColorAnimation(color, settle, TimeSpan.FromSeconds(3))
            {
                BeginTime = TimeSpan.FromSeconds(0.6), // hold the highlight briefly
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            anim.Completed += (_, _) => cell.ClearValue(BackgroundProperty);
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }
        catch { /* cosmetic only */ }
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    // Erase the old text character by character, then type the new text. Total ≈ 1.5 s.
    static async Task TypeSwap(string old, string @new, Action<string> set)
    {
        var per = Math.Clamp(1500 / Math.Max(1, old.Length + @new.Length), 4, 30);
        for (int i = old.Length - 1; i >= 0; i--) { set(old[..i]); await Task.Delay(per); }
        for (int i = 1; i <= @new.Length; i++) { set(@new[..i]); await Task.Delay(per); }
    }

    bool _busy;

    async Task UpdateRepos(List<RepoItem> sel)
    {
        var root = FolderBox.Text.Trim();
        if (_busy || sel.Count == 0 || root == "") return;
        _busy = true;
        GitUser = ProfileName();
        Directory.CreateDirectory(root);
        UpdateBtn.IsEnabled = false;
        Grid.IsEnabled = false; // disables row buttons while running
        foreach (var r in sel)
        {
            var (oldBranch, oldCommit) = (r.Branch, r.LastCommit);
            r.Hold = true; // keep showing the old text until we animate the swap
            r.Outcome = "";
            r.Status = "Working…";
            try { r.Status = await Task.Run(() => Sync(root, r)); }
            catch (Exception ex) { r.Status = "Error: " + ex.Message; }
            if (r.Status.StartsWith("OK")) r.LastUpdated = DateTime.Now; // only successful syncs
            try { r.RemoteState = await Task.Run(() => CheckRemote(root, r)); } // refresh Remote column
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
    }

    static string Sync(string root, RepoItem r)
    {
        var dir = Path.Combine(root, r.Name);
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            var (c, o) = Git(root, $"clone \"{r.CloneUrl}\" \"{r.Name}\"");
            if (c != 0) return "Clone failed: " + o;
        }
        var f = Git(dir, "fetch --all --prune");
        if (f.code != 0) return "Fetch failed: " + f.output;

        // Latest branch = remote branch with most recent commit
        var refs = Git(dir, "for-each-ref --sort=-committerdate --format=%(refname:short) refs/remotes/origin").output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x != "origin" && x != "origin/HEAD").ToList();
        if (refs.Count == 0) return "No remote branches";
        var branch = refs[0]["origin/".Length..];

        var co = Git(dir, $"checkout \"{branch}\"");
        if (co.code != 0) return $"Checkout {branch} failed: {co.output}";
        var pull = Git(dir, "pull --ff-only");
        r.Branch = branch;
        return pull.code == 0 ? "OK" : $"Pull failed on {branch}: {pull.output}";
    }

    static (int code, string output) Git(string dir, string args)
    {
        if (!string.IsNullOrEmpty(GitUser))
            args = $"-c credential.https://github.com.username={GitUser} " + args;
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (outp + err.Result).Trim());
    }
}
