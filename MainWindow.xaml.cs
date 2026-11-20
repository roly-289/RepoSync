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

namespace RepoSync;

public class RepoItem : INotifyPropertyChanged
{
    string _status = "";
    bool _sel;
    public string Name { get; set; } = "";
    public string CloneUrl { get; set; } = "";
    public bool Private { get; set; }
    public bool IsSelected { get => _sel; set { _sel = value; Raise(nameof(IsSelected)); } }
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
        foreach (var r in st.Repos) _repos.Add(r);
        ShowSaved(st.LastSaved);
        Closing += (_, _) => SaveSettings();
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("RepoSync");
    }

    void ShowSaved(DateTime? t) => SavedText.Text = t == null ? "Not saved yet" : $"Last saved: {t:dd MMM yyyy HH:mm:ss}";

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

    async void Update_Click(object s, RoutedEventArgs e)
    {
        var root = FolderBox.Text.Trim();
        var sel = _repos.Where(r => r.IsSelected).ToList();
        if (sel.Count == 0 || root == "") return;
        Directory.CreateDirectory(root);
        UpdateBtn.IsEnabled = false;
        foreach (var r in sel)
        {
            r.Status = "Working…";
            try { r.Status = await Task.Run(() => Sync(root, r)); }
            catch (Exception ex) { r.Status = "Error: " + ex.Message; }
        }
        UpdateBtn.IsEnabled = true;
        SaveSettings();
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
            UseShellExecute = false, CreateNoWindow = true
        };
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, (outp + err.Result).Trim());
    }
}
