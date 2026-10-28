"""Compare engine forks against the original live laboratory player, without RL."""

import argparse
import base64
import json
import shutil
import socket
import subprocess
import struct
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


def plan_corridor(worker, state, region, guidance, horizon, beam, root, quantum=6):
    started = time.monotonic()
    result = worker.call("region_plan", snapshot=state, region=region, guidance=guidance,
                        horizon=horizon, beam=beam, quantum=quantum, terrain_motion=True)
    corridor = result.pop("corridor")
    (root / "region-corridor.json").write_text(json.dumps(corridor))
    (root / "region-corridor-attempts.json").write_text(json.dumps(result["attempts"]))
    if result["status"] == "snapshot_required":
        boundary = worker.call("region_boundary_contract", snapshot=state, missing_tile=result["missing_tile"],
                              **result.pop("boundary_request"))
        assert boundary["passed"]
        result["boundary_contract"] = boundary
    result["worker_planning_ms"] = result["planning_wall_ms"]
    result["planning_wall_ms"] = (time.monotonic() - started) * 1000
    return result, corridor, result["require_support"]


def plan_observed_corridor(worker, state, guidance, args, root):
    started = time.monotonic()
    expansions = []
    while True:
        result, corridor, supported = plan_corridor(worker, state, args.region, guidance,
                                                    args.region_horizon, args.region_beam, root, args.region_quantum)
        if result["status"] != "snapshot_required" or not args.region_world:
            break
        expanded = worker.call("expand_snapshot", snapshot=state, missing_tile=result["missing_tile"])
        if expanded["status"] != "expanded":
            result["expansion"] = expanded
            break
        expansions.append(dict(previous_area=state["area"], missing_tile=result["missing_tile"],
                               area=expanded["snapshot"]["area"]))
        state = expanded["snapshot"]
        (root / "region-expanded-snapshot.json").write_text(json.dumps(state))
    if args.region_world:
        import hashlib
        result["world_source"] = dict(path=str(args.region_world),
            sha256=hashlib.sha256(args.region_world.read_bytes()).hexdigest(), expansions=expansions)
    result["planning_wall_ms"] = (time.monotonic() - started) * 1000
    return state, result, corridor, supported


def verify_region(worker, state, result, root):
    replay = worker.call("region_replay", snapshot=state, controls=result["controls"],
                         use_slots=result["use_slots"], placement_tile=result["placement_tile"],
                         mining_tile=result["mining_tile"], use_targets=result["use_targets"],
                         frames=result.get("frames"), anchor=result.get("anchor"))
    assert replay["outcome"] == "complete", replay["outcome"]
    if result.get("frames"):
        assert replay["playback_verified"]
    differences = [key for key in result["end_snapshot"]
                   if replay["end_snapshot"].get(key) != result["end_snapshot"][key]]
    if differences:
        checkpoint_replay = worker.call("region_replay", snapshot=state, controls=result["controls"],
            use_slots=result["use_slots"], use_targets=result["use_targets"], checkpoint_frames=result.get("quantum", 6))
        (root / "region-replay-failure.json").write_text(json.dumps(dict(result=result, replay=replay,
            checkpoint_replay=checkpoint_replay, differences=differences)))
    assert not differences, differences
    result["replay_verified"] = True
    return replay["end_snapshot"]


