using System;
using System.Collections.Generic;
using System.IO;
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
    public static bool TerrainOwned => phase is Phase.Preparing or Phase.Using or Phase.Mining or Phase.Excavating or Phase.JumpPlacing;
    public static bool ApproachingTerrain => phase == Phase.Approaching;
    public static bool OwnsExcavation => phase == Phase.Excavating && excavation != null;
    public static string Stage => refreshSource != null ? "Refreshing" : phase.ToString();
    enum Phase { Moving, Approaching, Braking, Preparing, Using, Mining, Excavating, JumpPlacing }
    static bool placementAirborne;
    static float placementStartY;
    static int[] placementControls = Array.Empty<int>();
    static uint placementInputAt;
    static bool continuingPlacement;
    static bool waitingPlacementTools;
    static VanillaExcavation excavation;
    static int excavationKeys, excavationSlot, excavationX, excavationY;
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
    static Edit? skippedEdit;
    static Vector2 skippedPosition;
    static Vector2 routeGoal, lastPosition;
    static int bestH = int.MaxValue, stable;
    static readonly VanillaContactRegion.Progress contactProgress = new();
    static Dictionary<(int, int), int> contactField;
    static uint progressAt, phaseAt, useAt;
    static bool useCached, needTerrain;
    static JsonObject previewedRoute, arrivalAnchor;
    static uint arrivalAt;
    static readonly JsonArray rejectedEdits = new();

    public static void Reset()
    {
        if (VanillaNavigationDemo.Instance.NavigationOwned) VanillaNavigationDemo.Instance.Stop();
        if (phase is Phase.Using or Phase.Mining) ItemUseCoordinator.Stop();
        if (excavation != null) MineCoordinator.Stop();
        excavation = null;
        phase = Phase.Moving; field = refreshSource = null; preparing = null; prepared = preparation = null;
        contactProgress.Reset(); contactField = null;
        edits.Clear(); batch.Clear(); corridor.Clear(); Terminal = false; RouteDirection = 0;
        bestH = int.MaxValue; stable = 0; needTerrain = false; useAt = uint.MaxValue;
        previewedRoute = arrivalAnchor = null;
        skippedEdit = null;
        continuingPlacement = false;
        waitingPlacementTools = false;
    }

    static void Fail(string reason)
    {
        if (reason == "jump_place_failed" && preparation != null)
            File.WriteAllText(Path.Combine(Main.SavePath, "vanilla-jump-placement-failure.json"),
                new JsonObject { ["request"] = preparation.DeepClone(), ["result"] = prepared?.DeepClone(),
                    ["actual"] = VanillaPrediction.Observe(Main.LocalPlayer) }.ToJsonString());
        if ((reason == "no_safe_terrain_action" || reason == "terrain_planning_timeout") && preparation != null)
            File.WriteAllText(Path.Combine(Main.SavePath, "vanilla-terrain-failure.json"),
                new JsonObject { ["request"] = preparation.DeepClone(),
                    ["attempts"] = rejectedEdits.DeepClone() }.ToJsonString());
        DiagLog.Write("[engine-nav-failed] " + reason);
        RecedingNav.LastStop = reason;
        RecedingNav.Stop();
    }

    public static bool Tick(Player player, int gx, int gy)
    {
        if (!Enabled) return false;
        try { Drive(player, gx, gy); }
        catch (Exception error) { Fail("engine_route:" + error.GetBaseException().Message); }
        return true;
    }

    static void Drive(Player player, int gx, int gy)
    {
        var demo = VanillaNavigationDemo.Instance;
        field = MazeWand.PeekFieldOrNull(gx, gy);
        if (field == null) return;
        if (phase != Phase.Moving || !ReferenceEquals(contactField, field)) contactProgress.Reset();
        contactField = field;
        if (refreshSource != null && !ReferenceEquals(refreshSource, field))
        { refreshSource = null; bestH = int.MaxValue; progressAt = Main.GameUpdateCount; }
        if (StateSpacePlanner.ExecRunning || MineCoordinator.IsActive && excavation == null || SkillExecutor.IsActive || BridgeBuilder.IsRunning)
        { Fail("legacy_movement_conflict"); return; }
        stable = Math.Abs(player.velocity.X) < .01f && player.velocity.Y == 0
            && Vector2.DistanceSquared(lastPosition, player.position) < .0001f ? stable + 1 : 0;
        lastPosition = player.position;
        if (phase == Phase.Excavating)
        {
            if (MineCoordinator.IsActive) { ApplyTerrainUse(player); return; }
            DiagLog.Write($"[engine-excavation-end] outcome={MineCoordinator.Outcome} reason={MineCoordinator.Reason} frames={Main.GameUpdateCount - phaseAt}");
            bool completed = MineCoordinator.Outcome == "done";
            MineCoordinator.Stop(); excavation = null;
            player.controlUseItem = player.controlLeft = player.controlRight = player.controlDown = false;
            var current = VanillaLocalPlanner.FootCell(player.position, player.width, player.height);
            if (completed && BuildCorridor(current.x, current.y, gx, gy) && StartExcavation(player)) return;
            demo.ReplanAfterTerrain(); phase = Phase.Moving;
            bestH = int.MaxValue; progressAt = Main.GameUpdateCount;
            return;
        }
        if (phase == Phase.Approaching)
        {
            if (stable >= 3 && (VanillaAsyncMovement.StateMatches(player, arrivalAnchor, true, out _, out _)
                || demo.ContactDescentLanded && VanillaContactRegion.ConnectedSupport(player.position,
                    new Vector2(arrivalAnchor["x"].GetValue<float>(), arrivalAnchor["y"].GetValue<float>()), player.width, player.height)))
            {
                demo.PauseForTerrain(); phase = Phase.Preparing; phaseAt = Main.GameUpdateCount;
                DiagLog.Write($"[engine-terrain-arrived] ready={preparing?.IsCompleted} lead_frames={Main.GameUpdateCount - progressAt}");
                if (StartExcavation(player)) return;
                Prepare(player);
            }
            else if (Main.GameUpdateCount > arrivalAt + 12) CancelPreview();
            return;
        }
        if (phase == Phase.Mining) { MinePassage(player, gx, gy); return; }
        if (phase == Phase.JumpPlacing)
        {
            placementAirborne |= player.velocity.Y != 0;
            if (placementAirborne && player.velocity.Y == 0 && VanillaContactRegion.Supported(player.position, player.width, player.height))
            {
                if (stable < 2) return;
                var tile = Main.tile[edit.X, edit.Y];
                bool success = tile.HasTile && tile.TileType == edit.Type && player.position.Y < placementStartY;
                if (!success)
                {
                    bool held = !player.releaseJump;
                    int next = (int)(Main.GameUpdateCount - placementInputAt);
                    for (int i = Math.Max(0, next); i < placementControls.Length; i++)
                    {
                        bool pressed = (placementControls[i] & 4) != 0;
                        if (pressed && !held) return;
                        held = pressed;
                    }
                }
                player.controlJump = player.controlUseItem = false;
                DiagLog.Write($"[engine-jump-place-end] tile=({edit.X},{edit.Y}) success={success} rise={placementStartY - player.position.Y} frames={Main.GameUpdateCount - phaseAt}");
                if (!success) { Fail("jump_place_failed"); return; }
                prepared = preparation = null; edits.Clear(); batch.Clear();
                if (ContinuePlacement(player)) return;
                phase = Phase.Moving; bestH = int.MaxValue; progressAt = Main.GameUpdateCount;
                demo.ReplanAfterTerrain();
            }
            else if (Main.GameUpdateCount - phaseAt > 180) Fail("jump_place_timeout");
            return;
        }
        if (phase == Phase.Preparing) { Prepare(player); return; }
        if (phase == Phase.Using) { FinishEdit(player); return; }
        var (cx, cy) = VanillaLocalPlanner.FootCell(player.position, player.width, player.height);
        if (phase == Phase.Moving && field.TryGetValue((cx, cy), out int contactCost)
            && contactProgress.ReturnedWithoutProgress(player, contactCost))
        {
            needTerrain = true;
            DiagLog.Write($"[engine-contact-cycle] cell=({cx},{cy}) cost={contactCost}; returned to same support without progress");
        }
        if (field.TryGetValue((cx, cy), out int h) && h < bestH)
        { bestH = h; progressAt = Main.GameUpdateCount; }
        if (phase == Phase.Braking)
        {
            if (stable >= 3 && player.itemAnimation == 0 && player.itemTime == 0)
            {
                demo.UpdateNavigationGoal(routeGoal);
                demo.PauseForTerrain();
                if (!CollectEdits(player, gx, gy)) return;
                if (StartExcavation(player)) return;
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
            if (StartExcavation(player)) return;
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
        rejectedEdits.Clear();
        var cellAtPlayer = VanillaLocalPlanner.FootCell(player.position, player.width, player.height);
        if (!BuildCorridor(cellAtPlayer.x, cellAtPlayer.y, gx, gy)) return false;
        if (corridor.Count > 0)
        {
            var end = corridor[^1];
            routeGoal = new Vector2(end.x * 16 + 8 - player.width / 2, (end.y + 1) * 16 - player.height);
            RouteDirection = corridor.Select(cell => Math.Sign(cell.x - cellAtPlayer.x)).FirstOrDefault(direction => direction != 0);
        }
        int pick = Array.FindIndex(player.inventory, 0, 10, item => !item.IsAir && item.pick > 0);
        var seen = new HashSet<(int, int)>();
        void AddMine(int x, int y, bool belowFeet)
        {
            if (pick < 0 || !Predicates.InBounds(x, y)) return;
            if (VanillaTerrainPlan.IsRouteObstacle(Main.tile[x, y], belowFeet) && seen.Add((x, y))
                && LavaSafety.CanMine(x, y) && WorldGen.CanKillTile(x, y) && Reach.CanMine(player, x, y)
                && DigTable.CostFrames(x, y) < DigTable.Unmineable)
                edits.Enqueue(new Edit(x, y, -1, pick));
        }
        if (routeGoal.Y < player.position.Y)
            for (int x = (int)Math.Floor(player.Left.X / 16); x <= (int)Math.Floor((player.Right.X - 1) / 16); x++)
                AddMine(x, (int)Math.Floor(player.Top.Y / 16) - 1, false);
        foreach (var cell in corridor)
        {
            float px = cell.x * 16 + 8 - player.width / 2;
            int left = (int)(px / 16), right = (int)((px + player.width - 1) / 16);
            for (int y = cell.y - 2; y <= cell.y + 1; y++)
            for (int x = left; x <= right; x++)
            {
                AddMine(x, y, y > cell.y);
            }
        }
        var mining = edits.OrderBy(candidate => IsClearing(candidate) ? 0 : 1).ToArray();
        edits.Clear();
        foreach (var candidate in mining) edits.Enqueue(candidate);
        int platform = NavCoordinator.FindPlatformSlot(player);
        if (platform < 0) { LogEmptyCorridor(player); return true; }
        int type = player.inventory[platform].createTile;
        var placements = new HashSet<(int, int)>();
        foreach (var cell in corridor)
        foreach (int offset in new[] { 0, -1, 1 })
        foreach (int vertical in new[] { 0, -1, 1 })
        {
            int x = cell.x + offset, y = cell.y + 1 + vertical;
            if (!placements.Add((x, y)) || !Predicates.InBounds(x, y) || Main.tile[x, y].HasTile || LavaSafety.IsLava(x, y)
                || !Reach.CanPlace(player, x, y) || !MazeWand.AnchorFor(x, y)
                || player.Hitbox.Intersects(new Rectangle(x * 16, y * 16, 16, 16))
                    && (type != Terraria.ID.TileID.Platforms || y != (int)(player.Bottom.Y / 16) - 1 || routeGoal.Y >= player.position.Y)) continue;
            edits.Enqueue(new Edit(x, y, type, platform));
        }
        var ordered = edits.OrderBy(candidate => IsClearing(candidate) ? 0
            : candidate.Type == Terraria.ID.TileID.Platforms
                && player.Hitbox.Intersects(new Rectangle(candidate.X * 16, candidate.Y * 16, 16, 16)) ? 1
            : candidate.Type < 0 ? 2 : 3).ToArray();
        edits.Clear();
        foreach (var candidate in ordered) edits.Enqueue(candidate);
        LogEmptyCorridor(player);
        return true;
    }

    static void LogEmptyCorridor(Player player)
    {
        if (edits.Count != 0 || phase != Phase.Braking) return;
        DiagLog.Write($"[engine-empty-corridor] player=({player.Center.X / 16:0.###},{player.Bottom.Y / 16:0.###}) route=({routeGoal.X / 16:0.###},{routeGoal.Y / 16:0.###}) nodes="
            + string.Join(";", corridor.Select(cell => $"{cell.x},{cell.y}:{(field.TryGetValue(cell, out int h) ? h : -1)}")));
        DiagLog.Write($"[engine-empty-platforms] slot={NavCoordinator.FindPlatformSlot(player)} candidates="
            + string.Join(";", corridor.SelectMany(cell => new[] { -1, 0, 1 }.Select(offset => (x: cell.x + offset, y: cell.y)))
                .Distinct().Where(cell => Predicates.InBounds(cell.x, cell.y + 1)).Select(cell =>
            {
                int x = cell.x, y = cell.y + 1;
                return $"{x},{y}:occupied={Main.tile[x, y].HasTile},lava={LavaSafety.IsLava(x, y)},reach={Reach.CanPlace(player, x, y)},anchor={MazeWand.AnchorFor(x, y)},body={player.Hitbox.Intersects(new Rectangle(x * 16, y * 16, 16, 16))}";
            })));
        if (Terraria.Program.LaunchParameters.ContainsKey("-vnavbaseline"))
        {
            var snapshot = VanillaFrame.Capture(player, VanillaLocalPlanner.SnapshotArea(player, routeGoal, 90), true);
            File.WriteAllText(Path.Combine(Main.SavePath, $"vanilla-empty-corridor-{Main.GameUpdateCount}.json"),
                new JsonObject { ["snapshot"] = snapshot, ["x"] = routeGoal.X, ["y"] = routeGoal.Y,
                    ["route_direction"] = RouteDirection, ["guidance"] = CaptureGuidance(snapshot) }.ToJsonString());
        }
    }

    static bool IsClearing(Edit candidate)
    {
        var tile = Main.tile[candidate.X, candidate.Y];
        return candidate.Type < 0 && (Main.tileCut[tile.TileType] || Terraria.ID.TileID.Sets.TouchDamageImmediate[tile.TileType] > 0)
            && !Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType];
    }

    public static void Prefetch(Player player, VanillaAsyncMovement movement, long tick)
    {
        if (phase == Phase.Approaching)
        {
            if (!movement.CurrentMatches(tick, player))
            {
                movement.DiscardRoute(player, tick);
                CancelPreview();
            }
            return;
        }
        if (phase != Phase.Moving || Terminal || field == null || player.itemAnimation != 0 || player.itemTime != 0
            || !needTerrain && movement.ActiveResult?["route_progress"]?.GetValue<double>() > 0
            || ReferenceEquals(previewedRoute, movement.ActiveResult)
            || !movement.CurrentMatches(tick, player)
            || !movement.TryArrival(tick, out var controls, out var anchor)) return;
        previewedRoute = movement.ActiveResult;
        var probe = new Player { position = new Vector2(anchor["x"].GetValue<float>(), anchor["y"].GetValue<float>()),
            width = player.width, height = player.height, inventory = player.inventory,
            selectedItem = player.selectedItem, blockRange = player.blockRange };
        var destination = VanillaLocalPlanner.FootCell(routeGoal, player.width, player.height);
        if (!CollectEdits(probe, destination.x, destination.y) || edits.Count == 0) return;
        if (edits.TryPeek(out var firstEdit) && firstEdit.Type == Terraria.ID.TileID.Platforms
            && probe.Hitbox.Intersects(new Rectangle(firstEdit.X * 16, firstEdit.Y * 16, 16, 16))) return;
        movement.CancelPending();
        arrivalAnchor = anchor;
        arrivalAt = Main.GameUpdateCount + (uint)controls.Length;
        phase = Phase.Approaching; phaseAt = progressAt = Main.GameUpdateCount;
        if (VanillaExcavation.Create(probe, corridor) != null)
        {
            preparing = null; preparation = null;
            DiagLog.Write($"[engine-excavation-prefetch] frames={controls.Length}; corridor checked without movement search");
        }
        else
        {
            BeginPreparation(probe, player, controls);
            DiagLog.Write($"[engine-terrain-prefetch] frames={controls.Length} tile=({edit.X},{edit.Y}) batch={batch.Count + 1}");
        }
    }

    static void CancelPreview()
    {
        VanillaNavigationDemo.Instance.CancelTerrain();
        preparing = null; preparation = prepared = arrivalAnchor = null;
        edits.Clear(); batch.Clear(); phase = Phase.Moving;
        DiagLog.Write("[engine-terrain-prefetch-cancel] arrival changed");
    }

    static void Prepare(Player player)
    {
        if (preparing?.IsCompleted != true && Main.GameUpdateCount - phaseAt > 900)
        { Fail("terrain_planning_timeout"); return; }
        if (waitingPlacementTools)
        {
            if (stable < 2 || !player.releaseJump || player.itemTime > 0 || player.itemAnimation > 0) return;
            waitingPlacementTools = false;
        }
        if (preparing != null)
        {
            if (!preparing.IsCompleted) return;
            var result = preparing.GetAwaiter().GetResult(); preparing = null;
            if (result["status"]?.GetValue<string>() != "planned")
                rejectedEdits.Add(new JsonObject { ["x"] = edit.X, ["y"] = edit.Y, ["type"] = edit.Type,
                    ["tiles"] = preparation["tiles"]?.DeepClone(), ["result"] = result.DeepClone() });
            DiagLog.Write($"[engine-terrain-result] ({edit.X},{edit.Y}) type={edit.Type} batch={batch.Count + 1} status={result["status"]} remaining={edits.Count} timing={result["terrain_timing"]?.ToJsonString()}");
            if (continuingPlacement && result["status"]?.GetValue<string>() != "planned")
            {
                continuingPlacement = false; prepared = preparation = null;
                phase = Phase.Moving; bestH = int.MaxValue; progressAt = Main.GameUpdateCount;
                VanillaNavigationDemo.Instance.ReplanAfterTerrain();
                return;
            }
            continuingPlacement = false;
            if (result["status"]?.GetValue<string>() == "planned")
            {
                if (result["jump_place"]?.GetValue<bool>() == true)
                {
                    bool matchesStart = VanillaAsyncMovement.StateMatches(player, result["anchor"].AsObject(), true,
                        out string startReason, out string startDifference);
                    if (matchesStart) matchesStart = VanillaFrame.ContextMatches(preparation["snapshot"].AsObject(), player,
                        out startDifference, result["validation_frames"].AsArray());
                    if (!matchesStart)
                    {
                        DiagLog.Write($"[engine-jump-place-retry] {startDifference ?? startReason}");
                        if (Terraria.Program.LaunchParameters.ContainsKey("-vnavbaseline"))
                            File.WriteAllText(Path.Combine(Main.SavePath, $"jump-place-rejected-{Main.GameUpdateCount}.json"),
                                new JsonObject { ["request"] = preparation.DeepClone(), ["result"] = result.DeepClone(),
                                    ["actual"] = VanillaPrediction.Observe(player), ["reason"] = startDifference ?? startReason }.ToJsonString());
                        prepared = preparation = null; edits.Clear(); batch.Clear();
                        phase = Phase.Moving; bestH = int.MaxValue; progressAt = Main.GameUpdateCount;
                        VanillaNavigationDemo.Instance.ReplanAfterTerrain();
                        return;
                    }
                    phase = Phase.JumpPlacing; phaseAt = Main.GameUpdateCount;
                    prepared = result;
                    placementAirborne = false; placementStartY = player.position.Y;
                    placementControls = result["controls"].AsArray().Select(key => key.GetValue<int>()).ToArray();
                    edit = new Edit(result["tile_x"].GetValue<int>(), result["tile_y"].GetValue<int>(), edit.Type, edit.Slot);
                    placementInputAt = uint.MaxValue;
                    DiagLog.Write($"[engine-jump-place] tile=({edit.X},{edit.Y}) predicted_place_after={result["place_after"]} rise={result["rise"]}");
                    return;
                }
                var checkedFrames = result["forecast"]["frames"].AsArray();
                if (preparation["arrival"] != null)
                {
                    checkedFrames = checkedFrames.DeepClone().AsArray();
                    checkedFrames.Add(result["anchor"].DeepClone());
                    foreach (var target in preparation["tiles"].AsArray())
                        checkedFrames.Add(new JsonObject { ["x"] = target[0].GetValue<int>() * 16f,
                            ["y"] = target[1].GetValue<int>() * 16f });
                }
                bool matches = VanillaFrame.ContextMatches(preparation["snapshot"].AsObject(), player, out var difference,
                    checkedFrames);
                if (matches && preparation["arrival"] != null)
                    matches = VanillaAsyncMovement.StateMatches(player, result["anchor"].AsObject(), true, out _, out difference);
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
                    if (result["terrain_required"]?.GetValue<bool>() == false)
                    {
                        skippedEdit = edit; skippedPosition = player.position;
                        var demo = VanillaNavigationDemo.Instance;
                        if (!demo.ResumeTerrain(result)) demo.ReplanAfterTerrain();
                        edits.Clear(); batch.Clear(); prepared = preparation = null;
                        phase = Phase.Moving; bestH = int.MaxValue; progressAt = Main.GameUpdateCount;
                        DiagLog.Write("[engine-terrain-skipped] unedited route advances " + result["route_progress"]);
                        return;
                    }
                    prepared = result;
                    if (edit.Type < 0 && StartExcavation(player)) return;
                    StartEdit();
                    phase = Phase.Using; phaseAt = Main.GameUpdateCount; useAt = uint.MaxValue;
                    DiagLog.Write($"[engine-terrain] {(edit.Type < 0 ? "mine" : "place")} ({edit.X},{edit.Y}) prepared tiles={batch.Count + 1}");
                    return;
                }
            }
        }
        if (edits.Count == 0) { Fail("no_safe_terrain_action"); return; }
        BeginPreparation(player, player, null);
    }

    static bool ContinuePlacement(Player player)
    {
        if (player.position.Y <= routeGoal.Y || player.velocity != Vector2.Zero
            || !VanillaContactRegion.SafeBody(player.position - new Vector2(0, 16), player.width, player.height)) return false;
        int slot = NavCoordinator.FindPlatformSlot(player);
        if (slot < 0 || player.inventory[slot].createTile != Terraria.ID.TileID.Platforms) return false;
        int x = edit.X, y = (int)(player.Bottom.Y / 16) - 1;
        if (!Predicates.InBounds(x, y) || Main.tile[x, y].HasTile || LavaSafety.IsLava(x, y)
            || !Reach.CanPlace(player, x, y) || !MazeWand.AnchorFor(x, y)
            || !player.Hitbox.Intersects(new Rectangle(x * 16, y * 16, 16, 16))) return false;
        edits.Enqueue(new Edit(x, y, Terraria.ID.TileID.Platforms, slot));
        phase = Phase.Preparing; phaseAt = Main.GameUpdateCount;
        continuingPlacement = waitingPlacementTools = true;
        DiagLog.Write($"[engine-placement-continue] next=({x},{y}) target_y={routeGoal.Y / 16:0.##}");
        return true;
    }

    static void BeginPreparation(Player player, Player capturePlayer, int[] arrival)
    {
        edit = edits.Dequeue();
        batch.Clear();
        var targets = new JsonArray(new JsonArray(edit.X, edit.Y));
        int supportY = (int)Math.Floor(player.Bottom.Y / 16);
        if (edit.Type < 0 && edit.Y <= supportY)
        {
            var remaining = edits.ToArray();
            var group = VanillaTerrainPlan.MiningBatch((edit.X, edit.Y),
                remaining.Where(candidate => candidate.Type < 0).Select(candidate => (candidate.X, candidate.Y)).ToArray(),
                player.Left.X, player.Right.X, supportY);
            var candidates = new[] { edit }.Concat(remaining).ToDictionary(candidate => (candidate.X, candidate.Y, candidate.Type));
            edit = candidates[(group[0].x, group[0].y, -1)];
            targets.Clear();
            foreach (var cell in group) targets.Add(new JsonArray(cell.x, cell.y));
            foreach (var cell in group.Skip(1)) batch.Enqueue(candidates[(cell.x, cell.y, -1)]);
            edits.Clear();
            foreach (var candidate in remaining)
                if (candidate.Type >= 0 || !group.Contains((candidate.X, candidate.Y))) edits.Enqueue(candidate);
        }
        var snapshot = VanillaFrame.Capture(capturePlayer, VanillaLocalPlanner.SnapshotArea(capturePlayer, routeGoal, 90), true);
        int wait = player.inventory[edit.Slot].useAnimation + 2;
        preparation = new JsonObject { ["command"] = "terrain_plan", ["snapshot"] = snapshot,
            ["tile_x"] = edit.X, ["tile_y"] = edit.Y, ["tile_type"] = edit.Type, ["wait_frames"] = wait,
            ["tiles"] = targets,
            ["x"] = routeGoal.X, ["y"] = routeGoal.Y, ["route_direction"] = RouteDirection,
            ["guidance"] = CaptureGuidance(snapshot) };
        if (arrival != null) preparation["arrival"] = new JsonArray(arrival.Select(key => JsonValue.Create(key)).ToArray());
        if (skippedEdit == edit && VanillaContactRegion.ConnectedSupport(skippedPosition, player.position, player.width, player.height))
            preparation["skip_unedited"] = true;
        if (arrival == null && edit.Type == Terraria.ID.TileID.Platforms
            && player.Hitbox.Intersects(new Rectangle(edit.X * 16, edit.Y * 16, 16, 16)))
        {
            preparation["jump_place"] = true;
            preparation["search_jump_place"] = true;
            DiagLog.Write($"[engine-jump-place-request] tile=({edit.X},{edit.Y}) continuing={continuingPlacement} release_jump={player.releaseJump} item_time={player.itemTime} item_animation={player.itemAnimation}");
        }
        preparing = VanillaNavigationDemo.Instance.PrepareTerrain(preparation);
        phaseAt = Main.GameUpdateCount;
    }

    static void StartEdit()
    {
        ItemUseCoordinator.Start(new ItemUseRequest { TargetWx = edit.X, TargetWy = edit.Y,
            Slot = edit.Slot, Strict = edit.Type < 0 });
        useAt = uint.MaxValue;
    }

    static bool StartExcavation(Player player)
    {
        var candidate = VanillaExcavation.Create(player, corridor);
        if (candidate == null) return false;
        var demo = VanillaNavigationDemo.Instance;
        demo.CancelTerrain(); demo.PauseForTerrain();
        ItemUseCoordinator.Stop(); preparing = null; prepared = preparation = null;
        edits.Clear(); batch.Clear(); arrivalAnchor = null;
        excavation = candidate;
        MineCoordinator.Start(candidate.Request);
        phase = Phase.Excavating; phaseAt = Main.GameUpdateCount; useAt = uint.MaxValue;
        DiagLog.Write($"[engine-excavation-start] dir={candidate.Request.Dir} start=({candidate.Request.StartWx},{candidate.Request.StartWy}) target=({candidate.Request.TargetWx},{candidate.Request.TargetWy}) tiles={candidate.Request.MineTiles.Count} cells="
            + string.Join(";", candidate.Request.MineTiles.Select(cell => $"{cell.wx},{cell.wy}")));
        ApplyTerrainUse(player);
        return true;
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
        if (phase == Phase.JumpPlacing)
        {
            if (placementInputAt == uint.MaxValue) placementInputAt = Main.GameUpdateCount;
            int frame = (int)(Main.GameUpdateCount - placementInputAt);
            int keys = frame < placementControls.Length ? placementControls[frame] : 0;
            player.controlLeft = (keys & 1) != 0; player.controlRight = (keys & 2) != 0;
            player.controlJump = (keys & 4) != 0;
            player.controlUp = (keys & 8) != 0; player.controlDown = (keys & 16) != 0;
            player.controlUseItem = false;
            player.selectedItem = edit.Slot;
            player.delayUseItem = player.mouseInterface = false;
            Main.HoveringOverAnNPC = Main.SmartInteractShowingGenuine = false;
            var tile = Main.tile[edit.X, edit.Y];
            if (!tile.HasTile && player.velocity.Y < 0 && !LavaSafety.IsLava(edit.X, edit.Y)
                && !player.Hitbox.Intersects(new Rectangle(edit.X * 16, edit.Y * 16, 16, 16))
                && Reach.CanPlace(player, edit.X, edit.Y) && MazeWand.AnchorFor(edit.X, edit.Y))
            {
                Cursor.AimTile(edit.X, edit.Y);
                player.controlUseItem = true;
            }
            return;
        }
        if (phase == Phase.Excavating)
        {
            player.delayUseItem = player.mouseInterface = false;
            Main.HoveringOverAnNPC = Main.SmartInteractShowingGenuine = false;
            if (useAt != Main.GameUpdateCount)
            {
                useAt = Main.GameUpdateCount;
                player.controlLeft = player.controlRight = player.controlDown = player.controlUseItem = false;
                if (excavation.Safe()) MineCoordinator.ApplyControls();
                else
                {
                    DiagLog.Write("[engine-excavation-abort] floor or hazard changed");
                    MineCoordinator.Stop();
                }
                excavationKeys = (player.controlLeft ? 1 : 0) | (player.controlRight ? 2 : 0)
                    | (player.controlDown ? 16 : 0);
                useCached = player.controlUseItem;
                excavationSlot = player.selectedItem;
                excavationX = (int)Math.Floor(Main.MouseWorld.X / 16);
                excavationY = (int)Math.Floor(Main.MouseWorld.Y / 16);
            }
            player.controlLeft = (excavationKeys & 1) != 0;
            player.controlRight = (excavationKeys & 2) != 0;
            player.controlDown = (excavationKeys & 16) != 0;
            player.controlUseItem = useCached;
            player.selectedItem = excavationSlot;
            Cursor.AimTile(excavationX, excavationY);
            return;
        }
        if (phase is not Phase.Using and not Phase.Mining) return;
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
            skippedEdit = null;
            if (batch.TryDequeue(out var next))
            {
                DiagLog.Write($"[engine-terrain] changed ({edit.X},{edit.Y}); next ({next.X},{next.Y}) without replanning");
                edit = next;
                StartEdit();
                phaseAt = Main.GameUpdateCount;
                return;
            }
            ItemUseCoordinator.Stop(); player.controlUseItem = false;
            if (prepared["next_jump_place"] is JsonArray nextPlacement)
            {
                int platform = NavCoordinator.FindPlatformSlot(player);
                if (platform < 0) { Fail("missing_platform_after_mining"); return; }
                edits.Clear(); batch.Clear();
                edits.Enqueue(new Edit(nextPlacement[0].GetValue<int>(), nextPlacement[1].GetValue<int>(),
                    player.inventory[platform].createTile, platform));
                prepared = preparation = null;
                phase = Phase.Preparing; phaseAt = Main.GameUpdateCount; waitingPlacementTools = true;
                DiagLog.Write($"[engine-mine-to-placement] next=({nextPlacement[0]},{nextPlacement[1]})");
                return;
            }
            bool resumed = VanillaNavigationDemo.Instance.ResumeTerrain(prepared);
            if (!resumed)
            {
                DiagLog.Write("[engine-terrain-replan] " + VanillaNavigationDemo.Instance.PlannerMismatch);
                VanillaNavigationDemo.Instance.ReplanAfterTerrain();
            }
            var end = prepared["forecast"]["frames"].AsArray()[^1];
            needTerrain = resumed && Vector2.Distance(player.position,
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
