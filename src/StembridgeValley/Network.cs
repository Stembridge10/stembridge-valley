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
                prefix: new HarmonyMethod(typeof(Network), nameof(CheckFarmhand_Prefix)));
            harmony.Patch(AccessTools.Method(typeof(GameServer), nameof(GameServer.sendAvailableFarmhands)),
                prefix: new HarmonyMethod(typeof(Network), nameof(SendAvailable_Prefix)));
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
        if (tag != SV.HailTag || !string.Equals(password, SV.Password, StringComparison.Ordinal) || key.Length < 8)
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
        Log.Info($"Approved connection from {who} (player {Short(key)}).");
        return true;
    }

    /// <summary>Use the player key as the farmhand owner ID, so a farmhand can only be used by the player who created it.</summary>
    private static void CheckFarmhand_Prefix(ref string userId, string connectionId, NetFarmerRoot farmer)
    {
        if (!KeyByConnection.TryGetValue(connectionId, out string? key))
            return;
        userId = key;
    }

    private static void SendAvailable_Prefix(ref string userId, string connectionId)
    {
        if (KeyByConnection.TryGetValue(connectionId, out string? key))
            userId = key;
    }

    private static string Short(string key) => key.Length <= 6 ? key : key[..6];
}
