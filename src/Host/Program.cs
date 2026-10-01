using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using HarmonyLib;
using StembridgeValley.Shared;

namespace StembridgeValley.Host;

/// <summary>Settings for one hidden game instance (the server, or a test player). Lives in the instance folder, never in the public repo.</summary>
internal sealed class HostSettings
{
    public string Role { get; set; } = "server";
    public string Address { get; set; } = "";
    public string Password { get; set; } = "";
    public string PlayerKey { get; set; } = "";
    public string Feed { get; set; } = PackSync.DefaultFeed;
    /// <summary>Stardew install folder. Blank = find it automatically.</summary>
    public string GamePath { get; set; } = "";
    /// <summary>Restart the game automatically if it crashes.</summary>
    public bool AutoRestart { get; set; } = true;
    /// <summary>Stop after this many minutes (0 = run forever). Used by tests.</summary>
    public int MaxMinutes { get; set; } = 0;
    /// <summary>Tests only: extra environment variables for the game (e.g. SV_TEST_CHARACTER).</summary>
    public Dictionary<string, string> TestEnv { get; set; } = new();
}

/// <summary>
/// Runs Stardew + SMAPI hidden, inside this process, with its own saves/settings folder and no sound,
/// on a private Windows desktop so it can never pop up over a game. Must sit inside a copy of the game folder.
/// </summary>
internal static class Program
{
    private static string Game = "";
    private static string Instance = "";
    private static StreamWriter? LogFile;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetDllDirectory(string path);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AllocConsole();

    [STAThread]
    private static int Main(string[] args)
    {
        string? instanceArg = Arg(args, "--instance");
        if (instanceArg == null)
            return Fail("Usage: SVHost --instance <folder> [--stop]");
        Instance = Path.GetFullPath(instanceArg);
        Directory.CreateDirectory(Instance);
        bool worker = args.Contains("--worker");
        LogFile = new StreamWriter(Path.Combine(Instance, worker ? "game-host.log" : "host.log"), append: false) { AutoFlush = true };
        Console.SetOut(LogFile);
        Console.SetError(LogFile);

        try
        {
            if (args.Contains("--stop"))
            {
                File.WriteAllText(Path.Combine(Instance, "stop.flag"), DateTime.Now.ToString("s"));
                return 0;
            }
            var settings = LoadSettings();
            Game = GameFinder.IsGameFolder(settings.GamePath) ? settings.GamePath : GameFinder.Find() ?? throw new Exception("Couldn't find Stardew Valley. Put its folder in host.json as GamePath.");
            Game = Path.GetFullPath(Game).TrimEnd(Path.DirectorySeparatorChar);
            if (!GameFinder.HasSmapi(Game))
                throw new Exception("SMAPI isn't installed in " + Game);
            return worker ? RunGame(settings) : Supervise(settings);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return 1;
        }
    }

    // ---------------- supervisor ----------------

