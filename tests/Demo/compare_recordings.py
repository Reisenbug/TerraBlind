"""Compare measured navigation recordings; reject unequal starting conditions."""

import argparse
import hashlib
import json
import math
from pathlib import Path


def read(directory):
    meta = json.loads((directory / "metadata.json").read_text())
    if hashlib.sha256((directory / "initial-terrain.bin").read_bytes()).hexdigest().upper() != meta["terrain_sha256"]:
        raise ValueError(f"Initial terrain checksum mismatch: {directory}")
    rows = [json.loads(line) for line in (directory / "frames.jsonl").read_text(encoding="utf-8-sig").splitlines()]
    if len(rows) != meta["frames"] or not rows:
        raise ValueError(f"Incomplete recording: {directory}")
    if [row["frame"] for row in rows] != list(range(len(rows))):
        raise ValueError(f"Discontinuous frame indices: {directory}")
    if any(row["input"] is None for row in rows):
        raise ValueError(f"Missing input sample: {directory}")
    milestones = {}
    moving_ms = using_ms = stationary_ms = distance = overhead_gap_ms = 0.0
    mined = placed = changed = 0
    previous_ms = 0.0
    stationary = None
    pauses = []
    start_depth = (meta["start"]["y"] + meta["height"]) / 16
    thresholds = range((math.floor(start_depth / 25) + 1) * 25, meta["goal_y"] + 1, 25)
    for row in rows:
        if row["wall_ms"] < previous_ms:
            raise ValueError("Wall clock moved backwards")
        delta = row["wall_ms"] - previous_ms
        overhead_gap_ms += max(0, row["before"]["wall_ms"] - previous_ms)
        previous_ms = row["wall_ms"]
        a, b = row["before"], row["after"]
        travelled = math.hypot(b["x"] - a["x"], b["y"] - a["y"])
        distance += travelled
        if travelled > .25:
            moving_ms += delta
            if stationary is not None:
                stationary["duration_ms"] = row["wall_ms"] - delta - stationary["start_ms"]
                pauses.append(stationary)
                stationary = None
        else:
            stationary_ms += delta
            if stationary is None:
                stationary = dict(start_ms=row["wall_ms"] - delta, x=(b["x"] + meta["width"] / 2) / 16,
                                  y=(b["y"] + meta["height"]) / 16)
            stationary.update(last_stage=row["nav_stage"], last_plan=row["plan"], last_rejection=row["rejection"])
        if row["input"]["use_item"] or b["item_animation"] > 0:
            using_ms += delta
        for change in row["terrain_changes"]:
            old, new = change["before"], change["after"]
            mined += old[0] and not new[0]
            placed += new[0] and not old[0]
            changed += old[0] and new[0] and old[1] != new[1]
        depth = (b["y"] + meta["height"]) / 16
        for threshold in thresholds:
            if depth >= threshold and str(threshold) not in milestones:
                milestones[str(threshold)] = row["wall_ms"]
    if stationary is not None:
        stationary["duration_ms"] = rows[-1]["wall_ms"] - stationary["start_ms"]
        pauses.append(stationary)
    return meta, dict(wall_ms=meta["wall_ms"], frames=len(rows), reached=meta["reached"], reason=meta["reason"],
                      moving_ms=moving_ms, stationary_ms=stationary_ms, item_active_ms=using_ms,
                      inter_frame_gap_ms=overhead_gap_ms, distance_tiles=distance / 16,
                      observed_removals=mined, observed_placements=placed, observed_type_changes=changed,
                      recording_overhead_ms=meta["recording_overhead_ms"], first_depth_crossings_ms=milestones,
                      longest_stationary=sorted(pauses, key=lambda pause: pause["duration_ms"], reverse=True)[:10],
                      note="item_active overlaps movement; inter_frame_gap includes normal frame scheduling and pauses")


def compare(first, second):
    human, human_stats = read(first)
    bot, bot_stats = read(second)
    differences = [key for key in ("schema", "seed", "world_width", "world_height", "game_mode", "goal_x", "goal_y",
                                   "width", "height", "inventory", "armor", "misc_equips", "initial_area", "terrain_sha256")
                   if human[key] != bot[key]]
    for key in ("x", "y", "vx", "vy", "hp", "grav_dir", "available_jumps", "wing_time", "wet", "honey_wet"):
        if human["start"][key] != bot["start"][key]:
            differences.append("start." + key)
    if human["controller"] != "human" or bot["controller"] != "bot":
        differences.append("controller labels must be human, bot")
    for label, meta in (("human", human), ("bot", bot)):
        if meta["left_initial_area"]:
            differences.append(label + " left initial terrain capture")
        if meta["reason"] == "automation_started_during_human_recording":
            differences.append(label + " controller changed")
    common = human_stats["first_depth_crossings_ms"].keys() & bot_stats["first_depth_crossings_ms"].keys()
    segments = []
    previous_human = previous_bot = 0.0
    for depth in sorted(common, key=int):
        h = human_stats["first_depth_crossings_ms"][depth]
        b = bot_stats["first_depth_crossings_ms"][depth]
        segments.append(dict(depth=int(depth), human_ms=h - previous_human, bot_ms=b - previous_bot,
                             extra_bot_ms=(b - previous_bot) - (h - previous_human)))
        previous_human, previous_bot = h, b
    return dict(comparable=not differences, mismatches=differences, human=human_stats, bot=bot_stats,
                bot_passed_150s=not differences and bot["reached"] and bot["wall_ms"] <= 150000,
                depth_segments=segments,
                interpretation="Depth bins locate slow stretches; coordinate and input traces are needed to establish the cause")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("human", type=Path)
    parser.add_argument("bot", type=Path)
    args = parser.parse_args()
    report = compare(args.human, args.bot)
    output = args.bot / "comparison.json"
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2))
    print(json.dumps(report, ensure_ascii=False, indent=2))
