"""Measure a reverse landing and its continuation on a recorded obstruction."""

import argparse
import base64
import json
import math
import struct
import time
from pathlib import Path

from check import Worker, compare


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", type=Path)
    parser.add_argument("--port", type=int, default=19052)
    parser.add_argument("--hold", type=int, default=18)
    parser.add_argument("--travel", type=int, default=60)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not 0 < args.hold <= 180 or not 0 < args.travel <= 180:
        parser.error("hold and travel must be between 1 and 180 frames")
    request = json.loads(args.evidence.read_text())["request"]
    direction = request["route_direction"]
    if direction not in (-1, 1):
        parser.error("recorded route_direction must be -1 or 1")
    reverse = 2 if direction == -1 else 1
    controls = [(reverse if i < args.travel else 0) | (4 if i < args.hold else 0)
                for i in range(180)]
    report = {"evidence": str(args.evidence), "hold": args.hold, "travel": args.travel,
              "passed": False, "stages": {}}
    started = time.perf_counter()
    worker = Worker(args.port)
    try:
        snapshot = request["snapshot"]
        predicted = []
        prefix = request["prefix"]
        if prefix:
            handoff = worker.call("replay", snapshot=snapshot, controls=prefix, capture_end=True)
            report["stages"]["handoff"] = handoff
            assert handoff["outcome"] == "complete" and handoff["end_snapshot"], "unsafe handoff"
            snapshot = handoff["end_snapshot"]
            predicted.extend(handoff["frames"])
        first = worker.call("replay", snapshot=snapshot, controls=controls, capture_end=True)
        report["stages"]["first"] = first
        assert first["outcome"] == "complete" and first["end_snapshot"], "unsafe first movement"
        landing = first["frames"][-1]
        assert landing["grounded"] and landing["vy"] == 0, "first movement did not land"
        predicted.extend(first["frames"])
        second = worker.call("plan", snapshot=first["end_snapshot"], prefix=[0],
                             x=request["x"], y=request["y"], guidance=request.get("guidance"),
                             terminal=request.get("terminal", False), route_direction=direction)
        report["stages"]["second"] = second
        assert second["status"] == "planned" and second["route_progress"] > 0, "no advancing second movement"
        settled = worker.call("replay", snapshot=first["end_snapshot"], controls=[0], capture_end=True)
        assert settled["end_snapshot"], "unsafe second handoff"
        predicted.extend(settled["frames"])
        second_replay = worker.call("replay", snapshot=settled["end_snapshot"], controls=second["controls"])
        assert second_replay["outcome"] == "complete", "unsafe second movement"
        for expected, observed in zip(second["forecast"]["frames"], second_replay["frames"]):
            for key in ("x", "y", "vx", "vy", "hp"):
                assert abs(expected[key] - observed[key]) < .001, ("second forecast", key)
        predicted.extend(second_replay["frames"])
        all_controls = prefix + controls + [0] + second["controls"]
        initial = worker.call("load", snapshot=request["snapshot"])["initial"]
        actual = worker.call("actual", controls=all_controls)["frames"]
        report["max_error"] = compare(predicted, actual)
        hp = initial["hp"]
        assert all(not frame["dead"] and frame["hp"] == hp for frame in actual), "damage during continuous replay"
        report["first_endpoint"] = {key: landing[key] for key in ("x", "y", "hp")}
        report["final_endpoint"] = {key: actual[-1][key] for key in ("x", "y", "hp")}
        guidance = request.get("guidance")
        if isinstance(guidance, str):
            values = [value[0] for value in struct.iter_unpack("<i", base64.b64decode(guidance))]
            guidance = zip(values[::3], values[1::3], values[2::3])
        field = {(x, y): cost for x, y, cost in guidance or []}
        width = request["snapshot"]["player"]["Terraria.Entity:width"]
        height = request["snapshot"]["player"]["Terraria.Entity:height"]
        costs = [field.get((math.floor((frame["x"] + width / 2) / 16),
                            math.floor((frame["y"] + height - 1) / 16)))
                 for frame in (initial, landing, actual[-1])]
        report["field_costs_start_landing_end"] = costs
        report["net_field_progress"] = costs[0] - costs[-1] if None not in costs else None
        report["frames"] = len(actual)
        report["passed"] = True
    except Exception as error:
        report["failure"] = str(error)
        raise
    finally:
        report["wall_ms"] = (time.perf_counter() - started) * 1000
        args.output.write_text(json.dumps(report, ensure_ascii=False))
        worker.close()
        print(json.dumps({key: value for key, value in report.items() if key != "stages"}, ensure_ascii=False))


if __name__ == "__main__":
    main()
