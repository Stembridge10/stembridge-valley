using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;

namespace StembridgeValley;

/// <summary>
/// Server side of the farms: who belongs to which farm, which farm a new player is placed on,
/// and how many cabins each farm has.
///
/// Every new player gets a farm of their own (one cabin). A friend joins it only when a member
/// invites them (Discord /invite), and each friend who joins gets a new cabin in the row, up to four.
/// Strangers are never placed on someone's farm. Farms are kept forever.
///
/// farm-members.json: { "SV_Farm1": ["discord-123", ...], ... } (first member founded the farm).
/// A slot is held from the moment a player is placed, so it's still theirs if they quit during character creation.
/// The Discord bot reads this file to answer /invite and /farm.
/// </summary>
internal static class FarmRoster
{
    private static Dictionary<string, string> codeByFarm = new();
    private static Dictionary<string, List<string>> members = new();

    private static string FilePath => Path.Combine(SV.StateDir, "farms.json");
    private static string MembersPath => Path.Combine(SV.StateDir, "farm-members.json");

    /// <summary>How many farms were open last time (read before the world loads, so they all get created).</summary>
    public static int SavedFarmCount()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath))?.Count ?? 0;
        }
        catch { }
        return 0;
    }

    public static IReadOnlyList<string> Members(string farm) => members.TryGetValue(farm, out var l) ? l : new List<string>();

    public static string? FarmOfKey(string key) => members.FirstOrDefault(kv => kv.Value.Contains(key)).Key;

    /// <summary>Always keep one farm nobody belongs to yet, so the next new player gets theirs instantly.</summary>
    public static void KeepOneEmptyFarm()
    {
        if (Farms.AllNames.All(f => Members(f).Count > 0))
            Farms.OpenFarm();
    }

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
        LoadMembers();
    }

    /// <summary>Read the member lists, then make sure every existing character is listed on the farm they live on.</summary>
    private static void LoadMembers()
    {
        try
        {
            if (File.Exists(MembersPath))
                members = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(MembersPath)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read farm-members.json: {ex.Message}");
        }
        foreach (Farmer f in Game1.netWorldState.Value.farmhandData.Values.Concat(Game1.getAllFarmers()))
        {
            string key = f.userID.Value ?? "";
            if (f.IsMainPlayer || !f.isCustomized.Value || key.Length == 0 || Farms.HomeFarmOf(f) is not string home)
                continue;
            string? listed = FarmOfKey(key);
            if (listed == home)
                continue;
            if (listed != null)
                members[listed].Remove(key);
            if (!members.TryGetValue(home, out var list))
                members[home] = list = new List<string>();
            list.Add(key);
        }
        SaveMembers();
    }

    private static void SaveMembers()
    {
        try
        {
            string tmp = MembersPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(members, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, MembersPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't save farm-members.json: {ex.Message}");
        }
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
            $"Open invite (gives a new player a farm of their own):",
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
    /// The farm a player without a character goes to: the farm they were invited to if it has room,
    /// otherwise the farm already held for them, otherwise a new farm of their own.
    /// </summary>
    public static string? PlaceNewPlayer(string key, string? invitedFarm)
    {
        string? current = FarmOfKey(key);
        if (invitedFarm != null && invitedFarm != current)
        {
            if (Members(invitedFarm).Count < Farms.PlayersPerFarm)
            {
                if (current != null)
                {
                    members[current].Remove(key); // they hadn't made a character there yet
                    Log.Info($"Player {Short(key)} moves from {Farms.DisplayName(current)} to {Farms.DisplayName(invitedFarm)} (invited).");
                }
                return Join(key, invitedFarm);
            }
            Log.Info($"Player {Short(key)} was invited to {Farms.DisplayName(invitedFarm)}, but it's full.");
        }
        if (current != null)
        {
            EnsureCabins(current);
            return current;
        }
        string? own = Farms.AllNames.FirstOrDefault(f => Members(f).Count == 0) ?? Farms.OpenFarm();
        return own == null ? null : Join(key, own);
    }

    private static string Join(string key, string farm)
    {
        if (!members.TryGetValue(farm, out var list))
            members[farm] = list = new List<string>();
        list.Add(key);
        SaveMembers();
        EnsureCabins(farm);
        Log.Info($"Player {Short(key)} joins {Farms.DisplayName(farm)} ({list.Count}/{Farms.PlayersPerFarm}).");
        return farm;
    }

    private static string Short(string key) => key.Length <= 14 ? key : key[..14];

    // ---------- cabins ----------

    /// <summary>One cabin per member (at least one), side by side, up to four.</summary>
    public static void EnsureCabins()
    {
        foreach (string name in Farms.AllNames)
            EnsureCabins(name);
        WriteStatus();
    }

    private static string lastStatus = "";

    /// <summary>farm-status.json for the Discord bot: each farm's members and whether they've made their farmer yet.</summary>
    private static void WriteStatus()
    {
        var started = Game1.netWorldState.Value.farmhandData.Values.Concat(Game1.getAllFarmers())
            .Where(f => !f.IsMainPlayer && f.isCustomized.Value).Select(f => f.userID.Value).ToHashSet();
        var status = Farms.AllNames.ToDictionary(f => f, f => new
        {
            name = Farms.DisplayName(f),
            members = Members(f).Select(k => new { key = k, started = started.Contains(k) }).ToList(),
        });
        string json = JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true });
        if (json == lastStatus)
            return;
        try
        {
            string path = Path.Combine(SV.StateDir, "farm-status.json");
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, overwrite: true);
            lastStatus = json;
        }
        catch (Exception ex)
        {
            Log.Debug($"Couldn't write farm-status.json: {ex.Message}");
        }
    }

    private static void EnsureCabins(string name)
    {
        if (Game1.getLocationFromName(name) is not Farm farm)
        {
            Log.Warn($"{name} is missing from the world.");
            return;
        }
        int want = Math.Clamp(Members(name).Count, 1, Farms.PlayersPerFarm);
        for (int have = farm.buildings.Count(b => b.isCabin); have < want; have++)
            if (!Server.BuildCabin(farm, have))
                break;
    }

    /// <summary>
    /// At startup (nobody online): remove spare empty cabins beyond one per member (one on an unclaimed farm).
    /// Only cabins whose farmer was never created are removed; anything a player made is never touched.
    /// </summary>
    public static void TrimSpareCabins()
    {
        foreach (string name in Farms.AllNames)
        {
            if (Game1.getLocationFromName(name) is not Farm farm)
                continue;
            int want = Math.Clamp(Members(name).Count, 1, Farms.PlayersPerFarm);
            var cabins = farm.buildings.Where(b => b.isCabin).ToList();
            var spare = cabins
                .Where(b => b.GetIndoors() is Cabin c && (!c.HasOwner || !c.owner.isCustomized.Value) && IsBare(c))
                .OrderByDescending(b => b.tileX.Value).ToList();
            int removed = 0;
            foreach (Building b in spare)
            {
                if (cabins.Count - removed <= want)
                    break;
                if (farm.destroyStructure(b))
                    removed++;
            }
            if (removed > 0)
            {
                Log.Info($"Removed {removed} empty cabin(s) on {Farms.DisplayName(name)} ({cabins.Count - removed} left).");
                farm.modData.Remove("SV.CabinsInRow"); // close the gaps: the rest slide to the start of the row
            }
        }
    }

    /// <summary>Nothing in it but the unopened starter gift box a new cabin comes with.</summary>
    private static bool IsBare(Cabin c) => c.objects.Values.All(o => o is StardewValley.Objects.Chest ch && ch.giftboxIsStarterGift.Value);
}
