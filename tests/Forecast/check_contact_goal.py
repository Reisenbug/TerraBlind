"""Replay real terrain with an uninformative cost field and a supported local goal."""

import argparse
import base64
import json
import struct
from pathlib import Path

from check import Worker


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("snapshot", type=Path)
    parser.add_argument("--goal", type=float, nargs=2, required=True)
    parser.add_argument("--port", type=int, default=19052)
    parser.add_argument("--keep-guidance", action="store_true")
    args = parser.parse_args()
    request = json.loads(args.snapshot.read_text())["request"]
    request.pop("command", None)
    request.pop("request_id", None)
    raw = base64.b64decode(request["guidance"])
    cells = struct.unpack("<" + "i" * (len(raw) // 4), raw)
    if not args.keep_guidance:
        request["guidance"] = [[cells[i], cells[i + 1], 0] for i in range(0, len(cells), 3)]
    request["x"], request["y"] = args.goal
    request["recover"] = False
    worker = Worker(args.port)
    try:
        plan = worker.call("plan", **request)
        assert plan["status"] == "planned" and plan["goal_contact"], plan["selected"]
        selected = next(c for c in plan["candidates"] if c["name"] == plan["selected"])
        assert selected["field_progress"] <= 0 and plan["route_progress"] > 0
        worker.call("load", snapshot=request["snapshot"])
        actual = worker.call("actual", controls=request["prefix"] + plan["controls"])["frames"]
        replay = actual[len(request["prefix"]):]
        assert len(plan["forecast"]["frames"]) == len(replay)
        for expected, observed in zip(plan["forecast"]["frames"], replay):
            for key in ("x", "y", "vx", "vy", "hp", "wing_time", "available_jumps"):
                assert abs(expected[key] - observed[key]) < .001, (key, expected[key], observed[key])
        assert all(not frame["dead"] and frame["hp"] == plan["anchor"]["hp"] for frame in actual)
        request["x"], request["y"] = plan["anchor"]["x"], plan["anchor"]["y"]
        already = worker.call("plan", **request)
        assert already["goal_supported"] and not already["goal_contact"]
        assert already["route_progress"] <= 0
        print(json.dumps({"selected": plan["selected"], "field_progress": selected["field_progress"],
                          "route_progress": plan["route_progress"],
                          "end": {key: actual[-1][key] for key in ("x", "y", "vx", "vy", "hp")},
                          "already_at_goal": already["selected"], "elapsed_ms": plan["elapsed_ms"]}))
    finally:
        worker.close()


if __name__ == "__main__":
    main()
