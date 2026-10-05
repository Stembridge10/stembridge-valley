using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
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
/// "Play Junimo Hollow": finds the game, installs SMAPI if it's missing, keeps its own Mods folder in sync
/// with the published pack, starts Stardew through SMAPI with that folder (the player's normal Mods/Vortex
/// setup is never touched), and joins the server. If the server has a newer pack, it updates and rejoins.
/// Shows a small friendly window; --headless runs the same steps as plain text (automated tests).
/// </summary>
internal static class Program
{
    // SV_LAUNCHER_ROOT lets the automated test use a throwaway folder.
    private static readonly string Root = Environment.GetEnvironmentVariable("SV_LAUNCHER_ROOT") is { Length: > 0 } testRoot
        ? testRoot
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StembridgeValley");
    private static readonly string SettingsPath = Path.Combine(Root, "launcher.json");
    private static readonly string StateDir = Path.Combine(Root, "state");
    private static readonly string LogPath = Path.Combine(Root, "launcher.log");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static StreamWriter? log;
    private static bool headless;

    [STAThread]
    private static int Main(string[] args)
    {
        // Elevated helper (started by SmapiSetup when the game folder needs admin rights): copy, then exit.
        if (args.Length == 3 && args[0] == "--copy-smapi")
        {
            try { SmapiSetup.CopyInto(args[1], args[2], _ => { }); return 0; }
            catch { return 1; }
        }
        headless = args.Contains("--headless");
        Directory.CreateDirectory(Root);
        log = new StreamWriter(LogPath, append: false) { AutoFlush = true };

        if (headless)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { } // no console window (piped)
            var console = new ConsoleUi(Say);
            return Guarded(console, args).GetAwaiter().GetResult();
        }

