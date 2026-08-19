using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	// /act — the COMPLETE action primitive. One call submits a list of steps: steps run SERIALLY, everything inside
	// one step runs in PARALLEL (hold these keys + aim the cursor here + swing, all at once, until a condition).
	// This is the surface the LLM writes against directly, so it must be complete (every meaningful vanilla control)
	// and it must EXPLAIN ITSELF when a step stops making progress — the LLM diagnoses from the report, not from a
	// bare error code. Termination is three-way, same shape as ItemUseCoordinator: done / no_progress / timeout,
	// plus invariant_broken for a declared invariant that snapped.
	public class ActStep
	{
		public bool Left, Right, Up, Down, Jump, UseItem, UseTile, Throw, Hook, Mount;
		public int Slot = -1;                 // 0-57; backpack slots get swapped into the hotbar
		public bool HasCursor;
		public int CurDx, CurDy;              // cursor offset in tiles from the ORIGIN CELL
		public bool CursorFrozen;             // "at": resolve the origin once at step start. else "rel": follow the player
		public string UntilKind = "";         // frames | times | consumed | moved | tile | placed
		public int UntilN;                    // frames / times / stack-to-consume
		public int UntilDx, UntilDy;          // moved: target delta; tile: relative cell
		public int UntilItemType = -1;        // consumed: which item
		public bool UntilTileHas;             // tile: wait for present(true)/absent(false)
		public string InvKind = "";           // on_rope | cursor_in_reach | on_ground   ("" = none)
		public bool InvWant;
	}

	public class ActExecutor : ModSystem
	{
		// a step that stops changing its progress number for this long is stuck. 90 frames = 1.5s, far longer than any
		// place/mine useTime, so a working action never trips it.
		private const int StallFrames = 90;
		private const int DefaultTimeout = 1800;

		private static List<ActStep> _steps;
		private static int _i;
		private static int _stepFrames, _totalFrames, _timeout;
		private static int _lastProgress, _stallFor;
		private static int _times;                       // swings issued this step
		private static int _startStack;                  // consumed: stack at step start
		private static int _startCx, _startCy;           // moved: origin cell at step start
		private static int _frozenCx, _frozenCy;         // "at" cursor: origin frozen at step start
		private static int _curWx = -1, _curWy = -1;     // last resolved cursor world cell (for the report)

		// REPEAT — the step list is a LOOP BODY, re-run until its own condition holds. Without this the caller has to
		// unroll by hand (20 ropes = 20 near-identical steps), which is where "the player will have climbed by then"
		// silently breaks: an unrolled chain freezes the geometry it was written against. One loop body re-reads the
		// world every pass instead. `Max` is mandatory in spirit — a loop that cannot be bounded can hang, so the
		// parser supplies a default — which is what makes non-termination structurally impossible here.
		private static bool _hasRepeat;
		private static string _repUntilKind = "";
		private static int _repUntilN, _repUntilItemType = -1, _repUntilDx, _repUntilDy;
		private static int _repMax, _laps;
		private static int _repStartStack, _repStartCx, _repStartCy;   // baselines for the loop-level condition

		public static string Outcome = "idle";           // idle running done no_progress timeout invariant_broken bad_request
		public static bool IsActive => _steps != null;
		private static readonly List<string> _why = new();

		public static void Start(List<ActStep> steps, int timeoutFrames)
		{
			_hasRepeat = false; _repUntilKind = ""; _laps = 0;
			StartInternal(steps, timeoutFrames);
			DiagLog.Write($"[act] start steps={steps.Count} timeout={_timeout}");
		}

		// same chain, but the step list is a loop body run until `untilKind` holds (or `max` laps elapse).
		public static void StartRepeat(List<ActStep> steps, int timeoutFrames,
			string untilKind, int untilN, int untilItemType, int untilDx, int untilDy, int max)
		{
			_hasRepeat = true;
			_repUntilKind = untilKind; _repUntilN = untilN; _repUntilItemType = untilItemType;
			_repUntilDx = untilDx; _repUntilDy = untilDy;
			_repMax = max > 0 ? max : 100;
			_laps = 0;
			StartInternal(steps, timeoutFrames);
			var p0 = Main.LocalPlayer;
			_repStartStack = (p0 != null && untilKind == "consumed") ? StackOf(p0, untilItemType) : 0;
			_repStartCx = p0 != null ? OriginCx(p0) : 0;
			_repStartCy = p0 != null ? OriginCy(p0) : 0;
			DiagLog.Write($"[act] start REPEAT body={steps.Count} until={untilKind}:{untilN} max={_repMax} timeout={_timeout}");
		}

		private static void StartInternal(List<ActStep> steps, int timeoutFrames)
		{
			_steps = steps; _i = 0;
			_timeout = timeoutFrames > 0 ? timeoutFrames : DefaultTimeout;
			_totalFrames = 0;
			Outcome = "running";
			_why.Clear();
			BeginStep();
		}

		public static void Stop()
		{
			if (Outcome == "running") Outcome = "stopped";
			_steps = null;
		}

		private static void BeginStep()
		{
			_stepFrames = 0; _stallFor = 0; _times = 0; _lastProgress = int.MinValue;
			var p = Main.LocalPlayer;
			if (p == null) return;
			_startCx = OriginCx(p); _startCy = OriginCy(p);
			_frozenCx = _startCx; _frozenCy = _startCy;
			var s = _steps[_i];
			_startStack = s.UntilKind == "consumed" ? StackOf(p, s.UntilItemType) : 0;
		}

		// ORIGIN CELL — the anchor for every relative coordinate. Column: of the 2-3 columns the 20px-wide body spans,
		// the one covering the most pixels; an exact 10/10 split takes the LEFT (strict > keeps the earlier column).
		// Row: the feet row, lifted 2px so standing on ground gives the cell the player OCCUPIES, not the floor below —
		// so [0,0] is the player's own cell and [0,1] is the tile being stood on.
		public static int OriginCx(Player p)
		{
			int c0 = (int)(p.position.X / 16f);
			int c1 = (int)((p.position.X + p.width - 1) / 16f);
			int best = c0; float bestCov = -1f;
			for (int c = c0; c <= c1; c++)
			{
				float cov = System.MathF.Min(p.position.X + p.width, (c + 1) * 16f)
						  - System.MathF.Max(p.position.X, c * 16f);
				if (cov > bestCov) { bestCov = cov; best = c; }
			}
			return best;
		}
		public static int OriginCy(Player p) => (int)((p.position.Y + p.height - 2f) / 16f);

		public static void ApplyControls()
		{
			if (_steps == null) return;
			var p = Main.LocalPlayer;
			if (p == null || !p.active) { _steps = null; return; }
			var s = _steps[_i];

			_stepFrames++; _totalFrames++;
			if (_totalFrames > _timeout) { Finish("timeout"); return; }

			// SLOT — selectedItem only holds 0-9, so a backpack slot must be swapped into the hotbar first (same trick
			// as ItemUseCoordinator: prefer an empty hotbar slot, else displace slot 0).
			int slot = s.Slot;
			if (slot >= 10 && slot < p.inventory.Length)
			{
				int hb = -1;
				for (int i = 0; i < 10; i++)
					if (p.inventory[i] == null || p.inventory[i].IsAir) { hb = i; break; }
				if (hb < 0) hb = 0;
				var tmp = p.inventory[hb];
				p.inventory[hb] = p.inventory[slot];
				p.inventory[slot] = tmp;
				slot = hb;
				s.Slot = hb;
			}
			if (slot >= 0 && slot <= 9) p.selectedItem = slot;

			// CURSOR — resolve the target cell and point the mouse at its centre. "rel" recomputes the origin every
			// frame (the aim follows the player, e.g. always 4 above the feet while climbing); "at" uses the origin
			// frozen at step start (the aim stays on one world cell while the player moves).
			if (s.HasCursor)
			{
				int ox = s.CursorFrozen ? _frozenCx : OriginCx(p);
				int oy = s.CursorFrozen ? _frozenCy : OriginCy(p);
				_curWx = ox + s.CurDx; _curWy = oy + s.CurDy;
				Cursor.AimTile(_curWx, _curWy);
			}

			// KEYS — written raw into the vanilla control fields. controlDown here is the real platform fall-through
			// (Player.cs `bool fallThrough = controlDown`), instant, no hold timer involved.
			if (s.Left) p.controlLeft = true;
			if (s.Right) p.controlRight = true;
			if (s.Up) p.controlUp = true;
			if (s.Down) p.controlDown = true;
			if (s.Jump) p.controlJump = true;
			if (s.UseTile) p.controlUseTile = true;
			if (s.Throw) p.controlThrow = true;
			if (s.Hook) p.controlHook = true;
			if (s.Mount) p.controlMount = true;

			// USE — TAP semantics. Vanilla gates reuse on `releaseUseItem = !controlUseItem` from the previous frame,
			// so holding the button down forever fires exactly once. Pressing only while itemAnimation == 0 gives the
			// press/release alternation for free: the swing sets itemAnimation, we stop pressing, release registers.
			if (s.UseItem && p.itemAnimation == 0)
			{
				p.controlUseItem = true;
				_times++;
			}

			// INVARIANT — a declared fact that must hold for the step to make sense. Break it and stop AT ONCE with a
			// full report, instead of flailing until the stall window expires. This is "stuck must be structurally
			// impossible": the failure is detected by construction, not by waiting for a timeout.
			if (s.InvKind.Length > 0 && CheckInv(p, s.InvKind) != s.InvWant)
			{
				_why.Add("invariant_" + s.InvKind);
				Finish("invariant_broken");
				return;
			}

			int prog = Progress(p, s);
			if (Satisfied(p, s, prog)) { Advance(); return; }

			// NO-PROGRESS — the progress number for this until-kind hasn't moved for StallFrames. Every kind exposes a
			// monotone counter (consumed / moved / times / frames) precisely so stalling is measurable rather than
			// guessed at. frames-based steps can't stall by definition and are skipped.
			if (s.UntilKind == "frames") return;
			if (prog == _lastProgress) { if (++_stallFor >= StallFrames) { Diagnose(p, s); Finish("no_progress"); } }
			else { _lastProgress = prog; _stallFor = 0; }
		}

		private static void Advance()
		{
			_i++;
			if (_i < _steps.Count) { BeginStep(); return; }

			// end of the step list. A plain chain is done; a loop body checks its own condition and goes round again.
			if (!_hasRepeat) { Finish("done"); return; }
			_laps++;
			if (RepeatSatisfied()) { Finish("done"); return; }
			if (_laps >= _repMax) { _why.Add("repeat_max_laps"); Finish("no_progress"); return; }
			_i = 0;
			BeginStep();
		}

		// the loop-level condition, measured against the baseline captured when the LOOP started (not the step).
		private static bool RepeatSatisfied()
		{
			var p = Main.LocalPlayer;
			if (p == null) return true;
			switch (_repUntilKind)
			{
				case "consumed": return _repStartStack - StackOf(p, _repUntilItemType) >= _repUntilN;
				case "moved":
					return (_repUntilDx == 0 || System.Math.Abs(OriginCx(p) - _repStartCx) >= System.Math.Abs(_repUntilDx))
						&& (_repUntilDy == 0 || System.Math.Abs(OriginCy(p) - _repStartCy) >= System.Math.Abs(_repUntilDy));
				case "times": return _laps >= _repUntilN;
				default: return false;   // no/unknown condition → only _repMax stops it
			}
		}

		private static void Finish(string outcome)
		{
			Outcome = outcome;
			DiagLog.Write($"[act] {outcome} step={_i}/{(_steps != null ? _steps.Count : 0)} frames={_totalFrames} why=[{string.Join(",", _why)}]");
			_steps = null;
		}

		private static int Progress(Player p, ActStep s)
		{
			switch (s.UntilKind)
			{
				case "consumed": return _startStack - StackOf(p, s.UntilItemType);
				case "moved": return System.Math.Abs(OriginCx(p) - _startCx) + System.Math.Abs(OriginCy(p) - _startCy);
				case "times": return _times;
				case "tile": return HasTileAt(s) ? 1 : 0;
				case "placed": return CursorCellFilled() ? 1 : 0;
				default: return _stepFrames;
			}
		}

		// "placed" — the natural terminator for a placement: the cell the cursor is aiming at now holds a tile. Without
		// it a caller has to spell the same cell out twice (once as the cursor, once as a tile condition) or fall back
		// to a frame count, and a frame count is how 19 of 20 ropes got skipped while the chain still reported done.
		private static bool CursorCellFilled()
		{
			if (_curWx < 0 || !InBounds(_curWx, _curWy)) return false;
			return Main.tile[_curWx, _curWy].HasTile;
		}

		private static bool Satisfied(Player p, ActStep s, int prog)
		{
			switch (s.UntilKind)
			{
				case "frames": return _stepFrames >= s.UntilN;
				case "times": return _times >= s.UntilN;
				case "consumed": return prog >= s.UntilN;
				case "moved":
					return (s.UntilDx == 0 || System.Math.Abs(OriginCx(p) - _startCx) >= System.Math.Abs(s.UntilDx))
						&& (s.UntilDy == 0 || System.Math.Abs(OriginCy(p) - _startCy) >= System.Math.Abs(s.UntilDy));
				case "tile": return HasTileAt(s) == s.UntilTileHas;
				case "placed": return CursorCellFilled();
				default: return false;
			}
		}

		private static bool HasTileAt(ActStep s)
		{
			var p = Main.LocalPlayer;
			int x = OriginCx(p) + s.UntilDx, y = OriginCy(p) + s.UntilDy;
			if (!InBounds(x, y)) return false;
			return Main.tile[x, y].HasTile;
		}

		private static bool CheckInv(Player p, string kind)
		{
			switch (kind)
			{
				case "on_rope": return OnRope(p);
				case "on_ground": return p.velocity.Y == 0f;
				case "cursor_in_reach":
					return _curWx >= 0 && p.IsInTileInteractionRange(_curWx, _curWy, Terraria.DataStructures.TileReachCheckSettings.Simple);
				default: return true;
			}
		}

		// Main.tileRope covers every rope variant (Rope/Vine/Silk/Web/Chain), so this stays right without an ID list.
		private static bool OnRope(Player p)
		{
			int cx = OriginCx(p), cy = OriginCy(p);
			for (int dy = 0; dy <= 1; dy++)
			{
				int y = cy + dy;
				if (!InBounds(cx, y)) continue;
				var t = Main.tile[cx, y];
				if (t.HasTile && Main.tileRope[t.TileType]) return true;
			}
			return false;
		}

		// WHY — the executor's mechanical self-check when a step stalls. Every entry is a directly testable world fact,
		// never a guess. The LLM reads these plus the raw scene below and works out the fix itself; that's the whole
		// point of reporting the scene instead of an error code.
		private static void Diagnose(Player p, ActStep s)
		{
			_why.Clear();
			var held = p.inventory[p.selectedItem];

			if (s.HasCursor && _curWx >= 0)
			{
				if (!p.IsInTileInteractionRange(_curWx, _curWy, Terraria.DataStructures.TileReachCheckSettings.Simple))
					_why.Add("cursor_out_of_reach");
				if (InBounds(_curWx, _curWy) && Main.tile[_curWx, _curWy].HasTile)
					_why.Add("target_occupied");
				// a placed tile needs something to attach to: any solid/rope/platform neighbour. Rope in particular
				// only extends from an existing rope or a ceiling, so a mid-air target silently does nothing.
				if (!ItemUseCoordinator.HasAnchor(_curWx, _curWy)) _why.Add("no_anchor");
			}
			if (held == null || held.IsAir) _why.Add("empty_hand");
			else
			{
				if (s.UseItem && held.createTile < 0 && held.createWall < 0 && held.pick == 0 && held.axe == 0 && held.hammer == 0)
					_why.Add("wrong_item");
				if (held.stack <= 0) _why.Add("out_of_stock");
			}
			if (s.UntilKind == "consumed" && StackOf(p, s.UntilItemType) <= 0) _why.Add("out_of_stock");
			if (s.Slot >= 0 && s.UntilItemType >= 0 && (held == null || held.type != s.UntilItemType)) _why.Add("slot_lost");
			if (s.UntilKind == "moved" && p.velocity.X == 0f && p.velocity.Y == 0f) _why.Add("player_not_moving");
			if (s.Up && Blocked(p, 0, -1)) _why.Add("blocked_above");
			if ((s.Left || s.Right) && Blocked(p, s.Left ? -1 : 1, 0)) _why.Add("blocked_sideways");
		}

		private static bool Blocked(Player p, int dx, int dy)
		{
			int x = OriginCx(p) + dx, y = OriginCy(p) + dy;
			if (!InBounds(x, y)) return true;
			var t = Main.tile[x, y];
			return Predicates.IsWall(x, y);
		}

		private static int StackOf(Player p, int type)
		{
			if (type < 0) return 0;
			int n = 0;
			for (int i = 0; i < 58 && i < p.inventory.Length; i++)
			{
				var it = p.inventory[i];
				if (it != null && !it.IsAir && it.type == type) n += it.stack;
			}
			return n;
		}

		private static bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Main.maxTilesX && y < Main.maxTilesY;

		// THE SCENE — everything the executor could see when it stopped, dumped raw. Not a verdict: the numbers the
		// LLM needs to reach its own verdict (where the cursor actually was, whether it was in reach, what sat in the
		// target cell, what was in hand, whether the player was on a rope).
		public static string StatusJson()
		{
			var p = Main.LocalPlayer;
			var sb = new StringBuilder();
			sb.Append("{\"outcome\":\"").Append(Outcome).Append('"')
			  .Append(",\"active\":").Append(IsActive ? "true" : "false")
			  .Append(",\"step\":").Append(_i)
			  .Append(",\"steps\":").Append(_steps != null ? _steps.Count : 0)
			  .Append(",\"frames\":").Append(_totalFrames);
			if (_hasRepeat) sb.Append(",\"laps\":").Append(_laps).Append(",\"max_laps\":").Append(_repMax);

			sb.Append(",\"why\":[");
			for (int i = 0; i < _why.Count; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(_why[i]).Append('"'); }
			sb.Append(']');

			var s = (_steps != null && _i < _steps.Count) ? _steps[_i] : null;
			if (s != null && p != null)
				sb.Append(",\"progress\":{\"now\":").Append(Progress(p, s)).Append(",\"want\":").Append(s.UntilN)
				  .Append(",\"kind\":\"").Append(s.UntilKind).Append("\"},\"stall\":").Append(_stallFor);

			if (p != null && p.active)
			{
				bool reach = _curWx >= 0 && p.IsInTileInteractionRange(_curWx, _curWy, Terraria.DataStructures.TileReachCheckSettings.Simple);
				sb.Append(",\"cursor\":{\"world\":[").Append(_curWx).Append(',').Append(_curWy)
				  .Append("],\"in_reach\":").Append(reach ? "true" : "false").Append('}');

				if (InBounds(_curWx, _curWy))
				{
					var t = Main.tile[_curWx, _curWy];
					sb.Append(",\"target_tile\":{\"has_tile\":").Append(t.HasTile ? "true" : "false")
					  .Append(",\"type\":").Append(t.HasTile ? t.TileType : -1)
					  .Append(",\"anchored\":").Append(ItemUseCoordinator.HasAnchor(_curWx, _curWy) ? "true" : "false").Append('}');
				}

				sb.Append(",\"player\":{\"origin_cell\":[").Append(OriginCx(p)).Append(',').Append(OriginCy(p))
				  .Append("],\"pos\":[").Append(p.position.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
				  .Append(',').Append(p.position.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
				  .Append("],\"vel\":[").Append(p.velocity.X.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
				  .Append(',').Append(p.velocity.Y.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
				  .Append("],\"on_ground\":").Append(p.velocity.Y == 0f ? "true" : "false")
				  .Append(",\"on_rope\":").Append(OnRope(p) ? "true" : "false")
				  .Append(",\"wet\":").Append(p.wet ? "true" : "false").Append('}');

				var held = p.inventory[p.selectedItem];
				sb.Append(",\"held\":{\"slot\":").Append(p.selectedItem)
				  .Append(",\"name\":\"").Append(held != null && !held.IsAir ? JsonEsc(held.Name) : "")
				  .Append("\",\"type\":").Append(held != null && !held.IsAir ? held.type : -1)
				  .Append(",\"stack\":").Append(held != null && !held.IsAir ? held.stack : 0)
				  .Append(",\"item_animation\":").Append(p.itemAnimation)
				  .Append(",\"can_place\":").Append(held != null && !held.IsAir && held.createTile >= 0 ? "true" : "false").Append('}');
			}
			sb.Append('}');
			return sb.ToString();
		}

		private static string JsonEsc(string s) =>
			string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
	}
}
