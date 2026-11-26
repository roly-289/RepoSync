using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NuGet.Versioning;

namespace NuGetCollector;

public static partial class PackageCollector
{
    private sealed record ProjectInfo(
        string Id, NuGetVersion Version, string Path, bool Confirmed);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    private sealed record PackPlan(ProjectInfo Project, NuGetVersion? Current);

    // Reads every project and decides which ones should be packed. Changes nothing on disk.
    private static async Task<(List<PackPlan> Plans, int Projects, int MsBuildReads)> PlanProjectsAsync(
        IReadOnlyList<string> projectFiles, IReadOnlyDictionary<string, NuGetVersion> existing,
        bool includeNewProjects, bool librariesOnly, List<string> warnings, IProgress<string> progress)
    {
        var files = projectFiles.Order(StringComparer.OrdinalIgnoreCase).ToList();

        // Pass 1: read the XML directly (fast, and several projects at once). Collect any we can't be sure about.
        progress.Report($"Reading {files.Count:N0} project(s)...");
        var reader = new FastProjectReader(librariesOnly);
        var projects = new List<ProjectInfo>();
        var uncertain = new List<string>();
        var reads = files.AsParallel()
            .WithDegreeOfParallelism(Environment.ProcessorCount)
            .Select<string, (string File, FastProjectReader.Result? Read, string? Error)>(file =>
            {
                try
                {
                    return (file, reader.Read(file), null);
                }
                catch (Exception ex) when (IsExpectedError(ex) || ex is System.Xml.XmlException)
                {
                    return (file, null, $"Could not read project {file}: {ex.Message}");
                }
            })
            .ToList();
        foreach (var (file, read, error) in reads)
        {
            if (error is not null) warnings.Add(error);
            else if (read!.Outcome == FastProjectReader.Outcome.Read)
                projects.Add(new ProjectInfo(read.Id, read.Version!, file, Confirmed: false));
            else if (read.Outcome == FastProjectReader.Outcome.NeedsMsBuild)
                uncertain.Add(file);
        }

        // Pass 2: ask MSBuild about the uncertain ones.
        projects.AddRange(await ReadWithMsBuildAsync(uncertain, librariesOnly, warnings, progress));

        // Pass 3: MSBuild has the final say before anything is packed. Check every project
        // sharing an ID with a pack candidate, so duplicate detection is accurate too.
        bool IsCandidate(ProjectInfo p) =>
            existing.TryGetValue(p.Id, out var highest) ? p.Version > highest : includeNewProjects;

        var toConfirm = projects
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Any(IsCandidate))
            .SelectMany(g => g)
            .Where(p => !p.Confirmed)
            .ToList();
        var pathsToConfirm = toConfirm.Select(p => p.Path).ToList();
        var confirmed = await ReadWithMsBuildAsync(pathsToConfirm, librariesOnly, warnings, progress);
        projects = projects.Except(toConfirm).Concat(confirmed).ToList();

        var plans = new List<PackPlan>();
        foreach (var group in projects.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
            {
                // Packing the wrong project into a feed is worse than packing neither.
                warnings.Add($"Not packed: {group.Key} is produced by more than one project: " +
                             string.Join(", ", group.Select(p => p.Path)));
                continue;
            }

            var project = group.Single();
            var version = project.Version.ToNormalizedString();
            var inTarget = existing.TryGetValue(project.Id, out var current);

            if (!inTarget && !includeNewProjects)
            {
                warnings.Add($"Not packed: {project.Id} {version} is not in the target yet.");
                continue;
            }
            if (inTarget && project.Version <= current!)
            {
                if (project.Version < current!)
                    warnings.Add($"Not packed: {project.Id} project version {version} is behind " +
                                 $"the target's {current!.ToNormalizedString()} ({project.Path}).");
                continue;
            }

            plans.Add(new PackPlan(project, inTarget ? current : null));
        }