        ApplicationConfiguration.Initialize();
        var window = new LauncherWindow();
        int result = 1;
        window.Shown += (_, _) => Task.Run(async () =>
        {
            result = await Guarded(window, args);
            window.Done("");
        });
        Application.Run(window);
        return result;
    }

    private static async Task<int> Guarded(IUi ui, string[] args)
    {
        try
        {
            return await Run(ui, args);
        }
        catch (Exception ex)
        {
            log?.WriteLine(ex);
            await ui.Problem("Something went wrong",
                ex.Message + "\n\nIf it keeps happening, send Stembridge this file: " + LogPath, canRetry: false);
            return 1;
        }
    }

    private static async Task<int> Run(IUi ui, string[] args)
    {
        Say("Junimo Hollow launcher");
        var settings = Load();

        // 1. Invite code
        string? invite = args.FirstOrDefault(a => a.StartsWith("sv:", StringComparison.OrdinalIgnoreCase));
        if (args.Contains("--reset") || invite != null || string.IsNullOrWhiteSpace(settings.Address))
            if (!await GetInvite(ui, settings, invite, null))
                return 3;
        if (string.IsNullOrWhiteSpace(settings.PlayerKey))
            settings.PlayerKey = Guid.NewGuid().ToString("N");

        // 2. Game folder
        ui.Status("Looking for Stardew Valley...");
        if (!GameFinder.IsGameFolder(settings.GamePath))
        {
            string? found = GameFinder.Find();
            string? problem = null;
            while (found == null)
            {
                string? path = (await ui.AskGameFolder(problem))?.Trim().Trim('"');
                if (path == null)
                    return 3;
                if (GameFinder.IsGameFolder(path))
                    found = path;
                else
                    problem = "That folder doesn't have Stardew Valley in it. Try again.";
            }
            settings.GamePath = found;
        }
        Save(settings);
        Say("Game: " + settings.GamePath);

        // 3. SMAPI (the mod loader)
        if (!await EnsureSmapi(ui, settings.GamePath))
            return 2;
        if (args.Contains("--setup-only"))
        {
            Say("Setup finished.");
            return 0;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            // 4. Mods
            ui.Status("Checking for mod updates...");
            ui.Progress(-1);
            string version = await PackSync.EnsureAsync(settings.Feed, Root, m => { Say(m); ui.Status("Getting the mods ready...", m); });
            ui.Progress(null);
            ClearFlags();

            // 5. Play
            ui.Status("Starting Stardew Valley...");
            Say("Starting Stardew Valley...");
            var started = DateTime.UtcNow;
            int code = Launch(settings, version, ui);

            if (File.Exists(Path.Combine(StateDir, "update-needed.txt")))
            {
                Say("The server has newer mods. Updating, then joining again...");
                ui.Status("The server has newer mods", "Updating them, then joining again...");
                Thread.Sleep(3000); // the published pack can lag the server by a moment
                continue;
            }
            if (File.Exists(Path.Combine(StateDir, "bad-password.txt")))
            {
                Say("The server didn't accept your code.");
                if (!await GetInvite(ui, settings, null, "The server didn't accept that code. Type /play in Discord for a fresh one."))
                    return 3;
                Save(settings);
                attempt = -1; // fresh code: start over
                continue;
            }
            string extraMods = Path.Combine(StateDir, "extra-mods.txt");
            if (File.Exists(extraMods))
            {
                string list = File.ReadAllText(extraMods).Trim();
                Say("Extra mods: " + list);
                await ui.Problem("The server only allows its own mods",
                    $"These extra mods were found: {list}\n\nRemove them from {Path.Combine(Root, "Mods")} and start the launcher again.", false);
                return 5;
            }
            if (code != 0 && DateTime.UtcNow - started < TimeSpan.FromMinutes(2))
            {
                Say($"Stardew closed early (code {code}).");
                if (await ui.Problem("Stardew Valley closed unexpectedly",
                        "It stopped while starting up. Its log is in %AppData%\\StardewValley\\ErrorLogs\\SMAPI-latest.txt if Stembridge needs it.", canRetry: true))
                {
                    attempt = -1;
                    continue;
                }
                return code;
            }
            Say(code == 0 ? "See you next time!" : $"Stardew closed (code {code}).");
            return code;
        }
        Say("Still out of date after updating.");
        await ui.Problem("Couldn't get the newest mods",
            "The server's mods are newer than the ones published for download. Tell Stembridge, then try again in a few minutes.", false);
        return 4;
    }

    // ---------- steps ----------

    private static async Task<bool> GetInvite(IUi ui, LauncherSettings settings, string? invite, string? problem)
    {
        while (true)
        {
            invite ??= await ui.AskInvite(problem);
            if (invite == null)
                return false;
            if (TryParseInvite(invite, out string address, out string password))
            {
                settings.Address = address;
                settings.Password = password;
                Save(settings);
                Say($"Got it. Server: {address}");
                return true;
            }
            Say("That doesn't look like an invite code.");
            if (headless && Console.IsInputRedirected && invite.Length == 0)
                return false; // test with no keyboard: don't loop forever
            problem = "That doesn't look like an invite code. It should start with sv:";
            invite = null;
        }
    }

    private static async Task<bool> EnsureSmapi(IUi ui, string gameDir)
    {
        var want = SmapiTarget();
        if (!SmapiSetup.NeedsInstall(gameDir, want, out var have))
        {
            Say($"SMAPI {have?.ToString(3)} found.");
            return true;
        }
        Say(have == null ? "SMAPI not installed." : $"SMAPI {have.ToString(3)} is too old (need {want.MinVersion}).");
        bool ok = await ui.Confirm(
            have == null ? "One-time setup: install SMAPI" : "One-time setup: update SMAPI",
            "Junimo Hollow needs SMAPI, the free mod loader almost every Stardew mod uses. "
            + "We'll download the official version and set it up for you. Your saves and any mods you already have stay as they are.",
            have == null ? "Install" : "Update");
        if (!ok)
        {
            Say("SMAPI install declined.");
            return false;
        }
        while (true)
        {
            try
            {
                while (SmapiSetup.IsGameRunning(gameDir))
                    if (!await ui.Problem("Please close Stardew Valley first", "SMAPI can't be installed while the game is open.", canRetry: true))
                        return false;
                ui.Status("Setting up SMAPI...", "Downloading the official installer from smapi.io's GitHub.");
                await SmapiSetup.InstallAsync(want, gameDir, Root,
                    m => { Say(m); ui.Status("Setting up SMAPI...", m); },
                    p => ui.Progress(p));
                ui.Progress(null);
                return true;
            }
            catch (Exception ex)
            {
                log?.WriteLine(ex);
                ui.Progress(null);
                Say("SMAPI install failed: " + ex.Message);
                if (!await ui.Problem("Couldn't install SMAPI",
                        ex.Message + "\n\nYou can also install it yourself from smapi.io, then start this launcher again.", canRetry: true))
                    return false;
            }
        }
    }

    /// <summary>The SMAPI release to install. Tests can point this at a local copy of the same official zip.</summary>
    private static SmapiInfo SmapiTarget()
    {
        var t = SmapiSetup.Pinned;
        string? url = Environment.GetEnvironmentVariable("SV_SMAPI_URL"), sha = Environment.GetEnvironmentVariable("SV_SMAPI_SHA256");
        if (!string.IsNullOrEmpty(url) && sha?.Length == 64)
            t = new SmapiInfo { Version = t.Version, Url = url, Sha256 = sha, MinVersion = t.MinVersion };
        return t;
    }

    private static int Launch(LauncherSettings settings, string version, IUi ui)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(settings.GamePath, "StardewModdingAPI.exe"),
            WorkingDirectory = settings.GamePath,
            UseShellExecute = false,
            CreateNoWindow = !headless, // no black SMAPI console; the game window still opens
        };
        start.ArgumentList.Add("--mods-path");
        start.ArgumentList.Add(Path.Combine(Root, "Mods"));
        if (!headless)
            start.ArgumentList.Add("--no-terminal");
        start.Environment["SV_ROLE"] = "client";
        start.Environment["SV_ADDRESS"] = settings.Address;
        start.Environment["SV_PASSWORD"] = settings.Password;
        start.Environment["SV_PLAYER_KEY"] = settings.PlayerKey;
        start.Environment["SV_PACK_VERSION"] = version;
        start.Environment["SV_STATE_DIR"] = StateDir;
        using var game = Process.Start(start) ?? throw new Exception("Couldn't start SMAPI.");
        Say("Have fun!");
        ui.Playing();
        game.WaitForExit();
        return game.ExitCode;
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

    private static void Say(string message)
    {
        if (headless)
            Console.WriteLine(message);
        lock (Json)
            log?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
