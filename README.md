# TerraBlind

[中文](#中文) · [English](#english)

一个 tModLoader mod：把泰拉瑞亚的感知、寻路、建造、战斗拆成一套可调用的工具，并用这些工具组合出一条**从新世界开局到打死肉山**的完整流程。
此项目完全在AI辅助下开发。

A tModLoader mod: a toolbox of perception, pathfinding, building and combat primitives for Terraria, plus one hand-written pipeline that chains them from a fresh world all the way to killing the Wall of Flesh.
This project was developed entirely with the assistance of AI.
![status](https://img.shields.io/badge/milestone-Wall%20of%20Flesh-red) ![ai](https://img.shields.io/badge/AI-none%20yet-lightgrey) ![tml](https://img.shields.io/badge/tModLoader-1.4.4.9-blue)

---

## 中文

### 这是什么

TerraBlind 里的每一样东西，本质上都是**工具**：寻路、瞄准、使用物品、开箱、放平台、盖房，等等。

`/start` 是**手工把这些工具串起来的一条流程**，目标是击败经典难度下的肉山。

> **当前 milestone：纯代码通关肉山，没有 AI 介入游戏。**

以后也许会尝试写一个 agent，让它自己去用这些工具玩Terraria。无论效果好坏。

### 完成度

主线流程已经在种子 **`1.1.2.38154567`** 上稳定跑通：输入/start → 建造速通4人NPC房屋 → 前往丛林并尽量从main entrance下地狱 → 地狱选址 → 搭桥 → 盖NPC单人房屋 → 引入爆破专家，购买雷管 → 引入向导，捅下岩浆 → 雷管定点爆破肉山。

其他种子有的能跑通，有的还会卡。世界生成的随机性太大。**"所有种子都通"不是这个项目的当前目标**。目前的做法是：遇到卡住就读日志、定位、修掉那一类结构性问题，而不是给某个种子打补丁。

为了避开一些复杂的机制/增加成功率/提升全程的观感，对原版逻辑采取的修改措施：
- 常驻鱼鳃，光芒buff
- 掉到岩浆上方，会将最表层的接触格转化为格子；岩浆内能够在脚下放方块
- 新建的玩家自带无限木材，4根火把用于初始房屋的建造
- 地狱把向导捅入岩浆的时候加长挖掘范围
- 下方的dragonlens修改

### 依赖 DragonLens

**跑这套流程目前离不开 [DragonLens](https://github.com/ScalarVector1/DragonLens)。** 它提供的这些功能是流程能跑完的前提：

| 用途 | 说明 |
|------|------|
| 关闭刷怪 | 怪物造成的影响过多 |
| 无敌 | 当前 milestone 不处理战斗生存，只求全程动作可行 |
| 倍速 | 一趟完整流程很长，调试时靠它压缩时间 |
| 坐标显示 | 对日志里的格坐标时用 |


### 流程长什么样

```
/start
  ├─ Torch     凑火把（当前无用）
  ├─ Site      选地表房址
  ├─ GotoSite  走过去
  ├─ House     盖房（21×10）
  ├─ Descend   规划一条穿过箱子与生命水晶的下降路线（主要是丛林主入口），一路采集到地狱
  └─ Hell      交给地狱段
       ├─ goto   走到桥的开工点
       ├─ house  在岩浆上方盖单间 NPC 房
       ├─ deck   沿算好的线铺 190 格桥面
       ├─ wof    买雷管 → 把向导换进房 → 挖他脚下 → 让他掉进岩浆
       └─ fight  肉山出现，边退边扔雷管
```

### 怎么跑

**只想用**：下载 release 里的 `.tmod`，丢进 `tModLoader/Mods/`，在游戏的模组列表里启用。

**要改源码**：

1. 把仓库放进 `tModLoader/ModSources/TerraBlind/`
2. 改完先跑 `./check.sh` 本地类型检查（不打包，游戏开着也能跑，约 1 秒）
3. 游戏主界面 → 创意工坊 → 开发模组 → 构建并重新加载（TerraBlind）

进世界后 HTTP 服务自动起在 `http://127.0.0.1:17878`。

触发主线（二选一）：

- 游戏内快捷键 / 聊天命令
- 配套 Python 仓库后[Terraria-Agent](https://github.com/Reisenbug/Tairaria) 使用 `/tb `。**Python 主要负责触发。**（尚未完工）

### 调试

| 东西 | 说明 |
|------|------|
| `/vis on` / `/vis off` | 调试图层总开关。桥线、房址框、宝藏框会画出来；录像前关掉 |
| `TerraBlindLogs/latest/` | 最近一局的日志，`latest` 是指向带时间戳目录的软链 |
| `latest/jump_trace.log` | 主时间线：规划、派发、每一步的决策 |
| `latest/events/*.log` | 按类分流：`plan` / `place` / `fail` / `exec` / `sentinel` |
| `latest/runs/<起点>__<终点>.log` | 单次导航的独立日志 |
| `./check.sh` | 本地类型检查，过了再进游戏构建 |

日志里的关键判断都会打出**当时用来判断的那个数**，而不只是结论。不然出问题只能靠猜。

### AI 辅助开发的工作流

这个项目是在 AI 辅助下写的，仓库里有几样东西专为此存在。

| 环节 | 做法 |
|------|------|
| 编译自验 | `./check.sh` 只做类型检查不打包，约 1 秒。游戏开着也能跑，改完立刻知道编译过没过 |
| 定位自验 | 三层日志 + 一条规矩：**每个判断都要打出它依据的那个数**，不只打结论。有了实测值才能定位，没有就只能猜 |
| 机器下限 | `stuck_contract.sh` 检查每个原语是否交出了失败现场；`dup.sh` 查同一件事有没有两份实现 |
| 写码约束 | `.claude/hooks/` 里挂了两条：注释块不许超长、标点必须 ASCII。|

真正花时间的是**判断哪个环节出了问题**。上面这套的意义就是把"读日志找原因"这一步也变成 AI 能独立做完的事。

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

Everything in TerraBlind is a **tool**: pathfinding, aiming, using items, opening chests, placing platforms, building houses, and so on.

`/start` is **one pipeline wired by hand** out of those tools, aimed at beating the Wall of Flesh on Classic difficulty.

> **Current milestone: beat the Wall of Flesh with code only. No AI plays the game.**

An agent that drives these tools by itself may come later, however well or badly it works.

### How far it gets

The pipeline completes reliably on seed **`1.1.2.38154567`**: type `/start`, speed-build a 4-room NPC house, head for the jungle and descend to hell through the main entrance where possible, pick a hell site, bridge, build a single-room NPC house, move the Demolitionist in and buy dynamite, move the Guide in and drop him into lava, then blow up the Wall of Flesh with placed dynamite.

Other seeds sometimes finish and sometimes get stuck. World generation varies enormously. **"Works on every seed" is not the current goal.** The approach is: when it gets stuck, read the log, find the structural cause, fix that class of problem. Never patch one seed.

Changes to vanilla behaviour, made to sidestep awkward mechanics, raise the success rate, and keep the run watchable:

- Gills and Shine buffs are always on
- Landing above lava turns the surface contact tile into a block; blocks can be placed underfoot inside lava
- A new player starts with unlimited wood and 4 torches for the first house
- Reach is extended while digging the Guide into lava
- The DragonLens toggles below

### DragonLens is required

**The pipeline currently depends on [DragonLens](https://github.com/ScalarVector1/DragonLens).** These features are prerequisites:

| Toggle | Why |
|--------|-----|
| Disable spawns | Enemies interfere too much |
| Godmode | This milestone does not handle combat survival, only that every action is feasible |
| Fast-forward | A full run is long; this compresses debugging time |
| Coordinate display | For cross-checking tile coordinates against the logs |

### The pipeline

```
/start
  ├─ Torch     gather torches (currently unused)
  ├─ Site      choose a surface house site
  ├─ GotoSite  walk there
  ├─ House     build it (21x10)
  ├─ Descend   plan a descent threading chests and life crystals (mainly via the
  │            jungle main entrance), looting all the way to hell
  └─ Hell      hand off to the hell stage
       ├─ goto   walk to the bridge's work point
       ├─ house  build a single NPC room above lava
       ├─ deck   lay the 190-tile deck along the planned line
       ├─ wof    buy dynamite, move the Guide in, dig under him, drop him into lava
       └─ fight  Wall of Flesh spawns; retreat and throw dynamite
```

### Running it

**Just to play it**: download the `.tmod` from releases, drop it into `tModLoader/Mods/`, enable it in the in-game mod list.

**To change the source**:

1. Put the repo in `tModLoader/ModSources/TerraBlind/`
2. Run `./check.sh` first (type check only, no packing, works while the game runs, ~1s)
3. Main menu, Workshop, Develop Mods, Build and Reload (TerraBlind)

The HTTP server starts automatically on `http://127.0.0.1:17878` when a world loads.

Two ways to trigger the pipeline:

- An in-game hotkey or chat command
- `/tb` from the companion Python repo [Terraria-Agent](https://github.com/Reisenbug/Tairaria). **Python mostly just triggers.** (unfinished)

### Debugging

| Thing | What it is |
|-------|------------|
| `/vis on` / `/vis off` | Master switch for the debug overlay. Turn it off before recording |
| `TerraBlindLogs/latest/` | Most recent run; `latest` symlinks to a timestamped directory |
| `latest/jump_trace.log` | Main timeline: planning, dispatch, per-step decisions |
| `latest/events/*.log` | Split by kind: `plan` / `place` / `fail` / `exec` / `sentinel` |
| `latest/runs/<from>__<to>.log` | Per-navigation log |
| `./check.sh` | Local type check; pass this before building in-game |

Every significant decision logs **the number it was based on**, not just the verdict. Otherwise diagnosis is guesswork.

### The AI-assisted workflow

This project was written with AI assistance, and a few things in the repo exist for that.

| Step | How |
|------|-----|
| Verify it compiles | `./check.sh` type-checks without packing, about 1s. Works while the game is running |
| Verify what went wrong | Three log layers plus one rule: **every decision prints the number behind it**, not just the verdict. Without measured values there is nothing to diagnose from |
| Machine-enforced floor | `stuck_contract.sh` checks that every primitive hands up a failure; `dup.sh` looks for the same thing implemented twice |
| Writing constraints | Two hooks in `.claude/hooks/`: comment blocks may not run long, punctuation must be ASCII. |

The time goes into working out **which part broke**. The point of the above is to make that step something the AI can finish on its own.

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
