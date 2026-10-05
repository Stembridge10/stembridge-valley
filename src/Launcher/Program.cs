using System.Diagnostics;
using System.Text;
using System.Text.Json;
using StembridgeValley.Shared;

namespace StembridgeValley.Launcher;

internal sealed class LauncherSettings
{
    public string Address { get; set; } = "";
    public string Password { get; set; } = "";
    public string PlayerKey { get; set; } = "";
    public string GamePath { get; set; } = "";
    public string Feed { get; set; } = PackSync.DefaultFeed;
}

/// <summary>
/// "Play Junimo Hollow": keeps its own Mods folder in sync with the published pack,
/// starts Stardew through SMAPI with that folder (the player's normal Mods/Vortex setup is never touched),
/// and joins the server automatically. If the server has a newer pack, it updates and rejoins.
/// </summary>
internal static class Program
{
    // SV_LAUNCHER_ROOT lets the automated test use a throwaway folder.
    private static readonly string Root = Environment.GetEnvironmentVariable("SV_LAUNCHER_ROOT") is { Length: > 0 } testRoot
        ? testRoot
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StembridgeValley");
    private static readonly string SettingsPath = Path.Combine(Root, "launcher.json");
    private static readonly string StateDir = Path.Combine(Root, "state");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static StreamWriter? log;

    private static int Main(string[] args)
    {
        Console.Title = "Junimo Hollow";
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(Root);
        log = new StreamWriter(Path.Combine(Root, "launcher.log"), append: false) { AutoFlush = true };
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            log.WriteLine(ex);
            Say("");
            Say("Something went wrong: " + ex.Message);
            Say("Send Stembridge the file: " + Path.Combine(Root, "launcher.log"));
            Pause();
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        Banner();
        var settings = Load();

        string? invite = args.FirstOrDefault(a => a.StartsWith("sv:", StringComparison.OrdinalIgnoreCase));
        if (args.Contains("--reset") || invite != null || string.IsNullOrWhiteSpace(settings.Address))
            AskForInvite(settings, invite);
        if (string.IsNullOrWhiteSpace(settings.PlayerKey))
            settings.PlayerKey = Guid.NewGuid().ToString("N");

        if (!GameFinder.IsGameFolder(settings.GamePath))
            settings.GamePath = GameFinder.Find() ?? AskForGameFolder();
        Save(settings);

        if (!GameFinder.HasSmapi(settings.GamePath))
        {
            Say("SMAPI (the Stardew mod loader) isn't installed yet.");
            Say("Install it from https://smapi.io, then run this launcher again.");
            TryOpen("https://smapi.io");
            Pause();
            return 2;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            string version = PackSync.EnsureAsync(settings.Feed, Root, Say).GetAwaiter().GetResult();
            ClearFlags();

            Say("Starting Stardew Valley...");
            int code = Launch(settings, version);

            if (File.Exists(Path.Combine(StateDir, "update-needed.txt")))
            {
                Say("The server has newer mods. Updating, then joining again...");
                Thread.Sleep(3000); // the published pack can lag the server by a moment
                continue;
            }
            if (File.Exists(Path.Combine(StateDir, "bad-password.txt")))
            {
                Say("The server didn't accept your code.");
                Say("Get a fresh one: type /play in the Junimo Hollow Discord.");
                AskForInvite(settings, null);
                Save(settings);
                attempt = -1; // fresh code: start over
                continue;
            }
            string extraMods = Path.Combine(StateDir, "extra-mods.txt");
            if (File.Exists(extraMods))
            {
                Say("The server only allows the Junimo Hollow mod pack, and your game had extra mods:");
                Say("  " + File.ReadAllText(extraMods).Trim());
                Say($"Remove them from {Path.Combine(Root, "Mods")} and start the launcher again.");
                Pause();
                return 5;
            }
            Say(code == 0 ? "See you next time!" : $"Stardew closed (code {code}).");
            Thread.Sleep(1500);
            return code;
        }
        Say("Still out of date after updating. Tell Stembridge: the server's mods may be newer than the published pack.");
        Pause();
        return 4;
    }

    private static int Launch(LauncherSettings settings, string version)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(settings.GamePath, "StardewModdingAPI.exe"),
            WorkingDirectory = settings.GamePath,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--mods-path");
        start.ArgumentList.Add(Path.Combine(Root, "Mods"));
        start.Environment["SV_ROLE"] = "client";
        start.Environment["SV_ADDRESS"] = settings.Address;
        start.Environment["SV_PASSWORD"] = settings.Password;
        start.Environment["SV_PLAYER_KEY"] = settings.PlayerKey;
        start.Environment["SV_PACK_VERSION"] = version;
        start.Environment["SV_STATE_DIR"] = StateDir;
        using var game = Process.Start(start) ?? throw new Exception("Couldn't start SMAPI.");
        Say("Have fun! (You can minimise this window.)");
        game.WaitForExit();
        return game.ExitCode;
    }

    // ---------- setup ----------

    private static void AskForInvite(LauncherSettings settings, string? invite)
    {
        while (true)
        {
            if (invite == null)
            {
                Say("Paste your invite code, then press Enter.");
                Say("(Get yours by typing /play in the Junimo Hollow Discord. It starts with  sv: )");
                Console.Write("> ");
                invite = Console.ReadLine();
            }
            if (TryParseInvite(invite, out string address, out string password))
            {
                settings.Address = address;
                settings.Password = password;
                Say($"Got it. Server: {address}");
                return;
            }
            Say("That doesn't look like an invite code. Try again.");
            invite = null;
        }
    }

    /// <summary>Invite format: sv:host:port/password</summary>
    public static bool TryParseInvite(string? text, out string address, out string password)
    {
        address = password = "";
        text = text?.Trim().Trim('"', '\'', '<', '>', '`');
        if (string.IsNullOrEmpty(text) || !text.StartsWith("sv:", StringComparison.OrdinalIgnoreCase))
            return false;
        string body = text[3..].TrimStart('/');
        int slash = body.IndexOf('/');
        if (slash <= 0 || slash == body.Length - 1)
            return false;
        address = body[..slash];
        password = body[(slash + 1)..];
        if (!address.Contains(':'))
            address += ":24642";
        return true;
    }

    private static string AskForGameFolder()
    {
        while (true)
        {
            Say("Couldn't find Stardew Valley. Paste its install folder (the one with \"Stardew Valley.exe\"):");
            Console.Write("> ");
            string? path = Console.ReadLine()?.Trim().Trim('"');
            if (GameFinder.IsGameFolder(path))
                return path!;
            Say("That folder doesn't have the game in it.");
        }
    }

    // ---------- helpers ----------

    private static LauncherSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsPath), Json) ?? new();
        }
        catch { }
        return new LauncherSettings();
    }

    private static void Save(LauncherSettings s) => File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Json));

    private static void ClearFlags()
    {
        Directory.CreateDirectory(StateDir);
        foreach (string f in new[] { "update-needed.txt", "bad-password.txt", "extra-mods.txt", "joined.txt" })
            File.Delete(Path.Combine(StateDir, f));
    }

    private static void Banner()
    {
        Say("==============================");
        Say("      Junimo Hollow");
        Say("==============================");
    }

    private static void Say(string message)
    {
        Console.WriteLine(message);
        log?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }

    private static void Pause()
    {
        if (Console.IsInputRedirected)
            return; // automated test or no keyboard: don't hang
        Say("Press Enter to close.");
        Console.ReadLine();
    }

    private static void TryOpen(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
