using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RepoSync;

/// <summary>Everything saved between sessions in %APPDATA%\RepoSync\settings.json.</summary>
public class Settings
{
    public string Profile { get; set; } = "";
    public string LocalFolder { get; set; } = "";
    public string NuGetFeed { get; set; } = "";
    public string Theme { get; set; } = "Dark"; // System | Light | Dark
    public bool IncludeNewLibraries { get; set; }
    public bool Compact { get; set; } // vertical mode
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
