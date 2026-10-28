"""Run the shipped regional controller in an isolated, headless full-mod process."""

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
    parser.add_argument("--chain", type=Path, required=True)
    parser.add_argument("--mod", type=Path, required=True)
    parser.add_argument("--root", type=Path, default=Path("/tmp/terrablind-region-handoff"))
    parser.add_argument("--scenario", choices=("early", "late", "terrain", "worker"), default="early")
    args = parser.parse_args()
    root = args.root.resolve()
    if not (root.is_relative_to("/tmp") or root.is_relative_to("/private/tmp")):
        raise ValueError("Temporary test root required")
    mods = root / "Mods"
    mods.mkdir(parents=True, exist_ok=True)
    subprocess.run(["dotnet", "build", "tests/Demo/Demo.csproj",
                    f"-p:ExtraBuildModFlags=-tmlsavedirectory {root}", "-v", "q", "--nologo"], check=True)
    shutil.copy2(args.mod, mods / "TerraBlind.tmod")
    shutil.copy2(args.world, root / "World.wld")
    (mods / "enabled.json").write_text('["TerraBlind", "Demo"]')
    source = json.loads(args.source.read_text())["request"]
    results = [json.loads(path.read_text()) for path in sorted(args.chain.glob("chain-*/result.json"))]
    if not results or any(result["status"] != "reached" for result in results):
        raise ValueError("A completed regional chain is required")
    fixture = root / "fixture.json"
    fixture.write_text(json.dumps(dict(snapshot=source["snapshot"], guidance=source["guidance"],
                                      results=results, scenario=args.scenario)))
    report = root / "region-handoff-result.json"
    report.unlink(missing_ok=True)
    tml = Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader"
    command = [str(tml / "dotnet_arm64/dotnet"), "tModLoader.dll", "-server", "-nosteam",
               "-tmlsavedirectory", str(root), "-world", str(root / "World.wld"), "-port", "17979",
               "-vanillaforecastport", "19079", "-regionhandoffcheck", str(fixture)]
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
    result = json.loads(report.read_text())
    assert result["passed"]
    if args.scenario != "worker":
        assert result["missing_input_frames"] == (5 if args.scenario == "late" else 0)
    print("REGION_HANDOFF_VERIFIED", result)


if __name__ == "__main__":
    main()
