# 已有能力清单

动手前先查这里。造轮子之前先搜一遍关键词。

约定:
- **异步原语** = `POST /x` 返回 `{accepted}` → 轮询 `/x_status` 拿 `{outcome}` → `/x_stop` 中止。
  `outcome` 取值:`running` / `done` / 各自的失败原因。
- 所有原语的终止判据都是**读世界事实**(格子里有没有那个 tile、脚有没有落地),不是帧数。

---

## 1. 移动

| 能力 | 接口 | 说明 |
|---|---|---|
| 跨地形寻路 | `/nav_recede` + `_done` `_stop` | 主力。会跳/挖/搭桥/砸罐子。**容差 24px(1.5格)**,停在隔壁列也算"到了" |
| **精确对齐到某一列** | `/settle {col}` | `SettleAt`。按 vanilla `runSlowdown` 算刹车距离,判定=中心距列心 ≤8px。**nav 之后要精确站位就用它** |
| 跳到某一行(可先对齐列) | `/hop_up {row, col}` | `col` 可选,给了就先横向走到那列再跳。内部有 Align 相位 |
| 走到平台边缘 | `/walk_to_edge {direction, extra_tiles}` | |
| 从平台掉下去 | `/drop` | 按住 `controlDown` 穿过脚下平台,落到实心地面 |
| **踩平台一格格下降** | `PlatformDown` (键 O) | 站干净→在 `feetY+2` 放平台→下键穿过→重复。列钉死在**平台**那列(`IsPlat`,砖不算),对不齐调 `SettleAt` |
| 单次跳跃 | `/jump` `/exec_jump_to` | |

## 2. 建造

| 能力 | 接口 | 说明 |
|---|---|---|
| 搭绳梯 | `/rope_ladder {item, n}` | 从人脚下那列往上。列在开工时钉死。status 报 `top` / `above_top` 给后续步骤当锚点 |
| 搭平台桥 | `/bridge {item, dir, n}` | 横向。放到手够不着就走出去再放 |
| 搭平台柱 | `/pillar {n}` | 往上。人右边一列,够不着就跳起来放 |
| 放背景墙 | `/place_walls {cells[]}` | 严格按给定顺序(vanilla 墙体合并依赖顺序) |
| 放一个东西 | `/place_at {item, x, y}` | 语义放置:只说放什么、放哪 |
| 边走边放家具 | `/walk_place {dest_x, targets[]}` | 走向目标列,路过够得着的目标就放 |
| 录制/回放建筑 | `/build_rec_start` `/build_replay_start` | 世界 diff 记录最终结构,不是记录过程 |

## 3. 感知(只读,同步返回)

| 能力 | 接口 | 说明 |
|---|---|---|
| 玩家+世界快照 | `/state` | 背包只报非空槽,带绝对 slot |
| **脚下那一格** | `/origin` | 覆盖像素最多的列,平分取左 |
| 读一片地形 | `/terrain {cx,cy,w,h}` | ASCII:`.`空 `#`实心 `-`平台 `+`有tile但非固体(树/草/藤) |
| 单格详情 | `/probe_cell {x,y}` | |
| 找某种 tile | `/find_tiles` | |
| 能不能站 | `/can_stand {x,y}` | |
| 找平地 | `/scan_flat {w,h,hazard_r,range}` | |
| **找房址(L形)** | `/scan_house {w,h,rope_h,range}` | 验证:落脚点 `CanStand` + 绳梯列 `Vacant` + 顶上 w×h `Vacant`。**注意:只验证落脚点那一列** |
| 房间合法性 | `/room_check {x,y}` | |
| 背包有多少 | `/have {id}` | |
| 到某格的真实代价 | `/path_cost {x,y}` | 返回 `dig` / `walk` 的**格数**(不是 cost) |
| 下地狱路线 | `/find_descent` `/descent_route` | |
| 找生物群系 | `/find_biome {name}` | |
| 找 NPC | `/npc_find {type}` | |
| 配方查询 | `/recipe {item}` | 返回材料 need/have + 需要的工作台 |