        return (plans, files.Count, uncertain.Count + pathsToConfirm.Count);
    }

    private static async Task<(int Projects, int MsBuildReads)> PackProjectsAsync(
        IReadOnlyList<string> projectFiles, string target, IReadOnlyDictionary<string, NuGetVersion> existing,
        bool includeNewProjects, List<PackageResult> results, List<string> warnings,
        IProgress<string> progress)
    {
        var (plans, projectCount, msBuildReads) = await PlanProjectsAsync(
            projectFiles, existing, includeNewProjects, librariesOnly: false, warnings, progress);

        foreach (var (project, current) in plans)
        {
            var version = project.Version.ToNormalizedString();
            progress.Report($"Packing {project.Id} {version}...");
            string status;
            try
            {
                await PackAndMoveAsync(project, target, warnings);
                status = "Packed and moved";
            }
            catch (Exception ex) when (IsExpectedError(ex))
            {
                status = $"Failed: {ex.Message}";
            }

            results.Add(new PackageResult(
                project.Id, version, current?.ToNormalizedString() ?? "(none)", status, project.Path));
        }

        return (projectCount, msBuildReads);
    }

    /// <summary>
    /// Finds the library projects in one folder (a repo) that are ahead of the feed. Read-only: nothing is
    /// packed or moved. <paramref name="feed"/> comes from <see cref="LoadFeedAsync"/>.
    /// </summary>
    public static async Task<RepoPackScan> ScanRepoAsync(
        string repoFolder, string feedFolder, IReadOnlyDictionary<string, NuGetVersion> feed,
        bool includeNewProjects, bool librariesOnly = true)
    {
        var repo = Normalize(repoFolder);
        var target = Normalize(feedFolder);
        var warnings = new List<string>();
        var quiet = new Progress<string>();

        var scan = FileScanner.Scan(repo, target, warnings, quiet);
        var (plans, _, _) = await PlanProjectsAsync(
            scan.Projects, feed, includeNewProjects, librariesOnly, warnings, quiet);

        var candidates = plans
            .Select(p => new PackCandidate(p.Project.Id, p.Project.Version.ToNormalizedString(),
                p.Current?.ToNormalizedString() ?? "(none)", p.Project.Path))
            .ToList();
        return new RepoPackScan(candidates, warnings.Distinct().ToList());
    }

    /// <summary>Packs the given projects in Release and moves each package into the feed folder.</summary>
    public static async Task<IReadOnlyList<PackageResult>> PackAsync(
        IEnumerable<PackCandidate> candidates, string feedFolder, IProgress<string>? progress = null)
    {
        var target = Normalize(feedFolder);
        Directory.CreateDirectory(target);
        var warnings = new List<string>();
        var results = new List<PackageResult>();

        foreach (var c in candidates)
        {
            progress?.Report($"Packing {c.Id} {c.Version}...");
            string status;
            try
            {
                var project = new ProjectInfo(c.Id, NuGetVersion.Parse(c.Version), c.ProjectPath, Confirmed: true);
                await PackAndMoveAsync(project, target, warnings);
                status = "Packed and moved";
            }
            catch (Exception ex) when (IsExpectedError(ex))
            {
                status = $"Failed: {ex.Message}";
            }
            results.Add(new PackageResult(c.Id, c.Version, c.ExistingVersion, status, c.ProjectPath));
        }
        return results;
    }

    // Each call starts a process, which is slow, so several run at once.
    private static async Task<List<ProjectInfo>> ReadWithMsBuildAsync(
        IReadOnlyList<string> files, bool librariesOnly, List<string> warnings, IProgress<string> progress)
    {
        var projects = new ProjectInfo?[files.Count];
        var errors = new string?[files.Count];
        var completed = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 12)
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count), options, async (i, _) =>
        {
            try
            {
                projects[i] = await ReadProjectAsync(files[i], librariesOnly);
            }
            catch (Exception ex) when (IsExpectedError(ex))
            {
                errors[i] = $"Could not read project {files[i]}: {ex.Message}";
            }
            progress.Report(
                $"Checking with MSBuild: {Interlocked.Increment(ref completed)} of {files.Count}");
        });

        warnings.AddRange(errors.OfType<string>());
        return projects.OfType<ProjectInfo>().ToList();
    }

    // MSBuild evaluates the project, so Directory.Build.props, VersionPrefix,
    // variables and conditions are all applied. Parsing the XML would miss them.
    private static async Task<ProjectInfo?> ReadProjectAsync(string file, bool librariesOnly)
    {
        var output = await RunDotNetAsync(file,
            "msbuild", file, "-nologo", "-p:Configuration=Release",
            "-getProperty:PackageId", "-getProperty:PackageVersion", "-getProperty:IsPackable",
            "-getProperty:OutputType", "-getProperty:PackAsTool");

        var start = output.IndexOf('{');
        if (start < 0)
            throw new InvalidDataException("MSBuild did not return the project properties.");

        using var json = JsonDocument.Parse(output[start..]);
        var properties = json.RootElement.GetProperty("Properties");
        string Value(string name) => properties.GetProperty(name).GetString()?.Trim() ?? "";

        if (Value("IsPackable").Equals("false", StringComparison.OrdinalIgnoreCase))
            return null;
        if (librariesOnly && FastProjectReader.IsExecutable(Value("OutputType")) &&
            !Value("PackAsTool").Equals("true", StringComparison.OrdinalIgnoreCase))
            return null;

        var id = Value("PackageId");
        if (id.Length == 0 || !NuGetVersion.TryParse(Value("PackageVersion"), out var version))
            throw new InvalidDataException(
                "No package ID or valid version (is this an SDK-style project?).");

        return new ProjectInfo(id, version, file, Confirmed: true);
    }

    private static async Task PackAndMoveAsync(
        ProjectInfo project, string target, List<string> warnings)
    {
        // Pack into an empty folder, so a failed or partial pack never reaches the target.
        var staging = Directory.CreateTempSubdirectory("NuGetCollector-").FullName;
        try
        {
            // With GeneratePackageOnBuild on, "dotnet pack" may not build first.
            await RunDotNetAsync(project.Path,
                "pack", project.Path, "-c", "Release", "-o", staging,
                "-p:GeneratePackageOnBuild=false");

            var package = Directory.GetFiles(staging, "*.nupkg")
                .Where(file => !IsSymbolsPackage(file))
                .Select(file => TryRead(file, warnings))
                .OfType<PackageInfo>()
                .FirstOrDefault(p =>
                    p.Id.Equals(project.Id, StringComparison.OrdinalIgnoreCase) &&
                    p.Version == project.Version)
                ?? throw new InvalidDataException(
                    "Pack did not produce the expected package ID and version.");

            File.Move(package.Path,
                Path.Combine(target, Path.GetFileName(package.Path)), overwrite: false);
        }
        finally
        {
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not delete temporary folder {staging}: {ex.Message}");
            }
        }
    }

    private static async Task<string> RunDotNetAsync(string projectPath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            // Running in the project folder makes any global.json apply.
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        // Stops lingering build processes from holding the redirected output open.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {arguments[0]} took too long and was stopped.");
        }

        var output = await stdout;
        var errors = await stderr;
        if (process.ExitCode != 0)
        {
            var lines = (output + "\n" + errors)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var relevant = lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase))
                .Distinct().Take(3).ToList();
            if (relevant.Count == 0) relevant = lines.TakeLast(3).ToList();
            throw new InvalidOperationException(
                $"dotnet {arguments[0]} failed: {string.Join(" | ", relevant)}");
        }
        return output;
    }

    private static bool IsExpectedError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or Win32Exception or TimeoutException
            or JsonException or KeyNotFoundException;
}
