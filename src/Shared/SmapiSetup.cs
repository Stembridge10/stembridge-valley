using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace StembridgeValley.Shared;

/// <summary>Where to get SMAPI. The launcher only runs an installer whose SHA-256 matches, so a swapped download is refused.</summary>
public sealed class SmapiInfo
{
    public string Version { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>Oldest SMAPI that still works with the pack. Older installs get updated.</summary>
    public string MinVersion { get; set; } = "";
}

/// <summary>Checks for SMAPI in the game folder and installs/updates it with the official installer (unattended).</summary>
public static class SmapiSetup
{
    // Official SMAPI release on GitHub (Pathoschild/SMAPI). The pack feed can name a newer one.
    public static readonly SmapiInfo Pinned = new()
    {
        Version = "4.5.2",
        Url = "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip",
        Sha256 = "dd01ddca7b566bfe0d3b3d2d03833496abc56c53da976241f2ab443f5484acc4",
        MinVersion = "4.0.0",
    };

    public static Version? InstalledVersion(string gameDir)
    {
        string dll = Path.Combine(gameDir, "StardewModdingAPI.dll");
        if (!File.Exists(dll) || !File.Exists(Path.Combine(gameDir, "StardewModdingAPI.exe")))
            return null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(dll);
            string text = info.ProductVersion ?? info.FileVersion ?? "";
            text = new string(text.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
            return System.Version.TryParse(text, out var v) ? v : new Version(0, 0);
        }
        catch { return new Version(0, 0); }
    }

    public static bool NeedsInstall(string gameDir, SmapiInfo want, out Version? have)
    {
        have = InstalledVersion(gameDir);
        if (have == null)
            return true;
        return System.Version.TryParse(want.MinVersion, out var min) && have < min;
    }

