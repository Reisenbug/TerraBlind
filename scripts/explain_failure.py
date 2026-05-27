#!/usr/bin/env python3
"""Summarise nav failures by goal coordinate.

Usage:
  explain_failure.py 1820,304          # list failures at (~)1820,304
  explain_failure.py 1820,304 1        # detailed report on the most-recent one
  explain_failure.py latest            # detailed report on the most-recent failure overall
"""
from __future__ import annotations
import json
import os
import sys
from pathlib import Path

LOG_DIR = Path.home() / "Library/Application Support/Terraria/tModLoader/TerraBlindLogs"
NAV_JSONL = LOG_DIR / "nav_events.jsonl"
ASCII_LOG = LOG_DIR / "ascii_map.log"

GOAL_TOL = 2          # tile tolerance for goal matching
PRE_NODES = 6         # how many nodes before failure to show
TICKS_PER_SEC = 60    # approx (game runs at 60fps)


def parse_jsonl(path: Path):
    with path.open() as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                yield json.loads(line)
            except json.JSONDecodeError:
                continue


def load_events():
    """Return list of all nav events in order."""
    return list(parse_jsonl(NAV_JSONL))


def find_failures(events, goal=None):
    """Walk events; return list of (failure_event, plan_done_event_or_None, surrounding_events_index_range).

    goal: (gx, gy) or None to mean any.
    Each failure is paired with the most recent plan_done before it (whose goal it inherits).
    """
    out = []
    last_plan_done = None
    last_plan_start_idx = None
    for i, ev in enumerate(events):
        e = ev.get("e")
        if e == "plan_done":
            last_plan_done = ev
            last_plan_start_idx = i
        elif e == "nav_failed":
            g = last_plan_done.get("goal") if last_plan_done else None
            if goal is not None:
                if g is None:
                    continue
                if abs(g[0] - goal[0]) > GOAL_TOL or abs(g[1] - goal[1]) > GOAL_TOL:
                    continue
            out.append({
                "fail_idx": i,
                "fail": ev,
                "plan": last_plan_done,
                "plan_idx": last_plan_start_idx,
                "goal": g,
            })
    return out


def latest_tick(events):
    for ev in reversed(events):
        if "tick" in ev:
            return ev["tick"]
    return 0


def fmt_age(now_tick: int, ev_tick: int) -> str:
    dt = (now_tick - ev_tick) / TICKS_PER_SEC
    if dt < 60:
        return f"{dt:.0f}s ago"
    if dt < 3600:
        return f"{dt/60:.1f}m ago"
    return f"{dt/3600:.1f}h ago"


def list_failures(failures, now_tick):
    """Print one-line summary per failure, newest first."""
    if not failures:
        print("(no matching failures)")
        return
    failures = list(reversed(failures))  # newest first
    for n, f in enumerate(failures, 1):
        fe = f["fail"]
        goal = f["goal"]
        gstr = f"goal=({goal[0]},{goal[1]})" if goal else "goal=?"
        node = fe.get("last_action") or fe.get("last_node_action") or ""
        node_part = f"  node={node}" if node else ""
        print(f"  [{n}] {fmt_age(now_tick, fe['tick']):>9}  "
              f"{fe.get('reason',''):20}  {gstr}{node_part}  tick={fe['tick']}")


def collect_node_history(events, fail_idx, plan_idx, n_back=PRE_NODES):
    """From plan_idx forward to fail_idx, collect node_enter/exit pairs."""
    nodes = []
    cur_enter = None
    for ev in events[plan_idx: fail_idx + 1]:
        e = ev.get("e")
        if e == "node_enter":
            cur_enter = ev
        elif e == "node_exit" and cur_enter is not None:
            nodes.append((cur_enter, ev))
            cur_enter = None
    if cur_enter is not None:
        nodes.append((cur_enter, None))   # incomplete (failed mid-node)
    return nodes[-n_back:]


def _parse_ascii_header(line: str):
    """Return (tick, event_name, goal_tuple_or_None) from `[tick=N e=... goal=[X, Y]]`."""
    try:
        tick = int(line.split("tick=", 1)[1].split(" ", 1)[0])
    except (ValueError, IndexError):
        return None, None, None
    ev = None
    if " e=" in line:
        try:
            ev = line.split(" e=", 1)[1].split(" ", 1)[0]
        except IndexError:
            pass
    goal = None
    if "goal=[" in line:
        try:
            gpart = line.split("goal=[", 1)[1].split("]", 1)[0]
            gx, gy = [int(s.strip()) for s in gpart.split(",")]
            goal = (gx, gy)
        except (ValueError, IndexError):
            pass
    return tick, ev, goal


