using System.Text.Json;
using System.Text.Json.Serialization;
using Lidgren.Network;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Network;

namespace StembridgeValley;

/// <summary>
/// Server side of Discord sign-in and the mod check.
///
/// The Discord bot (tools/discord_bot.py) gives each Discord member a personal code and writes
/// discord-players.json into the server's state folder. A code looks like sv:host:port/d-TOKEN.
/// The player's character belongs to their Discord account ("discord-ID"), not their PC.
/// Banned or departed members are listed under "revoked": they're turned away and kicked if online.
///
/// Mod check: a player may only run the mods the server runs. Anything extra gets them kicked with a list.
/// </summary>
internal static class Discord
{
    public const string CodePrefix = "d-";
    public const string KeyPrefix = "discord-";
    public const string DenyMods = "SV_MODS|";
    /// <summary>Second character: the launcher adds "~2" to the code, and the character's key gets "-2".</summary>
    public const string SlotSuffix = "~2", SlotKeySuffix = "-2";
    public const int MaxSlots = 2;

    private sealed class Player
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        /// <summary>Discord ID of a friend to be placed with (their farm), if any.</summary>
        [JsonPropertyName("friend")] public string? Friend { get; set; }
    }

    private sealed class Roster
    {
        [JsonPropertyName("tokens")] public Dictionary<string, Player> Tokens { get; set; } = new();
        [JsonPropertyName("revoked")] public HashSet<string> Revoked { get; set; } = new();
    }

    private static IModHelper Helper = null!;
    private static Roster roster = new();
    private static DateTime loadedStamp = DateTime.MinValue;
    private static string FilePath => Path.Combine(SV.StateDir, "discord-players.json");

    /// <summary>Once everyone has Discord codes, turn off the shared-password invites.</summary>
    public static bool Required => Environment.GetEnvironmentVariable("SV_REQUIRE_DISCORD") == "1";

    public static void Apply(IModHelper helper)
    {
        Helper = helper;
        helper.Events.GameLoop.OneSecondUpdateTicked += OnSecond;
    }

    private static void Reload()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;
            DateTime stamp = File.GetLastWriteTimeUtc(FilePath);
            if (stamp == loadedStamp)
                return;
            roster = JsonSerializer.Deserialize<Roster>(File.ReadAllText(FilePath)) ?? new();
            loadedStamp = stamp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read discord-players.json: {ex.Message}");
        }
    }

    /// <summary>A Discord code: who it belongs to (as a player key), and the farm of the friend they asked to join.</summary>
    public static bool TryReadCode(string sent, out string key, out string name, out string? friendFarm)
    {
        key = name = "";
        friendFarm = null;
        Reload();
        // d-DISCORDID-TOKEN: the ID tells the player's game which character is theirs; the token proves it.
        if (!TrySplit(sent, out string id, out string token, out int slot)
            || !roster.Tokens.TryGetValue(token, out Player? p) || p.Id != id || roster.Revoked.Contains(p.Id))
            return false;
        key = KeyFor(p.Id, slot);
        name = p.Name;
        // "friend" = the member who invited them (/invite): their first character is placed on that member's farm.
        if (p.Friend != null && Farms.Enabled && slot == 1)
            friendFarm = FarmRoster.FarmOfKey(KeyPrefix + p.Friend);
        return true;
    }

    /// <summary>Discord display name for a player key (discord-ID), if the bot has given them a code.</summary>
    public static string? NameOf(string key)
    {
        Reload();
        string id = AccountOf(key);
        return roster.Tokens.Values.FirstOrDefault(p => p.Id == id)?.Name;
    }

    /// <summary>Player key of a Discord account's character: discord-ID (first) or discord-ID-2 (second).</summary>
    public static string KeyFor(string id, int slot) => KeyPrefix + id + (slot == 2 ? SlotKeySuffix : "");

    /// <summary>The Discord ID behind a player key (either character).</summary>
    public static string AccountOf(string key)
    {
        string id = key.StartsWith(KeyPrefix) ? key[KeyPrefix.Length..] : key;
        return id.EndsWith(SlotKeySuffix) ? id[..^SlotKeySuffix.Length] : id;
    }

    public static int SlotOf(string key) => key.StartsWith(KeyPrefix) && key.EndsWith(SlotKeySuffix) ? 2 : 1;

    public static bool TrySplit(string code, out string id, out string token) => TrySplit(code, out id, out token, out _);

    public static bool TrySplit(string code, out string id, out string token, out int slot)
    {
        id = token = "";
        slot = 1;
        if (!code.StartsWith(CodePrefix, StringComparison.Ordinal))
            return false;
        code = code.Trim();
        if (code.EndsWith(SlotSuffix, StringComparison.Ordinal))
        {
            slot = 2;
            code = code[..^SlotSuffix.Length];
        }
        string[] parts = code.Split('-', 3);
        if (parts.Length != 3 || parts[1].Length == 0 || !parts[1].All(char.IsDigit) || parts[2].Length < 8)
            return false;
        id = parts[1];
        token = parts[2];
        return true;
    }

    private static int seconds;
    private static void OnSecond(object? sender, OneSecondUpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || ++seconds % 15 != 0)
            return;
        Reload();
        if (roster.Revoked.Count == 0)
            return;
        foreach (Farmer f in Game1.otherFarmers.Values.ToList())
        {
            string uid = f.userID.Value ?? "";
            if (uid.StartsWith(KeyPrefix) && roster.Revoked.Contains(AccountOf(uid)))
            {
                Log.Warn($"Kicking {f.Name}: no longer allowed on the Discord server.");
                Game1.server?.kick(f.UniqueMultiplayerID);
            }
        }
    }

    // ---------- mod check ----------

    private static HashSet<string>? allowed;

    /// <summary>Mods the joining player runs that the server doesn't (empty if none, or if SMAPI hasn't said).</summary>
    public static List<string> ExtraMods(long farmerId)
    {
        allowed ??= Helper.ModRegistry.GetAll().Select(m => m.Manifest.UniqueID.ToLowerInvariant()).ToHashSet();
        // A game without our mod can't get past the hail check at all, so a missing list just means "not sent yet".
        IMultiplayerPeer? peer = Helper.Multiplayer.GetConnectedPlayer(farmerId);
        if (peer == null || !peer.HasSmapi)
            return new List<string>();
        return peer.Mods.Where(m => !allowed.Contains((m.ID ?? "").ToLowerInvariant())).Select(m => string.IsNullOrEmpty(m.Name) ? m.ID : m.Name).ToList();
    }

    /// <summary>Close a connection before the player gets in, telling their game why.</summary>
    public static void Refuse(string connectionId, string message)
    {
        if (Game1.server is not GameServer gs)
            return;
        var servers = (System.Collections.IEnumerable)HarmonyLib.AccessTools.Field(typeof(GameServer), "servers").GetValue(gs)!;
        foreach (object s in servers)
            if (s is LidgrenServer ls && HarmonyLib.AccessTools.Field(typeof(LidgrenServer), "server").GetValue(ls) is NetServer net)
                foreach (NetConnection c in net.Connections)
                    if ("L_" + c.RemoteUniqueIdentifier == connectionId)
                    {
                        c.Disconnect(message);
                        net.FlushSendQueue();
                    }
    }
}
