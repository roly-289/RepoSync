using System.IO;
using System.Text.Json;

namespace NuGetCollector;

public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NuGetCollector", "settings.json");

    public string SourceDirectory { get; set; } = "";
    public string TargetDirectory { get; set; } = "";
    public bool PackProjects { get; set; } = true;
    public bool IncludeNewProjects { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A damaged or unreadable settings file must not stop the app from starting.
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}
