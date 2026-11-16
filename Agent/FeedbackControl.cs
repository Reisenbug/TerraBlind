using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using Terraria;

namespace TerraBlind;

public static class FeedbackControl
{
    static string context, request;
    static bool left, right, jump;
    static long started, updated;
    static int duration, appliedFrames, stalledFrames;
    static string blockedReason;
    static uint appliedTick;
    public static bool IsApplying => context != null && appliedFrames > 0 && appliedTick == Main.GameUpdateCount;

    public static void Clear()
    {
        context = request = null;
        blockedReason = null;
        appliedFrames = stalledFrames = 0;
    }

    public static JsonObject Set(JsonObject args)
    {
        if (args["active"]?.GetValue<bool>() == false) { Clear(); return Snapshot(); }
        string nextContext = args["context"]?.GetValue<string>();
        string nextRequest = args["request_id"]?.ToString();
        int milliseconds = args["expires_ms"]?.GetValue<int>() ?? 1000;
        var keys = args["keys"]?.AsArray().Select(k => k.GetValue<string>()).ToArray();
        if (string.IsNullOrWhiteSpace(nextContext) || string.IsNullOrWhiteSpace(nextRequest)
            || milliseconds is < 1 or > 2000 || keys == null || keys.Length == 0
            || keys.Any(k => k is not ("left" or "right" or "jump"))
            || keys.Contains("left") && keys.Contains("right"))
            return new JsonObject { ["error"] = "invalid_movement_intent" };
        bool same = context == nextContext && request == nextRequest;
        context = nextContext; request = nextRequest;
        left = keys.Contains("left"); right = keys.Contains("right"); jump = keys.Contains("jump");
        updated = Stopwatch.GetTimestamp(); duration = milliseconds;
        if (!same) { started = updated; appliedFrames = stalledFrames = 0; }
        return Snapshot();
    }

    public static void Apply(Player player)
    {
        if (context == null) return;
        if (player.dead || Stopwatch.GetElapsedTime(updated).TotalMilliseconds >= duration)
        { Clear(); return; }
        blockedReason = Blocked(player);
        if (blockedReason != null) return;
        player.controlLeft = left; player.controlRight = right;
        player.controlJump = jump && Stopwatch.GetElapsedTime(started).TotalMilliseconds < 250;
        appliedTick = Main.GameUpdateCount; appliedFrames++;
        stalledFrames = (left || right) && Math.Abs(player.velocity.X) < .1f ? stalledFrames + 1 : 0;
    }

    static string Blocked(Player player)
    {
        if (AgentTools.Control.HasRegion(ControlBehavior.Dodge)) return "regional_controller_required";
        if (Manual.On) return "manual_control";
        if (!AgentNetwork.CanAutomate) return "automation_disabled";
        if (PvpDuel.Active || PlayerCombat.Active) return "other_combat_controller";
        if (AgentTools.ContactEscapeActive || FightCoordinator.HoldingFallProtection(player)) return "survival_control";
        if (!AgentPermissions.CanDodge(delegated: true)) return "movement_permission";
        if (AgentTools.ControlSession.OwnsDirectMovement || AgentTools.ControlSession.Tool is not (null or "fight"))
            return "primary_action:" + AgentTools.ControlSession.Tool;
        if (Main.npc.Any(n => n is { active: true, boss: true }
            && Microsoft.Xna.Framework.Vector2.Distance(n.Center, player.Center) < 3200)) return "boss_control";
        return null;
    }

    public static JsonObject Snapshot() => new()
    {
        ["active"] = context != null, ["context"] = context, ["request_id"] = request,
        ["applied_frames"] = appliedFrames, ["stalled_frames"] = stalledFrames,
        ["blocked_reason"] = blockedReason,
        ["remaining_ms"] = context == null ? 0 : Math.Max(0, duration - Stopwatch.GetElapsedTime(updated).TotalMilliseconds)
    };
}