    private static int Supervise(HostSettings settings)
    {
        using var runLock = new FileStream(Path.Combine(Instance, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.Delete(Path.Combine(Instance, "stop.flag"));
        DateTime started = DateTime.UtcNow;
        int quickCrashes = 0;

        while (true)
        {
            string version;
            try
            {
                version = PackSync.EnsureAsync(settings.Feed, Instance, Say).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Say("Couldn't get the mod pack: " + ex.Message);
                return 3;
            }
            File.WriteAllText(Path.Combine(Instance, "pack-version.txt"), version);
            Directory.CreateDirectory(Path.Combine(Instance, "state"));
            File.Delete(Path.Combine(Instance, "state", "update-needed.txt"));

            DateTime runStart = DateTime.UtcNow;
            Say($"Starting {settings.Role} (pack {version}).");
            Func<bool> stopNow = () => File.Exists(Path.Combine(Instance, "stop.flag"))
                || (settings.MaxMinutes > 0 && DateTime.UtcNow - started > TimeSpan.FromMinutes(settings.MaxMinutes));
            int code = OperatingSystem.IsWindows()
                ? PrivateDesktop.Run(Environment.ProcessPath!, $"--instance \"{Instance}\" --worker", AppContext.BaseDirectory, Instance, stopNow)
                : RunChild(Environment.ProcessPath!,
                    (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet" ? $"\"{typeof(Program).Assembly.Location}\" " : "")
                    + $"--instance \"{Instance}\" --worker", AppContext.BaseDirectory, Instance, stopNow);
            Say($"Game stopped (code {code}).");

            if (File.Exists(Path.Combine(Instance, "stop.flag")))
            {
                Say("Stopped on request.");
                return 0;
            }
            if (settings.MaxMinutes > 0 && DateTime.UtcNow - started > TimeSpan.FromMinutes(settings.MaxMinutes))
                return code;
            if (File.Exists(Path.Combine(Instance, "state", "update-needed.txt")))
            {
                Say("Server has newer mods; updating and rejoining.");
                continue;
            }
            if (!settings.AutoRestart)
                return code;

            quickCrashes = DateTime.UtcNow - runStart < TimeSpan.FromMinutes(2) ? quickCrashes + 1 : 0;
            if (quickCrashes >= 3)
            {
                Say("Crashed 3 times in a row right after starting; giving up. See game-host.log.");
                return code;
            }
            Say("Restarting in 10 seconds.");
            Thread.Sleep(10_000);
        }
    }

    /// <summary>Linux: no desktop to hide from, so just run the worker as a child process.</summary>
    private static int RunChild(string app, string arguments, string workingDir, string instance, Func<bool> shouldStop)
    {
        var info = new ProcessStartInfo(app, arguments) { WorkingDirectory = workingDir, UseShellExecute = false };
        using var child = Process.Start(info)!;
        while (!child.WaitForExit(1000))
        {
            if (shouldStop())
            {
                try { child.Kill(entireProcessTree: true); } catch { }
                child.WaitForExit();
                break;
            }
        }
        return child.ExitCode;
    }

    // ---------------- hidden game ----------------

    private static int RunGame(HostSettings settings)
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (string folder in new[] { Game, Path.Combine(Game, "smapi-internal") })
            {
                string path = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(path))
                    return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
        {
            ArmPatch.Folder(Game, recursive: false);
            ArmPatch.Folder(Path.Combine(Instance, "Mods"), recursive: true);
        }
        Directory.SetCurrentDirectory(Game);
        if (OperatingSystem.IsWindows())
            SetDllDirectory(Game);
        // The game and MonoGame look for Content next to the "app"; point them at the real game folder.
        AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", Game + Path.DirectorySeparatorChar);
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "Stardew Valley.dll")))
            throw new Exception("Couldn't point the game at its folder.");

        // Own saves/settings/logs: never touch the PC owner's real Stardew data.
        var harmony = new Harmony("Stembridge.StembridgeValley.Host");
        foreach (MethodInfo method in typeof(Environment).GetMethods().Where(m => m.Name == nameof(Environment.GetFolderPath)))
            harmony.Patch(method, new HarmonyMethod(typeof(Program), nameof(FolderPrefix)));
        Directory.CreateDirectory(Path.Combine(Instance, "Roaming"));
        Directory.CreateDirectory(Path.Combine(Instance, "Local"));
        if (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) != Path.Combine(Instance, "Roaming"))
            throw new Exception("Folder redirection failed; refusing to start.");

        // Silent.
        Environment.SetEnvironmentVariable("SDL_AUDIODRIVER", "dummy");
        Environment.SetEnvironmentVariable("ALSOFT_DRIVERS", "null");

        // Tell the mod who it is.
        Environment.SetEnvironmentVariable("SV_ROLE", settings.Role);
        Environment.SetEnvironmentVariable("SV_ADDRESS", settings.Address);
        Environment.SetEnvironmentVariable("SV_PASSWORD", settings.Password);
        Environment.SetEnvironmentVariable("SV_PLAYER_KEY", settings.PlayerKey);
        Environment.SetEnvironmentVariable("SV_PACK_VERSION", File.ReadAllText(Path.Combine(Instance, "pack-version.txt")).Trim());
        Environment.SetEnvironmentVariable("SV_STATE_DIR", Path.Combine(Instance, "state"));
        foreach (var (key, value) in settings.TestEnv)
            Environment.SetEnvironmentVariable(key, value);

        Assembly game = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Game, "Stardew Valley.dll"));
        Type program = game.GetType("StardewValley.Program", true)!;
        // No Steam: the hidden copy must not sign in as the PC owner or fight his own game for Steam.
        object offline = Activator.CreateInstance(game.GetType("StardewValley.SDKs.NullSDKHelper", true)!, true)!;
        AccessTools.Field(program, "_sdk").SetValue(null, offline);
        string saves = (string)AccessTools.Method(program, "GetSavesFolder").Invoke(null, null)!;
        if (!saves.StartsWith(Instance + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new Exception("Save folder isolation failed: " + saves);

        Assembly smapi = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Game, "StardewModdingAPI.dll"));
        NoPhysicalInput.Apply(harmony);
        if (OperatingSystem.IsWindows())
            AllocConsole();
        Console.WriteLine($"Hidden {settings.Role} ready. Saves: {saves}");
        smapi.EntryPoint!.Invoke(null, new object[] { new[] { "--no-terminal", "--mods-path", Path.Combine(Instance, "Mods") } });
        return 0;
    }

    private static bool FolderPrefix(Environment.SpecialFolder folder, ref string __result)
    {
        if (folder == Environment.SpecialFolder.ApplicationData) { __result = Path.Combine(Instance, "Roaming"); return false; }
        if (folder == Environment.SpecialFolder.LocalApplicationData) { __result = Path.Combine(Instance, "Local"); return false; }
        return true;
    }

    // ---------------- helpers ----------------

    private static HostSettings LoadSettings()
    {
        string path = Path.Combine(Instance, "host.json");
        HostSettings settings = File.Exists(path)
            ? JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(path), Json) ?? new HostSettings()
            : new HostSettings();
        bool changed = !File.Exists(path);
        if (settings.Role == "server" && string.IsNullOrWhiteSpace(settings.Password))
        {
            settings.Password = MakePassword();
            changed = true;
        }
        if (settings.Role == "client" && string.IsNullOrWhiteSpace(settings.PlayerKey))
        {
            settings.PlayerKey = Guid.NewGuid().ToString("N");
            changed = true;
        }
        if (changed)
            File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
        return settings;
    }

    private static string MakePassword()
    {
        const string words = "acorn,amber,apple,badger,basil,berry,birch,bramble,breeze,brook,carrot,cedar,clover,cobble,daisy,dew,ember,fern,fig,finch,frost,garnet,ginger,hazel,heron,honey,iris,ivy,juniper,kale,lark,leek,lily,maple,meadow,melon,mint,moss,oak,otter,pebble,pine,plum,poppy,quartz,radish,rain,river,robin,rose,sage,seed,sparrow,spruce,sprout,star,straw,thyme,tulip,turnip,willow,wren";
        string[] list = words.Split(',');
        return string.Join("-", Enumerable.Range(0, 3).Select(_ => list[System.Security.Cryptography.RandomNumberGenerator.GetInt32(list.Length)]))
            + "-" + System.Security.Cryptography.RandomNumberGenerator.GetInt32(10, 100);
    }

    private static void Say(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    private static int Fail(string message) { Console.Error.WriteLine(message); return 2; }
    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
