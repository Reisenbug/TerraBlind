using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using Terraria;

namespace TerraBlind;

public static class FeedbackControl
{
    static string context, request;
    static bool left, right, jump, allowNavigation, navigationInterrupted;
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
        allowNavigation = args["allow_navigation"]?.GetValue<bool>() == true;
        updated = Stopwatch.GetTimestamp(); duration = milliseconds;
        if (!same) { started = updated; appliedFrames = stalledFrames = 0; }
        return Snapshot();
    }

    public static bool ApplyNavigation(Player player)
    {
        bool active = context != null && !player.dead && allowNavigation
            && Stopwatch.GetElapsedTime(updated).TotalMilliseconds < duration
            && AgentTools.ControlSession.Tool == "nav_to" && RecedingNav.Active;
        if (active) { blockedReason = Blocked(player); active = blockedReason == null; }
        if (!active)
        {
            if (navigationInterrupted)
            {
                StuckSentinel.Reset();
                navigationInterrupted = false;
                DiagLog.Write("[feedback-navigation] resume_from_current_position");
            }
            return false;
        }
        if (!navigationInterrupted)
        {
            RecedingNav.InterruptForSurvival();
            VanillaNavBridge.Reset();
            navigationInterrupted = true;
            DiagLog.Write("[feedback-navigation] motor_enter goal_retained=true");
        }
        Apply(player);
        return IsApplying;
    }

    public static void Apply(Player player)
    {
        if (context == null) return;
        if (player.dead || Stopwatch.GetElapsedTime(updated).TotalMilliseconds >= duration)
        { Clear(); return; }
        blockedReason = Blocked(player);
        if (blockedReason != null) return;
        if (allowNavigation && AgentTools.ControlSession.Tool == "nav_to" && !navigationInterrupted)
        { blockedReason = "navigation_handoff_pending"; return; }
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
        bool navigation = allowNavigation && AgentTools.ControlSession.Tool == "nav_to" && RecedingNav.Active;
        if (navigation && (StateSpacePlanner.ReservesItemUse || VanillaNavBridge.TerrainOwned
            || ItemUseCoordinator.IsActive || MineCoordinator.IsActive)) return "navigation_terrain_action";
        if (navigation && !navigationInterrupted)
        {
            var walk = StateSpacePlanner.PeekWalkIntent();
            var controls = StateSpacePlanner.PeekMovementControls(1, traceUnavailable: false);
            VanillaKeys? next = controls.Length > 0 ? (VanillaKeys)controls[0]
                : walk.HasValue ? walk.Value.Direction > 0 ? VanillaKeys.Right : VanillaKeys.Left : null;
            if (next.HasValue && next.Value.HasFlag(VanillaKeys.Left) == left
                && next.Value.HasFlag(VanillaKeys.Right) == right && (!jump || next.Value.HasFlag(VanillaKeys.Jump)))
                return "following_navigation";
        }
        if (AgentTools.ControlSession.OwnsDirectMovement || !navigation && AgentTools.ControlSession.Tool is not (null or "fight"))
            return "primary_action:" + AgentTools.ControlSession.Tool;
        if (Main.npc.Any(n => n is { active: true, boss: true }
            && Microsoft.Xna.Framework.Vector2.Distance(n.Center, player.Center) < 3200)) return "boss_control";
        return null;
    }

    public static JsonObject Snapshot() => new()
    {
        ["active"] = context != null, ["context"] = context, ["request_id"] = request,
        ["applied_frames"] = appliedFrames, ["stalled_frames"] = stalledFrames,
        ["navigation_interrupted"] = navigationInterrupted,
        ["blocked_reason"] = blockedReason,
        ["remaining_ms"] = context == null ? 0 : Math.Max(0, duration - Stopwatch.GetElapsedTime(updated).TotalMilliseconds)
    };
}
