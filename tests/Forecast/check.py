"""Compare engine forks against the original live laboratory player, without RL."""

import argparse
import json
import shutil
import socket
import subprocess
import time
from pathlib import Path


class Worker:
    def __init__(self, port):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=.5)
        self.socket.settimeout(120)
        self.socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.stream = self.socket.makefile("rw", buffering=1)

    def call(self, command, **fields):
        self.stream.write(json.dumps(dict(command=command, **fields)) + "\n")
        self.stream.flush()
        line = self.stream.readline()
        if not line:
            raise RuntimeError("Forecast worker disconnected")
        result = json.loads(line)
        if "error" in result:
            raise RuntimeError(result["error"])
        return result

    def close(self):
        try:
            self.call("close")
        except (OSError, RuntimeError):
            pass
        finally:
            self.stream.close()
            self.socket.close()


def compare(expected, actual):
    if len(expected) != len(actual):
        raise AssertionError(f"Frame count mismatch: {len(expected)} != {len(actual)}")
    maximum = 0
    for frame, (a, b) in enumerate(zip(expected, actual)):
        for key in ("x", "y", "vx", "vy", "wing_time", "grav_dir", "hp", "jump", "dash_delay", "available_jumps"):
            error = abs(a[key] - b[key])
            maximum = max(maximum, error)
            if error > .001:
                raise AssertionError(f"Frame {frame} {key}: predicted={a[key]} actual={b[key]} error={error}")
        for key in ("dead", "wet", "honey_wet", "frozen", "webbed", "control_right", "control_jump", "buff_types", "buff_times", "extra_jumps"):
            if a[key] != b[key]:
                raise AssertionError(f"Frame {frame} {key}: predicted={a[key]} actual={b[key]}")
    return maximum


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path("/tmp/terrablind-forecast-lab"))
    parser.add_argument("--port", type=int, default=19042)
    parser.add_argument("--case", action="append", default=[])
    parser.add_argument("--navigation", action="store_true")
    parser.add_argument("--replay", type=Path)
    parser.add_argument("--obstruction", type=Path)
    parser.add_argument("--terrain-probe", type=Path)
    parser.add_argument("--benchmark", type=Path)
    args = parser.parse_args()
    args.root.mkdir(parents=True, exist_ok=True)
    (args.root / "Mods").mkdir(exist_ok=True)
    (args.root / "Mods/enabled.json").write_text('["Forecast"]')
    worlds = args.root / "Worlds"
    worlds.mkdir(exist_ok=True)
    world = worlds / "Forecast.wld"
    if not world.exists() and (worlds / "World.wld").exists():
        world = worlds / "World.wld"
    if not world.exists():
        previous = Path("/tmp/terrablind-rl-lab/Worlds/World.wld")
        if previous.exists():
            shutil.copy2(previous, world)
    tml = Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader"
    command = [str(tml / "dotnet_arm64/dotnet"), "tModLoader.dll", "-server", "-nosteam",
               "-tmlsavedirectory", str(args.root), "-world", str(world), "-autocreate", "1",
               "-port", "17978", "-vanillaforecastport", str(args.port)]
    log = (args.root / "engine.log").open("w")
    process = subprocess.Popen(command, cwd=tml, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
    worker = None
    try:
        until = time.monotonic() + 240
        while time.monotonic() < until:
            if process.poll() is not None:
                raise RuntimeError(f"Engine exited {process.returncode}; inspect engine.log")
            try:
                worker = Worker(args.port)
                break
            except OSError:
                time.sleep(.25)
        else:
            raise TimeoutError("Worker startup timed out")
        if args.benchmark:
            request = json.loads(args.benchmark.read_text())["request"]
            results = []
            for run in range(10):
                result = worker.call(**request)
                results.append(result)
                print("PLANNER_BENCHMARK", "run=", run, "compute_ms=", result.get("request_compute_ms"),
                      "selected=", result.get("selected"), "progress=", result.get("route_progress"), flush=True)
            (args.root / "planner-benchmark.json").write_text(json.dumps(results))
            return
        if args.terrain_probe:
            request = json.loads(args.terrain_probe.read_text())["request"]
            request["command"] = "terrain_probe"
            result = worker.call(**request)
            (args.root / "terrain-probe.json").write_text(json.dumps(result))
            for row in result["results"]:
                plan = row["result"]
                print("TERRAIN_PROBE", row["x"], row["y"], "wait=", row["wait"],
                      "clear=", row["clear"],
                      "status=", plan.get("status"), "progress=", plan.get("route_progress"), flush=True)
            return
        if args.obstruction:
            request = json.loads(args.obstruction.read_text())["request"]
            original_continuation = request.get("continuation", [])
            results = []
            for direction in (-1, 1):
                request["route_direction"] = direction
                key = 2 if direction > 0 else 1
                variants = [("original", original_continuation)]
                for delay in (12, 30, 48):
                    variants.append((f"turn_{delay}", [3 - key] * delay + [key] * (180 - delay)))
                    variants.append((f"lift_{delay}", [4] * delay + [key | 4] * (180 - delay)))
                variants.append(("brake", [key] * 36 + [0] * 144))
                for name, controls in variants:
                    request["continuation"] = controls
                    result = worker.call(**request)
                    frames = (result.get("forecast") or {}).get("frames", [])
                    results.append(dict(direction=direction, variant=name, result=result))
                    print("OBSTRUCTION_DIRECTION", direction, name, "selected=", result.get("selected"),
                          "progress=", result.get("route_progress"), "end=",
                          {key: frames[-1][key] for key in ("x", "y", "hp")} if frames else None, flush=True)
            (args.root / "obstruction-probe.json").write_text(json.dumps(results))
            return
        if args.replay:
            report = json.loads(args.replay.read_text())
            request = report["request"]
            expected = worker.call("replay", snapshot=request["snapshot"], controls=request["prefix"])
            start = report["handoff"] - len(request["prefix"])
            for actual in report["observed"]:
                index = actual["tick"] - start - 1
                if not 0 <= index < len(expected["frames"]):
                    continue
                predicted = expected["frames"][index]
                differences = {key: [predicted[key], actual[key]] for key in
                               ("x", "y", "vx", "vy", "direction", "keys", "use_item", "release_jump", "jump", "available_jumps", "item_animation")
                               if predicted[key] != actual[key]}
                if differences:
                    row = dict(index=index, differences=differences, predicted=predicted, actual=actual)
                    (args.root / "replay-result.json").write_text(json.dumps(row, indent=2))
                    print("FIRST_REPLAY_DIFFERENCE", index, differences, flush=True)
                    return
            print("REPLAY_PREFIX_MATCHED", flush=True)
            return
        if args.navigation:
            from navigation import run
            run(worker, args.root)
            return
        cleanup = worker.call("async_cleanup")
        assert cleanup["passed"]
        print("ASYNC_REJECTION_CLEANUP_CONTRACT_PASS", flush=True)
        cutoff = worker.call("damage_cutoff_contract")
        assert cutoff["passed"]
        print("DAMAGE_CUTOFF_CONTRACT_PASS", cutoff, flush=True)
        transit = worker.call("transit_contract")
        assert transit["passed"]
        print("TRANSIT_CONTINUITY_CONTRACT_PASS", transit, flush=True)
        terrain = worker.call("terrain_contract")
        assert terrain["passed"]
        print("TERRAIN_NATIVE_CONTROLS_CONTRACT_PASS", terrain, flush=True)
        shelf = worker.call("falling_shelf_contract")
        assert shelf["passed"]
        print("FALLING_SHELF_CONTRACT_PASS", shelf, flush=True)
        controls = [6] * 18 + [2] * 3 + [6] * 20 + [2] * 31
        repeated_jumps = ([6] * 12 + [2] * 2) * 7 + [2] * 2
        cases = [
            ("flat", {}), ("half_bricks", {"terrain": "half"}), ("slope", {"terrain": "slope"}),
            ("head_collision", {"terrain": "ceiling"}), ("honey_liquid", {"terrain": "honey"}),
            ("honey_block", {"terrain": "honey_block"}),
            ("embedded", {"terrain": "embedded"}),
            ("cloud", {"equipment": [53]}), ("blizzard", {"equipment": [987]}),
            ("sandstorm", {"equipment": [857]}), ("fart", {"equipment": [1724]}),
            ("tsunami", {"equipment": [3201]}),
            ("five_extra_jumps", {"equipment": [53, 987, 857, 1724, 3201]}),
            ("balloon_cloud", {"equipment": [53, 159]}), ("angel_wings", {"equipment": [493]}),
            ("leaf_wings", {"equipment": [1162]}), ("featherfall", {"airborne": True, "buffs": [[8, 600]]}),
            ("featherfall_expires", {"airborne": True, "buffs": [[8, 12]]}),
            ("gravitation", {"buffs": [[18, 600]]}), ("slow", {"buffs": [[32, 600]]}),
            ("chilled", {"buffs": [[46, 600]]}), ("webbed", {"buffs": [[149, 20]]}),
            ("frozen", {"buffs": [[47, 20]]}), ("tabi_dash", {"equipment": [977]}),
            ("cloud_mid_jump", {"equipment": [53], "pre_controls": [6] * 18 + [2] * 3 + [6] * 3}),
            ("wings_mid_flight", {"equipment": [493], "pre_controls": [6] * 30}),
            ("five_jump_baseline", {}),
            ("fall_damage", {"airborne": True, "airborne_height": 640}),
            ("sandstorm_fall_recovery", {"airborne": True, "airborne_height": 640, "equipment": [857]}),
            ("featherfall_landing", {"airborne": True, "airborne_height": 640, "buffs": [[8, 600]]}),
        ]
        results = []
        for name, setup in cases:
            if args.case and name not in args.case:
                continue
            initial = worker.call("reset", **setup)
            sequence = controls
            if name in ("five_extra_jumps", "five_jump_baseline"):
                sequence = repeated_jumps
            if name in ("half_bricks", "slope", "embedded"):
                sequence = [2] * 72
            if name in ("fall_damage", "featherfall_landing"):
                sequence = [0] * 120
            if name == "sandstorm_fall_recovery":
                sequence = [0] * 48 + [4] * 6 + [0] * 66
            if name == "gravitation":
                sequence = [8] + [0] * 25 + [8] + [0] * 45
            if name == "tabi_dash":
                sequence = [2] * 2 + [0] * 2 + [2] * 68
            prediction = worker.call("predict", controls=sequence)
            alternate = worker.call("predict", controls=[1] * len(sequence))
            repeat = worker.call("predict", controls=sequence)
            if prediction["frames"] == alternate["frames"]:
                raise AssertionError(f"{name} alternative controls had no effect")
            if prediction["outcome"] != "complete" or alternate["outcome"] != "complete":
                raise AssertionError(f"{name} prediction left known world or died: {prediction['outcome']} {alternate['outcome']}")
            if not all(result["source_unchanged"] and result["checkpoint_restored"] for result in (prediction, alternate, repeat)):
                raise AssertionError(f"{name} fork mutated its source or failed to restore its checkpoint")
            compare(prediction["frames"], repeat["frames"])
            actual = worker.call("actual", controls=sequence)
            error = compare(prediction["frames"], actual["frames"])
            if name == "sandstorm_fall_recovery":
                assert min(frame["hp"] for frame in actual["frames"]) == initial["initial"]["hp"]
                assert any(frame["available_jumps"] < initial["initial"]["available_jumps"] for frame in actual["frames"])
                assert any(frame["grounded"] for frame in actual["frames"]), "Recovery did not land"
            if name in ("wings_mid_flight", "five_extra_jumps"):
                worker.call("load", snapshot=initial["snapshot"])
                transferred = worker.call("predict", controls=sequence)
                compare(prediction["frames"], transferred["frames"])
            row = dict(name=name, frames=len(sequence), max_error=error,
                       forecast_ms=prediction["elapsed_ms"], initial=initial["initial"],
                       final=actual["frames"][-1], trace=prediction["frames"])
            results.append(row)
            print("FORECAST_CASE_PASS", name, "frames=", len(sequence), "error=", error, flush=True)
        (args.root / "traces.json").write_text(json.dumps(results, indent=2))
        if args.case:
            if len(results) != len(set(args.case)):
                raise AssertionError("Unknown case name")
            print("VANILLA_FORECAST_SUBSET_COMPLETE cases=", len(results), flush=True)
            return
        by_name = {row["name"]: row for row in results}
        def trace(name):
            return by_name[name]["trace"]
        def height(name):
            return by_name[name]["initial"]["y"] - min(frame["y"] for frame in trace(name))
        assert height("cloud") > height("flat") + 10, "Cloud jump was never exercised"
        assert height("five_extra_jumps") > height("five_jump_baseline") + 80, "Multiple extra jumps were never exercised"
        jump_fields = "Terraria.DataStructures.ExtraJumpState:"
        assert sum(jump[jump_fields + "_enabled"] and not jump[jump_fields + "_available"]
                   for jump in trace("five_extra_jumps")[90]["extra_jumps"]) == 5, "Not all five extra jumps were consumed"
        assert any(frame["wing_time"] < by_name["angel_wings"]["initial"]["wing_time"] for frame in trace("angel_wings")), "Wing flight was never exercised"
        assert any(frame["grav_dir"] == -1 for frame in trace("gravitation")), "Gravity never inverted"
        assert any(frame["honey_wet"] for frame in trace("honey_liquid")), "Honey collision was never exercised"
        assert 8 not in by_name["featherfall_expires"]["final"]["buff_types"], "Buff expiration was never exercised"
        assert max(abs(frame["vx"]) for frame in trace("tabi_dash")) > 8, "Dash never activated"
        assert height("head_collision") < height("flat"), "Ceiling never affected movement"
        assert 7.9 <= height("half_bricks") <= 8.1, "Half-brick step was never exercised"
        assert height("honey_block") < height("flat") / 2, "Honey-block movement penalty was never exercised"
        assert all(not frame["control_right"] and abs(frame["vx"]) < .001 for frame in trace("frozen")[1:10]), "Frozen did not prevent input"
        assert by_name["fall_damage"]["final"]["hp"] < by_name["fall_damage"]["initial"]["hp"], "Fall damage was never exercised"
        assert by_name["featherfall_landing"]["final"]["hp"] == by_name["featherfall_landing"]["initial"]["hp"], "Featherfall failed to prevent damage"
        initial = worker.call("reset")
        expected = worker.call("predict", controls=[2] * 48)
        invalid = [("predict", dict(controls=[]), "horizon_must_be_1_to_240"),
                   ("predict", dict(controls=[2] * 241), "horizon_must_be_1_to_240"),
                   ("predict", dict(controls=[3]), "invalid_keys"),
                   ("predict", dict(controls=[32]), "invalid_keys"),
                   ("load", dict(snapshot=dict(initial["snapshot"], engine="different-engine")), "engine_mismatch")]
        for command, fields, message in invalid:
            try:
                worker.call(command, **fields)
            except RuntimeError as error:
                if message not in str(error):
                    raise
            else:
                raise AssertionError(f"Invalid request accepted: {message}")
            after_error = worker.call("predict", controls=[2] * 48)
            compare(expected["frames"], after_error["frames"])
            assert after_error["source_unchanged"] and after_error["checkpoint_restored"]
        summary = dict(cases=len(results), results=results, engine=initial["snapshot"]["engine"],
                       rejected_requests=len(invalid),
                       omitted_player_references=initial["snapshot"]["omitted_player_references"],
                       scope="isolated Player.Update fork versus original laboratory player; not client acceptance")
        (args.root / "result.json").write_text(json.dumps(summary, indent=2))
        print("VANILLA_FORECAST_CHECKS_COMPLETE cases=", len(results), flush=True)
    finally:
        try:
            if worker is not None:
                worker.close()
        finally:
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.terminate()
                process.wait(timeout=10)
            log.close()


if __name__ == "__main__":
    main()
