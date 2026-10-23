"""Small PPO experiment against a disposable tModLoader server."""

import argparse
import json
import socket
import subprocess
import time
from pathlib import Path

import gymnasium as gym
import numpy as np
import torch
from stable_baselines3 import PPO
from stable_baselines3.common.env_checker import check_env
from stable_baselines3.common.vec_env import DummyVecEnv, VecFrameStack


class TerrariaEnv(gym.Env):
    def __init__(self, port=19041):
        self.socket = socket.create_connection(("127.0.0.1", port), timeout=.5)
        self.socket.settimeout(30)
        self.socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.stream = self.socket.makefile("rw", buffering=1)
        self.action_space = gym.spaces.Discrete(6)
        self.observation_space = gym.spaces.Box(-100, 100, (151,), dtype=np.float32)
        self.scenario = None
        self.last_action = 0
        self.last_info = None

    def request(self, command, **fields):
        self.stream.write(json.dumps(dict(command=command, **fields)) + "\n")
        self.stream.flush()
        line = self.stream.readline()
        if not line:
            raise RuntimeError("Training engine disconnected; inspect engine.log")
        result = json.loads(line)
        if "error" in result:
            raise RuntimeError(result["error"])
        return result

    def observation(self, response):
        return np.asarray(response["observation"] + [self.last_action / 5], dtype=np.float32)

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self.last_action = 0
        scenario = self.scenario or options or {"gear": int(self.np_random.integers(0, 3))}
        response = self.request("reset", seed=int(self.np_random.integers(0, 2**30)), **scenario)
        self.last_info = response["info"]
        return self.observation(response), response["info"]

    def step(self, action):
        self.last_action = int(action)
        response = self.request("step", action=self.last_action)
        return (self.observation(response), response["reward"], response["terminated"],
                response["truncated"], response["info"])

    def close(self):
        try:
            self.request("close")
        finally:
            self.stream.close()
            self.socket.close()


def evaluate(model, env, raw, seeds, random_policy=False, replays=None):
    results = []
    rng = np.random.default_rng(9182)
    for gear in range(4):
        for height in (0, 3, 5):
            wins, rewards, frames = 0, [], []
            for seed in seeds:
                raw.scenario = {"gear": gear, "height": height, "distance": 12 + seed % 10}
                env.seed(seed)
                observation = env.reset()
                trajectory = [dict(raw.last_info, action=0)]
                total = 0
                for _ in range(120):
                    action = np.asarray([rng.integers(6)]) if random_policy else model.predict(observation, deterministic=True)[0]
                    observation, reward, done, info = env.step(action)
                    trajectory.append(dict(info[0], action=int(action[0])))
                    trajectory[-1].pop("terminal_observation", None)
                    total += float(reward[0])
                    if done[0]:
                        wins += int(info[0]["success"])
                        frames.append(info[0]["frames"])
                        break
                rewards.append(total)
                if replays is not None and seed == seeds[0]:
                    replays.append(dict(policy="random" if random_policy else "trained", gear=gear,
                                        height=height, distance=12 + seed % 10, trajectory=trajectory,
                                        success=bool(info[0]["success"])))
            results.append(dict(gear=gear, held_out_combination=gear == 3, height=height,
                                successes=wins, episodes=len(seeds), mean_return=float(np.mean(rewards)),
                                mean_frames=float(np.mean(frames))))
    raw.scenario = None
    return results