def run_corridor_chain(worker, state, source, args):
    import math
    data = base64.b64decode(source["guidance"])
    values = struct.unpack("=" + "i" * (len(data) // 4), data)
    field = {(values[i], values[i + 1]): values[i + 2] for i in range(0, len(values), 3)}
    report = dict(status="running", source=str(args.region_probe), region=args.region,
                  quantum=args.region_quantum, beam=args.region_beam,
                  requested_steps=args.region_chain_steps, scope="isolated sequential replay with fixed initial guidance", steps=[])
    (args.root / "region-chain.json").write_text(json.dumps(report))
    for step in range(args.region_chain_steps):
        root = args.root / f"chain-{step:02d}"
        root.mkdir(exist_ok=True)
        state, result, corridor, supported = plan_observed_corridor(worker, state, source["guidance"], args, root)
        summary = dict(step=step, status=result["status"], start_cost=corridor["start_cost"],
                       frames=len(result["controls"]), planning_ms=result["planning_wall_ms"],
                       cleared_count=result.get("cleared_count", 0), require_support=supported,
                       consumed=result.get("consumed", 0), backtracks=result.get("backtracks", 0))
        report["steps"].append(summary)
        (root / "result.json").write_text(json.dumps(result))
        if result["status"] != "reached" or not result["controls"]:
            report["status"] = "stopped"
            break
        state = verify_region(worker, state, result, root)
        player = state["player"]
        assert result["require_support"], "Regional controller must not receive an unfinished airborne segment"
        idle = worker.call("region_replay", snapshot=state, controls=[0] * 6,
                           use_slots=[-1] * 6, use_targets=[[0, 0]] * 6,
                           anchor=result["frames"][-1], frames=[result["frames"][-1]] * 6)
        assert idle["outcome"] == "complete"
        assert idle["playback_verified"], "Handoff anchor changes while idle"
        assert idle["end_snapshot"]["player"]["Terraria.Entity:position"] == player["Terraria.Entity:position"], \
            "Regional handoff drifts while the next request is pending"
        assert sum(segment["frames"] for segment in result["segments"]) == len(result["controls"])
        position = player["Terraria.Entity:position"]
        foot = (math.floor((position["Microsoft.Xna.Framework.Vector2:X"] + player["Terraria.Entity:width"] / 2) / 16),
                math.floor((position["Microsoft.Xna.Framework.Vector2:Y"] + player["Terraria.Entity:height"] - 1) / 16))
        summary["end_cost"] = field.get(foot)
        summary["foot_cell"] = foot
        summary["replay_verified"] = True
        (root / "result.json").write_text(json.dumps(result))
        assert summary["end_cost"] is not None and summary["end_cost"] < summary["start_cost"], summary
        print("CORRIDOR_CHAIN_STEP", summary, flush=True)
        (args.root / "region-chain.json").write_text(json.dumps(report))
    else:
        report["status"] = "steps_completed"
    report["total_frames"] = sum(step["frames"] for step in report["steps"] if step.get("replay_verified"))
    report["total_planning_ms"] = sum(step["planning_ms"] for step in report["steps"])
    (args.root / "region-chain.json").write_text(json.dumps(report))
    print("CORRIDOR_CHAIN_RESULT", report, flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path("/tmp/terrablind-forecast-lab"))
    parser.add_argument("--port", type=int, default=19042)
    parser.add_argument("--case", action="append", default=[])
    parser.add_argument("--navigation", action="store_true")
    parser.add_argument("--replay", type=Path)
    parser.add_argument("--region-probe", type=Path)
    parser.add_argument("--reachability-check", action="store_true")
    parser.add_argument("--reachability-probe", type=Path)
    parser.add_argument("--reachability-baseline", type=Path)
    parser.add_argument("--movement-probe", type=Path)
    parser.add_argument("--movement-baseline", type=Path)
    parser.add_argument("--region", type=int, nargs=4)
    parser.add_argument("--region-horizon", type=int, default=90)
    parser.add_argument("--region-quantum", type=int, choices=(6, 12))
    parser.add_argument("--region-beam", type=int, default=6)
    parser.add_argument("--region-airborne", action="store_true")
    parser.add_argument("--region-seed", type=int)
    parser.add_argument("--region-reference", action="store_true")
    parser.add_argument("--region-surfaces", action="store_true")
    parser.add_argument("--region-surface-seed", type=int)
    parser.add_argument("--region-place", type=int, nargs=2)
    parser.add_argument("--region-mine", type=int, nargs=2)
    parser.add_argument("--region-mine-tile", type=int, nargs=2, action="append")
    parser.add_argument("--region-corridor", action="store_true")
    parser.add_argument("--region-chain-steps", type=int, default=0)
    parser.add_argument("--region-trace-check", action="store_true")
    parser.add_argument("--region-end-probe", type=Path)
    parser.add_argument("--region-world", type=Path)
    parser.add_argument("--obstruction", type=Path)
    parser.add_argument("--terrain-probe", type=Path)
    parser.add_argument("--terrain-replay", type=Path)
    parser.add_argument("--clear-tile", type=int, nargs=2, action="append", default=[])
    parser.add_argument("--replace-tiles", action="store_true")
    parser.add_argument("--settled-probe", type=Path)
    parser.add_argument("--jump-place-probe", type=Path)
    parser.add_argument("--expect-slope-start", action="store_true")
    parser.add_argument("--placement-order-probe", type=Path)
    parser.add_argument("--native-placement-probe", type=Path)
    parser.add_argument("--native-mining-probe", action="store_true")
    parser.add_argument("--feather-probe", action="store_true")
    parser.add_argument("--stalled-jump-probe", type=Path)
    parser.add_argument("--opening-probe", type=Path)
    parser.add_argument("--expect-redundant-opening", action="store_true")
    parser.add_argument("--expect-next-placement", action="store_true")
    parser.add_argument("--ceiling-loop-probe", type=Path)
    parser.add_argument("--platform-anchor-probe", type=Path)
    parser.add_argument("--terminal-progress-probe", type=Path)
    parser.add_argument("--landing-progress-probe", type=Path)
    parser.add_argument("--expect-terrain-planned", action="store_true")
    parser.add_argument("--movement-only", action="store_true")
    parser.add_argument("--route-direction", type=int, choices=(-1, 0, 1))
    parser.add_argument("--reverse-tiles", action="store_true")
    parser.add_argument("--benchmark", type=Path)
    parser.add_argument("--landing-probe", type=Path)
    parser.add_argument("--expect-landing-rejected", action="store_true")
    parser.add_argument("--cancel-probe", type=Path)
    args = parser.parse_args()
    if args.region_quantum is None:
        args.region_quantum = 12 if args.region_corridor or args.region_chain_steps else 6
    args.root.mkdir(parents=True, exist_ok=True)
    (args.root / "Mods").mkdir(exist_ok=True)
    (args.root / "Mods/enabled.json").write_text('["Forecast"]')
    worlds = args.root / "Worlds"
    worlds.mkdir(exist_ok=True)
    world = worlds / "Forecast.wld"
    if args.region_world:
        world = worlds / "RegionSource.wld"
        shutil.copy2(args.region_world, world)
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
    if args.cancel_probe:
        command.append("-forecasttrace")
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
        if args.movement_probe:
            import random
            source = json.loads(args.movement_probe.read_text())["request"]
            state = source["snapshot"]
            if source.get("prefix"):
                state = worker.call("replay", snapshot=state, controls=source["prefix"], capture_end=True)["end_snapshot"]
            position = state["player"]["Terraria.Entity:position"]
            x = position["Microsoft.Xna.Framework.Vector2:X"] + state["player"]["Terraria.Entity:width"] / 2
            y = position["Microsoft.Xna.Framework.Vector2:Y"] + state["player"]["Terraria.Entity:height"]
            a = state["area"]
            left, top = max(a[0] * 16 + 32, int(x) - 192), max(a[1] * 16 + 32, int(y) - 320)
            right, bottom = min((a[0] + a[2]) * 16 - 32, int(x) + 192), min((a[1] + a[3]) * 16 - 32, int(y) + 96)
            surfaces = worker.call("region_surfaces", snapshot=state, region=[left, top, right-left, bottom-top])["surfaces"]
            surfaces = [r for r in surfaces if abs(r[1]-y) > 8 or not r[0]-8 <= x <= r[0]+r[2]+8]
            assert surfaces, "no_real_map_destinations"
            near = sorted(surfaces, key=lambda r: abs(r[0]+r[2]/2-x)+abs(r[1]-y))[:4]
            targets = near + random.Random(79).sample([r for r in surfaces if r not in near], min(3, max(0, len(surfaces)-len(near))))
            output = args.root / ("movement-" + args.movement_probe.stem)
            output.mkdir(exist_ok=True)
            rows = []
            for index, target in enumerate(targets):
                row = dict(region=target)
                plans = [("previous", False, [target]), ("reactive", True, [target])]
                for name, enabled, goals in plans:
                    result = worker.call("region_search", snapshot=state, region=target, horizon=120,
                                         goal_regions=goals, direct=True, terrain_motion=enabled, require_support=True, exclude_start=True)
                    row[name] = {k: result.get(k) for k in ("status", "elapsed_ms", "wall_ms", "timing", "distance", "expanded", "simulated_frames")}
                    (output / f"movement-{index}-{name}.json").write_text(json.dumps(result))
                    if args.movement_baseline:
                        baseline = json.loads((args.movement_baseline / output.name / f"movement-{index}-{name}.json").read_text())
                        for key in ("status", "controls", "end_snapshot"):
                            assert result.get(key) == baseline.get(key), f"movement_changed:{index}:{name}:{key}"
                    if name == "reactive" and result["status"] != "reached":
                        neighbors = [r for r in surfaces if abs(r[1]-target[1]) <= 16
                                     and r[0] <= target[0]+target[2]+24 and r[0]+r[2] >= target[0]-24]
                        plans.append(("nearby", True, neighbors))
                    if result["controls"]:
                        replay = worker.call("region_replay", snapshot=state, **{k: result[k] for k in ("controls", "use_slots", "use_targets")})
                        assert replay["outcome"] == "complete"
                        assert replay["end_snapshot"] == result["end_snapshot"], "movement_replay_mismatch"
                        trace = worker.call("replay", snapshot=state, controls=result["controls"])
                        assert trace["outcome"] == "complete"
                        frames = trace["frames"]
                        distances = [max(target[0]-f["x"]-state["player"]["Terraria.Entity:width"]/2,
                                         f["x"]+state["player"]["Terraria.Entity:width"]/2-target[0]-target[2], 0)**2
                                     + (f["y"]+state["player"]["Terraria.Entity:height"]-target[1])**2 for f in frames]
                        row[name]["closest_frame"] = min(range(len(frames)), key=lambda i: distances[i]) + 1
                        row[name]["closest_distance"] = min(distances)**.5
                        row[name]["extra_jump_frames"] = [i + 1 for i in range(1, len(frames))
                            if frames[i]["available_jumps"] < frames[i-1]["available_jumps"]]
                        (output / f"movement-{index}-{name}-trace.json").write_text(json.dumps(trace))
                rows.append(row)
                print("MOVEMENT_PROBE", row, flush=True)
            (output / "movement-summary.json").write_text(json.dumps(rows))
            assert any(r["reactive"]["status"] == "reached" for r in rows)
            assert all(r["reactive"]["status"] == "reached" for r in rows if r["previous"]["status"] == "reached")
            print("MOVEMENT_PROBE_PASS", len(rows), flush=True)
            return
        if args.reachability_check or args.reachability_probe:
            import random
            cases = []
            if args.reachability_probe:
                source = json.loads(args.reachability_probe.read_text())["request"]
                state = source["snapshot"]
                if source.get("prefix"):
                    state = worker.call("replay", snapshot=state, controls=source["prefix"], capture_end=True)["end_snapshot"]
                cases.append(("map", state))
            else:
                for name, equipment in (("base", []), ("sandstorm", [857])):
                    cases.append((name, worker.call("reset", equipment=equipment)["snapshot"]))
            summaries = []
            for name, state in cases:
                result = worker.call("reachability", snapshot=state, coarse=not bool(args.reachability_baseline))
                (args.root / f"reachability-{name}.json").write_text(json.dumps(result))
                nodes = result["nodes"]
                if args.reachability_baseline:
                    baseline = json.loads((args.reachability_baseline / f"reachability-{name}.json").read_text())
                    for key in ("expanded", "merged", "dropped", "unsafe"):
                        assert result[key] == baseline[key], f"reachability changed: {name}/{key}"
                    assert [{k: v for k, v in n.items() if k != "controls"} for n in nodes] == baseline["nodes"]
                origin = nodes[0]
                assert result["horizon"] == 90
                assert len(result["layers"]) == 90 // result["quantum"]
                for layer in result["layers"]:
                    assert len(layer["nodes"]) <= result["capacity"]
                picks = [min(nodes[1:], key=lambda n: n["y"]), max(nodes[1:], key=lambda n: abs(n["x"] - origin["x"]))]
                picks += random.Random(79).sample(nodes[1:], min(3, len(nodes) - 1))
                refined_node = next((node for node in nodes[1:] if len(set(node["controls"])) > 1), None)
                if refined_node:
                    picks.append(refined_node)
                for node in picks:
                    segments = []
                    current = node
                    while current["parent"] >= 0:
                        segments.append(current["controls"])
                        current = nodes[current["parent"]]
                    controls = [key for segment in reversed(segments) for key in segment]
                    assert len(controls) == node["frame"]
                    replay = worker.call("replay", snapshot=state, controls=controls)
                    assert replay["outcome"] == "complete"
                    last = replay["frames"][-1]
                    assert abs(last["x"] + state["player"]["Terraria.Entity:width"] / 2 - node["x"]) < .001
                    assert abs(last["y"] + state["player"]["Terraria.Entity:height"] - node["y"]) < .001
                    assert last["available_jumps"] == node["jumps"]
                summary = dict(name=name, max_horizontal=max(abs(n["x"] - origin["x"]) for n in nodes),
                               max_rise=origin["y"] - min(n["y"] for n in nodes),
                               elapsed_ms=result["elapsed_ms"], expanded=result["expanded"],
                               dropped=result["dropped"], refined=result["refined"], replayed=len(picks))
                summaries.append(summary)
                print("REACHABILITY_CASE_PASS", summary, flush=True)
            if args.reachability_check:
                assert summaries[1]["max_horizontal"] > summaries[0]["max_horizontal"]
                assert summaries[1]["max_rise"] > summaries[0]["max_rise"]
            (args.root / "reachability-summary.json").write_text(json.dumps(summaries))
            return
        if args.region_end_probe:
            snapshot = json.loads(args.region_end_probe.read_text())["end_snapshot"]
            result = worker.call("settled_probe", snapshot=snapshot, prefix=[])
            (args.root / "region-end-probe.json").write_text(json.dumps(result))
            print("REGION_END_PROBE", {key: value for key, value in result.items() if key != "released"}, flush=True)
            return
        if args.region_probe:
            evidence = json.loads(args.region_probe.read_text())
            source = evidence["request"]
            assert args.region is not None
            state = source["snapshot"]
            if source.get("prefix"):
                state = worker.call("replay", snapshot=state, controls=source["prefix"], capture_end=True)["end_snapshot"]
            if args.region_trace_check:
                reports = []
                baseline = None
                for trace in (True, False, False, True):
                    result = worker.call("region_search", snapshot=state, region=args.region,
                        horizon=args.region_horizon, beam=args.region_beam, quantum=args.region_quantum,
                        require_support=not args.region_airborne, placement_tile=args.region_place,
                        mining_tile=args.region_mine, mining_tiles=args.region_mine_tile, trace_layers=trace)
                    comparable = {key: result[key] for key in ("status", "controls", "use_slots", "use_targets",
                                  "end_snapshot", "expanded", "simulated_frames")}
                    assert result["expanded"] > 0 and result["simulated_frames"] > 0, "trace_check_requires_actual_search"
                    assert not trace or result["layers"], "trace_check_requires_recorded_layers"
                    if baseline is None:
                        baseline = comparable
                    assert comparable == baseline, "trace_setting_changed_search_result"
                    assert trace or not result["layers"]
                    reports.append(dict(trace=trace, elapsed_ms=result["elapsed_ms"],
                        gc_pause_ms=result["timing"]["gc_pause_ms"], expanded=result["expanded"],
                        simulated_frames=result["simulated_frames"], result_bytes=len(json.dumps(result))))
                (args.root / "region-trace-check.json").write_text(json.dumps(reports))
                print("REGION_TRACE_CHECK_PASS", reports, flush=True)
                return
            if args.region_chain_steps:
                if not 1 <= args.region_chain_steps <= 20:
                    raise ValueError("region-chain-steps must be between 1 and 20")
                try:
                    run_corridor_chain(worker, state, source, args)
                except Exception as error:
                    path = args.root / "region-chain.json"
                    report = json.loads(path.read_text()) if path.exists() else {}
                    report.update(status="error", error=str(error))
                    path.write_text(json.dumps(report))
                    raise
                return
            if args.region_seed is not None:
                import random
                rng = random.Random(args.region_seed)
                args.region[0] += rng.randint(-32, 32)
                args.region[1] += rng.randint(-32, 32)
            if args.region_surfaces or args.region_surface_seed is not None:
                result = worker.call("region_surfaces", snapshot=state, region=args.region, validate_samples=True)
                result["test_input"] = dict(source=str(args.region_probe), region=args.region, seed=args.region_seed)
                (args.root / "region-surfaces.json").write_text(json.dumps(result))
                print("REGION_SURFACES", {key: value for key, value in result.items()
                                          if key != "platform_candidates"}, "platform_candidates", len(result["platform_candidates"]), flush=True)
                assert not result["invalid_samples"], result["invalid_samples"]
                if args.region_surfaces:
                    return
                import random
                assert result["surfaces"], "no_existing_support_in_sample_area"
                args.region = random.Random(args.region_surface_seed).choice(result["surfaces"])
            planning_started = time.monotonic()
            corridor = None
            if args.region_reference:
                reference = worker.call("replay", snapshot=state,
                                        controls=evidence["result"]["controls"][:args.region_horizon], capture_end=True)
                (args.root / "region-reference.json").write_text(json.dumps(reference))
                x, y, width, height = args.region
                player_width = state["player"]["Terraria.Entity:width"]
                player_height = state["player"]["Terraria.Entity:height"]
                hits = [i + 1 for i, frame in enumerate(reference["frames"])
                        if x <= frame["x"] + player_width / 2 <= x + width
                        and y <= frame["y"] + player_height <= y + height
                        and (args.region_airborne or frame["vy"] == 0)]
                support = None
                if hits:
                    support = worker.call("settled_probe", snapshot=state,
                                          prefix=evidence["result"]["controls"][:hits[0]])["supported"]
                print("REGION_REFERENCE", dict(outcome=reference["outcome"], hits=hits, supported=support), flush=True)
            search_requires_support = not args.region_airborne
            if args.region_corridor:
                state, result, corridor, search_requires_support = plan_observed_corridor(worker, state,
                    source.get("guidance"), args, args.root)
                print("REGION_CORRIDOR", corridor, flush=True)
            else:
                result = worker.call("region_search", snapshot=state, region=args.region,
                                     horizon=args.region_horizon, beam=args.region_beam,
                                     require_support=not args.region_airborne, placement_tile=args.region_place,
                                     mining_tile=args.region_mine, mining_tiles=args.region_mine_tile, quantum=args.region_quantum)
            result["planning_wall_ms"] = (time.monotonic() - planning_started) * 1000
            result["test_input"] = dict(source=str(args.region_probe), region=args.region,
                                        seed=args.region_seed, surface_seed=args.region_surface_seed,
                                        require_support=search_requires_support)
            if corridor:
                result["test_input"]["corridor"] = corridor
            if result["controls"]:
                verify_region(worker, state, result, args.root)
            if args.region_place and result["status"] == "reached":
                assert result["placed"] and result["consumed"] == 1
            (args.root / "region-search.json").write_text(json.dumps(result))
            print("REGION_SEARCH_RESULT", {key: value for key, value in result.items()
                                          if key not in ("end_snapshot", "controls", "layers", "use_slots", "use_targets", "world_random_indices")}, flush=True)
            return
        if args.native_mining_probe:
            state_contract = worker.call("worker_state_contract")
            assert state_contract["passed"]
            print("WORKER_STATE_REUSE_PASS", state_contract, flush=True)
            result = worker.call("native_mining_probe")
            (args.root / "native-mining.json").write_text(json.dumps(result))
            print("NATIVE_MINING_RESULT", result, flush=True)
            baseline, native, serialized = result["results"]
            assert baseline["broken"] and baseline["first_damage"] > 0, baseline
            for restored in (native, serialized):
                assert restored["broken"] and restored["frames"] == baseline["frames"], restored
                assert restored["restored_damage"] == baseline["first_damage"], restored
            return
        if args.native_placement_probe:
            evidence = json.loads(args.native_placement_probe.read_text())
            request = evidence["request"]
            plan = worker.call(**request)
            assert plan["status"] == "planned", plan
            result = worker.call("placement_sequence", snapshot=request["snapshot"],
                                 tile_x=plan["tile_x"], tile_y=plan["tile_y"],
                                 place_after=plan["place_after"], controls=plan["controls"], native_use=True)
            (args.root / "native-placement.json").write_text(json.dumps(dict(plan=plan, result=result)))
            assert result["status"] == "complete" and result["placed"] and result["supported"], result
            assert result["consumed"] == 1, result["consumed"]
            assert abs(result["frames"][-1]["y"] + request["snapshot"]["player"]["Terraria.Entity:height"]
                       - plan["tile_y"] * 16) < .1
            print("NATIVE_PLACEMENT_PASS", dict(tile=[plan["tile_x"], plan["tile_y"]],
                                               consumed=result["consumed"], elapsed_ms=result["elapsed_ms"]), flush=True)
            return
        if args.placement_order_probe:
            evidence = json.loads(args.placement_order_probe.read_text())
            request, old = evidence["request"], evidence["result"]
            def replay(plan):
                return worker.call("placement_sequence", snapshot=request["snapshot"],
                                   tile_x=plan["tile_x"], tile_y=plan["tile_y"],
                                   place_after=plan["place_after"], controls=plan["controls"])
            assert replay(old)["status"] == "body_blocks_placement"
            plan = worker.call(**request)
            assert plan["status"] == "planned", plan["status"]
            actual = replay(plan)
            assert actual["status"] == "complete" and actual["placed"] and actual["supported"]
            assert abs(actual["frames"][-1]["y"] + 42 - plan["tile_y"] * 16) < .1
            args.placement_order_probe.with_suffix(".order-replay.json").write_text(json.dumps(plan))
            print("PLACEMENT_ORDER_PASS", old["tile_x"], old["tile_y"], "->",
                  plan["tile_x"], plan["tile_y"], "rise", plan["rise"], flush=True)
            return
        if args.landing_progress_probe:
            evidence = json.loads(args.landing_progress_probe.read_text())
            result = worker.call(**evidence["request"])
            old = next(row for row in result["candidates"] if row["name"] == evidence["result"]["selected"])
            assert old["landing_frames"] > 0 and old["score_progress"] <= 0, old
            assert result["status"] == "planned" and result["selected"] != old["name"]
            args.landing_progress_probe.with_suffix(".landing-progress.json").write_text(json.dumps(result))
            print("LANDING_PROGRESS_PASS", old["name"], "->", result["selected"],
                  "old_landed_progress", old["score_progress"], "new_progress", result["route_progress"], flush=True)
            return
        if args.terminal_progress_probe:
            evidence = json.loads(args.terminal_progress_probe.read_text())
            old = evidence["result"]
            result = worker.call(**evidence["request"])
            assert result["status"] == "planned"
            assert any(key & 4 for key in old["controls"])
            assert not any(key & 4 for key in result["controls"]), result["selected"]
            previous = next(row for row in result["candidates"] if row["name"] == old["selected"])
            selected = next(row for row in result["candidates"] if row["name"] == result["selected"])
            old_end = dict(x=previous["score_end_x"], y=previous["score_end_y"])
            end = dict(x=selected["score_end_x"], y=selected["score_end_y"])
            goal = evidence["request"]
            distance = lambda frame: (frame["x"] - goal["x"]) ** 2 + (frame["y"] - goal["y"]) ** 2
            assert distance(end) <= distance(old_end) + .01
            args.terminal_progress_probe.with_suffix(".terminal-replay.json").write_text(json.dumps(result))
            print("TERMINAL_PROGRESS_PASS", old["selected"], "->", result["selected"], flush=True)
            return
        if args.platform_anchor_probe:
            evidence = json.loads(args.platform_anchor_probe.read_text())
            request = evidence["request"]
            bad = evidence["result"]
            anchor = worker.call("platform_anchor", snapshot=request["snapshot"],
                                 tile_x=bad["tile_x"], tile_y=bad["tile_y"])
            assert not anchor["native"] and not anchor["planner"], anchor
            rejected = worker.call(**dict(request, jump_place=True, search_jump_place=False,
                                         tile_x=bad["tile_x"], tile_y=bad["tile_y"]))
            assert rejected["status"] == "unanchored_platform", rejected["status"]
            replacement = worker.call(**request)
            if replacement["status"] == "planned":
                x, y = replacement["tile_x"], replacement["tile_y"]
                anchor = worker.call("platform_anchor", snapshot=request["snapshot"], tile_x=x, tile_y=y)
                assert anchor["native"] and anchor["planner"], anchor
                snapshot = request["snapshot"]
                x0, y0, width, height = snapshot["area"]
                raw = base64.b64decode(snapshot["tiles"])
                tiles = struct.unpack(f"<{len(raw) // 4}i", raw)
                neighbors = [((yy - y0) * width + xx - x0) * 10
                             for xx in range(x - 1, x + 2) for yy in range(y - 1, y + 2)
                             if x0 <= xx < x0 + width and y0 <= yy < y0 + height]
                assert any(tiles[index] or tiles[index + 2] for index in neighbors)
            args.platform_anchor_probe.with_suffix(".anchor-replay.json").write_text(json.dumps(replacement))
            print("PLATFORM_ANCHOR_PASS", rejected["status"], "replacement", replacement["status"],
                  replacement.get("tile_x"), replacement.get("tile_y"), flush=True)
            return
        if args.ceiling_loop_probe:
            evidence = json.loads(args.ceiling_loop_probe.read_text())
            result = worker.call(**evidence["request"])
            args.ceiling_loop_probe.with_suffix(".ceiling-replay.json").write_text(json.dumps(result))
            old_name = evidence["result"]["selected"]
            old_candidate = next(candidate for candidate in result["candidates"] if candidate["name"] == old_name)
            assert old_candidate["repeated_ceiling_contact"] and not old_candidate["safe"], old_candidate
            assert result["status"] == "planned" and result["selected"] != old_name, result["selected"]
            selected = next(candidate for candidate in result["candidates"] if candidate["name"] == result["selected"])
            assert selected["safe"] and not selected["repeated_ceiling_contact"]
            print("CEILING_LOOP_PASS", old_name, "->", result["selected"], flush=True)
            return
        if args.opening_probe:
            source = json.loads(args.opening_probe.read_text())["request"]
            settled = worker.call("replay", snapshot=source["snapshot"], controls=[0] * 90, capture_end=True)
            assert settled["outcome"] == "complete"
            assert len(args.clear_tile) >= 2
            request = dict(snapshot=settled["end_snapshot"], tile_type=-1, wait_frames=24,
                           x=source["x"], y=source["y"], guidance=source["guidance"],
                           route_direction=source["route_direction"])
            results = []
            for cells in [[]] + [[cell] for cell in args.clear_tile] + [args.clear_tile]:
                first = cells[0] if cells else args.clear_tile[0]
                plan = worker.call("terrain_plan", **request, tile_x=first[0], tile_y=first[1], tiles=cells)
                results.append(dict(tiles=cells, result=plan))
                print("OPENING_PROBE", cells, plan["status"], plan.get("route_progress"), flush=True)
            args.opening_probe.with_suffix(".opening.json").write_text(json.dumps(results))
            final = results[-1]["result"]
            assert final["status"] == "planned"
            if args.expect_next_placement:
                assert all(row["result"]["status"] != "planned" for row in results[:-1])
                x, y = final["next_jump_place"]
                standing = worker.call("replay", snapshot=final["context"],
                                       controls=[0] * final["wait_frames"], capture_end=True)
                jump = worker.call("terrain_plan", snapshot=standing["end_snapshot"],
                                   tile_x=x, tile_y=y, tile_type=19, jump_place=True)
                assert jump["status"] == "planned" and jump["rise"] > 0
                print("OPENING_PLACEMENT_PASS", jump["rise"], flush=True)
                return
            assert final["route_progress"] > 0
            if args.expect_redundant_opening:
                assert all(row["result"]["route_progress"] == results[0]["result"]["route_progress"]
                           and row["result"]["controls"] == results[0]["result"]["controls"] for row in results[1:])
            else:
                assert results[-1]["result"]["route_progress"] > results[0]["result"].get("route_progress", 0)
            return
        if args.stalled_jump_probe:
            evidence = json.loads(args.stalled_jump_probe.read_text())
            old = evidence["result"]
            assert old["route_progress"] <= 0 and any(key & 4 for key in old["controls"])
            result = worker.call(**evidence["request"])
            args.stalled_jump_probe.with_suffix(".stalled-replay.json").write_text(json.dumps(result))
            assert result["status"] == "planned" and result["selected"] == "wait", result["selected"]
            assert all(key == 0 for key in result["controls"]), result["controls"]
            assert result["route_progress"] >= old["route_progress"] - .01
            hp = result["anchor"]["hp"]
            assert all(not frame["dead"] and frame["hp"] >= hp for frame in result["forecast"]["frames"])
            print("STALLED_JUMP_PASS", args.stalled_jump_probe.name,
                  old["selected"], "->", result["selected"], "progress", result["route_progress"], flush=True)
            return
        if args.feather_probe:
            initial = worker.call("reset", airborne=True, airborne_height=320, buffs=[[8, 600]])
            snapshot = initial["snapshot"]
            coast = worker.call("replay", snapshot=snapshot, controls=[2] * 180)
            hover = worker.call("replay", snapshot=snapshot, controls=[10] * 180)
            assert coast["outcome"] == hover["outcome"] == "complete"
            a, b = coast["frames"][-1], hover["frames"][-1]
            assert b["y"] < a["y"] - 16, (a["y"], b["y"])
            plan = worker.call("plan", snapshot=snapshot, prefix=[0],
                               x=initial["initial"]["x"] + 500, y=initial["initial"]["y"] + 40)
            report = dict(coast_end=a, hover_end=b, selected=plan.get("selected"), plan=plan)
            (args.root / "feather-probe.json").write_text(json.dumps(report))
            assert plan["status"] == "planned" and plan["selected"].startswith("feather_"), plan.get("selected")
            snapshot["navigation"] = True
            x0, y0, width, height = snapshot["area"]
            gx = int((initial["initial"]["x"] + 510) // 16)
            gy = int((initial["initial"]["y"] + 82) // 16)
            cells = [value for x in range(x0, x0 + width) for y in range(y0, y0 + height)
                     for value in (x, y, abs(x - gx) + abs(y - gy))]
            guidance = base64.b64encode(struct.pack(f"<{len(cells)}i", *cells)).decode()
            transit = worker.call("plan", snapshot=snapshot, prefix=[0], guidance=guidance,
                                  x=gx * 16 - 10, y=gy * 16 - 42, route_direction=1)
            report["transit"] = transit
            (args.root / "feather-probe.json").write_text(json.dumps(report))
            glide = next(candidate for candidate in transit["candidates"] if candidate["name"] == "feather_glide")
            assert transit["status"] == "planned" and glide["safe"] and glide["field_progress"] > 0, glide
            assert glide["coast_hp_loss"] == 0 and glide["coast_ms"] > 0, glide
            print("FEATHER_CONTROL_PASS", {"coast_y": a["y"], "hover_y": b["y"],
                  "selected": plan["selected"], "transit_selected": transit["selected"],
                  "transit_glide_progress": glide["field_progress"]}, flush=True)
            return
        if args.jump_place_probe:
            capture = json.loads(args.jump_place_probe.read_text())
            snapshot = capture.get("request", capture)["snapshot"]
            settle = worker.call("replay", snapshot=snapshot, controls=[0] * 90, capture_end=True)
            assert settle["outcome"] == "complete", settle["outcome"]
            snapshot = settle["end_snapshot"]
            end = settle["frames"][-1]
            support = worker.call("settled_probe", snapshot=snapshot, prefix=[])
            print("JUMP_START_SUPPORT", {key: support[key] for key in ("supported", "contact_supported")}, flush=True)
            if args.expect_slope_start:
                assert support["supported"] and not support["contact_supported"]
            x, y = int((end["x"] + 10) // 16), int((end["y"] + 42) // 16) - 1
            request = dict(snapshot=snapshot, jump_place=True, tile_x=x, tile_y=y, tile_type=19)
            if args.route_direction is not None:
                request["route_direction"] = args.route_direction
            held = json.loads(json.dumps(snapshot))
            held["player"]["Terraria.Player:releaseJump"] = False
            rejected = worker.call("terrain_plan", **dict(request, snapshot=held), search_jump_place=True)
            assert rejected["status"] == "unsafe_jump_place_start" and not rejected["start"]["release_jump"]
            old = worker.call("terrain_plan", **request)
            plan = worker.call("terrain_plan", **request, search_jump_place=True)
            assert plan["status"] == "planned", (old["status"], plan["status"], end)
            if args.expect_slope_start:
                assert plan["rise"] >= old.get("rise", 0) and plan["rise"] > 0
            else:
                assert plan["rise"] > old.get("rise", 0), (old.get("rise"), plan["rise"])
            replay = worker.call("placement_sequence", snapshot=snapshot,
                                 tile_x=plan["tile_x"], tile_y=plan["tile_y"],
                                 place_after=plan["place_after"], controls=plan["controls"])
            assert replay["status"] == "complete" and replay["placed"] and replay["supported"], replay["status"]
            assert all(not frame["dead"] and frame["hp"] >= end["hp"] for frame in replay["frames"])
            assert abs(replay["frames"][-1]["y"] + 42 - plan["tile_y"] * 16) < .1
            report = dict(baseline_status=old["status"], baseline_rise=old.get("rise", 0), rise=plan["rise"],
                          tile=[plan["tile_x"], plan["tile_y"]],
                          elapsed_ms=plan["terrain_timing"]["total_ms"], plan=plan, replay=replay)
            args.jump_place_probe.with_suffix(".jump-place.json").write_text(json.dumps(report))
            print("REAL_JUMP_PLACE_PASS", {k: v for k, v in report.items() if k not in ("plan", "replay")}, flush=True)
            return
        if args.settled_probe:
            request = json.loads(args.settled_probe.read_text())["request"]
            result = worker.call("settled_probe", snapshot=request["snapshot"], prefix=request["prefix"])
            plan = worker.call(**request)
            result["selected"] = plan["selected"]
            assert plan["selected"] == "wait" and plan["route_progress"] <= 0, plan["selected"]
            args.settled_probe.with_suffix(".settlement.json").write_text(json.dumps(result))
            print("REAL_SETTLEMENT_PROBE", {"supported": result["supported"],
                  "before": result["state"], "released_end": result["released"][-1]}, flush=True)
            return
        if args.terrain_replay:
            capture = json.loads(args.terrain_replay.read_text())
            if args.route_direction is not None:
                capture["request"]["route_direction"] = args.route_direction
            if args.movement_only:
                request = capture["request"]
                result = worker.call("plan", snapshot=request["snapshot"], prefix=[0],
                                     x=request["x"], y=request["y"], guidance=request["guidance"],
                                     route_direction=request["route_direction"])
                args.terrain_replay.with_suffix(".movement.json").write_text(json.dumps(result))
                print("MOVEMENT_REPLAY", result["status"], result.get("selected"), result.get("route_progress"), flush=True)
                if args.expect_terrain_planned:
                    assert result["status"] == "planned" and result["route_progress"] > 0
                    hp = request["snapshot"]["player"]["Terraria.Player:statLife"]
                    assert all(not frame["dead"] and frame["hp"] >= hp for frame in result["forecast"]["frames"])
                return
            results = []
            for attempt in capture["attempts"]:
                request = dict(capture["request"], tile_x=attempt["x"], tile_y=attempt["y"],
                               tile_type=attempt["type"], tiles=attempt["tiles"])
                if args.clear_tile:
                    request.update(tile_type=-1, tiles=args.clear_tile +
                                   (attempt["tiles"] if attempt["type"] < 0 and not args.replace_tiles else []))
                if args.reverse_tiles:
                    request["tiles"] = list(reversed(request["tiles"]))
                result = worker.call(**request)
                if args.expect_terrain_planned:
                    assert result["status"] == "planned", result
                    initial_hp = request["snapshot"]["player"]["Terraria.Player:statLife"]
                    assert all(not frame["dead"] and frame["hp"] >= initial_hp for frame in result["forecast"]["frames"])
                results.append(dict(x=attempt["x"], y=attempt["y"], type=attempt["type"], result=result))
                print("TERRAIN_REPLAY", attempt["x"], attempt["y"], result["status"], flush=True)
            args.terrain_replay.with_suffix(".replayed.json").write_text(json.dumps(results))
            return
        if args.cancel_probe:
            request = json.loads(args.cancel_probe.read_text())["request"]
            baseline = worker.call(**request)
            latencies = []
            for request_id in range(100, 104):
                request["request_id"] = request_id
                worker.stream.write(json.dumps(request) + "\n")
                worker.stream.flush()
                deadline = time.monotonic() + 5
                while f"FORECAST_REQUEST_STARTED {request_id}" not in (args.root / "engine.log").read_text():
                    if time.monotonic() > deadline:
                        raise TimeoutError("Cancellation test request did not start")
                    time.sleep(.005)
                time.sleep(.05)
                started = time.perf_counter()
                worker.stream.write(json.dumps(dict(command="cancel", request_id=request_id)) + "\n")
                worker.stream.flush()
                cancelled = json.loads(worker.stream.readline())
                elapsed = (time.perf_counter() - started) * 1000
                assert cancelled["status"] == "cancelled" and cancelled["request_id"] == request_id, cancelled
                assert elapsed < 500, elapsed
                latencies.append(elapsed)
                resumed = worker.call(**{key: value for key, value in request.items() if key != "request_id"})
                assert resumed["controls"] == baseline["controls"]
                assert resumed["forecast"]["frames"] == baseline["forecast"]["frames"]
            print("CANCELLATION_CONTRACT_PASS", dict(latency_ms=latencies, restored=True), flush=True)
            return
        if args.landing_probe:
            result = worker.call("landing_probe", plan=json.loads(args.landing_probe.read_text()),
                                 expect_rejected=args.expect_landing_rejected)
            print("LANDING_PROBE", result, flush=True)
            return
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
        reuse = worker.call("route_reuse_contract")
        assert reuse["passed"]
        print("ROUTE_REUSE_CONTRACT_PASS", reuse, flush=True)
        cutoff = worker.call("damage_cutoff_contract")
        assert cutoff["passed"]
        print("DAMAGE_CUTOFF_CONTRACT_PASS", cutoff, flush=True)
        landing = worker.call("landing_probe", expect_rejected=True)
        assert landing["candidate_rejected"] and landing["safe_extra_jump"]
        print("LANDING_SETTLEMENT_CONTRACT_PASS", landing, flush=True)
        water_fall = worker.call("water_fall_contract")
        assert water_fall["passed"]
        print("WATER_FALL_CONTRACT_PASS", water_fall, flush=True)
        transit = worker.call("transit_contract")
        assert transit["passed"]
        print("TRANSIT_CONTINUITY_CONTRACT_PASS", transit, flush=True)
        descent_region = worker.call("contact_descent_region")
        assert descent_region["passed"] and descent_region["cases"] == 12
        print("CONTACT_DESCENT_REGION_PASS", descent_region, flush=True)
        branch = worker.call("worker_state_contract")
        assert branch["passed"] and branch["changed_tiles"] == 2
        print("WORKER_STATE_CONTRACT_PASS", branch, flush=True)
        slopes = worker.call("slope_body_contract")
        assert slopes["passed"] and slopes["cases"] == 16
        print("SLOPE_BODY_CONTRACT_PASS", slopes, flush=True)
        refresh = worker.call("refresh_future_terrain_contract")
        assert refresh["passed"]
        print("REFRESH_FUTURE_TERRAIN_CONTRACT_PASS", refresh, flush=True)
        progress = worker.call("region_progress_contract")
        assert progress["passed"]
        print("REGION_PROGRESS_CONTRACT_PASS", progress, flush=True)
        expansion = worker.call("snapshot_expansion_contract")
        assert expansion["passed"]
        print("SNAPSHOT_EXPANSION_CONTRACT_PASS", expansion, flush=True)
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
