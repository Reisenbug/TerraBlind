"""Repair a recorded route on real terrain with deliberately uninformative guidance."""

import argparse
import base64
import json
import struct
from pathlib import Path

from check import Worker


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("evidence", type=Path)
    parser.add_argument("--port", type=int, default=19052)
    args = parser.parse_args()
    evidence = json.loads(args.evidence.read_text())
    snapshot = evidence["snapshot"]
    endpoint = evidence["active_plan"]["forecast"]["frames"][-1]
    x, y, width, height = snapshot["area"]
    cells = [value for tx in range(x, x + width) for ty in range(y, y + height) for value in (tx, ty, 0)]
    guidance = base64.b64encode(struct.pack("<" + "i" * len(cells), *cells)).decode()
    worker = Worker(args.port)
    try:
        plan = worker.call("plan", snapshot=snapshot, prefix=[0] * 24,
                           continuation=evidence["remaining_controls"], guidance=guidance,
                           x=endpoint["x"], y=endpoint["y"], recover=True, continuation_only=True)
        assert plan["status"] == "planned" and plan["selected"] == "continue", plan["status"]
        assert plan["goal_contact"] and len(plan["candidates"]) == 1
        worker.call("load", snapshot=snapshot)
        actual = worker.call("actual", controls=[0] * 24 + plan["controls"])["frames"]
        assert len(actual) == 24 + len(plan["forecast"]["frames"])
        assert all(not frame["dead"] and frame["hp"] == plan["anchor"]["hp"] for frame in actual)
        for expected, observed in zip(plan["forecast"]["frames"], actual[24:]):
            for key in ("x", "y", "vx", "vy", "hp"):
                assert abs(expected[key] - observed[key]) < .001, (key, expected[key], observed[key])
        left, right, landing_y = plan["goal_contact_region"]
        assert left <= actual[-1]["x"] <= right and abs(actual[-1]["y"] - landing_y) < .01
        print(json.dumps({"compute_ms": plan["request_compute_ms"], "region": plan["goal_contact_region"],
                          "endpoint_offset_x": actual[-1]["x"] - endpoint["x"], "hp": actual[-1]["hp"]}))
    finally:
        worker.close()


if __name__ == "__main__":
    main()