def smoke(raw):
    raw.scenario = {"gear": 0, "height": 0, "distance": 12}
    _, initial = raw.reset(seed=71)
    for _ in range(10):
        _, _, terminated, truncated, stationary = raw.step(0)
        if terminated or truncated:
            raise RuntimeError(f"Idle character reached or failed the task: {stationary}")
    if abs(stationary["x"] - initial["x"]) > 1 or abs(stationary["y"] - initial["y"]) > 1:
        raise RuntimeError(f"Idle character drifted: {initial} -> {stationary}")
    measured = []
    for gear in range(4):
        raw.scenario = {"gear": gear, "height": 0, "distance": 30}
        observation, initial = raw.reset(seed=71)
        if abs(initial["x"] - initial["start_x"] - initial["player_width"] / 2) > 1:
            raise RuntimeError(f"Spawn position changed: {initial}")
        minimum_y = initial["y"]
        for step in range(40):
            action = 5 if step < 5 or 7 <= step < 15 else 2
            observation, _, terminated, truncated, info = raw.step(action)
            minimum_y = min(minimum_y, info["y"])
            if terminated or truncated:
                break
        measured.append(dict(gear=gear, horizontal_pixels=info["x"] - initial["x"],
                             jump_height_pixels=initial["y"] - minimum_y,
                             initial_extra_jump=initial["extra_jump_available"],
                             engine_player_updates=info["engine_player_updates"]))
    raw.scenario = None
    if not all(row["horizontal_pixels"] > 20 and row["jump_height_pixels"] > 20 for row in measured):
        raise RuntimeError(f"Engine movement failed: {measured}")
    if measured[1]["jump_height_pixels"] <= measured[0]["jump_height_pixels"] + 10:
        raise RuntimeError(f"Cloud extra jump did not alter engine motion: {measured}")
    if measured[2]["jump_height_pixels"] <= measured[0]["jump_height_pixels"] + 10:
        raise RuntimeError(f"Balloon did not alter engine motion: {measured}")
    return measured


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--steps", type=int, default=32768)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--port", type=int, default=19041)
    parser.add_argument("--game-port", type=int, default=17977)
    parser.add_argument("--root", type=Path, default=Path("/tmp/terrablind-rl-lab"))
    parser.add_argument("--tml", type=Path, default=Path.home() / "Library/Application Support/Steam/steamapps/common/tModLoader")
    parser.add_argument("--smoke-only", action="store_true")
    args = parser.parse_args()
    if args.root.resolve() == (Path.home() / "Library/Application Support/Terraria/tModLoader").resolve():
        raise ValueError("Use a disposable save directory")
    args.root.mkdir(parents=True, exist_ok=True)
    (args.root / "Mods").mkdir(exist_ok=True)
    (args.root / "Mods/enabled.json").write_text('["Rl"]')
    (args.root / "Worlds").mkdir(exist_ok=True)
    worlds = sorted((args.root / "Worlds").glob("*.wld"))
    world = worlds[0] if worlds else args.root / "Worlds/RlLab.wld"
    command = [str(args.tml / "dotnet_arm64/dotnet"), "tModLoader.dll", "-server", "-nosteam",
               "-tmlsavedirectory", str(args.root), "-world", str(world),
               "-autocreate", "1", "-seed", "1.1.1.92481", "-port", str(args.game_port),
               "-rlport", str(args.port), "-maxplayers", "2"]
    engine_log = (args.root / "engine.log").open("w")
    engine = subprocess.Popen(command, cwd=args.tml, stdin=subprocess.DEVNULL, stdout=engine_log, stderr=subprocess.STDOUT)
    raw = None
    try:
        until = time.monotonic() + 240
        while time.monotonic() < until:
            if engine.poll() is not None:
                raise RuntimeError(f"Engine exited {engine.returncode}; see {args.root / 'engine.log'}")
            try:
                raw = TerrariaEnv(args.port)
                break
            except OSError:
                pass
            time.sleep(.25)
        else:
            raise TimeoutError("Isolated engine startup timed out")
        evidence = smoke(raw)
        print("RL_ENGINE_SMOKE", json.dumps(evidence), flush=True)
        check_env(raw, warn=True)
        if args.smoke_only:
            return
        torch.set_num_threads(1)
        env = VecFrameStack(DummyVecEnv([lambda: raw]), n_stack=4)
        replays = []
        baseline = evaluate(None, env, raw, range(10), random_policy=True, replays=replays)
        model = PPO("MlpPolicy", env, seed=args.seed, device="cpu", verbose=1,
                    n_steps=1024, batch_size=64, n_epochs=5, learning_rate=3e-4,
                    policy_kwargs=dict(net_arch=[64, 64]))
        started = time.monotonic()
        model.learn(total_timesteps=args.steps)
        elapsed = time.monotonic() - started
        model.save(args.root / "ppo_movement")
        trained = evaluate(model, env, raw, range(10), replays=replays)
        result = dict(seed=args.seed, requested_steps=args.steps, actual_steps=model.num_timesteps,
                      elapsed_seconds=elapsed, steps_per_second=model.num_timesteps / elapsed,
                      engine_smoke=evidence, random_baseline=baseline, trained=trained, replays=replays,
                      scope="Actual Player.Update in a loaded isolated server; not a complete game loop or client acceptance")
        (args.root / "result.json").write_text(json.dumps(result, indent=2))
        template = Path(__file__).with_name("replay.html").read_text()
        (args.root / "replay.html").write_text(template.replace("__DATA__", json.dumps(result)))
        print("RL_EXPERIMENT_COMPLETE", json.dumps({key: value for key, value in result.items() if key != "replays"}), flush=True)
    finally:
        try:
            if raw is not None:
                raw.close()
        finally:
            try:
                engine.wait(timeout=10)
            except subprocess.TimeoutExpired:
                engine.terminate()
                engine.wait(timeout=10)
            engine_log.close()


if __name__ == "__main__":
    main()
