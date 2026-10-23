"""Launch a disposable graphical client and compare normal updates with the worker."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import sys
from check import compare


def main():
    worker_root = Path("/tmp/terrablind-forecast-lab")
    root = Path("/tmp/terrablind-forecast-client")
    for folder in (root / "Mods", root / "Worlds"):
        folder.mkdir(parents=True, exist_ok=True)
    shutil.copy2(worker_root / "Mods/Forecast.tmod", root / "Mods/Forecast.tmod")
    shutil.copy2(worker_root / "Worlds/Forecast.wld", root / "Worlds/Forecast.wld")
    (root / "Mods/enabled.json").write_text('["Forecast"]')
    config = {}
    config.update(Fullscreen=False, DisplayWidth=960, DisplayHeight=540, Language="en-US",
                  AutoPause=False, CloudSavingDefault=False, ThrottleWhenInactive=False, LastLaunchedTModLoaderVersion="2026.8.3.0")
    (root / "config.json").write_text(json.dumps(config))
    result_path = root / "client-result.json"
    asynchronous = "--async" in sys.argv
    if asynchronous:
        result_path = root / "async-result.json"
    result_path.unlink(missing_ok=True)
    tml = Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader"
    base = [str(tml / "dotnet_arm64/dotnet"), "tModLoader.dll", "-nosteam"]
    worker_log = (root / "worker.log").open("w")
    client_log = (root / "client.log").open("w")
    worker = subprocess.Popen(base + ["-server", "-tmlsavedirectory", str(worker_root), "-world",
                              str(worker_root / "Worlds/Forecast.wld"), "-port", "17978", "-vanillaforecastport", "19042"],
                              cwd=tml, stdin=subprocess.DEVNULL, stdout=worker_log, stderr=subprocess.STDOUT)
    client = None
    try:
        deadline = time.monotonic() + 120
        while "VANILLA_FORECAST_READY" not in (root / "worker.log").read_text():
            if worker.poll() is not None or time.monotonic() > deadline:
                raise RuntimeError("Worker failed to start; inspect worker.log")
            time.sleep(.25)
        env = dict(os.environ, DYLD_LIBRARY_PATH=str(tml / "Libraries/Native/OSX"))
        client = subprocess.Popen(base + ["-tmlsavedirectory", str(root), "-forecastclient"] + (["-forecastasync"] if asynchronous else []),
                                  cwd=tml, env=env, stdin=subprocess.DEVNULL, stdout=client_log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 180
        while client.poll() is None and time.monotonic() < deadline:
            if "FORECAST_CLIENT_COMPLETE" in (root / "client.log").read_text():
                break
            time.sleep(.25)
        if client.poll() not in (None, 0):
            raise RuntimeError(f"Graphical client failed or timed out: {client.poll()}; inspect {root / 'client.log'}")
        if "FORECAST_CLIENT_COMPLETE" not in (root / "client.log").read_text():
            raise AssertionError("Client completion marker missing")
        results = json.loads(result_path.read_text())
        if asynchronous:
            expected = {"late": "late_result", "revision": "revision_changed", "terrain": "context_changed", "knockback": "state_changed:x"}
            for row in results:
                if row["name"] in expected:
                    assert row["status"] == expected[row["name"]], row
                    assert row["accepted"] == 0 and row["rejected"] > 0
                else:
                    assert row["reached"] and row["accepted"] > 0, (row["name"], row["status"])
                ticks = [frame["world_tick"] for frame in row["trace"]]
                assert all(b == a + 1 for a, b in zip(ticks, ticks[1:])), "World did not advance continuously"
                print("ASYNC_CLIENT_CASE_PASS", row["name"], "ticks", len(ticks), "accepted", row["accepted"], "status", row["status"], flush=True)
                assert 30 <= row["lead_frames"] <= 90
                if row["name"] == "late":
                    assert row["lead_frames"] > 30, "Late response did not increase the planning lead"
            assert len(results) == 6
            print("ASYNC_CLIENT_CHECKS_COMPLETE cases=", len(results), flush=True)
            return
        for row in results:
            error = compare(row["predicted"]["frames"], row["actual"])
            print("GRAPHICAL_CLIENT_CASE_PASS", row["name"], "max_error", error, flush=True)
        assert len(results) == 9
        print("GRAPHICAL_CLIENT_CHECKS_COMPLETE cases=", len(results), flush=True)
    finally:
        for process in (client, worker):
            if process is not None and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
        worker_log.close()
        client_log.close()


if __name__ == "__main__":
    main()
