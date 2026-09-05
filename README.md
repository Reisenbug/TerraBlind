# TerraBlind

[中文](#中文) · [English](#english)

一个 tModLoader mod：把泰拉瑞亚的感知、寻路、建造、战斗拆成一套可调用的工具，并用这些工具组合出一条**从新世界开局到打死血肉之墙**的完整流程。

A tModLoader mod: a toolbox of perception, pathfinding, building and combat primitives for Terraria, plus one hand-written pipeline that chains them from a fresh world all the way to killing the Wall of Flesh.

![status](https://img.shields.io/badge/milestone-Wall%20of%20Flesh-red) ![ai](https://img.shields.io/badge/AI-none%20yet-lightgrey) ![tml](https://img.shields.io/badge/tModLoader-1.4.4.9-blue)

---

## 中文

### 这是什么

TerraBlind 里的每一样东西，本质上都是**工具**：找路、跳、搭桥、挖矿、开箱、放平台、盖房、瞄准、扔雷管。

`/start` 不是"AI"，它是**我手工把这些工具串起来的一条流程**，目标是打死血肉之墙。流程里的每一步都是写死的判断和调用，没有任何模型参与决策。

> **当前 milestone：纯代码通关肉山，没有 AI 介入。**

以后我大概会写一个 agent，让它自己去用这些工具玩游戏。无论效果好坏，总要试一次。但那不在这个 milestone 里，现在仓库里也没有任何相关代码。

### 完成度

主线流程已经在种子 **`1.1.2.38154567`** 上稳定跑通：开局 → 采集下降 → 地狱选址 → 搭桥 → 盖房 → 换向导 → 捅进岩浆 → 雷管打死肉山。

其他种子有的能跑通，有的还会卡。世界生成的随机性太大（种子有几十亿个），**"所有种子都通"不是这个项目的目标，也不现实**。目前的做法是：遇到卡住就读日志、定位、修掉那一类结构性问题，而不是给某个种子打补丁。

### 依赖 DragonLens

**跑这套流程目前离不开 [DragonLens](https://github.com/ScalarVector1/DragonLens)。** 它提供的这些开关是流程能跑完的前提：

| 用途 | 说明 |
|------|------|
| 关闭刷怪 | 否则怪物会持续打断建造和赶路 |
| 无敌 | 当前 milestone 不处理战斗生存，只求全程动作可行 |
| 倍速 | 一趟完整流程很长，调试时靠它压缩时间 |
| 坐标显示 | 对日志里的格坐标时用 |

这不是"作弊换通关"，而是**先把导航与建造这层做对**：血量、逃跑、打怪是另一个 milestone 的事。

### 流程长什么样

```
/start
  ├─ Torch     凑火把
  ├─ Site      选地表房址
  ├─ GotoSite  走过去
  ├─ House     盖房（21×10）
  ├─ Descend   规划一条穿过箱子与生命水晶的下降路线，一路采集到地狱
  └─ Hell      交给地狱段
       ├─ goto   走到桥的开工点
       ├─ house  在岩浆上方盖单间 NPC 房
       ├─ deck   沿算好的线铺 190 格桥面
       ├─ wof    买雷管 → 把向导换进房 → 挖他脚下 → 让他掉进岩浆
       └─ fight  肉山出现，边退边扔雷管
```

地狱那条桥的行不是随便挑的：`HellLine` 用 Dijkstra 在天花板与岩浆面之间搜一条 190 格的线，同时权衡挖掘量、坡度（升降一格至少跨 4 列）、离岩浆/天花板的距离，并且拒绝任何以现有镐力挖不动的格子。

### 怎么跑

```bash
# 1. 放进 tModLoader 的 ModSources
#    tModLoader/ModSources/TerraBlind/

# 2. 改完代码先本地类型检查（不打包，游戏开着也能跑，约 1 秒）
./check.sh

# 3. 游戏内构建
#    Mod Sources → Build + Reload   或聊天框输入 /build TerraBlind
```

进世界后 HTTP 服务自动起在 `http://127.0.0.1:17878`。

触发主线（二选一）：

- 游戏内快捷键 / 聊天命令
- 配套 Python 仓库 [Terraria-Agent](https://github.com/Reisenbug/Tairaria) 里的 `/tb N`。**Python 只负责触发，所有编排都在 mod 里**

### 调试

| 东西 | 说明 |
|------|------|
| `/vis on` / `/vis off` | 调试图层总开关。桥线、房址框、宝藏框会画出来；录像前关掉 |
| `TerraBlindLogs/latest/` | 最近一局的日志，`latest` 是指向带时间戳目录的软链 |
| `latest/jump_trace.log` | 主时间线：规划、派发、每一步的决策 |
| `latest/events/*.log` | 按类分流：`plan` / `place` / `fail` / `exec` / `sentinel` |
| `latest/runs/<起点>__<终点>.log` | 单次导航的独立日志 |
| `./check.sh` | 本地类型检查，过了再进游戏 `/build` |

日志里的关键判断都会打出**当时用来判断的那个数**，而不只是结论。不然出问题只能靠猜。

### 代码结构

141 个 `.cs`，按职责分目录（namespace 统一是 `TerraBlind`，不跟目录走）：

| 目录 | 内容 |
|------|------|
| `Core/` | 判据与共享概念：够不着的四把尺子、格子分类、失败原因、光标瞄准、动作执行 |
| `Perception/` | 眼睛：状态快照、物理模拟器、建造录制、差分 |
| `Nav/` | 寻路：距离场、贪心逐步、状态空间 A\*、陷阱逃逸、可视化 |
| `Actions/` | 原语：放置、挖掘、柱子、平台梯、走位、合成、购物、脱困栈 |
| `Build/` | 建造：地狱线规划、桥面铺设、房屋、桥起点 |
| `Flow/` | 流程编排：`/start` 主链、地狱段、肉山准备与战斗、顺路采集 |
| `Infra/` | HTTP 服务、日志、聊天输出 |
| `Debug/` | 录制回放、卡死哨兵、冻结、可视化命令 |

设计上的一条硬规则：**"卡住"必须在结构上不可能**。每个原语失败时都要交出可被上层处理的失败现场（`Blocker`），而不是静默 `return`。这条由 `check.sh` 里的契约检查强制。

### 文档

| 文件 | 内容 |
|------|------|
| [`CAPABILITIES.md`](CAPABILITIES.md) | 已有能力清单。写新动作前先查，别重复造 |
| [`PROJECT_STATE.md`](PROJECT_STATE.md) | 项目总体状态与方向 |
| [`PERCEPTION_DESIGN.md`](docs/PERCEPTION_DESIGN.md) | 感知/执行架构设计 |
| [`DECISIONS.md`](docs/DECISIONS.md) | 决策记录：每个坑为什么这么填 |
| [`BRIDGE_CASES.md`](docs/BRIDGE_CASES.md) | 碰撞箱与搭桥的边界情形 |

### HTTP 接口

152 个端点，完整清单见 [`CAPABILITIES.md`](CAPABILITIES.md)。主线只要这几个：

| 端点 | 作用 |
|------|------|
| `POST /start_run` | 起整条主线 |
| `GET /start_run_status` | 当前相位与结果 |
| `GET /start_run_stop` | 停 |
| `POST /hell_run` | 只跑地狱段（桥 + 房 + 肉山） |
| `GET /state` | 完整游戏状态快照 |
| `GET /health` | 连通性检查 |

> 地址一律用 `127.0.0.1`，**不要用 `localhost`**。它会走 `::1`，前缀匹配不上，表现得像端点没注册。

### 环境

- Terraria 1.4.4.9
- tModLoader（对应 1.4.4.9 的版本）
- DragonLens（见上）

### License

MIT

---

## English

### What this is

Everything in TerraBlind is a **tool**: pathfind, jump, bridge, mine, open chests, place platforms, build houses, aim, throw dynamite.

`/start` is not "an AI". It is **one pipeline I wired by hand** out of those tools, aimed at killing the Wall of Flesh. Every step in it is hard-coded logic. No model makes any decision.

> **Current milestone: beat the Wall of Flesh with code only. No AI involved.**

I will probably write an agent later that drives these tools itself. Worth trying regardless of how well it works. That is not part of this milestone, and no such code is in the repo yet.

### How far it gets

The pipeline completes reliably on seed **`1.1.2.38154567`**: spawn, descend while looting, pick a hell site, bridge, house, swap the Guide in, drop him into lava, dynamite the Wall of Flesh.

Other seeds sometimes finish and sometimes get stuck. World generation varies enormously (billions of seeds), so **"works on every seed" is neither the goal nor realistic**. The approach is: when it gets stuck, read the log, find the structural cause, fix that class of problem. Never patch one seed.

### DragonLens is required

**The pipeline currently depends on [DragonLens](https://github.com/ScalarVector1/DragonLens).** These toggles are prerequisites:

| Toggle | Why |
|--------|-----|
| Disable spawns | Otherwise enemies constantly interrupt building and travel |
| Godmode | This milestone does not handle combat survival, only that every action is feasible |
| Fast-forward | A full run is long; this compresses debugging time |
| Coordinate display | For cross-checking tile coordinates against the logs |

This is not "cheating to win". It is **getting navigation and building right first**: health, fleeing and fighting belong to a different milestone.

### The pipeline

```
/start
  ├─ Torch     gather torches
  ├─ Site      choose a surface house site
  ├─ GotoSite  walk there
  ├─ House     build it (21×10)
  ├─ Descend   plan a descent threading chests and life crystals, looting on the way
  └─ Hell      hand off to the hell stage
       ├─ goto   walk to the bridge's work point
       ├─ house  build a single NPC room above lava
       ├─ deck   lay the 190-tile deck along the planned line
       ├─ wof    buy dynamite, move the Guide in, dig under him, drop him into lava
       └─ fight  Wall of Flesh spawns; retreat and throw dynamite
```

The bridge row is not arbitrary: `HellLine` runs Dijkstra between the ceiling and the lava surface to find a 190-tile line, trading off digging volume, slope (a one-row change must span at least 4 columns), clearance from lava and ceiling, and refusing any tile the current pickaxe cannot break.

### Running it

```bash
# 1. Drop into tModLoader's ModSources
#    tModLoader/ModSources/TerraBlind/

# 2. Type-check locally before building (no packing, works while the game runs, ~1s)
./check.sh

# 3. Build in-game
#    Mod Sources → Build + Reload   or type /build TerraBlind in chat
```

The HTTP server starts automatically on `http://127.0.0.1:17878` when a world loads.

Trigger the pipeline either from an in-game command, or via `/tb N` in the companion Python repo [Terraria-Agent](https://github.com/Reisenbug/Tairaria). **Python only triggers; all orchestration lives in the mod.**

### Debugging

| Thing | What it is |
|-------|------------|
| `/vis on` / `/vis off` | Master switch for the debug overlay. Turn it off before recording |
| `TerraBlindLogs/latest/` | Most recent run; `latest` symlinks to a timestamped directory |
| `latest/jump_trace.log` | Main timeline: planning, dispatch, per-step decisions |
| `latest/events/*.log` | Split by kind: `plan` / `place` / `fail` / `exec` / `sentinel` |
| `latest/runs/<from>__<to>.log` | Per-navigation log |
| `./check.sh` | Local type check; pass this before `/build` |

Every significant decision logs **the number it was based on**, not just the verdict. Otherwise diagnosis is guesswork.

### Code layout

141 `.cs` files grouped by responsibility (the namespace is flat `TerraBlind`; it does not follow directories):

| Directory | Contents |
|-----------|----------|
| `Core/` | Predicates and shared concepts: the four reach rulers, cell classification, blockers, cursor aiming, action execution |
| `Perception/` | The eye: state snapshots, physics simulator, build recording, diffing |
| `Nav/` | Pathfinding: distance field, greedy stepping, state-space A\*, trap escape, visualization |
| `Actions/` | Primitives: place, mine, pillar, platform ladder, settle, craft, shop, the unstick stack |
| `Build/` | Building: hell line planning, deck laying, houses, bridge start |
| `Flow/` | Orchestration: the `/start` chain, hell stage, WoF prep and fight, opportunistic looting |
| `Infra/` | HTTP server, logging, chat output |
| `Debug/` | Record/replay, stuck sentinel, freeze, visualization commands |

One hard rule: **getting stuck must be structurally impossible.** Every primitive must hand a usable failure (`Blocker`) up the stack rather than silently `return`ing. `check.sh` enforces this contract.

### Docs

| File | Contents |
|------|----------|
| [`CAPABILITIES.md`](CAPABILITIES.md) | Inventory of existing capabilities. Check before writing a new action |
| [`PROJECT_STATE.md`](PROJECT_STATE.md) | Overall project state and direction |
| [`PERCEPTION_DESIGN.md`](docs/PERCEPTION_DESIGN.md) | Perception/execution architecture |
| [`DECISIONS.md`](docs/DECISIONS.md) | Decision log: why each pitfall is handled the way it is |
| [`BRIDGE_CASES.md`](docs/BRIDGE_CASES.md) | Hitbox and bridge-placement edge cases |

### HTTP API

152 endpoints; the full list is in [`CAPABILITIES.md`](CAPABILITIES.md). The pipeline only needs these:

| Endpoint | Purpose |
|----------|---------|
| `POST /start_run` | Start the whole pipeline |
| `GET /start_run_status` | Current phase and outcome |
| `GET /start_run_stop` | Stop |
| `POST /hell_run` | Hell stage only (bridge + house + WoF) |
| `GET /state` | Full game state snapshot |
| `GET /health` | Connectivity check |

> Always use `127.0.0.1`, **never `localhost`**. It resolves to `::1`, the prefix does not match, and it looks like the endpoint was never registered.

### Requirements

- Terraria 1.4.4.9
- tModLoader (matching 1.4.4.9)
- DragonLens (see above)

### License

MIT
