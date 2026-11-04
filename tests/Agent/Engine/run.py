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
    parser.add_argument("--world", type=Path, default=Path("/tmp/terrablind-descent-world/Worlds/World.wld"), help="World copied into the disposable save")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--behavior", action="store_true", help="Saved JS combat, live Jev judgment, loot and original-goal continuation")
    mode.add_argument("--control", action="store_true", help="Native control takeover and terrain veto trial, no model calls")
    mode.add_argument("--live", action="store_true", help="After checks, run the real Codex agent for at most 180 seconds; consumes model quota")
    mode.add_argument("--batch", action="store_true", help="Only collect ten placed crystals through production gather; no model calls")
    mode.add_argument("--health-live", action="store_true", help="Real agent obtains 300 permanent HP with navigation stubbed; consumes model quota")
    mode.add_argument("--workflow-live", action="store_true", help="Real model writes monitored gathering, pauses on low HP, then resumes; consumes quota")
    mode.add_argument("--watch-live", action="store_true", help="Real model handles a passive chest event, loots, changes gathering target and resumes; consumes quota")
    mode.add_argument("--decision-live", action="store_true", help="Also require a model-authored explicit decision point between looting and resumed collection")
    mode.add_argument("--biome-check", action="store_true", help="Verify a jungle entrance against sky exposure, body clearance and native player biome")
    mode.add_argument("--automatic-live", action="store_true", help="Luna collects and loots without being told to register watches or background handles")
    mode.add_argument("--planning-readonly", action="store_true", help="Real model queries actual game state then proposes an action without executing it")
    mode.add_argument("--interactions", action="store_true", help="Real equipment swaps and generic NPC dialogue buttons, no model calls")
    mode.add_argument("--hornets", action="store_true", help="Sequential hornets in a low arena, throwing knives, no model calls")
    mode.add_argument("--hornets-wall", action="store_true", help="Same hornet trial with a short stone obstacle")
    mode.add_argument("--script", action="store_true", help="Real script-tool collection/use/verification and timeout cleanup, no model calls")
    mode.add_argument("--continuation", action="store_true", help="Pause and resume real gathering across host-persisted script segments")
    mode.add_argument("--model-script", type=Path, help="Replay a saved model per-item.json through real game APIs, without further model calls")
    mode.add_argument("--teleport-check", action="store_true", help="Check exact teleports without terrain edits, fall reset and nearest tile ordering")
    args = parser.parse_args()
    root = Path("/tmp") / ("terrablind-agent-engine-" + str(time.time_ns()))
    (root / "Mods").mkdir(parents=True)
    (root / "Worlds").mkdir()
    if args.model_script:
        events = json.loads(args.model_script.read_text())
        scripts = [event["args"]["code"] for event in events if event.get("tool") == "run_script"]
        if len(scripts) != 1:
            raise ValueError("This per-item acceptance requires exactly one model-authored script")
        shutil.copy2(args.model_script, root / "model-script.json")
    source = args.world
    if not source.exists():
        raise RuntimeError("The isolated test world is missing")
    shutil.copy2(source, root / "Worlds/Checks.wld")
    staging = Path.home() / "Library/Application Support/TerraBlindDev/agent-277ba5a4e4c2/staging/Mods/TerraBlind.tmod"
    shutil.copy2(staging, root / "Mods/TerraBlind.tmod")
    user = Path.home() / "Library/Application Support/Terraria/tModLoader"
    if args.behavior:
        key = json.loads((user / "ModConfigs/TerraBlind_Config.json").read_text()).get("TypeSafeKey", "")
        if not key.strip():
            raise RuntimeError("The isolated behavior trial requires a configured TypeSafe key")
        (root / "ModConfigs").mkdir()
        config = root / "ModConfigs/TerraBlind_Config.json"
        config.write_text(json.dumps({"TypeSafeKey": key}))
        config.chmod(0o600)
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
    if args.control:
        command.remove("-agentenginecheck")
        command.append("-agentcontrolcheck")
    if args.behavior:
        command.remove("-agentenginecheck")
        command.append("-agentbehaviorcheck")
    if args.live:
        command.append("-agentlivecheck")
    if args.batch:
        command.append("-agentbatchcheck")
    if args.health_live:
        command.append("-agenthealthcheck")
    if args.workflow_live:
        command.append("-agentworkflowcheck")
    if args.watch_live or args.decision_live or args.automatic_live:
        command.append("-agentwatchcheck")
    if args.decision_live:
        command.append("-agentdecisioncheck")
    if args.biome_check:
        command.append("-agentbiomecheck")
    if args.automatic_live:
        command.append("-agentautomaticcheck")
    if args.planning_readonly:
        command.append("-agentplanningcheck")
    if args.interactions:
        command.append("-agentinteractioncheck")
    if args.hornets or args.hornets_wall:
        command.append("-agenthornetcheck")
    if args.hornets_wall:
        command.append("-agenthornetwall")
    if args.script or args.model_script or args.continuation:
        command.append("-agentscriptcheck")
    if args.continuation:
        command.append("-agentcontinuationcheck")
    if args.teleport_check:
        command.append("-agentteleportcheck")
    with (root / "client.log").open("w") as log:
        process = subprocess.Popen(command, cwd=tml,
            env=dict(os.environ, DYLD_LIBRARY_PATH=str(tml / "Libraries/Native/OSX")),
            stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT)
        print("AGENT_ENGINE_PID", process.pid, flush=True)
        try:
            material_review = args.behavior and os.environ.get("TB_MATERIAL_MODEL_REVIEW") == "1"
            full_craft = args.behavior and os.environ.get("TB_FULL_CRAFT_TRIAL") == "1"
            deadline = time.monotonic() + (1900 if full_craft else 300 if material_review or args.live or args.health_live or args.workflow_live or args.watch_live or args.decision_live or args.automatic_live or args.planning_readonly else 180)
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
    if args.control:
        if not report.exists() or not json.loads(report.read_text())["ok"] or "CONTROL_TAKEOVER_COMPLETE native_engine=true model_calls=0" not in (root / "client.log").read_text():
            raise RuntimeError(f"Control trial failed: exit={process.returncode}; inspect {root}/client.log")
        print("CONTROL_TRIAL_PASS", root, flush=True)
        return
    if not report.exists() or not json.loads(report.read_text())["ok"]:
        raise RuntimeError(f"Engine checks failed: exit={process.returncode}; inspect {root}/client.log and result JSON")
    print(json.loads(report.read_text())["detail"], flush=True)


if __name__ == "__main__":
    main()
