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
    private static readonly Dictionary<string, string> PlacementByKey = new();

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
        bool inviteOk = !Farms.Enabled ? true : FarmRoster.TryReadInvite(password, out password, out invitedFarm);
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
        Log.Info($"Approved connection from {who} (player {Short(key)}{(invitedFarm != null ? ", invited to " + Farms.DisplayName(invitedFarm) : "")}).");
        return true;
    }

    /// <summary>Use the player key as the farmhand owner ID, so a farmhand can only be used by the player who created it.</summary>
    private static void CheckFarmhand_Prefix(ref string userId, string connectionId, NetFarmerRoot farmer)
    {
        if (!KeyByConnection.TryGetValue(connectionId, out string? key))
            return;
        userId = key;
        BeginPlacement(key, connectionId);
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
    /// 4-player farms: a returning player only sees their own farmer; a new player only sees the empty
    /// cabins on the one farm chosen for them (their invite's farm, or any farm with room).
    /// </summary>
    private static void BeginPlacement(string key, string connectionId)
    {
        placingKey = placingFarm = null;
        if (!Farms.Enabled)
            return;
        placingKey = key;
        bool hasCharacter = Game1.netWorldState.Value.farmhandData.Values.Any(f => f.isCustomized.Value && f.userID.Value == key);
        if (hasCharacter)
            return;
        // Keep the same choice between "show the list" and "I picked this cabin", unless that farm filled up meanwhile.
        if (!PlacementByKey.TryGetValue(key, out string? farm) || FarmRoster.FreeSlots(farm) == 0)
        {
            InvitedFarmByConnection.TryGetValue(connectionId, out string? invited);
            farm = FarmRoster.PlaceNewPlayer(invited);
            if (farm == null)
            {
                Log.Warn($"Every farm is full; player {Short(key)} can't join.");
                return;
            }
            PlacementByKey[key] = farm;
            Log.Info($"New player {Short(key)} goes to {Farms.DisplayName(farm)}" + (invited != null && invited != farm ? $" ({Farms.DisplayName(invited)} was full)." : "."));
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
