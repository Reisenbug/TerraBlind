"""Build and run one worktree's tModLoader client without shared saves or services."""

import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import sys


REPO = Path(__file__).resolve().parents[1]
TML = Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader"
USER_SAVES = Path.home() / "Library/Application Support/Terraria/tModLoader"


def layout(role):
    identity = hashlib.sha256(str(REPO).encode()).hexdigest()[:12]
    root = Path.home() / "Library/Application Support/TerraBlindDev" / f"{role}-{identity}"
    return root, root / "staging", root / "client"


def copy_missing(source, destination):
    if source.exists() and not destination.exists():
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)


def prepare(save, world=None, player=None):
    (save / "Mods").mkdir(parents=True, exist_ok=True)
    for name in ("config.json", "input profiles.json"):
        copy_missing(USER_SAVES / name, save / name)
    for config in (USER_SAVES / "ModConfigs").glob("TerraBlind*.json"):
        copy_missing(config, save / "ModConfigs" / config.name)
    enabled = save / "Mods/enabled.json"
    mods = json.loads(enabled.read_text()) if enabled.exists() else []
    if "TerraBlind" not in mods:
        mods.append("TerraBlind")
    enabled.write_text(json.dumps(mods))
    for source, folder, extension in ((world, "Worlds", ".twld"), (player, "Players", ".tplr")):
        if source:
            source = source.resolve(strict=True)
            copy_missing(source, save / folder / source.name)
            copy_missing(source.with_suffix(extension), save / folder / source.with_suffix(extension).name)


def running(pid_file, save):
    if not pid_file.exists():
        return False
    pid = int(pid_file.read_text())
    result = subprocess.run(["ps", "-p", str(pid), "-o", "command="], capture_output=True, text=True)
    return result.returncode == 0 and str(save) in result.stdout and "tModLoader.dll" in result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("info", "prepare", "build", "run"))
    parser.add_argument("--role", required=True, choices=("navigation", "agent"))
    parser.add_argument("--port", type=int)
    parser.add_argument("--world", type=Path)
    parser.add_argument("--player", type=Path)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    port = args.port if args.port is not None else 17878 if args.role == "navigation" else 17879
    if not 1 <= port <= 65535:
        parser.error("port must be between 1 and 65535")
    if REPO.name != "TerraBlind":
        parser.error("Keep the worktree directory name TerraBlind, e.g. navigation/TerraBlind")
    root, staging, save = layout(args.role)
    artifact = staging / "Mods/TerraBlind.tmod"
    plan = dict(role=args.role, source=str(REPO), runtime=str(root), save=str(save),
                staging=str(staging), http=f"http://127.0.0.1:{port}/", artifact=str(artifact))
    print(json.dumps(plan, ensure_ascii=False, indent=2), flush=True)
    if args.action == "info" or args.dry_run:
        return
    pid_file = root / "client.pid"
    if args.action in ("prepare", "run") and running(pid_file, save):
        raise RuntimeError("This client's save directory is in use; close its window first")
    if args.action == "build":
        staging.mkdir(parents=True, exist_ok=True)
        subprocess.run(["dotnet", "build", "TerraBlind.csproj",
                        f'-p:ExtraBuildModFlags=-tmlsavedirectory "{staging}"', "-v", "q", "--nologo"], cwd=REPO, check=True)
        digest = hashlib.sha256(artifact.read_bytes()).hexdigest()
        plan.update(sha256=digest,
                    commit=subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=REPO, text=True).strip(),
                    dirty=subprocess.check_output(["git", "status", "--porcelain"], cwd=REPO, text=True).splitlines())
        (staging / "build.json").write_text(json.dumps(plan, ensure_ascii=False, indent=2))
        print("BUILT", digest, flush=True)
        return
    if args.action == "prepare":
        prepare(save, args.world, args.player)
        return
    if not artifact.exists():
        raise RuntimeError("Build this worktree first with the same --role")
    lock = (root / "client.lock").open("a")
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        raise RuntimeError("This client's launcher is already running") from None
    with socket.socket() as connection:
        if connection.connect_ex(("127.0.0.1", port)) == 0:
            raise RuntimeError(f"Port {port} is already in use; choose another --port")
    prepare(save, args.world, args.player)
    installed = save / "Mods/TerraBlind.tmod"
    shutil.copy2(artifact, installed)
    digest = hashlib.sha256(installed.read_bytes()).hexdigest()
    manifest = json.loads((staging / "build.json").read_text())
    if digest != manifest["sha256"]:
        raise RuntimeError("Built artifact no longer matches build.json; build again")
    shutil.copy2(staging / "build.json", save / "loaded-build.json")
    command = [str(TML / "dotnet_arm64/dotnet"), str(TML / "tModLoader.dll"), "-nosteam",
               "-tmlsavedirectory", str(save), "-tbhttpport", str(port),
               "-tbdevrole", args.role, "-tbdevbuild", digest[:12]]
    environment = dict(os.environ, DYLD_LIBRARY_PATH=str(TML / "Libraries/Native/OSX"))
    with (root / "client.log").open("w") as log:
        process = subprocess.Popen(command, cwd=TML, env=environment, stdout=log, stderr=subprocess.STDOUT)
        pid_file.write_text(str(process.pid))
        print(f"RUNNING pid={process.pid} build={digest[:12]} log={root / 'client.log'}", flush=True)
        try:
            code = process.wait()
        except KeyboardInterrupt:
            process.terminate()
            code = process.wait()
        finally:
            pid_file.unlink(missing_ok=True)
            lock.close()
        sys.exit(code)


if __name__ == "__main__":
    main()
