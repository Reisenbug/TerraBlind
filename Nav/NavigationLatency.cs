using System.Diagnostics;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind;

internal static class NavigationLatency
{
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct Timespec { public long Seconds, Nanoseconds; }

    [System.Runtime.InteropServices.DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "clock_gettime")]
    static extern int ClockGetTime(int clock, out Timespec time);

    internal static double ThreadCpuMilliseconds()
    {
        if (!System.OperatingSystem.IsMacOS() || ClockGetTime(16, out var time) != 0) return double.NaN;
        return time.Seconds * 1000d + time.Nanoseconds / 1_000_000d;
    }

    static long requestedAt;
    static int requestId;
    static bool waiting;
    static double fieldMs, fieldQueueMs, searchMs;

    internal static int Begin(long timestamp, int x, int y)
    {
        requestedAt = timestamp;
        waiting = true;
        fieldMs = fieldQueueMs = searchMs = 0;
        DiagLog.Write($"[nav-latency-request] id={++requestId} goal=({x},{y})");
        return requestId;
    }

    internal static void Field(int id, double queue, double compute)
    {
        if (id != requestId) return;
        fieldQueueMs = queue;
        fieldMs = compute;
        DiagLog.Write($"[nav-latency-field] id={id} queue_ms={queue:0.###} compute_ms={compute:0.###}");
    }

    internal static void Search(double elapsed, string kind)
    {
        if (!waiting) return;
        searchMs += elapsed;
        DiagLog.Write($"[nav-latency-search] id={requestId} kind={kind} compute_ms={elapsed:0.###}");
    }

    internal static void Observe(Player player)
    {
        if (!waiting || !RecedingNav.Active) return;
        bool horizontal = (player.controlLeft || player.controlRight) && player.velocity.X != 0;
        bool vertical = (player.controlJump || player.controlDown) && player.velocity.Y != 0;
        if (!horizontal && !vertical) return;
        waiting = false;
        DiagLog.Write($"[nav-latency-first-input] id={requestId} total_ms={Stopwatch.GetElapsedTime(requestedAt).TotalMilliseconds:0.###} field_queue_ms={fieldQueueMs:0.###} field_compute_ms={fieldMs:0.###} search_ms={searchMs:0.###} field_ready={RecedingNav.FieldReady} keys={(player.controlLeft ? 1 : 0) | (player.controlRight ? 2 : 0) | (player.controlJump ? 4 : 0) | (player.controlDown ? 16 : 0)} velocity=({player.velocity.X},{player.velocity.Y})");
    }
}

public sealed class NavigationLatencyPlayer : ModPlayer
{
    public override void PostUpdate()
    {
        if (Player == Main.LocalPlayer) NavigationLatency.Observe(Player);
    }
}
