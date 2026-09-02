using Terraria;

namespace TerraBlind
{
    // FRAME-DRIVEN build replayer — the whole record/replay orchestration lives here in the mod, not in Python.
    // Python is a trigger: /build_replay_start kicks this off, /build_replay_status polls, /build_replay_stop aborts.
    // Ticked every frame from PreUpdate (before RecedingNav.Tick, so a nav started this frame is driven immediately).
    //
    // Per recorded event it runs a small state machine that STARTS the existing async executors and advances when
    // they report done — the same shape as the descent itinerary, but resident in the game loop:
    //   NAV  → RecedingNav.Start(a standable cell near the target); wait for Active to clear.
    //   ACT  → ItemUseCoordinator.Start (place: non-strict, swings at the exact cell; mine: strict, watches removal);
    //          wait for IsActive to clear, read Outcome.
    //   NEXT → advance the event index; conflicting place cells and unreachable/failed steps are SKIPPED, not fatal.
    // Uses BuildOverlay for the loaded events + anchor + conflict set (already draws the faint preview on Load).
    public static class BuildReplayer
    {
        private enum St { Idle, Nav, Act, Done }
        private static St _st = St.Idle;
        private static int _i;                 // current event index
        private static int _navTry;            // which standable-near candidate we're navving to
        private static int _standCx, _standCy; // the standable cell chosen for the current event's target

        public static int Placed, Mined, Skipped;
        public static string FailReason = "";
        public static bool Running => _st == St.Nav || _st == St.Act;
        public static int Total => BuildOverlay.Events.Count;
        public static int Index => _i;

        // standable cells to try near a placement/mine target, in order (you can't stand where the tile goes).
        private static readonly (int dx, int dy)[] StandOffsets =
            { (0, 1), (-1, 0), (1, 0), (0, 2), (-1, 1), (1, 1) };

        // start: BuildOverlay.Load already parsed the file, rebased at the anchor, and drew the faint overlay +
        // conflicts. returns false (with a reason) if there's nothing to replay.
        public static bool Start(int anchorX, int anchorY, out string reason)
        {
            reason = "";
            if (!BuildOverlay.Load(anchorX, anchorY)) { reason = "no_build_rec"; return false; }
            BuildOverlay.PreviewAndConflicts();   // draw overlay + populate the conflict set
            _i = 0; _navTry = 0; Placed = Mined = Skipped = 0; FailReason = "";
            _st = St.Nav;
            BeginEvent();
            return true;
        }

        public static void Stop()
        {
            if (Running) { RecedingNav.Stop(); ItemUseCoordinator.Stop(); }
            _st = St.Idle;
        }

        public static void Tick()
        {
            switch (_st)
            {
                case St.Idle:
                case St.Done:
                    return;

                case St.Nav:
                    if (RecedingNav.Active) return;                 // still walking
                    if (RecedingNav.LastStop == "done")
                    { _st = St.Act; StartAct(); return; }
                    // this standable cell was unreachable → try the next candidate; exhausted → skip the event.
                    _navTry++;
                    if (_navTry < StandOffsets.Length && SetStand(_navTry))
                    { RecedingNav.Start(_standCx, _standCy); return; }
                    DiagLog.Write($"[build-replay] event {_i} target unreachable — skip");
                    Skipped++; Advance();
                    return;

                case St.Act:
                    if (ItemUseCoordinator.IsActive) return;        // still swinging
                    var ev = BuildOverlay.Events[_i];
                    string oc = ItemUseCoordinator.Outcome;
                    if (ev.Act == "mine")
                    {
                        if (oc == "removed") Mined++;
                        else { DiagLog.Write($"[build-replay] mine {_i} ended {oc} — skip"); Skipped++; }
                    }
                    else
                    {
                        // placement completes on the swing budget (Outcome n/a); verify a tile actually appeared.
                        int wx = BuildOverlay.AnchorX + ev.Rcx, wy = BuildOverlay.AnchorY + ev.Rcy;
                        var t = Main.tile[wx, wy];
                        if (t.HasTile || t.WallType > 0) Placed++;
                        else { DiagLog.Write($"[build-replay] place {_i} left cell empty ({oc}) — skip"); Skipped++; }
                    }
                    Advance();
                    return;
            }
        }

        // move to the next event; place cells that already clash are counted skipped without navving.
        private static void Advance()
        {
            _i++;
            _navTry = 0;
            BeginEvent();
        }