    /// <summary>Is a copy of the game in <paramref name="gameDir"/> open? (Files can't be replaced while it runs.)</summary>
    public static bool IsGameRunning(string gameDir)
    {
        string want = Path.GetFullPath(gameDir).TrimEnd('\\') + "\\";
        foreach (var p in Process.GetProcessesByName("Stardew Valley").Concat(Process.GetProcessesByName("StardewModdingAPI")))
        {
            try
            {
                string? exe = p.MainModule?.FileName;
                if (exe == null || exe.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch { return true; } // can't tell: be careful
            finally { p.Dispose(); }
        }
        return false;
    }

    /// <summary>Download, check, and run the official installer for <paramref name="gameDir"/>. Progress goes to <paramref name="progress"/> (0-100, or -1 for "busy").</summary>
    public static async Task InstallAsync(SmapiInfo want, string gameDir, string workDir, Action<string> log, Action<int> progress)
    {
        string dir = Path.Combine(workDir, "smapi");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        string zip = Path.Combine(dir, "installer.zip");

        log($"Downloading SMAPI {want.Version}...");
        if (want.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(want.Url))
        {
            File.Copy(want.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(want.Url).LocalPath : want.Url, zip, true);
            progress(100);
        }
        else
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("JunimoHollowLauncher/1.0");
            using var response = await http.GetAsync(want.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zip);
            var buf = new byte[81920];
            long done = 0;
            int read, last = -1;
            while ((read = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read));
                done += read;
                int pct = total > 0 ? (int)(done * 100 / total) : -1;
                if (pct != last) { progress(pct); last = pct; }
            }
        }

        string hash;
        using (var f = File.OpenRead(zip))
        using (var sha = SHA256.Create())
            hash = Convert.ToHexString(sha.ComputeHash(f)).ToLowerInvariant();
        if (hash != want.Sha256.ToLowerInvariant())
        {
            Directory.Delete(dir, true);
            throw new Exception("The SMAPI download failed its safety check, so it wasn't installed. Try again later.");
        }

        progress(-1);
        log("Installing SMAPI...");
        string unpacked = Path.Combine(dir, "unpacked");
        ZipFile.ExtractToDirectory(zip, unpacked);
        string bundle = Directory.EnumerateFiles(unpacked, "install.dat", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Replace('/', '\\').Contains(@"\internal\windows\", StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception("The SMAPI download was missing files. Your antivirus may have removed some; try again.");
        string files = Path.Combine(dir, "files");
        ZipFile.ExtractToDirectory(bundle, files);
        if (!File.Exists(Path.Combine(files, "StardewModdingAPI.exe")) || !Directory.Exists(Path.Combine(files, "smapi-internal")))
            throw new Exception("The SMAPI download was missing files. Your antivirus may have removed some; try again.");

        try
        {
            CopyInto(files, gameDir, log);
        }
        catch (UnauthorizedAccessException) when (Environment.GetEnvironmentVariable("SV_NO_ELEVATE") != "1")
        {
            // The game folder needs admin rights to change: ask Windows once, then copy the same checked files.
            log("Asking Windows for permission to finish installing...");
            if (!RunElevatedCopy(files, gameDir))
                throw new Exception("Windows didn't give permission to change the Stardew Valley folder, so SMAPI wasn't installed.");
        }
        var have = InstalledVersion(gameDir);
        if (have == null)
            throw new Exception("SMAPI's files didn't end up in the game folder.");
        try { Directory.Delete(dir, true); } catch { }
        log($"SMAPI {have.ToString(3)} is installed.");
    }

    // Files an older SMAPI may have left that the new one replaces (from SMAPI's own uninstall list, Windows only).
    private static readonly string[] OldFiles =
    {
        "StardewModdingAPI.deps.json", "StardewModdingAPI.dll", "StardewModdingAPI.exe", "StardewModdingAPI.exe.config",
        "StardewModdingAPI.pdb", "StardewModdingAPI.runtimeconfig.json", "StardewModdingAPI.xml", "smapi-internal", "steam_appid.txt",
    };

    /// <summary>
    /// The same steps SMAPI's Windows installer takes: remove the old SMAPI files, copy the new ones next to the game,
    /// give SMAPI a copy of the game's deps.json, and add SMAPI's two built-in mods. Mods the player already has are left alone.
    /// </summary>
    public static void CopyInto(string files, string gameDir, Action<string> log)
    {
        string? keepConfig = null;
        string userConfig = Path.Combine(gameDir, "smapi-internal", "config.user.json");
        if (File.Exists(userConfig))
            keepConfig = File.ReadAllText(userConfig);

        foreach (string name in OldFiles)
        {
            string p = Path.Combine(gameDir, name);
            if (Directory.Exists(p)) Directory.Delete(p, true);
            else if (File.Exists(p)) File.Delete(p);
        }
        foreach (string src in Directory.EnumerateFileSystemEntries(files))
        {
            string name = Path.GetFileName(src);
            if (name.Equals("Mods", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Directory.Exists(src)) CopyDir(src, Path.Combine(gameDir, name));
            else File.Copy(src, Path.Combine(gameDir, name), true);
        }
        File.Copy(Path.Combine(gameDir, "Stardew Valley.deps.json"), Path.Combine(gameDir, "StardewModdingAPI.deps.json"), true);
        if (keepConfig != null)
            File.WriteAllText(userConfig, keepConfig);

        string mods = Path.Combine(gameDir, "Mods");
        Directory.CreateDirectory(mods);
        string bundled = Path.Combine(files, "Mods");
        if (Directory.Exists(bundled))
            foreach (string mod in Directory.EnumerateDirectories(bundled))
            {
                string target = Path.Combine(mods, Path.GetFileName(mod));
                if (!Directory.Exists(target))
                    CopyDir(mod, target);
            }
        log("SMAPI files copied.");
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.EnumerateFiles(from))
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (string d in Directory.EnumerateDirectories(from))
            CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }

    /// <summary>Runs this launcher again with admin rights just to copy the already-checked files.</summary>
    private static bool RunElevatedCopy(string files, string gameDir)
    {
        string self = Environment.ProcessPath ?? throw new Exception("couldn't find the launcher");
        var start = new ProcessStartInfo
        {
            FileName = self,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--copy-smapi");
        start.ArgumentList.Add(files);
        start.ArgumentList.Add(gameDir);
        try
        {
            using var p = Process.Start(start);
            if (p == null || !p.WaitForExit(120_000))
                return false;
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // the player said no
        }
    }
}
