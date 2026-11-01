"""Exercise the shipped /vnav entry with the complete mod in a disposable graphical world."""

import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time


def main():
    routed = "--route" in sys.argv
    worker_check = "--worker-startup" in sys.argv
    focused = any(flag in sys.argv for flag in ("--preview-isolation", "--bridge-recovery", "--pillar-probe", "--snapshot-check", "--slope-stall", "--survival"))
    unlimited = "--no-time-limit" in sys.argv
    continuity = "--continuity" in sys.argv
    circuit = "--circuit" in sys.argv
    journey = "--journey" in sys.argv
    recorded_goal = "--recorded-goal" in sys.argv
    if recorded_goal and ("--baseline" not in sys.argv or journey or continuity or circuit):
        raise ValueError("--recorded-goal requires --baseline and a single route")
    continuity |= circuit or journey
    descent = "--descent" in sys.argv or continuity or recorded_goal or "--readme-route" in sys.argv
    root = Path("/tmp/terrablind-continuity-check" if continuity else "/tmp/terrablind-descent-check" if descent else "/tmp/terrablind-route-check" if routed else "/tmp/terrablind-demo-app")
    if "--root" in sys.argv:
        root = Path(sys.argv[sys.argv.index("--root") + 1]).resolve()
        if not (root.is_relative_to("/tmp") or root.is_relative_to("/private/tmp")):
            raise ValueError("Test root must be temporary")
    (root / "Worlds").mkdir(parents=True, exist_ok=True)
    source_world = Path("/tmp/terrablind-forecast-lab/Worlds/Forecast.wld")
    if not source_world.exists() and (source_world.parent / "World.wld").exists():
        source_world = source_world.parent / "World.wld"
    if descent:
        source_world = Path("/tmp/terrablind-descent-world/Worlds/World.wld")
        if not source_world.exists():
            raise RuntimeError("Generate the fixed-seed world with tests/Demo/world.py first")
    if not source_world.exists():
        raise RuntimeError(f"Prepare the test world before launching a client: {source_world}")
    shutil.copy2(source_world, root / "Worlds/Demo.wld")
    (root / "Mods").mkdir(parents=True, exist_ok=True)
    (root / "Mods/enabled.json").write_text('["TerraBlind","Demo"]')
    user_root = Path.home() / "Library/Application Support/Terraria/tModLoader"
    manual = "--manual" in sys.argv
    user_config = user_root / "config.json"
    settings = json.loads(user_config.read_text()) if user_config.exists() else {}
    settings["CloudSavingDefault"] = False
    (root / "config.json").write_text(json.dumps(settings))
    if (user_root / "input profiles.json").exists():
        shutil.copy2(user_root / "input profiles.json", root / "input profiles.json")
    if "--prepare-only" in sys.argv:
        print("PREPARED_TEST_ROOT", root, flush=True)
        return
    for name in ("TerraBlind.tmod", "Demo.tmod"):
        if not (root / "Mods" / name).exists():
            raise RuntimeError(f"Missing staged test mod: {root / 'Mods' / name}")
    (root / "demo-result.json").unlink(missing_ok=True)
    tml = Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader"
    show = "--show" in sys.argv or manual
    command = [str(tml / "dotnet_arm64/dotnet"), str(tml / "tModLoader.dll"), "-nosteam",
               "-tmlsavedirectory", str(root), "-vnavdescentcheck" if descent else "-vnavdemocheck"]
    if show:
        command.append("-vnavshow")
    if "--trace" in sys.argv:
        command.append("-vnavtrace")
    if "--keep-open" in sys.argv:
        command.append("-vnavkeepopen")
    if manual:
        command.append("-vnavmanualcheck")
        if "--autostart" in sys.argv:
            command.append("-vnavmanualstart")
    if "--wings" in sys.argv:
        command.append("-vnavwingsonly")
    if routed:
        command.append("-vnavroutecheck")
    if "--recording" in sys.argv:
        command.append("-navrecordcheck")
    if "--baseline" in sys.argv:
        command.extend(["-vnavbaseline", sys.argv[sys.argv.index("--baseline") + 1]])
    if recorded_goal:
        command.append("-vnavrecordedgoal")
    if "--snapshot" in sys.argv:
        command.extend(["-vnavsnapshot", sys.argv[sys.argv.index("--snapshot") + 1]])
    if continuity:
        command.append("-vnavcontinuitycheck")
    if circuit:
        command.append("-vnavcircuitcheck")
    if journey:
        command.append("-vnavjourneycheck")
    if "--cloud" in sys.argv:
        command.append("-vnavcloudcheck")
    if "--background" in sys.argv:
        command.append("-vnavbackgroundcheck")
    if "--old-nav" in sys.argv:
        command.append("-vnavoldnavcheck")
    if "--readme-route" in sys.argv:
        command.extend(["-vnavreadmeroute", "-vnavoldnavcheck"])
    if "--reverse" in sys.argv:
        if "--readme-route" not in sys.argv:
            raise ValueError("--reverse requires --readme-route")
        command.append("-vnavreadmereverse")
    if worker_check:
        command.append("-vnavworkercheck")
    if "--preview-isolation" in sys.argv:
        command.append("-vnavpreviewcheck")
    if "--bridge-recovery" in sys.argv:
        command.append("-vnavbridgerecoverycheck")
    if "--pillar-probe" in sys.argv:
        command.append("-vnavpillarprobe")
    if "--snapshot-check" in sys.argv:
        command.append("-vnavsnapshotcheck")
    if "--slope-stall" in sys.argv:
        command.append("-vnavslopestallcheck")
    if "--survival" in sys.argv:
        command.append("-vnavsurvivalcheck")
    if "--survival-resume" in sys.argv:
        command.append("-vnavsurvivalresume")
    if "--survival-platforms" in sys.argv:
        command.append("-vnavsurvivalplatforms")
    if unlimited:
        command.append("-vnavnotimelimit")
    log = (root / "demo-client.log").open("w")
    process = subprocess.Popen(command, cwd=tml, env=dict(os.environ, DYLD_LIBRARY_PATH=str(tml / "Libraries/Native/OSX")),
                               stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
    keep = "--keep-open" in sys.argv
    try:
        deadline = float("inf") if unlimited or descent else time.monotonic() + 240
        while time.monotonic() < deadline:
            output = (root / "demo-client.log").read_text()
            if manual and "VNAV_MANUAL_READY" in output:
                keep = True
                print("MANUAL_WINDOW_READY pid=", process.pid, flush=True)
                return
            if ("VNAV_DEMO_COMPLETE" if focused else "VNAV_WORKER_COMPLETE" if worker_check else "VNAV_CONTINUITY_COMPLETE" if continuity else "VNAV_DESCENT_COMPLETE" if descent else "VNAV_DEMO_COMPLETE") in output:
                break
            if "VNAV_DEMO_FAILED" in output or process.poll() is not None:
                raise RuntimeError(f"Demo failed (exit={process.poll()}); inspect {root / 'demo-client.log'}")
            time.sleep(.25)
        else:
            raise TimeoutError("Demo did not complete within 240 seconds")
        if worker_check:
            print("WORKER_LIFECYCLE_CHECK_COMPLETE", flush=True)
            return
        if "--preview-isolation" in sys.argv:
            assert "PREVIEW_ISOLATION_PASS cases=4" in output
            print("PREVIEW_ISOLATION_CHECK_COMPLETE", flush=True)
            return
        if "--bridge-recovery" in sys.argv:
            assert "BRIDGE_RECOVERY_PASS cases=6" in output
            print("BRIDGE_RECOVERY_CHECK_COMPLETE", flush=True)
            return
        if "--pillar-probe" in sys.argv:
            assert "PILLAR_PROBE_RESULT" in output
            print("PILLAR_PROBE_COMPLETE", flush=True)
            return
        if "--snapshot-check" in sys.argv:
            assert "SNAPSHOT_ROUNDTRIP_PASS" in output
            print("SNAPSHOT_CHECK_COMPLETE", flush=True)
            return
        if "--slope-stall" in sys.argv:
            assert "SLOPE_STALL_MEASURED" in output
            print("SLOPE_STALL_CHECK_COMPLETE", flush=True)
            return
        if "--survival" in sys.argv:
            assert "SURVIVAL_CONTROL_PASS cases=9" in output
            print("SURVIVAL_CONTROL_CHECK_COMPLETE", flush=True)
            return
        if descent:
            row = json.loads((root / "descent-result.json").read_text())
            assert row["done"] and row["hp"] > 0 and (continuity or recorded_goal or "--reverse" in sys.argv or row["y"] >= row["underworld"])
            if circuit:
                assert [leg["offset"] for leg in row["legs"]] == [180, 360, -180]
            if journey:
                assert [leg["kind"] for leg in row["legs"]] == ["main", "crystal", "chest", "spore", "main_return", "surface"]
            if "--readme-route" in sys.argv:
                assert [leg["kind"] for leg in row["legs"]] == ["hell_to_jungle" if "--reverse" in sys.argv else "jungle_to_hell"]
            if "--background" in sys.argv:
                samples = json.loads((root / "descent-trace.json").read_text())
                assert samples and all(not sample["focused"] for sample in samples)
            print("NATURAL_CONTINUITY_CHECK_COMPLETE" if continuity else "NATURAL_DESCENT_CHECK_COMPLETE", row, flush=True)
            return
        rows = json.loads((root / "demo-result.json").read_text())
        expected = [1] if "--wings" in sys.argv else [0, 1]
        assert len(rows) == len(expected) and all(row["reached"] and row["hp"] == 400 for row in rows)
        if not routed:
            assert all(row["route_points"] > 1 for row in rows)
            assert all((root / f"demo-{index}.png").exists() for index in expected)
        print("VISIBLE_VNAV_DEMO_CHECKS_COMPLETE", json.dumps(rows), flush=True)
        if keep:
            print("DEMO_WINDOW_LEFT_OPEN pid=", process.pid, flush=True)
    finally:
        if not keep:
            try:
                process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                pass
            for sig in (signal.SIGTERM, signal.SIGKILL):
                try:
                    os.killpg(process.pid, sig)
                except ProcessLookupError:
                    break
                if sig == signal.SIGTERM:
                    time.sleep(.5)
            process.wait(timeout=8)
        log.close()


if __name__ == "__main__":
    def interrupted(signum, frame):
        raise KeyboardInterrupt(f"test interrupted by signal {signum}")
    signal.signal(signal.SIGTERM, interrupted)
    main()
