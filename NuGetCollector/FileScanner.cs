using System.Collections.Concurrent;
using System.IO;
using System.IO.Enumeration;

namespace NuGetCollector;

public sealed record ScanResult(IReadOnlyList<string> Packages, IReadOnlyList<string> Projects);

// One pass over the folder tree finds .nupkg files and project files together. It replaces four
// separate walks (one per pattern), and each walk looked up every folder twice and made an extra
// call per folder to check for links. Top-level folders are scanned in parallel.
public static class FileScanner
{
    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        // "packages" holds restored third-party dependencies in packages.config solutions.
        "packages", ".nuget", "node_modules", ".git", ".vs", "obj"
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ExcludedLookup =
        ExcludedFolders.GetAlternateLookup<ReadOnlySpan<char>>();

    private sealed class State(string target, string? targetName, IProgress<string> progress)
    {
        public string Target { get; } = target;
        public string? TargetName { get; } = targetName;
        public IProgress<string> Progress { get; } = progress;
        public int Folders;
        public int Errors;
    }

    public static ScanResult Scan(string source, string target, List<string> warnings, IProgress<string> progress)
    {
        // Only needed when the target folder sits inside the source tree.
        var targetName = PackageCollector.IsSameOrInside(target, source) ? Path.GetFileName(target) : null;
        var state = new State(target, targetName, progress);
        var found = new ConcurrentBag<string>();
        var unreadable = new ConcurrentBag<string>();

        // Files directly in the source folder.
        Collect(source, recurse: false, state, found, unreadable);

        var topLevel = new List<string>();
        try
        {
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.ReparsePoint, // don't follow junctions or symbolic links
                IgnoreInaccessible = true
            };
            foreach (var folder in Directory.EnumerateDirectories(source, "*", options))
            {
                if (ExcludedFolders.Contains(Path.GetFileName(folder))) continue;
                if (PackageCollector.IsSameOrInside(folder, target)) continue;
                topLevel.Add(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Skipped folder {source}: {ex.Message}");
        }

        Parallel.ForEach(
            topLevel,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 16) },
            folder => Collect(folder, recurse: true, state, found, unreadable));

        warnings.AddRange(unreadable.Distinct().Select(m => m));
        if (state.Errors > 0)
            warnings.Add($"{state.Errors} folder(s) could not be read and were skipped.");

        var packages = new List<string>();
        var projects = new List<string>();
        foreach (var file in found)
            (file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ? packages : projects).Add(file);
        return new ScanResult(packages, projects);
    }

    private static void Collect(
        string folder, bool recurse, State state, ConcurrentBag<string> found, ConcurrentBag<string> unreadable)
    {
        try
        {
            using var entries = new Enumerator(folder, recurse, state);
            while (entries.MoveNext())
                found.Add(entries.Current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable.Add($"Skipped folder {folder}: {ex.Message}");
        }
    }

    private sealed class Enumerator(string folder, bool recurse, State state)
        : FileSystemEnumerator<string>(folder, new EnumerationOptions
        {
            RecurseSubdirectories = recurse,
            AttributesToSkip = FileAttributes.ReparsePoint,
            BufferSize = 64 * 1024 // fewer calls into the file system
        })
    {
        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory) return false;
            var name = entry.FileName;
            return name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            if (ExcludedLookup.Contains(entry.FileName)) return false;
            // Skip the target folder if it is inside the source tree (cheap name check first).
            if (state.TargetName is not null
                && entry.FileName.Equals(state.TargetName, StringComparison.OrdinalIgnoreCase)
                && PackageCollector.IsSameOrInside(entry.ToFullPath(), state.Target))
                return false;

            var count = Interlocked.Increment(ref state.Folders);
            if (count % 250 == 0) state.Progress.Report($"Scanning... {count:N0} folders");
            return true;
        }

        protected override string TransformEntry(ref FileSystemEntry entry) => entry.ToFullPath();

        protected override bool ContinueOnError(int error)
        {
            Interlocked.Increment(ref state.Errors); // access denied, folder removed mid-scan, etc.
            return true;
        }
    }
}
