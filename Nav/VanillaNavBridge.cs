using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public static class VanillaNavBridge
{
    public static bool Enabled;
    public static bool Terminal { get; private set; }
    public static int RouteDirection { get; private set; }
    public static bool TerrainOwned => phase is Phase.Preparing or Phase.Using or Phase.Mining;
    public static string Stage => refreshSource != null ? "Refreshing" : phase.ToString();
    enum Phase { Moving, Braking, Preparing, Using, Mining }
    readonly record struct Edit(int X, int Y, int Type, int Slot);
    static Phase phase;
    static Dictionary<(int, int), int> field;
    static Dictionary<(int, int), int> refreshSource;
    static readonly List<(int x, int y)> corridor = new();
    static readonly Queue<Edit> edits = new();
    static readonly Queue<Edit> batch = new();
    static Task<JsonObject> preparing;
    static JsonObject preparation, prepared;
    static Edit edit;
    static Vector2 routeGoal, lastPosition;
    static int bestH = int.MaxValue, stable;
    static uint progressAt, phaseAt, useAt;
    static bool useCached, needTerrain;

    public static void Reset()
    {
        if (VanillaNavigationDemo.Instance.NavigationOwned) VanillaNavigationDemo.Instance.Stop();
        if (phase is Phase.Using or Phase.Mining) ItemUseCoordinator.Stop();
        phase = Phase.Moving; field = refreshSource = null; preparing = null; prepared = preparation = null;
        edits.Clear(); batch.Clear(); corridor.Clear(); Terminal = false; RouteDirection = 0;
        bestH = int.MaxValue; stable = 0; needTerrain = false; useAt = uint.MaxValue;
    }

    static void Fail(string reason)
    {
        DiagLog.Write("[engine-nav-failed] " + reason);
        RecedingNav.LastStop = reason;
        RecedingNav.Stop();
    }

    public static bool Tick(Player player, int gx, int gy)
    {
        if (!Enabled) return false;
        if (!AgentTools.ControlAllows(ControlBehavior.Navigation, ControlChannels.All, delegated: true))
        { Fail("agent_navigation_forbidden"); return true; }
        try { Drive(player, gx, gy); }
        catch (Exception error) { Fail("engine_route:" + error.GetBaseException().Message); }
        return true;
    }

    static void Drive(Player player, int gx, int gy)
    {
        var demo = VanillaNavigationDemo.Instance;
        field = MazeWand.PeekFieldOrNull(gx, gy);
        if (field == null) return;
        if (refreshSource != null && !ReferenceEquals(refreshSource, field))
        { refreshSource = null; bestH = int.MaxValue; progressAt = Main.GameUpdateCount; }
        if (StateSpacePlanner.ExecRunning || MineCoordinator.IsActive || SkillExecutor.IsActive || BridgeBuilder.IsRunning)
        { Fail("legacy_movement_conflict"); return; }
        stable = Math.Abs(player.velocity.X) < .01f && player.velocity.Y == 0
            && Vector2.DistanceSquared(lastPosition, player.position) < .0001f ? stable + 1 : 0;
        lastPosition = player.position;
        if (phase == Phase.Mining) { MinePassage(player, gx, gy); return; }
        if (phase == Phase.Preparing) { Prepare(player); return; }
        if (phase == Phase.Using) { FinishEdit(player); return; }
        var (cx, cy) = VanillaLocalPlanner.FootCell(player.position, player.width, player.height);
        if (field.TryGetValue((cx, cy), out int h) && h < bestH)
        { bestH = h; progressAt = Main.GameUpdateCount; }
        if (phase == Phase.Braking)
        {
            if (stable >= 3 && player.itemAnimation == 0 && player.itemTime == 0)
            {
                demo.UpdateNavigationGoal(routeGoal);
                demo.PauseForTerrain();
                if (!CollectEdits(player, gx, gy)) return;
                phase = Phase.Preparing; phaseAt = Main.GameUpdateCount;
                Prepare(player);
            }
            else if (Main.GameUpdateCount - phaseAt > 300) Fail("cannot_settle_for_terrain");
            return;
        }
        var destination = new Vector2(gx * 16 + 8 - player.width / 2, (gy + 1) * 16 - player.height);
        Terminal = Math.Abs(cx - gx) + Math.Abs(cy - gy) <= 12
            && !Collision.SolidCollision(destination, player.width, player.height);
        if (!BuildCorridor(cx, cy, gx, gy)) return;
        Vector2? target = Terminal ? destination : null;
        foreach (var cell in corridor)
        {
            var position = new Vector2(cell.x * 16 + 8 - player.width / 2, (cell.y + 1) * 16 - player.height);
            if (!Terminal && Vector2.Distance(player.position, position) >= 40
                && !Collision.SolidCollision(position, player.width, player.height)) target = position;
        }
        if (!target.HasValue)
            target = corridor.Count > 0
                ? new Vector2(corridor[^1].x * 16 + 8 - player.width / 2, (corridor[^1].y + 1) * 16 - player.height)
                : player.position;
        routeGoal = target.Value;
        RouteDirection = Math.Sign(routeGoal.X - player.position.X);
        if (!demo.NavigationOwned)
        {
            demo.StartNavigation(routeGoal); progressAt = Main.GameUpdateCount;
            DiagLog.Write($"[engine-route] start ({cx},{cy}) -> ({gx},{gy})");
        }
        else demo.UpdateNavigationGoal(routeGoal);
        if (!demo.WorkerReady) { progressAt = Main.GameUpdateCount; return; }
        bool movementBlocked = demo.PlanAccepted > 0 && demo.PlannerResult?["route_progress"]?.GetValue<double>() <= 0;
        if (player.velocity.Y == 0 && stable >= 3 && movementBlocked)
        {
            if (!CollectEdits(player, gx, gy)) return;
            if (edits.TryPeek(out var first) && first.Type < 0 && first.Y < (int)Math.Floor(player.Bottom.Y / 16))
            {
                edit = first; phase = Phase.Mining; phaseAt = Main.GameUpdateCount;
                StartEdit(); demo.TerrainChanged(edit.X, edit.Y);
                DiagLog.Write($"[engine-mine] start ({edit.X},{edit.Y}) after native progress={demo.PlannerResult?["route_progress"]} selected={demo.LastControl.Plan}");
                return;
            }
        }
        if (demo.PlanAccepted == 0 && demo.PlanRejected == 0) progressAt = Main.GameUpdateCount;
        bool blocked = demo.PlannerRejection == "no_safe_plan" && !demo.HasQueuedMovement;
        if (player.velocity.Y == 0 && player.itemAnimation == 0 && player.itemTime == 0 && (needTerrain || blocked || movementBlocked && stable >= 3
            || Main.GameUpdateCount - progressAt > 180 && !demo.HasQueuedMovement))
        {
            phase = Phase.Braking; phaseAt = Main.GameUpdateCount; needTerrain = false;
            Terminal = true; RouteDirection = 0; demo.UpdateNavigationGoal(player.position);
            demo.PauseForTerrain();
            DiagLog.Write($"[engine-terrain-brake] vx={player.velocity.X:0.###} vy={player.velocity.Y:0.###}; retired movement queue");
        }
    }

    static bool BuildCorridor(int cx, int cy, int gx, int gy)
    {
        corridor.Clear();
        var cell = (x: cx, y: cy);
        var visited = new HashSet<(int, int)> { cell };
        for (int i = 0; i < 24 && cell != (gx, gy); i++)
        {
            var direction = MazeWand.FieldDir(field, cell.x, cell.y);
            if (direction == (0, 0)) return RefreshCorridor(cx, cy, "missing direction");
            cell = (cell.x + direction.dx, cell.y + direction.dy);
            if (!visited.Add(cell)) return RefreshCorridor(cx, cy, "cycle");
            corridor.Add(cell);
        }
        return true;
    }

    static bool RefreshCorridor(int cx, int cy, string reason)
    {
        if (!ReferenceEquals(refreshSource, field))
        {
            refreshSource = field;
            DiagLog.Write($"[engine-corridor-refresh] cell=({cx},{cy}) reason={reason}");
            RecedingNav.RebuildFieldAsync("engine corridor " + reason);
        }
        corridor.Clear();
        return false;
    }

    static bool CollectEdits(Player player, int gx, int gy)
    {
        edits.Clear();
        var cellAtPlayer = VanillaLocalPlanner.FootCell(player.position, player.width, player.height);
        if (!BuildCorridor(cellAtPlayer.x, cellAtPlayer.y, gx, gy)) return false;
        int pick = Array.FindIndex(player.inventory, 0, 10, item => !item.IsAir && item.pick > 0);
        var seen = new HashSet<(int, int)>();
        foreach (var cell in corridor)
        {
            float px = cell.x * 16 + 8 - player.width / 2;
            int left = (int)(px / 16), right = (int)((px + player.width - 1) / 16);
            for (int y = cell.y - 2; y <= cell.y; y++)
            for (int x = left; x <= right; x++)
            {
                if (pick < 0 || !seen.Add((x, y)) || !Predicates.InBounds(x, y)) continue;
                var tile = Main.tile[x, y];
                if (tile.HasTile && !tile.IsActuated && (Main.tileSolid[tile.TileType] || Main.tileCut[tile.TileType])
                    && LavaSafety.CanMine(x, y) && WorldGen.CanKillTile(x, y) && Reach.CanMine(player, x, y)
                    && DigTable.CostFrames(x, y) < DigTable.Unmineable)
                    edits.Enqueue(new Edit(x, y, -1, pick));
            }
        }
        int platform = NavCoordinator.FindPlatformSlot(player);
        if (platform < 0) { LogEmptyCorridor(player); return true; }
        int type = player.inventory[platform].createTile;
        foreach (var cell in corridor)
        {
            int x = cell.x, y = cell.y + 1;
            if (!Predicates.InBounds(x, y) || Main.tile[x, y].HasTile || LavaSafety.IsLava(x, y)
                || !Reach.CanPlace(player, x, y) || !MazeWand.AnchorFor(x, y)
                || player.Hitbox.Intersects(new Rectangle(x * 16, y * 16, 16, 16))) continue;
            edits.Enqueue(new Edit(x, y, type, platform));
        }
        LogEmptyCorridor(player);
        return true;
    }

    static void LogEmptyCorridor(Player player)
    {
        if (edits.Count != 0 || phase != Phase.Braking) return;
        DiagLog.Write($"[engine-empty-corridor] player=({player.Center.X / 16:0.###},{player.Bottom.Y / 16:0.###}) route=({routeGoal.X / 16:0.###},{routeGoal.Y / 16:0.###}) nodes="
            + string.Join(";", corridor.Select(cell => $"{cell.x},{cell.y}:{(field.TryGetValue(cell, out int h) ? h : -1)}")));
    }

    static void Prepare(Player player)
    {
        if (Main.GameUpdateCount - phaseAt > 900) { Fail("terrain_planning_timeout"); return; }
        if (preparing != null)
        {
            if (!preparing.IsCompleted) return;
            var result = preparing.GetAwaiter().GetResult(); preparing = null;
            if (result["status"]?.GetValue<string>() == "planned")
            {
                bool matches = VanillaFrame.ContextMatches(preparation["snapshot"].AsObject(), player, out var difference,
                    result["forecast"]["frames"].AsArray());
                if (stable < 3 || !matches)
                {
                    if (stable < 3) { Fail("terrain_start_changed:moving"); return; }
                    DiagLog.Write("[engine-terrain-retry] " + difference);
                    var retry = new[] { edit }.Concat(batch).Concat(edits).ToArray();
                    edits.Clear(); batch.Clear();
                    foreach (var target in retry) edits.Enqueue(target);
                }
                else
                {
                    prepared = result;
                    StartEdit();
                    phase = Phase.Using; phaseAt = Main.GameUpdateCount; useAt = uint.MaxValue;
                    DiagLog.Write($"[engine-terrain] {(edit.Type < 0 ? "mine" : "place")} ({edit.X},{edit.Y}) prepared tiles={batch.Count + 1}");
                    return;
                }
            }
        }
        if (edits.Count == 0) { Fail("no_safe_terrain_action"); return; }
        edit = edits.Dequeue();
        batch.Clear();
        var targets = new JsonArray(new JsonArray(edit.X, edit.Y));
        int supportY = (int)Math.Floor(player.Bottom.Y / 16);
        if (edit.Type < 0 && edit.Y < supportY)
        {
            while (edits.TryPeek(out var next) && next.Type < 0 && next.Y < supportY)
            {
                batch.Enqueue(edits.Dequeue());
                targets.Add(new JsonArray(next.X, next.Y));
            }
        }
        else if (edit.Type < 0 && edit.Y == supportY)
        {
            while (edits.TryPeek(out var next) && next.Type < 0 && next.Y == supportY
                && next.X * 16 < player.Right.X && (next.X + 1) * 16 > player.Left.X)
            {
                batch.Enqueue(edits.Dequeue());
                targets.Add(new JsonArray(next.X, next.Y));
            }
        }
        var snapshot = VanillaFrame.Capture(player, VanillaLocalPlanner.SnapshotArea(player, routeGoal, 90), true);
        int wait = player.inventory[edit.Slot].useAnimation + 2;
        preparation = new JsonObject { ["command"] = "terrain_plan", ["snapshot"] = snapshot,
            ["tile_x"] = edit.X, ["tile_y"] = edit.Y, ["tile_type"] = edit.Type, ["wait_frames"] = wait,
            ["tiles"] = targets,
            ["x"] = routeGoal.X, ["y"] = routeGoal.Y, ["route_direction"] = Math.Sign(routeGoal.X - player.position.X),
            ["guidance"] = CaptureGuidance(snapshot) };
        preparing = VanillaNavigationDemo.Instance.PrepareTerrain(preparation);
    }

    static void StartEdit()
    {
        if (AgentTools.Control.CheckAt(edit.Type < 0 ? ControlBehavior.Mining : ControlBehavior.Placement, edit.X, edit.Y) != null)
        { Fail("agent_region_forbidden"); return; }
        ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = edit.X, TargetWy = edit.Y,
            Slot = edit.Slot, Strict = edit.Type < 0 });
        useAt = uint.MaxValue;
    }

    static void MinePassage(Player player, int gx, int gy)
    {
        if (player.velocity.Y != 0 || !Reach.CanMine(player, edit.X, edit.Y)
            || edit.Y >= (int)Math.Floor(player.Bottom.Y / 16))
        {
            DiagLog.Write($"[engine-mine] release ({edit.X},{edit.Y}) vy={player.velocity.Y:0.###} reachable={Reach.CanMine(player, edit.X, edit.Y)}");
            EndMining(player);
            return;
        }
        if (Main.tile[edit.X, edit.Y].HasTile)
        {
            if (!ItemUseCoordinator.IsActive || Main.GameUpdateCount - phaseAt > 600)
                Fail("passage_mining:" + ItemUseCoordinator.Outcome + ":" + ItemUseCoordinator.Reason);
            return;
        }
        var demo = VanillaNavigationDemo.Instance;
        demo.TerrainChanged(edit.X, edit.Y);
        DiagLog.Write($"[engine-mine] removed ({edit.X},{edit.Y}) frames={Main.GameUpdateCount - phaseAt} vx={player.velocity.X:0.##}");
        if (!CollectEdits(player, gx, gy)) { EndMining(player); return; }
        if (edits.TryPeek(out var next) && next.Type < 0 && next.Y < (int)Math.Floor(player.Bottom.Y / 16))
        {
            edit = next; StartEdit(); phaseAt = Main.GameUpdateCount;
            return;
        }
        EndMining(player);
    }

    static void EndMining(Player player)
    {
        ItemUseCoordinator.Stop(); player.controlUseItem = false;
        phase = Phase.Moving; bestH = int.MaxValue; progressAt = Main.GameUpdateCount;
    }

    public static void ApplyTerrainUse(Player player)
    {
        if (phase is not Phase.Using and not Phase.Mining) return;
        if (AgentTools.Control.CheckAt(edit.Type < 0 ? ControlBehavior.Mining : ControlBehavior.Placement, edit.X, edit.Y) != null)
        { player.controlUseItem = false; Fail("agent_region_forbidden"); return; }
        player.delayUseItem = player.mouseInterface = false;
        Main.HoveringOverAnNPC = Main.SmartInteractShowingGenuine = false;
        if (edit.Type < 0 && !LavaSafety.CanMine(edit.X, edit.Y))
        { Fail("lava_risk"); return; }
        if (useAt != Main.GameUpdateCount)
        {
            useAt = Main.GameUpdateCount;
            ItemUseCoordinator.ApplyControls();
            useCached = player.controlUseItem;
        }
        else
        {
            player.selectedItem = edit.Slot;
            Cursor.AimTile(edit.X, edit.Y);
            player.controlUseItem = useCached;
        }
    }

    static void FinishEdit(Player player)
    {
        var tile = Main.tile[edit.X, edit.Y];
        bool changed = edit.Type < 0 ? !tile.HasTile : tile.HasTile && tile.TileType == edit.Type;
        if (changed)
        {
            if (batch.TryDequeue(out var next))
            {
                DiagLog.Write($"[engine-terrain] changed ({edit.X},{edit.Y}); next ({next.X},{next.Y}) without replanning");
                edit = next;
                StartEdit();
                phaseAt = Main.GameUpdateCount;
                return;
            }
            ItemUseCoordinator.Stop(); player.controlUseItem = false;
            if (!VanillaNavigationDemo.Instance.ResumeTerrain(prepared))
            { Fail("terrain_handoff_changed:" + VanillaNavigationDemo.Instance.PlannerMismatch); return; }
            var end = prepared["forecast"]["frames"].AsArray()[^1];
            needTerrain = Vector2.Distance(player.position,
                new Vector2(end["x"].GetValue<float>(), end["y"].GetValue<float>())) < 16;
            prepared = preparation = null; phase = Phase.Moving; bestH = int.MaxValue;
            progressAt = Main.GameUpdateCount;
            DiagLog.Write($"[engine-terrain] changed ({edit.X},{edit.Y}); native controls installed");
            return;
        }
        if (!ItemUseCoordinator.IsActive || Main.GameUpdateCount - phaseAt > 600)
            Fail("terrain_action:" + ItemUseCoordinator.Outcome + ":" + ItemUseCoordinator.Reason);
    }

    public static string CaptureGuidance(JsonObject snapshot)
    {
        if (field == null) return null;
        var result = new List<int>();
        int x0 = snapshot["area"][0].GetValue<int>(), y0 = snapshot["area"][1].GetValue<int>();
        int width = snapshot["area"][2].GetValue<int>(), height = snapshot["area"][3].GetValue<int>();
        for (int x = x0; x < x0 + width; x++)
        for (int y = y0; y < y0 + height; y++)
            if (field.TryGetValue((x, y), out int cost)) { result.Add(x); result.Add(y); result.Add(cost); }
        return VanillaFrame.PackInts(result.ToArray());
    }

    public static void TerrainAction()
    {
        if (Enabled) throw new InvalidOperationException("legacy_terrain_dispatch_in_engine_route");
    }
}
