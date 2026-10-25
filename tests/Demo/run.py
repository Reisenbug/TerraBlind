"""Exercise the shipped /vnav entry with the complete mod in a disposable graphical world."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time


def main():
    routed = "--route" in sys.argv
    continuity = "--continuity" in sys.argv
    circuit = "--circuit" in sys.argv
    continuity |= circuit
    descent = "--descent" in sys.argv or continuity
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
        mods = source_world.parent.parent / "Mods"
        mods.mkdir(parents=True, exist_ok=True)
        shutil.copy2("Assets/VanillaWorker.bin", mods / "Forecast.tmod")
        subprocess.run([sys.executable, "tests/Forecast/check.py", "--case", "flat"], check=True)
        if not source_world.exists():
            source_world = source_world.parent / "World.wld"
    shutil.copy2(source_world, root / "Worlds/Demo.wld")
    (root / "Mods/enabled.json").write_text('["TerraBlind","Demo"]')
    user_root = Path.home() / "Library/Application Support/Terraria/tModLoader"
    manual = "--manual" in sys.argv
    if manual or descent:
        shutil.copy2(user_root / "Mods/DragonLens.tmod", root / "Mods/DragonLens.tmod")
        (root / "Mods/enabled.json").write_text('["TerraBlind","Demo","DragonLens"]')
    user_config = user_root / "config.json"
    settings = json.loads(user_config.read_text()) if user_config.exists() else {}
    settings["CloudSavingDefault"] = False
    (root / "config.json").write_text(json.dumps(settings))
    if (user_root / "input profiles.json").exists():
        shutil.copy2(user_root / "input profiles.json", root / "input profiles.json")
    if "--prepare-only" in sys.argv:
        print("PREPARED_TEST_ROOT", root, flush=True)
        return
    (root / "demo-result.json").unlink(missing_ok=True)
    tml = Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader"
    show = "--show" in sys.argv or manual
    command = [str(tml / "dotnet_arm64/dotnet"), str(tml / "tModLoader.dll"), "-nosteam",
               "-tmlsavedirectory", str(root), "-vnavdescentcheck" if descent else "-vnavdemocheck"]
    if show:
        command.append("-vnavshow")
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
    if continuity:
        command.append("-vnavcontinuitycheck")
    if circuit:
        command.append("-vnavcircuitcheck")
    if "--background" in sys.argv:
        command.append("-vnavbackgroundcheck")
    if "--old-nav" in sys.argv:
        command.append("-vnavoldnavcheck")
    log = (root / "demo-client.log").open("w")
    process = subprocess.Popen(command, cwd=tml, env=dict(os.environ, DYLD_LIBRARY_PATH=str(tml / "Libraries/Native/OSX")),
                               stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
    keep = False
    try:
        deadline = time.monotonic() + (1000 if descent else 240)
        while time.monotonic() < deadline:
            output = (root / "demo-client.log").read_text()
            if manual and "VNAV_MANUAL_READY" in output:
                keep = True
                print("MANUAL_WINDOW_READY pid=", process.pid, flush=True)
                return
            if ("VNAV_CONTINUITY_COMPLETE" if continuity else "VNAV_DESCENT_COMPLETE" if descent else "VNAV_DEMO_COMPLETE") in output:
                break
            if "VNAV_DEMO_FAILED" in output or process.poll() is not None:
                if show and process.poll() is None:
                    keep = True
                raise RuntimeError(f"Demo failed (exit={process.poll()}); inspect {root / 'demo-client.log'}")
            time.sleep(.25)
        else:
            raise TimeoutError("Demo did not complete within 240 seconds")
        if descent:
            row = json.loads((root / "descent-result.json").read_text())
            assert row["done"] and row["hp"] > 0 and (continuity or row["y"] >= row["underworld"])
            if circuit:
                assert [leg["offset"] for leg in row["legs"]] == [180, 360, -180]
            if "--background" in sys.argv:
                samples = json.loads((root / "descent-trace.json").read_text())
                assert samples and all(not sample["focused"] for sample in samples)
            print("NATURAL_CONTINUITY_CHECK_COMPLETE" if continuity else "NATURAL_DESCENT_CHECK_COMPLETE", row, flush=True)
            if show:
                keep = True
            return
        rows = json.loads((root / "demo-result.json").read_text())
        expected = [1] if "--wings" in sys.argv else [0, 1]
        assert len(rows) == len(expected) and all(row["reached"] and row["hp"] == 400 for row in rows)
        if not routed:
            assert all(row["route_points"] > 1 for row in rows)
            assert all((root / f"demo-{index}.png").exists() for index in expected)
        print("VISIBLE_VNAV_DEMO_CHECKS_COMPLETE", json.dumps(rows), flush=True)
        if show:
            keep = True
            print("DEMO_WINDOW_LEFT_OPEN pid=", process.pid, flush=True)
    finally:
        if not keep and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait(timeout=8)
        log.close()


if __name__ == "__main__":
    main()
