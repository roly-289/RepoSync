using System.Diagnostics;
using System.IO;
using System.Text;

namespace RepoSync;

/// <summary>Runs git and implements the per-repo operations: sync (clone/fetch/checkout/pull) and remote check.</summary>
public static class GitService
{
    /// <summary>GitHub profile name. Passed to git so Git Credential Manager doesn't ask which account to use.</summary>
    public static string? User { get; set; }

    // Fetches, then compares the checked-out branch with its upstream.
    public static string CheckRemote(string root, RepoItem r)
    {
        var state = CheckUpstream(root, r);
        if (state.StartsWith("Not cloned") || state.StartsWith("Fetch failed")) return state;

        // Also flag when another remote branch has newer commits than the one checked out
        var dir = Path.Combine(root, r.Name);
        var cur = Run(dir, "rev-parse --abbrev-ref HEAD").output;
        r.Branch = cur;
        r.LastCommit = Run(dir, "log -1 --format=%s").output;
        var latest = Run(dir, "for-each-ref --sort=-committerdate --count=3 --format=%(refname:short) refs/remotes/origin").output
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
        var f = Run(dir, "fetch --all --prune");
        if (f.code != 0) return "Fetch failed: " + f.output;
        var (c, o) = Run(dir, "rev-list --left-right --count HEAD...@{u}");
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

    public static string Sync(string root, RepoItem r)
    {
        var dir = Path.Combine(root, r.Name);
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            var (c, o) = Run(root, $"clone \"{r.CloneUrl}\" \"{r.Name}\"");
            if (c != 0) return "Clone failed: " + o;
        }
        var f = Run(dir, "fetch --all --prune");
        if (f.code != 0) return "Fetch failed: " + f.output;

        // Latest branch = remote branch with most recent commit
        var refs = Run(dir, "for-each-ref --sort=-committerdate --format=%(refname:short) refs/remotes/origin").output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x != "origin" && x != "origin/HEAD").ToList();
        if (refs.Count == 0) return "No remote branches";
        var branch = refs[0]["origin/".Length..];

        var co = Run(dir, $"checkout \"{branch}\"");
        if (co.code != 0) return $"Checkout {branch} failed: {co.output}";
        var pull = Run(dir, "pull --ff-only");
        r.Branch = branch;
        return pull.code == 0 ? "OK" : $"Pull failed on {branch}: {pull.output}";
    }

    public static (int code, string output) Run(string dir, string args)
    {
        if (!string.IsNullOrEmpty(User))
            args = $"-c credential.https://github.com.username={User} " + args;
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
