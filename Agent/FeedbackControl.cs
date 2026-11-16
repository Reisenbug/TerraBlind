using System;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using Terraria;
using Jint;

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

public static class FrameProgram
{
    static Engine engine, previousEngine;
    static JsonObject data = new(), memory = new();
    static string error, blocked;
    static long frames, idleFrames;
    static double maxMilliseconds;
    public static bool Active => engine != null;

    public static void Clear() { engine = previousEngine = null; data = new(); memory = new(); }

    public static JsonObject Set(JsonObject args)
    {
        if (args["active"]?.GetValue<bool>() == false) { Clear(); return Snapshot(); }
        if (args["code"] is JsonValue code)
        {
            var candidate = new Engine(options => options.Strict().MaxStatements(10000).LimitMemory(8_000_000));
            candidate.Execute(code.GetValue<string>() + "\nfunction __frame(encoded){return JSON.stringify(tick(JSON.parse(encoded)));}");
            if (candidate.Evaluate("typeof tick").AsString() != "function")
                throw new ArgumentException("frame_program requires function tick(state)");
            previousEngine = engine; engine = candidate; error = blocked = null; frames = idleFrames = 0; maxMilliseconds = 0;
        }
        if (args["data"] is JsonObject input) data = input.DeepClone().AsObject();
        return Snapshot();
    }

    public static bool Apply(Player p)
    {
        if (!Active) return false;
        if (p.dead || Manual.On || !AgentNetwork.CanAutomate || AgentTools.ControlSession.PrimaryActive
            || !AgentTools.ControlAllows(ControlBehavior.Direct, ControlChannels.All)
            || AgentTools.Control.HasRegion(ControlBehavior.Navigation) || AgentTools.Control.HasRegion(ControlBehavior.Dodge))
        { blocked = "other_input_owner"; return false; }
        var clock = Stopwatch.StartNew();
        try
        {
            blocked = null;
            if (NavigationSurvival.Apply(p)) { frames++; return true; }
            int cx = (int)(p.Center.X / 16), cy = (int)((p.position.Y + p.height - 1) / 16);
            var tiles = new JsonArray();
            for (int x = cx - 5; x <= cx + 5; x++) for (int y = cy - 5; y <= cy + 4; y++)
            {
                if (!WorldGen.InWorld(x, y)) continue;
                var tile = Main.tile[x, y];
                if (tile.HasTile || tile.LiquidAmount > 0) tiles.Add(new JsonObject {
                    ["x"] = x, ["y"] = y, ["type"] = tile.HasTile ? (int)tile.TileType : -1,
                    ["solid"] = tile.HasUnactuatedTile && Main.tileSolid[tile.TileType],
                    ["platform"] = tile.HasTile && Main.tileSolidTop[tile.TileType],
                    ["liquid"] = tile.LiquidAmount, ["liquid_type"] = tile.LiquidType,
                    ["mineable"] = tile.HasTile && DigTable.CostFrames(x, y) < DigTable.Unmineable });
            }
            var state = new JsonObject { ["tick"] = Main.GameUpdateCount, ["data"] = data.DeepClone(), ["memory"] = memory.DeepClone(),
                ["player"] = new JsonObject { ["x"] = p.position.X, ["y"] = p.position.Y, ["width"] = p.width,
                    ["height"] = p.height, ["vx"] = p.velocity.X, ["vy"] = p.velocity.Y, ["hp"] = p.statLife,
                    ["item_animation"] = p.itemAnimation, ["selected_slot"] = p.selectedItem }, ["tiles"] = tiles };
            var output = JsonNode.Parse(engine.Invoke("__frame", state.ToJsonString()).AsString()).AsObject();
            if (output["done"]?.GetValue<bool>() == true) { Clear(); return false; }
            var keys = output["keys"]?.AsArray().Select(k => k.GetValue<string>()).ToArray() ?? Array.Empty<string>();
            if (keys.Any(k => k is not ("left" or "right" or "jump" or "down" or "up")))
                throw new ArgumentException("unsupported frame key");
            p.controlLeft = keys.Contains("left"); p.controlRight = keys.Contains("right");
            p.controlJump = keys.Contains("jump"); p.controlDown = keys.Contains("down"); p.controlUp = keys.Contains("up");
            if (output["slot"] is JsonValue slot && p.itemAnimation == 0 && p.itemTime == 0)
            {
                int index = slot.GetValue<int>();
                if (index < 0 || index >= 50) throw new ArgumentException("slot needs 0..49");
                p.selectedItem = index;
            }
            p.controlUseItem = output["use"]?.GetValue<bool>() == true;
            if (output["aim"] is JsonObject aim)
            {
                int x = aim["x"].GetValue<int>(), y = aim["y"].GetValue<int>();
                Cursor.AimTile(x, y);
                var behavior = p.HeldItem.pick > 0 ? ControlBehavior.Mining
                    : p.HeldItem.createTile >= 0 ? ControlBehavior.Placement : ControlBehavior.Attack;
                if (!AgentTools.ControlAllows(behavior, ControlChannels.ItemUse)
                    || AgentTools.Control.ForbiddenAt(behavior, x, y)) p.controlUseItem = false;
            }
            if (output["memory"] is JsonObject saved) memory = saved.DeepClone().AsObject();
            frames++; if (keys.Length == 0 && !p.controlUseItem) idleFrames++;
            maxMilliseconds = Math.Max(maxMilliseconds, clock.Elapsed.TotalMilliseconds);
            return true;
        }
        catch (Exception failure)
        {
            error = failure.Message; engine = previousEngine; previousEngine = null;
            DiagLog.Write("[frame_program_failed] " + error);
            return Active && Apply(p);
        }
    }

    public static JsonObject Snapshot() => new() { ["active"] = Active, ["frames"] = frames, ["idle_output_frames"] = idleFrames,
        ["max_execution_ms"] = maxMilliseconds, ["error"] = error, ["blocked"] = blocked, ["memory"] = memory.DeepClone() };
}
