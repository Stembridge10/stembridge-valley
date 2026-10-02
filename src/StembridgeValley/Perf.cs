using System.Diagnostics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace StembridgeValley;

/// <summary>
/// Server health sampler: how long each game tick takes, how many ticks per second the server manages,
/// memory, network traffic, and how long saves/new days take. Writes state/perf.csv every 10 seconds.
/// A tick has 16.7 ms to finish; if ticks regularly take longer, everyone sees lag.
/// </summary>
internal static class Perf
{
    private static readonly Stopwatch tick = new(), window = Stopwatch.StartNew(), save = new();
    private static readonly List<double> tickMs = new(1000);
    private static int ticks;
    private static long lastSent, lastRecv;
    private static double lastSaveMs, lastNewDayMs;
    private static DateTime dayEndingAt;

    public static void Apply(IModHelper helper)
    {
        helper.Events.GameLoop.UpdateTicking += (_, _) => tick.Restart();
        helper.Events.GameLoop.UpdateTicked += OnTicked;
        helper.Events.GameLoop.DayEnding += (_, _) => dayEndingAt = DateTime.UtcNow;
        helper.Events.GameLoop.Saving += (_, _) => save.Restart();
        helper.Events.GameLoop.Saved += (_, _) => { lastSaveMs = save.Elapsed.TotalMilliseconds; };
        helper.Events.GameLoop.DayStarted += (_, _) =>
        {
            if (dayEndingAt != default)
                lastNewDayMs = (DateTime.UtcNow - dayEndingAt).TotalMilliseconds;
        };
        SV.WriteFlag("perf.csv", "time,players,tps,tick_avg_ms,tick_p95_ms,tick_max_ms,slow_ticks,mem_mb,kb_out_s,kb_in_s,save_ms,newday_ms,clock\n");
    }

    private static void OnTicked(object? sender, UpdateTickedEventArgs e)
    {
        tickMs.Add(tick.Elapsed.TotalMilliseconds);
        ticks++;
        if (window.Elapsed.TotalSeconds < 10)
            return;

        double secs = window.Elapsed.TotalSeconds;
        window.Restart();
        tickMs.Sort();
        double avg = tickMs.Count > 0 ? tickMs.Average() : 0;
        double p95 = tickMs.Count > 0 ? tickMs[(int)(tickMs.Count * 0.95)] : 0;
        double max = tickMs.Count > 0 ? tickMs[^1] : 0;
        int slow = tickMs.Count(t => t > 16.7);
        tickMs.Clear();

        long sent = 0, recv = 0;
        try
        {
            var lidgren = (Game1.server as StardewValley.Network.GameServer)?.GetServer<StardewValley.Network.LidgrenServer>()?.server;
            if (lidgren != null)
            {
                sent = lidgren.Statistics.SentBytes;
                recv = lidgren.Statistics.ReceivedBytes;
            }
        }
        catch { }
        double kbOut = Math.Max(0, sent - lastSent) / 1024.0 / secs, kbIn = Math.Max(0, recv - lastRecv) / 1024.0 / secs;
        lastSent = sent;
        lastRecv = recv;

        int players = Context.IsWorldReady ? Game1.getOnlineFarmers().Count - 1 : 0;
        string clock = Context.IsWorldReady ? $"{Game1.season} {Game1.dayOfMonth} {Game1.timeOfDay}" : "-";
        long mem = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        string line = $"{DateTime.Now:HH:mm:ss},{players},{ticks / secs:0.0},{avg:0.00},{p95:0.00},{max:0.0},{slow},{mem},{kbOut:0.0},{kbIn:0.0},{lastSaveMs:0},{lastNewDayMs:0},{clock}\n";
        ticks = 0;
        lastSaveMs = 0;
        lastNewDayMs = 0;
        try { File.AppendAllText(Path.Combine(SV.StateDir, "perf.csv"), line); } catch { }
    }
}