### Predicates（C# 内部谓词，写 mod 时直接用）
`InBounds` `IsSolid` `IsGround` `IsPassable` `IsLava` `IsAnyLiquid` `CanStand`
`Headroom(cap)` `ClearWidth(cap)` `NearHazard(r,lavaOnly)` **`Vacant`** `ScanHouse` `ScanFlat`
`RoomJson` `Have` `NpcJson` `CellJson`

> **`IsPassable` ≠ `Vacant`**:树/草是"不挡路"但"格子被占",放不进东西。
> 要判断能不能放置,用 `Vacant`(`!HasTile && WallType==0`)。这个坑踩过两次。

## 4. 动作

| 能力 | 接口 | 说明 |
|---|---|---|
| 通用动作原语 | `/act {steps[]}` | 步骤串行、步内并行。每步必须带 `until`。`invariant` 三选一 |
| 挖 | `/mine` `/mine_reach` | |
| 用物品(带观测) | `/item_use` + `_status` | 放置会观测目标格是否长出 `createTile` |
| 交互 | `/interact` | 开箱子等 |
| 合成 | `/craft` | 按内部名解析,失败报 `free_slots` |
| 全部拾取 | `/loot_all` | |
| 打架 | `/fight` `/fight_active` | |
| 喝药 | `/quick_heal` | |
| 换手持 | `/swap` | |

## 5. 保命 / 反卡(自动，不用调)

| 能力 | 位置 | 说明 |
|---|---|---|
| 保命反射 | `SurvivalReflex` | 每帧跑。跳出岩浆、掉血喝药。触发后推 `interrupted` 事件 |
| 卡死哨兵 | `StuckSentinel` | 每帧看四个信号(位移/H/挖掘伤害/周围tile)。0.5s 内走安全步,6-8s 放弃这段 |
| 顺手砸罐子/蜘蛛网 | `RecedingNav.SmashPot/SmashWeb` | 赶路时自动 |
| 平台自动补货 | `second_player._top_up_platforms` | 少于 50 就合成到 150,每段路开始前查一次 |

## 6. 调试

| 能力 | 接口 |
|---|---|
| 断点/单步 | `/breakpoint_set` `/step_node` `/continue` `/freeze` `/unfreeze` |
| 决策可视化 | `RecedingVis` — 白框=当前H,蓝线=场梯度,绿框=降H候选,黄框=选中 |
| 卡死快照 | `StuckSnapshot` — 检测到循环时把整个决策局面写盘 |
| 日志 | `Main.SavePath/TerraBlindLogs/jump_trace.log`,每段路另存 `runs/sx_sy__gx_gy.log` |

---

## 已知的坑

1. **`IsPassable` ≠ `Vacant`** — 见上。树干那一列 `IsPassable` 全过,但绳子放不进去。
2. **nav 容差 1.5 格** — 要精确站位必须 nav 之后再 `/settle`。
3. **原语默认用"人现在站的那一列"** — `rope_ladder`/`pillar` 都是。人飘一格,东西就盖到隔壁去了。传坐标或先 settle。
4. **`ItemID.Search` 查内部名** — `createItem.Name` 在中文环境返回中文,别用来匹配。
5. **绳子叫「绳」不叫「绳子」** — 本地化表里是 `ItemName/Rope = "绳"`。
6. **`StepCost` 的 `Impassable = int.MaxValue`** — 相加会溢出成大负数,消费方必须先 `continue`。
7. **玩家 20px 宽 / 42px 高** — 占 2 列 3 行。方块和平台放不进碰撞箱,绳子可以。

## 已实现但目前没用上的

`/scan_flat` `/room_check` `/npc_find` `/measure` `/nav_h` `/can_stand` `/jump_envelope`
`/mark_placeable` `/sim_jump` `/test_plat_*` `/debug_jump_edges` `WaypointPlanner` `SegmentedNavCoordinator`
`ActionGraphPlanner`(被 `StateSpacePlanner` 取代) `BuildOverlay`

> 需要类似功能时先看这一节 —— 大概率已经有了。
