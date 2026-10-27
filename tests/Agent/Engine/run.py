"""Run real game tools in a disposable client, without a model or normal saves."""
import json
import argparse
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--live", action="store_true", help="After checks, run the real Codex agent for at most 180 seconds; consumes model quota")
    mode.add_argument("--batch", action="store_true", help="Only collect ten placed crystals through production gather; no model calls")
    mode.add_argument("--health-live", action="store_true", help="Real agent obtains 300 permanent HP with navigation stubbed; consumes model quota")
    args = parser.parse_args()
    root = Path("/tmp") / ("terrablind-agent-engine-" + str(time.time_ns()))
    (root / "Mods").mkdir(parents=True)
    (root / "Worlds").mkdir()
    source = Path("/tmp/terrablind-descent-world/Worlds/World.wld")
    if not source.exists():
        raise RuntimeError("The isolated test world is missing")
    shutil.copy2(source, root / "Worlds/Checks.wld")
    staging = Path.home() / "Library/Application Support/TerraBlindDev/agent-277ba5a4e4c2/staging/Mods/TerraBlind.tmod"
    shutil.copy2(staging, root / "Mods/TerraBlind.tmod")
    user = Path.home() / "Library/Application Support/Terraria/tModLoader"
    for name in ("config.json", "input profiles.json"):
        shutil.copy2(user / name, root / name)
    (root / "Mods/enabled.json").write_text('["TerraBlind","Engine"]')
    project = Path(__file__).resolve().parent / "AgentChecks.csproj"
    subprocess.run(["dotnet", "build", str(project), f'-p:ExtraBuildModFlags=-tmlsavedirectory "{root}"', "-v", "q"], check=True)
    tml = Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader"
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    print("AGENT_ENGINE_ROOT", root, flush=True)
    command = [str(tml / "dotnet_arm64/dotnet"), "tModLoader.dll", "-nosteam",
        "-tmlsavedirectory", str(root), "-agentenginecheck", "-tbhttpport", str(port)]
    if args.live:
        command.append("-agentlivecheck")
    if args.batch:
        command.append("-agentbatchcheck")
    if args.health_live:
        command.append("-agenthealthcheck")
    with (root / "client.log").open("w") as log:
        process = subprocess.Popen(command, cwd=tml,
            env=dict(os.environ, DYLD_LIBRARY_PATH=str(tml / "Libraries/Native/OSX")),
            stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
        print("AGENT_ENGINE_PID", process.pid, flush=True)
        try:
            deadline = time.monotonic() + (300 if args.live or args.health_live else 180)
            while process.poll() is None:
                if (root / "agent-engine-result.json").exists():
                    break
                if time.monotonic() > deadline:
                    raise TimeoutError(f"Engine check timed out; inspect {root}/client.log")
                if "An error occurred while loading" in (root / "client.log").read_text():
                    raise RuntimeError(f"Mod load failed; inspect {root}/client.log")
                time.sleep(.25)
        finally:
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)
    report = root / "agent-engine-result.json"
    if not report.exists() or not json.loads(report.read_text())["ok"]:
        raise RuntimeError(f"Engine checks failed: exit={process.returncode}; inspect {root}/client.log and result JSON")
    print(json.loads(report.read_text())["detail"], flush=True)


if __name__ == "__main__":
    main()
