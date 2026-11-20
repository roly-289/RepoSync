using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using BranchKit;

namespace RepoSync;

public class RepoItem : INotifyPropertyChanged
{
    string _status = "";
    DateTime? _updated;
    string _remote = "";
    bool _sel;
    public string Name { get; set; } = "";
    public string CloneUrl { get; set; } = "";
    public bool Private { get; set; }
    public bool IsSelected { get => _sel; set { _sel = value; Raise(nameof(IsSelected)); } }
    public DateTime? LastUpdated { get => _updated; set { _updated = value; Raise(nameof(LastUpdated)); } }
    public string RemoteState { get => _remote; set { _remote = value; Raise(nameof(RemoteState)); } }
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }
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
        foreach (var line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.TrimEnd('\r').Split('\x1f');
            if (f.Length < 5) continue;
            var label = f[3] == "" ? f[4] : $"{f[4]}  [{f[3]}]";
            var time = DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[2])).LocalDateTime;
            var node = new BranchNode<string>(f[0], label, time);
            var firstParent = f[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (tree == null) { tree = new BranchTree(node); root = node; }
            else tree.Add(node, firstParent != null && map.TryGetValue(firstParent, out var p) ? p : root);
            map[f[0]] = node;
        }
        if (tree == null) return (null, "no commits");

        var head = Git(dir, "rev-parse HEAD").output;
        if (map.TryGetValue(head, out var h)) tree.MoveTo(h);
        return (tree, $"{map.Count} commits" + (map.Count == Max ? " (latest shown)" : ""));
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
        var old = _repos.ToDictionary(r => r.Name);
        _repos.Clear();
        try
        {
            Http.DefaultRequestHeaders.Authorization = token == "" ? null : new AuthenticationHeaderValue("Bearer", token);
            // With a token (and no profile) list the token owner's repos incl. private; otherwise public repos of the profile.
            var baseUrl = token != "" ? "https://api.github.com/user/repos?per_page=100" : $"https://api.github.com/users/{Uri.EscapeDataString(user)}/repos?per_page=100";
            for (int page = 1; ; page++)
            {
                var json = await Http.GetStringAsync($"{baseUrl}&page={page}");
                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement.EnumerateArray().ToList();
                foreach (var r in arr.Where(x => token != "" || user == "" || string.Equals(x.GetProperty("owner").GetProperty("login").GetString(), user, StringComparison.OrdinalIgnoreCase)))
                    _repos.Add(new RepoItem { Name = r.GetProperty("name").GetString()!, CloneUrl = r.GetProperty("clone_url").GetString()!, Private = r.GetProperty("private").GetBoolean() });
                if (arr.Count < 100) break;
            }
            string who = "no token";
            if (token != "")
            {
                try { using var d = JsonDocument.Parse(await Http.GetStringAsync("https://api.github.com/user")); who = "token user: " + d.RootElement.GetProperty("login").GetString(); }
                catch (Exception ex) { who = "TOKEN REJECTED: " + ex.Message; }
            }
            Title = $"Repo Sync v{Ver} - {_repos.Count} repos ({_repos.Count(r => r.Private)} private), {who}";
            if (_repos.Count == 0)
                MessageBox.Show(token != ""
                    ? "GitHub returned no repos for this token. For a fine-grained token, set Repository access to 'All repositories' (or select repos) and the right Resource owner."
                    : $"No public repos found for '{user}'. Private repos need a token.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Load failed"); }
        finally
        {
            foreach (var r in _repos)
                if (old.TryGetValue(r.Name, out var o)) { r.IsSelected = o.IsSelected; r.Status = o.Status; }
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

    bool _busy;

    async Task UpdateRepos(List<RepoItem> sel)
    {
        var root = FolderBox.Text.Trim();
        if (_busy || sel.Count == 0 || root == "") return;
        _busy = true;
        Directory.CreateDirectory(root);
        UpdateBtn.IsEnabled = false;
        Grid.IsEnabled = false; // disables row buttons while running
        foreach (var r in sel)
        {
            r.Status = "Working…";
            try { r.Status = await Task.Run(() => Sync(root, r)); }
            catch (Exception ex) { r.Status = "Error: " + ex.Message; }
            if (r.Status.StartsWith("OK")) r.LastUpdated = DateTime.Now; // only successful syncs
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
        return pull.code == 0 ? $"OK — {branch}" : $"Pull failed on {branch}: {pull.output}";
    }

    static (int code, string output) Git(string dir, string args)
    {
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
