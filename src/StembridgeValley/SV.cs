namespace StembridgeValley;

internal enum Role { None, Server, Client }

/// <summary>Shared state. Launchers pass connection details through environment variables, never through files in the pack.</summary>
internal static class SV
{
    public const string ModId = "Stembridge.StembridgeValley";
    public const string HailTag = "SV1";
    public const string DenyBadPassword = "SV_BADPASS";
    public const string DenyUpdate = "SV_UPDATE";

    public static Role Role = Role.None;
    public static ModConfig Config = new();

    /// <summary>Server address (client) as host:port.</summary>
    public static string Address = "";
    /// <summary>Shared server password.</summary>
    public static string Password = "";
    /// <summary>Stable per-player key so nobody can take someone else's farmhand.</summary>
    public static string PlayerKey = "";
    /// <summary>Mod pack version; server turns away players on a different version so their launcher updates.</summary>
    public static string PackVersion = "dev";
    /// <summary>Folder the launcher watches for restart/update flags.</summary>
    public static string StateDir = "";

    public static void Load()
    {
        string role = Env("SV_ROLE").ToLowerInvariant();
        Role = role switch { "server" => Role.Server, "client" => Role.Client, _ => Role.None };
        Address = Env("SV_ADDRESS");
        Password = Env("SV_PASSWORD");
        PlayerKey = Env("SV_PLAYER_KEY");
        PackVersion = Env("SV_PACK_VERSION") is { Length: > 0 } v ? v : "dev";
        StateDir = Env("SV_STATE_DIR");
    }

    public static void WriteFlag(string name, string content = "")
    {
        if (string.IsNullOrEmpty(StateDir))
            return;
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(Path.Combine(StateDir, name), content);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write flag {name}: {ex.Message}");
        }
    }

    private static string Env(string name) => Environment.GetEnvironmentVariable(name)?.Trim() ?? "";
}
