using System.Text.RegularExpressions;

namespace StembridgeValley.Shared;

/// <summary>Finds the Stardew Valley install (Steam libraries, GOG, Xbox-less common paths).</summary>
public static class GameFinder
{
    public static bool IsGameFolder(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "Stardew Valley.dll"));

    public static bool HasSmapi(string dir) =>
        File.Exists(Path.Combine(dir, "StardewModdingAPI.exe")) && File.Exists(Path.Combine(dir, "StardewModdingAPI.dll"));

    public static string? Find()
    {
        foreach (string dir in Candidates())
            if (IsGameFolder(dir))
                return dir;
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var steamRoots = new List<string>();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string sp)
                steamRoots.Add(sp.Replace('/', '\\'));
        }
        catch { }
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        steamRoots.Add(Path.Combine(pf86, "Steam"));
        steamRoots.Add(Path.Combine(pf, "Steam"));

        foreach (string root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "steamapps", "common", "Stardew Valley");
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;
            string text;
            try { text = File.ReadAllText(vdf); } catch { continue; }
            foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                yield return Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps", "common", "Stardew Valley");
        }

        yield return Path.Combine(pf86, "GOG Galaxy", "Games", "Stardew Valley");
        yield return Path.Combine(pf, "GOG Galaxy", "Games", "Stardew Valley");
        yield return Path.Combine(pf86, "GOG Games", "Stardew Valley");
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
        {
            yield return Path.Combine(drive.Name, "SteamLibrary", "steamapps", "common", "Stardew Valley");
            yield return Path.Combine(drive.Name, "Games", "Stardew Valley");
        }
    }
}