        // set up the current event: DONE if past the end; skip a place onto a known conflict; else start nav.
        private static void BeginEvent()
        {
            var evs = BuildOverlay.Events;
            while (_i < evs.Count)
            {
                var ev = evs[_i];
                int wx = BuildOverlay.AnchorX + ev.Rcx, wy = BuildOverlay.AnchorY + ev.Rcy;
                if (ev.Act == "place" && BuildOverlay.IsConflict(wx, wy)) { Skipped++; _i++; continue; }
                if (!SetStand(0)) { DiagLog.Write($"[build-replay] event {_i} no standable cell — skip"); Skipped++; _i++; continue; }
                _navTry = 0;
                _st = St.Nav;
                RecedingNav.Start(_standCx, _standCy);
                return;
            }
            _st = St.Done;
            DiagLog.Write($"[build-replay] done placed={Placed} mined={Mined} skipped={Skipped}");
            Chatter.Say($"[TerraBlind] 建造回放完成：放{Placed} 挖{Mined} 跳过{Skipped}");
        }

        // choose the k-th standable cell near the current event's target (returns false if it's solid/out of bounds).
        private static bool SetStand(int k)
        {
            if (k >= StandOffsets.Length) return false;
            var ev = BuildOverlay.Events[_i];
            int wx = BuildOverlay.AnchorX + ev.Rcx, wy = BuildOverlay.AnchorY + ev.Rcy;
            var (dx, dy) = StandOffsets[k];
            int sx = wx + dx, sy = wy + dy;
            if (sx < 0 || sy < 0 || sx >= Main.maxTilesX || sy >= Main.maxTilesY) return false;
            _standCx = sx; _standCy = sy;
            return true;
        }

        private static void StartAct()
        {
            _navTry = 0;
            var ev = BuildOverlay.Events[_i];
            int wx = BuildOverlay.AnchorX + ev.Rcx, wy = BuildOverlay.AnchorY + ev.Rcy;
            if (ev.Act == "mine")
            {
                int slot = BestPickSlot();
                ItemUseCoordinator.Start(new ItemUseRequest
                { TargetWx = wx, TargetWy = wy, Slot = slot, DurationTicks = 0, Strict = true });
            }
            else
            {
                // find the recorded item by ID wherever it sits now (diff). ItemUseCoordinator swaps a backpack slot
                // into the hotbar itself. non-strict: TrySnap leaves a build item's target untouched, so it swings at
                // the exact cell; completes when the tile actually appears (or after its swing grace), not on a budget.
                int slot = FindItemSlot(ev.Type);
                if (slot < 0)
                {
                    DiagLog.Write($"[build-replay] place {_i} item id={ev.Type} not in inventory — skip");
                    Chatter.Say($"[TerraBlind] 背包没有 id={ev.Type}，跳过");
                    Skipped++; Advance(); return;   // Advance→BeginEvent re-sets the state
                }
                ItemUseCoordinator.Start(new ItemUseRequest
                { TargetWx = wx, TargetWy = wy, Slot = slot, DurationTicks = 0, Strict = false });
            }
        }

        private static int FindItemSlot(int type)
        {
            var p = Main.LocalPlayer;
            if (p == null) return -1;
            for (int i = 0; i < 58; i++)
            {
                var it = p.inventory[i];
                if (it != null && !it.IsAir && it.type == type) return i;
            }
            return -1;
        }

        private static int BestPickSlot()
        {
            var p = Main.LocalPlayer;
            if (p == null) return -1;
            int slot = -1, best = 0;
            for (int i = 0; i < 58; i++)
            {
                var it = p.inventory[i];
                if (it != null && !it.IsAir && it.pick > best) { best = it.pick; slot = i; }
            }
            return slot;
        }

        // status for /build_replay_status
        public static string StatusJson()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\"running\":").Append(Running ? "true" : "false")
              .Append(",\"i\":").Append(_i)
              .Append(",\"total\":").Append(Total)
              .Append(",\"placed\":").Append(Placed)
              .Append(",\"mined\":").Append(Mined)
              .Append(",\"skipped\":").Append(Skipped)
              .Append(",\"done\":").Append(_st == St.Done ? "true" : "false")
              .Append(",\"fail_reason\":\"").Append(FailReason).Append("\"}");
            return sb.ToString();
        }
    }
}
