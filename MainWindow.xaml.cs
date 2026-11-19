using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;

namespace RepoSync;

public class RepoItem : INotifyPropertyChanged
{
    string _status = "";
    bool _sel;
    public string Name { get; set; } = "";
    public string CloneUrl { get; set; } = "";
    public bool IsSelected { get => _sel; set { _sel = value; Raise(nameof(IsSelected)); } }
    public string Status { get => _status; set { _status = value; Raise(nameof(Status)); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}

public partial class MainWindow : Window
{
    readonly ObservableCollection<RepoItem> _repos = new();
    static readonly HttpClient Http = new();

    public MainWindow()
    {
        InitializeComponent();
        Grid.ItemsSource = _repos;
        FolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("RepoSync");
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
        var user = UserBox.Text.Trim();
        var token = TokenBox.Password;
        if (user == "" && token == "") { MessageBox.Show("Enter a profile name or token."); return; }
        LoadBtn.IsEnabled = false;
        _repos.Clear();
        try
        {
            Http.DefaultRequestHeaders.Authorization = token == "" ? null : new AuthenticationHeaderValue("Bearer", token);
            // With a token (and no profile) list the token owner's repos incl. private; otherwise public repos of the profile.
            var baseUrl = user == "" ? "https://api.github.com/user/repos?per_page=100" : $"https://api.github.com/users/{Uri.EscapeDataString(user)}/repos?per_page=100";
            for (int page = 1; ; page++)
            {
                var json = await Http.GetStringAsync($"{baseUrl}&page={page}");
                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement.EnumerateArray().ToList();
                foreach (var r in arr)
                    _repos.Add(new RepoItem { Name = r.GetProperty("name").GetString()!, CloneUrl = r.GetProperty("clone_url").GetString()! });
                if (arr.Count < 100) break;
            }
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Load failed"); }
        finally { LoadBtn.IsEnabled = true; }
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
