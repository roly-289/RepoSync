using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NuGet.Versioning;

namespace NuGetCollector;

// Reads package properties straight from the XML: the project file plus the nearest
// Directory.Build.props and Directory.Build.targets. It is deliberately not an MSBuild
// engine. Whenever it can't be sure of an answer it returns NeedsMsBuild.
// librariesOnly: leave out executables (OutputType Exe/WinExe) unless they are packed as a tool.
public sealed partial class FastProjectReader(bool librariesOnly = false)
{
    public enum Outcome { Read, NotPackable, NeedsMsBuild }

    public sealed record Result(Outcome Outcome, string Id = "", NuGetVersion? Version = null);

    // Shared props files are parsed once per run, not once per project.
    // The reader is used from several threads at once, so both caches are thread-safe.
    private readonly ConcurrentDictionary<string, Lazy<XElement>> _documents = new(StringComparer.OrdinalIgnoreCase);

    // Folder + file name -> nearest file of that name in the folder or above it. Sibling projects
    // share the answer instead of each walking up the tree.
    private readonly ConcurrentDictionary<(string Folder, string FileName), string?> _above =
        new(new FolderKeyComparer());

    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_.\-]*)\)")]
    private static partial Regex PropertyReference();

    public Result Read(string projectPath)
    {
        var project = Load(projectPath);
        var isSdkStyle = project.Attribute("Sdk") is not null ||
            project.Elements().Any(e => e.Name.LocalName == "Sdk" ||
                (e.Name.LocalName == "Import" && e.Attribute("Sdk") is not null));
        if (!isSdkStyle) return new(Outcome.NeedsMsBuild);

        var folder = Path.GetDirectoryName(projectPath)!;
        var files = new[]
        {
            FindAbove(folder, "Directory.Build.props"),
            projectPath,
            FindAbove(folder, "Directory.Build.targets")
        };

        // A null value means "defined, but we can't tell what it is".
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(projectPath)
        };
        var isTestProject = false;

        foreach (var file in files)
        {
            if (file is null) continue;
            var root = Load(file);

            // An import or a Choose block could change any property.
            if (root.Elements().Any(e => e.Name.LocalName is "Choose" ||
                    (e.Name.LocalName == "Import" && e.Attribute("Sdk") is null)))
                return new(Outcome.NeedsMsBuild);

            foreach (var group in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
            foreach (var element in group.Elements())
            {
                var conditional = group.Attribute("Condition") is not null ||
                                  element.Attribute("Condition") is not null;
                properties[element.Name.LocalName] =
                    conditional ? null : Expand(element.Value.Trim(), properties);
            }

            isTestProject |= root.Descendants().Any(e =>
                e.Name.LocalName == "PackageReference" &&
                string.Equals((string?)e.Attribute("Include"), "Microsoft.NET.Test.Sdk",
                    StringComparison.OrdinalIgnoreCase));
        }

        var unsure = false;
        string Get(string name)
        {
            if (!properties.TryGetValue(name, out var value)) return "";
            if (value is null) unsure = true;
            return value ?? "";
        }

        var packable = properties.GetValueOrDefault("IsPackable");
        if (string.Equals(packable, "false", StringComparison.OrdinalIgnoreCase) ||
            (isTestProject && !properties.ContainsKey("IsPackable")))
            return new(Outcome.NotPackable);

        if (librariesOnly && IsExecutable(Get("OutputType")) &&
            !string.Equals(Get("PackAsTool"), "true", StringComparison.OrdinalIgnoreCase))
            return new(Outcome.NotPackable);

        // Same precedence as the .NET SDK.
        var id = Get("PackageId");
        if (id.Length == 0) id = Get("AssemblyName");
        if (id.Length == 0) id = Path.GetFileNameWithoutExtension(projectPath);

        var version = Get("PackageVersion");
        if (version.Length == 0) version = Get("Version");
        if (version.Length == 0)
        {
            var prefix = Get("VersionPrefix");
            var suffix = Get("VersionSuffix");
            version = prefix.Length == 0 ? "1.0.0" : prefix;
            if (suffix.Length > 0) version += "-" + suffix;
        }

        return !unsure && NuGetVersion.TryParse(version, out var parsed)
            ? new(Outcome.Read, id, parsed)
            : new(Outcome.NeedsMsBuild);
    }

    internal static bool IsExecutable(string outputType) =>
        outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
        outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase) ||
        outputType.Equals("AppContainerExe", StringComparison.OrdinalIgnoreCase);

    private static string? Expand(string value, Dictionary<string, string?> properties)
    {
        var unknown = false;
        var expanded = PropertyReference().Replace(value, match =>
        {
            if (properties.TryGetValue(match.Groups[1].Value, out var found) && found is not null)
                return found;
            unknown = true;
            return "";
        });
        // Anything left over is a property function or other syntax we don't evaluate.
        return unknown || expanded.Contains("$(") ? null : expanded;
    }

    private XElement Load(string path) =>
        _documents.GetOrAdd(path, p => new Lazy<XElement>(() =>
            XDocument.Load(p).Root ?? throw new InvalidDataException($"Empty XML file: {p}"))).Value;

    private string? FindAbove(string folder, string fileName) =>
        _above.GetOrAdd((folder, fileName), key =>
        {
            var candidate = Path.Combine(key.Folder, key.FileName);
            if (File.Exists(candidate)) return candidate;
            var parent = Path.GetDirectoryName(key.Folder);
            return parent is null ? null : FindAbove(parent, key.FileName);
        });

    private sealed class FolderKeyComparer : IEqualityComparer<(string Folder, string FileName)>
    {
        public bool Equals((string Folder, string FileName) a, (string Folder, string FileName) b) =>
            string.Equals(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Folder, string FileName) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Folder),
                StringComparer.OrdinalIgnoreCase.GetHashCode(key.FileName));
    }
}
