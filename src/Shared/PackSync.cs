using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;

namespace StembridgeValley.Shared;

/// <summary>What the launcher downloads: a small pack.json pointing at a zip of the Mods folder.</summary>
public sealed class PackInfo
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
}

/// <summary>Keeps a Mods folder identical to the published pack. Only our own trusted feed is used, and every zip is checked against its SHA-256 before install.</summary>
public static class PackSync
{
    public const string DefaultFeed = "https://github.com/Stembridge10/stembridge-valley/releases/latest/download/pack.json";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static async Task<PackInfo> FetchInfoAsync(string feed)
    {
        string text = IsLocal(feed)
            ? await File.ReadAllTextAsync(LocalPath(feed))
            : await Http().GetStringAsync(feed + (feed.Contains('?') ? "&" : "?") + "t=" + DateTime.UtcNow.Ticks);
        var info = JsonSerializer.Deserialize<PackInfo>(text, Json) ?? throw new Exception("pack.json is empty");
        if (string.IsNullOrWhiteSpace(info.Version) || string.IsNullOrWhiteSpace(info.Url) || info.Sha256.Length != 64)
            throw new Exception("pack.json is missing version, url or sha256");
        return info;
    }

    public static string? InstalledVersion(string root)
    {
        string path = Path.Combine(root, "pack-installed.json");
        if (!File.Exists(path))
            return null;
        try { return JsonSerializer.Deserialize<PackInfo>(File.ReadAllText(path), Json)?.Version; }
        catch { return null; }
    }

    /// <summary>Make <paramref name="root"/>/Mods match the feed. Returns the installed version.</summary>
    public static async Task<string> EnsureAsync(string feed, string root, Action<string> log)
    {
        Directory.CreateDirectory(root);
        string? have = InstalledVersion(root);
        PackInfo info;
        try
        {
            info = await FetchInfoAsync(feed);
        }
        catch (Exception ex) when (have != null && Directory.Exists(Path.Combine(root, "Mods")))
        {
            log($"Couldn't check for updates ({ex.Message}). Using the mods you already have ({have}).");
            return have;
        }

        if (have == info.Version && Directory.Exists(Path.Combine(root, "Mods")))
        {
            log($"Mods are up to date ({have}).");
            return have;
        }

        log(have == null ? $"Downloading mods ({info.Version})..." : $"Updating mods {have} -> {info.Version}...");
        string zip = Path.Combine(root, "pack-download.zip");
        if (IsLocal(info.Url))
            File.Copy(LocalPath(info.Url), zip, overwrite: true);
        else
        {
            using var response = await Http().GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(zip);
            await response.Content.CopyToAsync(file);
        }

        string hash;
        using (var check = File.OpenRead(zip))
        using (var sha = SHA256.Create())
            hash = Convert.ToHexString(sha.ComputeHash(check)).ToLowerInvariant();
        if (hash != info.Sha256.ToLowerInvariant())
        {
            File.Delete(zip);
            throw new Exception("The downloaded mod pack failed its safety check (checksum mismatch). Nothing was installed.");
        }

        string mods = Path.Combine(root, "Mods");
        string fresh = Path.Combine(root, "Mods.new");
        string old = Path.Combine(root, "Mods.old");
        if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
        ZipFile.ExtractToDirectory(zip, fresh); // .NET rejects paths that escape the folder
        if (Directory.Exists(old)) Directory.Delete(old, true);
        if (Directory.Exists(mods)) Directory.Move(mods, old);
        Directory.Move(fresh, mods);
        File.Delete(zip);
        File.WriteAllText(Path.Combine(root, "pack-installed.json"), JsonSerializer.Serialize(info, Json));
        log($"Mods ready ({info.Version}).");
        return info.Version;
    }

    private static HttpClient? http;
    private static HttpClient Http()
    {
        if (http == null)
        {
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("StembridgeValleyLauncher/1.0");
        }
        return http;
    }

    private static bool IsLocal(string s) => s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(s);
    private static string LocalPath(string s) => s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(s).LocalPath : s;
}
