"""Validate preparatory mining followed by platform ascent on a real failed route."""

import argparse
import json
from pathlib import Path

from check import Worker


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", type=Path)
    parser.add_argument("--port", type=int, default=19072)
    args = parser.parse_args()
    evidence = json.loads(args.evidence.read_text())
    request = evidence["request"]
    mining = next(attempt for attempt in evidence["attempts"] if attempt["type"] < 0)
    request.pop("command", None)
    request.pop("request_id", None)
    request.update(tile_x=mining["x"], tile_y=mining["y"], tile_type=-1, tiles=mining["tiles"])
    worker = Worker(args.port)
    try:
        plan = worker.call("terrain_plan", **request)
        assert plan["status"] == "planned" and plan["route_progress"] <= 0, plan["status"]
        x, y = plan["next_jump_place"]
        blocked = worker.call("terrain_plan", snapshot=request["snapshot"], jump_place=True,
                              tile_x=x, tile_y=y, tile_type=19)
        assert blocked["status"] != "planned", "mining was unnecessary for this placement"
        after = worker.call("replay", snapshot=plan["context"], controls=[0] * plan["wait_frames"], capture_end=True)
        assert after["outcome"] == "complete" and after["end_snapshot"]
        next_plan = worker.call("terrain_plan", snapshot=after["end_snapshot"], jump_place=True,
                                tile_x=x, tile_y=y, tile_type=19)
        assert next_plan["status"] == "planned" and next_plan["rise"] > 0, next_plan["status"]
        print(json.dumps({"mined_tiles": mining["tiles"], "next_platform": [x, y],
                          "before_mining": blocked["status"], "after_mining": next_plan["status"],
                          "rise": next_plan["rise"], "movement_progress": plan["route_progress"]}))
    finally:
        worker.close()


if __name__ == "__main__":
    main()
