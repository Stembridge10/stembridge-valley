using StardewModdingAPI;

namespace StembridgeValley;

/// <summary>World rules. The server's copy decides world rules (clock, crops); each player's copy decides personal rules (energy, naps).</summary>
public sealed class ModConfig
{
    /// <summary>Real minutes for one in-game day (6am to 2am). Vanilla is about 14.</summary>
    public int RealMinutesPerDay { get; set; } = 60;

    /// <summary>Keep the clock running while nobody is online.</summary>
    public bool ClockRunsWhenEmpty { get; set; } = true;

    /// <summary>Energy carries over to the next day instead of resetting each morning.</summary>
    public bool KeepEnergyOvernight { get; set; } = true;

    /// <summary>Using a bed is a nap: refills energy, the day does not end.</summary>
    public bool BedIsNap { get; set; } = true;

    /// <summary>Energy regained per real minute while sitting.</summary>
    public float SittingEnergyPerMinute { get; set; } = 60f;

    /// <summary>Energy regained per real minute while not using tools (slow trickle).</summary>
    public float IdleEnergyPerMinute { get; set; } = 6f;

    /// <summary>Coming back after this many real minutes away refills energy.</summary>
    public int RestedAfterMinutesAway { get; set; } = 20;

    /// <summary>The 2am pass-out costs no money and sends no letter.</summary>
    public bool NoPassOutPenalty { get; set; } = true;

    /// <summary>Crops already planted keep growing when the season changes instead of dying.</summary>
    public bool CropsSurviveSeasonChange { get; set; } = true;

    /// <summary>Friendship does not drop on days you did not talk to someone.</summary>
    public bool NoFriendshipDecay { get; set; } = true;

    public ServerConfig Server { get; set; } = new();
}

public sealed class ServerConfig
{
    public string FarmName { get; set; } = "Stembridge Valley";
    public int StartingCabins { get; set; } = 3;
    /// <summary>Most players on the farm at once, counting the hidden server's own farmer. Vanilla is 8.</summary>
    public int MaxPlayers { get; set; } = 8;
    /// <summary>0 = Standard farm.</summary>
    public int FarmType { get; set; } = 0;
    /// <summary>Ask the router to open the game port automatically (UPnP). Harmless if the router says no.</summary>
    public bool TryAutomaticPortForward { get; set; } = true;
}

internal static class Log
{
    public static IMonitor Monitor = null!;
    public static void Info(string m) => Monitor.Log(m, LogLevel.Info);
    public static void Debug(string m) => Monitor.Log(m, LogLevel.Debug);
    public static void Warn(string m) => Monitor.Log(m, LogLevel.Warn);
    public static void Error(string m) => Monitor.Log(m, LogLevel.Error);
}
