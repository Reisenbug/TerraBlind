"""Generate the README seed once, without loading or changing any user world."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import time

root = Path("/tmp/terrablind-descent-world")
(root / "Mods").mkdir(parents=True, exist_ok=True)
(root / "Worlds").mkdir(exist_ok=True)
shutil.copy2("Assets/VanillaWorker.bin", root / "Mods/Forecast.tmod")
(root / "Mods/enabled.json").write_text(json.dumps(["Forecast"]))
tml = Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader"
with (root / "generation.log").open("w") as output:
    process = subprocess.Popen([str(tml / "dotnet_arm64/dotnet"), "tModLoader.dll", "-server", "-nosteam",
        "-tmlsavedirectory", str(root), "-world", str(root / "Worlds/World.wld"), "-autocreate", "1",
        "-seed", "1.1.2.38154567", "-vanillaforecastport", "0"], cwd=tml, stdin=subprocess.DEVNULL,
        stdout=output, stderr=subprocess.STDOUT)
    try:
        deadline = time.monotonic() + 240
        while time.monotonic() < deadline:
            if "VANILLA_FORECAST_READY" in (root / "generation.log").read_text():
                assert (root / "Worlds/World.wld").exists()
                print("FIXED_SEED_WORLD_READY seed=1.1.2.38154567", flush=True)
                break
            if process.poll() is not None:
                raise RuntimeError("World generation exited; inspect generation.log")
            time.sleep(.25)
        else:
            raise TimeoutError("World generation timed out")
    finally:
        if process.poll() is None:
            process.kill(); process.wait(timeout=10)
