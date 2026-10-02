using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace StembridgeValley;

/// <summary>
/// Server side of the 4-player farms: secret per-farm invite codes, which farm a new player is placed on,
/// and keeping exactly four cabins on every farm.
///
/// Invite codes are the normal server code plus a farm code: sv:host:port/password+farmcode.
/// The launcher passes the whole thing as the password, so no launcher change is needed.
/// No farm code = "put me anywhere with room".
/// </summary>
internal static class FarmRoster
{
    private static Dictionary<string, string> codeByFarm = new();

    private static string FilePath => Path.Combine(SV.StateDir, "farms.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                codeByFarm = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read farms.json: {ex.Message}");
        }
        bool changed = false;
        foreach (string name in Farms.AllNames)
            if (!codeByFarm.ContainsKey(name))
            {
                codeByFarm[name] = NewCode();
                changed = true;
            }
        if (changed)
            File.WriteAllText(FilePath, JsonSerializer.Serialize(codeByFarm, new JsonSerializerOptions { WriteIndented = true }));
        WriteInvites();
    }

    private static string NewCode()
    {
        string[] words = { "acorn", "berry", "clover", "dew", "fern", "gourd", "hops", "iris", "kale", "leek", "moss", "nut", "oat", "pear", "quince", "rye", "sage", "thyme", "yam", "zest" };
        return $"{words[Random.Shared.Next(words.Length)]}-{words[Random.Shared.Next(words.Length)]}-{Random.Shared.Next(10, 100)}";
    }

    /// <summary>A private list for Stembridge: one invite per farm, plus the open invite.</summary>
    private static void WriteInvites()
    {
        string host = Environment.GetEnvironmentVariable("SV_PUBLIC_ADDRESS") is { Length: > 0 } a ? a : "<server-address>:24642";
        var lines = new List<string>
        {
            "Stembridge Valley invites. Keep these private.",
            "",
            $"Open invite (puts a new player on any farm with room):",
            $"  sv:{host}/{SV.Password}",
            "",
            "Farm invites (puts a new player on that farm, if it has room):",
        };
        foreach (var (farm, code) in codeByFarm.OrderBy(k => k.Key))
            if (Farms.AllNames.Contains(farm))
                lines.Add($"  {Farms.DisplayName(farm)}: sv:{host}/{SV.Password}+{code}");
        SV.WriteFlag("invites.txt", string.Join("\n", lines) + "\n");
    }

    /// <summary>Split "password+farmcode". Returns the farm the code belongs to (or null), and whether the code was valid.</summary>
    public static bool TryReadInvite(string sent, out string password, out string? farm)
    {
        farm = null;
        int plus = sent.IndexOf('+');
        if (plus < 0)
        {
            password = sent;
            return true;
        }
        password = sent[..plus];
        string code = sent[(plus + 1)..].Trim().ToLowerInvariant();
        foreach (var (name, c) in codeByFarm)
            if (c == code && Farms.AllNames.Contains(name))
            {
                farm = name;
                return true;
            }
        return false;
    }

    /// <summary>
    /// Which farm a brand-new player should go to: the farm in their invite if it has room,
    /// otherwise a farm that already has people and room (so groups fill up), otherwise the first empty farm.
    /// </summary>
    public static string? PlaceNewPlayer(string? invitedFarm)
    {
        if (invitedFarm != null && FreeSlots(invitedFarm) > 0)
            return invitedFarm;
        var candidates = Farms.AllNames.Where(f => FreeSlots(f) > 0).ToList();
        return candidates.Where(f => Farms.MembersOf(f).Count > 0).OrderByDescending(f => Farms.MembersOf(f).Count).FirstOrDefault()
            ?? candidates.FirstOrDefault();
    }

    public static int FreeSlots(string farmName) => Game1.getLocationFromName(farmName) is { } farm
        ? farm.buildings.Count(b => b.GetIndoors() is Cabin c && (!c.HasOwner || !c.owner.isCustomized.Value))
        : 0;

    // ---------- cabins ----------

    /// <summary>Every farm gets exactly four cabins (built once; never removed).</summary>
    public static void EnsureCabins()
    {
        foreach (string name in Farms.AllNames)
        {
            if (Game1.getLocationFromName(name) is not Farm farm)
            {
                Log.Warn($"{name} is missing from the world.");
                continue;
            }
            int have = farm.buildings.Count(b => b.isCabin);
            for (int i = have; i < Farms.PlayersPerFarm; i++)
                if (!Server.BuildCabin(farm, i))
                    break;
        }
    }
}
