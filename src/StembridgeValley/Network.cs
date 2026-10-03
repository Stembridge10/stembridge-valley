using HarmonyLib;
using Lidgren.Network;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Network;

namespace StembridgeValley;

/// <summary>
/// Join-by-address plumbing on top of the game's own LAN networking:
/// the client sends a small "hail" (password, player key, pack version) with its connection request,
/// and the server turns away wrong passwords and out-of-date packs before they reach the farmhand menu.
/// Each player key owns one farmhand, so nobody can pick someone else's character.
/// </summary>
internal static class Network
{
    private static readonly Dictionary<string, string> KeyByConnection = new();
    private static readonly Dictionary<string, string?> InvitedFarmByConnection = new();
    /// <summary>Farm a connection is being placed on, while the game asks "is this farmhand available?".</summary>
    [ThreadStatic] private static string? placingKey;
    [ThreadStatic] private static string? placingFarm;

    public static void Apply(Harmony harmony)
    {
        if (SV.Role == Role.Client)
        {
            harmony.Patch(AccessTools.Method(typeof(LidgrenClient), "receiveHandshake"),
                prefix: new HarmonyMethod(typeof(Network), nameof(ClientHandshake_Prefix)));
            harmony.Patch(AccessTools.Method(typeof(LidgrenClient), "clientRemotelyDisconnected"),
                prefix: new HarmonyMethod(typeof(Network), nameof(ClientDisconnected_Prefix)));
            // The player key becomes the farmhand's owner ID (vanilla LAN play leaves it blank).
            harmony.Patch(AccessTools.Method(typeof(LidgrenClient), nameof(LidgrenClient.getUserID)),
                postfix: new HarmonyMethod(typeof(Network), nameof(ClientUserId_Postfix)));
        }
        else if (SV.Role == Role.Server)
        {
            harmony.Patch(AccessTools.Method(typeof(NetConnection), nameof(NetConnection.Approve), Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(Network), nameof(Approve_Prefix)));
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.checkFarmhandRequest)),
                prefix: new HarmonyMethod(typeof(Network), nameof(CheckFarmhand_Prefix)),
                finalizer: new HarmonyMethod(typeof(Network), nameof(ClearPlacement)));
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.sendAvailableFarmhands)),
                prefix: new HarmonyMethod(typeof(Network), nameof(SendAvailable_Prefix)),
                finalizer: new HarmonyMethod(typeof(Network), nameof(ClearPlacement)));
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.IsFarmhandAvailable)),
                postfix: new HarmonyMethod(typeof(Network), nameof(IsFarmhandAvailable_Postfix)));
        }
    }

    // ---------- client ----------

    private static bool ClientHandshake_Prefix(LidgrenClient __instance, NetIncomingMessage msg)
    {
        NetOutgoingMessage hail = __instance.client.CreateMessage();
        hail.Write(SV.HailTag);
        hail.Write(SV.Password);
        hail.Write(SV.PlayerKey);
        hail.Write(SV.PackVersion);
        __instance.client.Connect(msg.SenderEndPoint.Address.ToString(), msg.SenderEndPoint.Port, hail);
        return false;
    }

    private static void ClientDisconnected_Prefix(string message)
    {
        if (message == SV.DenyUpdate)
        {
            Log.Warn("Server says the mod pack is out of date. Closing so the launcher can update.");
            SV.WriteFlag("update-needed.txt", "The server has a newer mod pack.");
            Exit();
        }
        else if (message == SV.DenyBadPassword)
        {
            Log.Error("Server rejected the password.");
            SV.WriteFlag("bad-password.txt", "The server password was wrong.");
            Exit();
        }
        else if (message.StartsWith(Discord.DenyMods, StringComparison.Ordinal))
        {
            string list = message[Discord.DenyMods.Length..];
            Log.Error($"Server turned us away for extra mods: {list}");
            SV.WriteFlag("extra-mods.txt", list);
            Exit();
        }
    }

    private static void ClientUserId_Postfix(ref string __result)
    {
        if (!string.IsNullOrEmpty(SV.PlayerKey))
            __result = SV.PlayerKey;
    }

    private static void Exit()
    {
        try { Game1.game1.Exit(); } catch { Environment.Exit(0); }
    }

    // ---------- server ----------

    private static bool Approve_Prefix(NetConnection __instance)
    {
        string tag = "", password = "", key = "", version = "";
        try
        {
            NetIncomingMessage? hail = __instance.RemoteHailMessage;
            if (hail != null)
            {
                hail.Position = 0;
                tag = hail.ReadString();
                password = hail.ReadString();
                key = hail.ReadString();
                version = hail.ReadString();
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"Unreadable hail: {ex.Message}");
        }

        string who = __instance.RemoteEndPoint?.ToString() ?? "?";
        string? invitedFarm = null;
        string discordName = "";
        bool inviteOk;
        if (password.StartsWith(Discord.CodePrefix, StringComparison.Ordinal))
        {
            // Personal Discord code: the character belongs to the Discord account, whatever PC they're on.
            inviteOk = Discord.TryReadCode(password, out string discordKey, out discordName, out invitedFarm);
            if (inviteOk)
            {
                key = discordKey;
                password = SV.Password;
            }
            else
                Log.Warn($"Turned away {who}: Discord code not recognised, or the player was banned or left the Discord.");
        }
        else if (Discord.Required)
        {
            inviteOk = false;
            Log.Warn($"Turned away {who}: old-style invite; a Discord code is needed now.");
        }
        else
            inviteOk = !Farms.Enabled || FarmRoster.TryReadInvite(password, out password, out invitedFarm);
        if (tag != SV.HailTag || !inviteOk || !string.Equals(password, SV.Password, StringComparison.Ordinal) || key.Length < 8)
        {
            Log.Warn($"Turned away {who}: wrong password or not using the Stembridge Valley launcher.");
            __instance.Deny(SV.DenyBadPassword);
            return false;
        }
        if (version != SV.PackVersion)
        {
            Log.Info($"Turned away {who}: pack {version}, server has {SV.PackVersion}. Their launcher will update.");
            __instance.Deny(SV.DenyUpdate);
            return false;
        }

        KeyByConnection["L_" + __instance.RemoteUniqueIdentifier] = key;
        InvitedFarmByConnection["L_" + __instance.RemoteUniqueIdentifier] = invitedFarm;
        Log.Info($"Approved connection from {who} (player {Short(key)}{(discordName != "" ? ", Discord " + discordName : "")}{(invitedFarm != null ? ", placed with " + Farms.DisplayName(invitedFarm) : "")}).");
        return true;
    }

    /// <summary>Use the player key as the farmhand owner ID, so a farmhand can only be used by the player who created it.</summary>
    private static bool CheckFarmhand_Prefix(ref string userId, string connectionId, NetFarmerRoot farmer)
    {
        if (!KeyByConnection.TryGetValue(connectionId, out string? key))
            return true;
        userId = key;
        // Mod check: same mods as the server, nothing extra. (SMAPI sends the mod list just before this.)
        // (Skip it if that farmer is already online: the game itself turns that request down.)
        if (farmer.Value != null && !Game1.otherFarmers.ContainsKey(farmer.Value.UniqueMultiplayerID)
            && Discord.ExtraMods(farmer.Value.UniqueMultiplayerID) is { Count: > 0 } extra)
        {
            string list = string.Join(", ", extra);
            Log.Warn($"Turned away player {Short(key)}: extra mods {list}.");
            Discord.Refuse(connectionId, Discord.DenyMods + list);
            return false;
        }
        BeginPlacement(key, connectionId);
        return true;
    }

    private static void SendAvailable_Prefix(ref string userId, string connectionId)
    {
        // Someone is about to see the cabin list: make sure there's a free one right now, not in a few seconds.
        try { Server.EnsureFreeCabin(); } catch (Exception ex) { Log.Debug($"Cabin check failed: {ex.Message}"); }
        if (KeyByConnection.TryGetValue(connectionId, out string? key))
        {
            userId = key;
            BeginPlacement(key, connectionId);
        }
    }

    /// <summary>
    /// A returning player only sees their own farmer; a new player only sees the empty cabins on the farm chosen
    /// for them (a farm they were invited to, or their own).
    /// </summary>
    private static void BeginPlacement(string key, string connectionId)
    {
        placingKey = placingFarm = null;
        if (!Farms.Enabled)
            return;
        placingKey = key;
        bool hasCharacter = Game1.netWorldState.Value.farmhandData.Values.Concat(Game1.getAllFarmers())
            .Any(f => !f.IsMainPlayer && f.isCustomized.Value && f.userID.Value == key);
        if (hasCharacter)
            return;
        InvitedFarmByConnection.TryGetValue(connectionId, out string? invited);
        string? farm = FarmRoster.PlaceNewPlayer(key, invited);
        if (farm == null)
        {
            Log.Warn($"No farm available; player {Short(key)} can't join.");
            return;
        }
        placingFarm = farm;
    }

    private static Exception? ClearPlacement(Exception? __exception)
    {
        placingKey = placingFarm = null;
        return __exception;
    }

    private static void IsFarmhandAvailable_Postfix(Farmer farmhand, ref bool __result)
    {
        if (!__result || placingKey == null)
            return;
        // Judge by the server's own copy: the one a joining player sends is already filled in by them.
        if (Game1.netWorldState.Value.farmhandData.TryGetValue(farmhand.UniqueMultiplayerID, out Farmer stored) && stored != null)
            farmhand = stored;
        if (farmhand.isCustomized.Value)
            __result = farmhand.userID.Value == placingKey; // only your own character
        else
            __result = placingFarm != null && Farms.HomeFarmOf(farmhand) == placingFarm; // blanks on your farm only
    }

    private static string Short(string key) => key.Length <= 6 ? key : key[..6];
}
