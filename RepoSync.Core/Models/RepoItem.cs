using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using NuGetCollector;

namespace RepoSync;

public enum PackPhase { Unknown, Scanning, None, Ready, Packing }

public class RepoItem : INotifyPropertyChanged
{
    PackPhase _pack;
    IReadOnlyList<PackCandidate> _candidates = [];
    string _packNote = "";
    IReadOnlyList<string> _solutions = [];
    [JsonIgnore] public IReadOnlyList<string> Solutions => _solutions;
    [JsonIgnore] public bool OpenEnabled => _solutions.Count > 0;
    [JsonIgnore] public bool HasManySolutions => _solutions.Count > 1; // shows the dropdown half of the split button
    /// <summary>The solution the main half of the button opens: the one named after the repo, else the first.</summary>
    public string? DefaultSolution() =>
        _solutions.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(Name, StringComparison.OrdinalIgnoreCase))
        ?? _solutions.FirstOrDefault();
    [JsonIgnore] public string OpenTip => _solutions.Count switch
    {
        0 => "No solution file found (is the repo cloned?)",
        1 => $"Open {Path.GetFileName(_solutions[0])}",
        _ => $"Open {Path.GetFileName(DefaultSolution())} (▾ for the other {_solutions.Count - 1})",
    };
    public void SetSolutions(IReadOnlyList<string> solutions)
    {
        _solutions = solutions;
        Raise(nameof(Solutions)); Raise(nameof(OpenEnabled)); Raise(nameof(OpenTip)); Raise(nameof(HasManySolutions));
    }

    public int ScanGen; // bumped to discard the results of an out-of-date scan

    [JsonIgnore] public IReadOnlyList<PackCandidate> PackCandidates => _candidates;
    [JsonIgnore] public PackPhase Pack => _pack;
    [JsonIgnore] public bool PackEnabled => _pack == PackPhase.Ready;
    [JsonIgnore] public bool PackBusy => _pack is PackPhase.Scanning or PackPhase.Packing;
    /// <summary>Vertical mode shows symbols instead of words.</summary>
    public static bool Compact { get; set; }
    public void RefreshText() => Raise(nameof(PackText));
    [JsonIgnore] public string PackText => Compact
        ? _pack switch { PackPhase.Ready => $"▣{_candidates.Count}", PackPhase.Packing => "…", _ => "▣" }
        : _pack switch { PackPhase.Ready => $"Pack ({_candidates.Count})", PackPhase.Packing => "Packing…", _ => "Pack" };
    [JsonIgnore] public string PackTip => _pack switch
    {
        PackPhase.Scanning => "Scanning projects…",
        PackPhase.Packing => "Packing…",
        PackPhase.None => "No library projects are ahead of the NuGet feed",
        PackPhase.Ready => string.Join("\n", _candidates.Select(c => $"{c.Id}: {c.ExistingVersion} → {c.Version}")),
        _ => _packNote != "" ? _packNote : "Not scanned",
    };

    public void SetPack(PackPhase phase, IReadOnlyList<PackCandidate>? candidates = null, string note = "")
    {
        _pack = phase;
        _candidates = candidates ?? [];
        _packNote = note;
        foreach (var n in new[] { nameof(Pack), nameof(PackEnabled), nameof(PackBusy), nameof(PackText), nameof(PackTip) })
            Raise(n);
    }

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

    /// <summary>One icon for the whole row: ✔ fine, ↻ out of sync with remote, ✘ failed, – not cloned, … busy.</summary>
    [JsonIgnore]
    public string StatusIcon
    {
        get
        {
            if (_status == "Working…" || _remote == "Checking…") return "…";
            if (_status != "" && !_status.StartsWith("OK")) return "✘"; // last sync failed
            if (_remote == "") return _status.StartsWith("OK") ? "✔" : "";
            if (_remote.StartsWith("Fetch failed") || _remote.StartsWith("Error")) return "✘";
            return _remote switch { "Not cloned" => "–", "Up to date" => "✔", _ => "↻" }; // ↻ = out of sync, needs an update
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
