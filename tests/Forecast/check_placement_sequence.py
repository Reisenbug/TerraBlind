"""Probe jump-before-placement geometry on a captured real-world obstruction."""

import argparse
import json
from pathlib import Path

from check import Worker


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", type=Path)
    parser.add_argument("--tile", type=int, nargs=2, required=True)
    parser.add_argument("--port", type=int, default=19072)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    evidence = json.loads(args.evidence.read_text())
    snapshot = evidence["snapshot"]
    results = []
    worker = Worker(args.port)
    try:
        initial = worker.call("load", snapshot=snapshot)["initial"]
        for hold in (6, 12, 18):
            for place_after in (0, 1, 2, 4, 8, 12, 18):
                result = worker.call("placement_sequence", snapshot=snapshot,
                                     tile_x=args.tile[0], tile_y=args.tile[1], place_after=place_after,
                                     controls=[4] * hold + [0] * (120 - hold))
                frames = result.pop("frames")
                end = frames[-1] if frames else initial
                result.update(hold=hold, place_after=place_after,
                              rise=initial["y"] - end["y"], hp=end["hp"],
                              end={key: end[key] for key in ("x", "y", "vx", "vy")})
                results.append(result)
        assert all(r["status"] == "body_blocks_placement" for r in results if r["place_after"] == 0)
        successes = [r for r in results if r["status"] == "complete" and r["placed"]
                     and r["supported"] and r["hp"] == initial["hp"] and r["rise"] > 0]
        print(json.dumps({"cases": len(results), "safe_raised_landings": len(successes),
                          "examples": successes[:3]}, ensure_ascii=False))
        assert successes, "no safe raised landing after jump-placement"
    finally:
        args.output.write_text(json.dumps({"evidence": str(args.evidence), "tile": args.tile,
                                          "results": results}, ensure_ascii=False))
        worker.close()


if __name__ == "__main__":
    main()
