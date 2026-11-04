"""Measure the production Dijkstra on a copied real world without a graphical client."""

import argparse
import json
import os
import shutil
import signal
import subprocess
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--world", type=Path, required=True)
    parser.add_argument("--mod", type=Path, required=True)
    parser.add_argument("--root", type=Path, default=Path("/tmp/terrablind-field-timing"))
    args = parser.parse_args()
    root = args.root.resolve()
    if not root.is_relative_to("/private/tmp") and not root.is_relative_to("/tmp"):
        raise ValueError("Temporary test root required")
    mods = root / "Mods"
    mods.mkdir(parents=True, exist_ok=True)
    subprocess.run(["dotnet", "build", "tests/Demo/Demo.csproj",
                    f"-p:ExtraBuildModFlags=-tmlsavedirectory {root}", "-v", "q", "--nologo"], check=True)
    shutil.copy2(args.mod, mods / "TerraBlind.tmod")
    shutil.copy2(args.world, root / "World.wld")
    shutil.copy2(args.source, root / "fixture.json")
    (mods / "enabled.json").write_text('["TerraBlind", "Demo"]')
    report = root / "field-timing.json"
    report.unlink(missing_ok=True)
    tml = Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader"
    command = [str(tml / "dotnet_arm64/dotnet"), "tModLoader.dll", "-server", "-nosteam",
               "-tmlsavedirectory", str(root), "-world", str(root / "World.wld"), "-port", "17979",
               "-vanillaforecastport", "19079", "-fieldtimingcheck", str(root / "fixture.json")]
    with (root / "engine.log").open("w") as log:
        with subprocess.Popen(command, cwd=tml, stdin=subprocess.DEVNULL, stdout=log,
                              stderr=subprocess.STDOUT, start_new_session=True) as process:
            try:
                code = process.wait(timeout=180)
            finally:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            if code:
                raise subprocess.CalledProcessError(code, command)
    rows = json.loads(report.read_text())
    assert len(rows) == 8 and all(row["start_reachable"] for row in rows)
    print("FIELD_TIMING_VERIFIED", json.dumps(rows))


if __name__ == "__main__":
    main()
