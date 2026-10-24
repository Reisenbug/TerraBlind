"""Receding-horizon control using the production local planner and actual Player.Update."""

import json
import statistics
import time


def run(worker, root):
    cases = [
        ("flat", {}, 0, True),
        ("ledge_naked", {"terrain": "ledge", "height": 12}, 12, False),
        ("ledge_five_jumps", {"terrain": "ledge", "height": 12, "equipment": [53, 987, 857, 1724, 3201]}, 12, True),
        ("ledge_wings", {"terrain": "ledge", "height": 12, "equipment": [493]}, 12, True),
        ("wall_cloud", {"terrain": "wall", "height": 8, "equipment": [53]}, 0, True),
        ("lava_gap_naked", {"terrain": "lava_gap"}, 0, False),
        ("lava_gap_wings", {"terrain": "lava_gap", "equipment": [493]}, 0, True),
        ("fall_no_recovery", {"airborne": True, "airborne_height": 640}, 0, False),
    ]
    rows = []
    for name, setup, elevation, expected in cases:
        initial = worker.call("reset", **setup)["initial"]
        start = time.monotonic()
        result = worker.call("navigate", x=initial["x"] + 22 * 16, y=initial["y"] - elevation * 16,
                             limit=160 if not expected else 320)
        result.update(name=name, setup=setup, expected=expected, wall_seconds=time.monotonic() - start)
        rows.append(result)
        (root / "navigation.json").write_text(json.dumps(rows, indent=2))
        times = [plan["elapsed_ms"] for plan in result["plans"]]
        print("NAVIGATION_CASE", name, "reached", result["reached"], "frames", len(result["trace"]),
              "median_plan_ms", round(statistics.median(times), 2),
              "median_snapshot_ms", round(statistics.median(plan["snapshot_ms"] for plan in result["plans"]), 2),
              "median_restore_ms", round(statistics.median(plan["timing"]["restore_ms"] for plan in result["plans"]), 2),
              "median_update_ms", round(statistics.median(plan["timing"]["update_ms"] for plan in result["plans"]), 2), flush=True)
        assert result["reached"] == expected, f"{name}: reached={result['reached']} expected={expected}"
        assert result["max_error"] <= .001
        assert all(frame["hp"] >= initial["hp"] and not frame["dead"] for frame in result["trace"])
        if name in ("ledge_five_jumps", "wall_cloud"):
            assert any(frame["available_jumps"] < initial["available_jumps"] for frame in result["trace"]), "Extra jumps were not used"
        if name in ("ledge_wings", "lava_gap_wings"):
            assert any(frame["wing_time"] < initial["wing_time"] for frame in result["trace"]), "Wings were not used"
        if name == "lava_gap_naked":
            assert any(not candidate["safe"] and candidate["hp_loss"] > 0
                       for plan in result["plans"] for candidate in plan["candidates"]), "No harmful rollout was rejected"
        if name == "fall_no_recovery":
            assert not result["trace"] and result["plans"][0]["status"] == "no_safe_candidate"
    print("VANILLA_NAVIGATION_CHECKS_COMPLETE cases=", len(rows), flush=True)