def find_ascii_block_for_tick(target_tick: int, prefer_goal=None) -> tuple | None:
    """Find the ASCII map block closest to target_tick (within ±SNAP_TOL ticks).

    Note: ASCII log's `goal=` is the NavCoordinator final goal; jsonl's plan_done
    `goal` is the PathPlanner internal walking target — these differ. We pick by
    tick proximity instead, preferring nav_failed events.
    """
    SNAP_TOL = 120  # ~2 sec
    if not ASCII_LOG.exists():
        return None
    best = None     # (abs_dt, prefers_failed, tick, header, lines)
    cur_tick = None
    cur_ev = None
    cur_lines: list[str] = []
    cur_header = ""

    def consider():
        nonlocal best
        if cur_tick is None:
            return
        dt = abs(cur_tick - target_tick)
        if dt > SNAP_TOL:
            return
        prefers = 0 if cur_ev == "nav_failed" else 1
        cand = (dt, prefers, cur_tick, cur_header, list(cur_lines))
        if best is None or cand < best:
            best = cand

    with ASCII_LOG.open() as f:
        for line in f:
            if line.startswith("[tick="):
                consider()
                cur_tick, cur_ev, _ = _parse_ascii_header(line)
                cur_header = line.rstrip("\n")
                cur_lines = []
            else:
                cur_lines.append(line.rstrip("\n"))
        consider()
    if best is None:
        return None
    _, _, tick, header, lines = best
    return (tick, header, lines)


def format_node(enter, exit_):
    """Render one node entry as 1-2 lines describing expected vs actual."""
    a = enter.get("action", "?")
    sx, sy = enter.get("exp_start_wx"), enter.get("exp_start_wy")
    ex, ey = enter.get("exp_end_wx"), enter.get("exp_end_wy")
    ax, ay = enter.get("actual_px"), enter.get("actual_py")
    vx, vy = enter.get("vx"), enter.get("vy")
    mode = enter.get("mode", "")
    head = (f"  node[{enter.get('node_idx','?')}] {a:8} "
            f"exp ({sx},{sy})→({ex},{ey})  enter ({ax},{ay}) vx={vx} vy={vy} {mode}")
    if exit_ is None:
        return head + "\n    (no exit — failed mid-node)"
    aex, aey = exit_.get("actual_end_wx"), exit_.get("actual_end_wy")
    dx, dy = exit_.get("delta_x"), exit_.get("delta_y")
    status = exit_.get("status", "?")
    dur = exit_.get("duration_ticks")
    vxe, vye = exit_.get("vx_end"), exit_.get("vy_end")
    return (head + "\n"
            f"    exit  ({aex},{aey})  delta=({dx},{dy})  vx={vxe} vy={vye}  "
            f"status={status} dur={dur}t")


def detail_report(events, f):
    fe = f["fail"]
    plan = f["plan"]
    goal = f["goal"]
    print(f"=== failure @ tick={fe['tick']}  reason={fe.get('reason')} ===")
    if goal:
        print(f"goal: ({goal[0]},{goal[1]})  plan tick={plan['tick']}  "
              f"path_len={plan.get('path_len')}  cost={plan.get('cost')}")
    print(f"fail state: px={fe.get('px')} py={fe.get('py')} "
          f"dx={fe.get('dx')} dy={fe.get('dy')} stall={fe.get('stall_count')} "
          f"last_node_idx={fe.get('last_node_idx')} last_action={fe.get('last_action')}")
    print()

    # planned path
    if plan and "path" in plan:
        print("planned path:")
        for i, n in enumerate(plan["path"]):
            print(f"  [{i}] {n.get('action','?'):10} → ({n.get('wx')},{n.get('wy')})")
        print()

    # node history (expected vs actual)
    nodes = collect_node_history(events, f["fail_idx"], f["plan_idx"])
    if nodes:
        print(f"last {len(nodes)} node(s) executed (exp → actual):")
        for enter, exit_ in nodes:
            print(format_node(enter, exit_))
        print()

    # ascii snapshot closest to failure tick
    snap = find_ascii_block_for_tick(fe["tick"], prefer_goal=goal)
    if snap:
        snap_tick, header, lines = snap
        delta = fe["tick"] - snap_tick
        print(f"terrain snapshot ({header}, {delta}t before failure):")
        for ln in lines:
            print(f"  {ln}")
    else:
        print("(no ascii snapshot found)")


def parse_goal_arg(s: str) -> tuple[int, int]:
    parts = s.replace(" ", "").split(",")
    if len(parts) != 2:
        raise ValueError(f"bad goal: {s!r} (want gx,gy)")
    return int(parts[0]), int(parts[1])


def main():
    if len(sys.argv) < 2:
        print(__doc__.strip())
        sys.exit(2)
    events = load_events()
    now = latest_tick(events)

    arg1 = sys.argv[1]
    if arg1 == "latest":
        all_fails = find_failures(events, goal=None)
        if not all_fails:
            print("no failures recorded")
            return
        detail_report(events, all_fails[-1])
        return

    goal = parse_goal_arg(arg1)
    failures = find_failures(events, goal=goal)
    if len(sys.argv) >= 3:
        idx = int(sys.argv[2])
        rev = list(reversed(failures))
        if not (1 <= idx <= len(rev)):
            print(f"index {idx} out of range (1..{len(rev)})")
            sys.exit(1)
        detail_report(events, rev[idx - 1])
    elif len(failures) == 1:
        detail_report(events, failures[0])
    else:
        print(f"failures near goal ({goal[0]},{goal[1]}) (tolerance ±{GOAL_TOL}):")
        list_failures(failures, now)
        if failures:
            print(f"\nrun with index to drill in:  explain_failure.py {arg1} 1")


if __name__ == "__main__":
    main()
