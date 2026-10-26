import argparse
import collections
import csv
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("root", type=Path)
    parser.add_argument("--pick-type", type=int, required=True)
    args = parser.parse_args()
    rows = list(csv.DictReader((args.root / "control-trace.csv").open()))
    recordings = list((args.root / "NavigationRecordings").glob("*/frames.jsonl"))
    if len(recordings) != 1:
        raise ValueError("Expected one recording in this isolated test root")
    phases = collections.defaultdict(float)
    wet_phases = collections.defaultdict(float)
    wet_rejection = collections.defaultdict(float)
    episodes = []
    episode = None
    aligned = 0
    with recordings[0].open(encoding="utf-8-sig") as stream:
        for i, line in enumerate(stream):
            frame = json.loads(line)
            current, previous = rows[i + 1], rows[i]
            state = frame["after"]
            if float(current["x"]) != state["x"] or float(current["y"]) != state["y"]:
                raise ValueError(f"Recording/control alignment differs at frame {i}")
            aligned += 1
            now = float(current["wall_ms"])
            dt = now - float(previous["wall_ms"])
            moved = sum((float(current[k]) - float(previous[k])) ** 2 for k in ("x", "y")) > .0001
            pick = frame["input"]["item_type"] == args.pick_type and (
                current["use_item"] == "True" or state["item_animation"] > 0)
            if moved or pick:
                episode = None
                continue
            phase = current["phase"]
            phases[phase] += dt
            if state["wet"]:
                wet_phases[phase] += dt
            if current["rejection"] == "physics_changed:wet":
                wet_rejection[phase] += dt
            if episode is None or episode["phase"] != phase:
                episode = {"phase": phase, "start_ms": now - dt, "duration_ms": 0,
                           "x": state["x"] / 16, "y": (state["y"] + 42) / 16,
                           "wet_ms": 0, "rejection_markers": []}
                episodes.append(episode)
            episode["duration_ms"] += dt
            episode["wet_ms"] += dt if state["wet"] else 0
            reason = current["rejection"]
            if reason and reason not in episode["rejection_markers"]:
                episode["rejection_markers"].append(reason)
    recorded = json.loads((args.root / "route-timing.json").read_text())["activity_ms"]["stationary_without_pick"]
    if abs(sum(phases.values()) - recorded) > 100:
        raise ValueError("Reconstructed idle time differs from runtime accounting by over 100ms")
    print(json.dumps({"aligned_frames": aligned, "idle_ms": sum(phases.values()),
                      "phase_ms": phases, "wet_overlap_ms": wet_phases,
                      "wet_rejection_marker_ms": wet_rejection,
                      "note": "Wet overlap and rejection markers are observations, not causal attribution.",
                      "longest_episodes": sorted(episodes, key=lambda e: -e["duration_ms"])[:20]}, indent=2))


if __name__ == "__main__":
    main()
