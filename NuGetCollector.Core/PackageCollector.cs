using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using NuGet.Versioning;

namespace NuGetCollector;

public sealed record PackageInfo(string Id, NuGetVersion Version, string Path, DateTime Modified);

public sealed record PackageResult(
    string Id, string Version, string PreviousVersion, string Status, string SourcePath);

public sealed record CollectResult(
    IReadOnlyList<PackageResult> Packages, IReadOnlyList<string> Warnings,
    int ScannedCount, int ProjectCount, int MsBuildReadCount,
    TimeSpan ScanElapsed, TimeSpan TotalElapsed);

/// <summary>A library project whose version is ahead of the feed (or not in it yet).</summary>
public sealed record PackCandidate(string Id, string Version, string ExistingVersion, string ProjectPath);

public sealed record RepoPackScan(IReadOnlyList<PackCandidate> Candidates, IReadOnlyList<string> Warnings);

public static partial class PackageCollector
{
    /// <summary>Highest version of each package in a feed folder.</summary>
    public static Task<IReadOnlyDictionary<string, NuGetVersion>> LoadFeedAsync(string feedFolder) =>
        Task.Run<IReadOnlyDictionary<string, NuGetVersion>>(() =>
        {
            var feed = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(feedFolder)) return feed;
            foreach (var package in ReadAll(
                         Directory.EnumerateFiles(feedFolder, "*.nupkg").Where(f => !IsSymbolsPackage(f)), []))
            {
                if (!feed.TryGetValue(package.Id, out var current) || package.Version > current)
                    feed[package.Id] = package.Version;
            }
            return feed;
        });

    public static async Task<CollectResult> RunAsync(
        string sourceDirectory, string targetDirectory, bool packProjects,
        bool includeNewProjects, IProgress<string> progress)
    {
        var total = Stopwatch.StartNew();
        var source = Normalize(sourceDirectory);
        var target = Normalize(targetDirectory);

        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"Solutions folder not found: {source}");
        if (IsSameOrInside(source, target))
            throw new InvalidOperationException(
                "The target folder cannot be the solutions folder or one of its parents.");

        Directory.CreateDirectory(target);
        var warnings = new List<string>();

        // Highest version of each package already in the target, taken before anything moves.
        var existing = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        progress.Report("Reading target folder...");
        foreach (var package in ReadAll(
                     Directory.EnumerateFiles(target, "*.nupkg").Where(f => !IsSymbolsPackage(f)), warnings))
        {
            if (!existing.TryGetValue(package.Id, out var current) || package.Version > current)
                existing[package.Id] = package.Version;
        }

        // One pass finds packages and projects together.
        var scan = FileScanner.Scan(source, target, warnings, progress);
        progress.Report($"Reading {scan.Packages.Count:N0} package file(s)...");
        var found = ReadAll(scan.Packages.Where(f => !IsSymbolsPackage(f)), warnings);
        var scanElapsed = total.Elapsed;

        // The same ID and version can exist more than once (e.g. Debug and Release).
        // Use the most recently written copy.
        var candidates = found
            .GroupBy(p => (Id: p.Id.ToUpperInvariant(), p.Version))
            .Select(g => g.MaxBy(p => p.Modified)!)
            .Where(p => !existing.TryGetValue(p.Id, out var current) || p.Version > current)
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Version)
            .ToList();

        var results = new List<PackageResult>();
        foreach (var package in candidates)
        {
            var previous = existing.TryGetValue(package.Id, out var old)
                ? old.ToNormalizedString()
                : "(none)";
            string status;
            try
            {
                var destination = Path.Combine(target, Path.GetFileName(package.Path));
                File.Move(package.Path, destination, overwrite: false);
                status = "Moved";
                // Candidates are in ascending order, so this ends at the highest version moved.
                // The project step then compares against the updated target.
                existing[package.Id] = package.Version;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status = $"Failed: {ex.Message}";
            }

            results.Add(new PackageResult(
                package.Id, package.Version.ToNormalizedString(), previous, status, package.Path));
        }

        var projectCount = 0;
        var msBuildReads = 0;
        if (packProjects)
        {
            (projectCount, msBuildReads) = await PackProjectsAsync(
                scan.Projects, target, existing, includeNewProjects, results, warnings, progress);
        }

        return new CollectResult(results, warnings.Distinct().ToList(), found.Count,
            projectCount, msBuildReads, scanElapsed, total.Elapsed);
    }

    // Opening each package is mostly waiting on the disk, so several are read at once.
    private static List<PackageInfo> ReadAll(IEnumerable<string> files, List<string> warnings)
    {
        var results = files.AsParallel()
            .WithDegreeOfParallelism(Math.Clamp(Environment.ProcessorCount * 2, 4, 32))
            .Select(TryReadCore)
            .ToList();
        warnings.AddRange(results.Select(r => r.Warning).OfType<string>());
        return results.Select(r => r.Package).OfType<PackageInfo>().ToList();
    }

    // ID and version come from the .nuspec inside the package. File names are ambiguous.
    private static PackageInfo? TryRead(string file, List<string> warnings)
    {
        var (package, warning) = TryReadCore(file);
        if (warning is not null) warnings.Add(warning);
        return package;
    }

    private static (PackageInfo? Package, string? Warning) TryReadCore(string file)
    {
        try
        {
            using var archive = ZipFile.OpenRead(file);
            var nuspec = archive.Entries.FirstOrDefault(e =>
                !e.FullName.Contains('/') &&
                e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("No .nuspec found.");

            using var stream = nuspec.Open();
            var metadata = XDocument.Load(stream).Root?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "metadata");
            string? Value(string name) => metadata?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

            var id = Value("id");
            if (string.IsNullOrEmpty(id) || !NuGetVersion.TryParse(Value("version"), out var version))
                throw new InvalidDataException("Missing or invalid package ID or version.");

            return (new PackageInfo(id, version, file, File.GetLastWriteTimeUtc(file)), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException or System.Xml.XmlException)
        {
            return (null, $"Skipped {file}: {ex.Message}");
        }
    }

    private static bool IsSymbolsPackage(string file) =>
        file.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    // True if "path" is "parent" or is inside it.
    internal static bool IsSameOrInside(string path, string parent)
    {
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
