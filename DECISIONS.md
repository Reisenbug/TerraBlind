# TerraBlind 决策记录

记录关键设计决策、排除方案和已知局限。按时间倒序追加。

---

## 2026-05-18 平台放置判定逻辑

### 完整判定流程（源码：Player.cs）

**第一关：目标格条件**（318112行）
目标格必须满足以下之一才进入放置流程：
- `!tile.active()`（air）
- `PlaceThing_IsReplacableBlock(tile)`（318122行）：`tileCut[type]=true` 的格子（植物、藤蔓、石堆 type=185 等）或 `BreakableWhenPlacing[type]=true`——这类格子会被自动替换

**第二关：邻居条件**（`BlockPlacementForAssortedThings`，319618行 else 分支）
上下左右4格中至少一个满足：
- `active() && (tileSolid[type] || IsBeam[type] || tileRope[type] || type==314)`
- 或 `wall > 0`（背景墙）
- 或目标格本身 `wall > 0`

**第三关：平台专属兜底**（319700行）
若第二关失败，平台额外检查 `[-1,1]×[-1,1]` 的3×3区域：
```
只要有任何 active() 的格子 → canPlace=true
```
这就是为什么藤蔓、type=185 石堆、植物等 `tileSolid=false` 的物体**附近**也能放平台——它们 `active()=true` 满足兜底条件。

### 对规划层的意义
`CanPlacePlatform(wx, wy)` 的判断：
1. 目标格是 air 或 tileCut 物体
2. 且 `[-1,1]×[-1,1]` 的9格内有任何 `active()` 格子（或背景墙）

// ASSUMPTION: 此逻辑来自 1.4.5.4 原版，tModLoader 未修改

---

## 2026-05-18 A* 行为 cost / 触发条件 / 限制条件一览

### 搜索范围
| 参数 | 值 | 说明 |
|------|-----|------|
| GoalRangeFwd | 60格 | A* 向前（sign方向）最远扩展 |
| GoalRangeBack | 60格 | A* 向后最远扩展 |
| AStarScanUp | 50格 | A* 向上最远扩展 |
| AStarScanDown | 50格 | A* 向下最远扩展 |
| HeuristicWeight | 3 | weighted A*，h×3，加速收敛，牺牲最优性 |
| maxMineDepth | \|goalX-pcx\|+\|goalY-feetY\|+8 | mine 边最大串联深度，动态计算 |

### 边 cost 公式
| 边类型 | cost 公式 | 触发条件 | 限制条件 |
|--------|-----------|---------|---------|
| move | `1 + DistToGround(nx,ny)` | `Standable(cx,cy)`，目标格非solid | 头顶两列 ny-1/ny-2 净空；双列 fall 检查 |
| fall | `0.5 × 格数` | `cx` 和 `cx+1` 两列脚下都无 floor | 逐格展开，A* 自动串联 |
| jump | `max(col + overhead - riseBonus, 1)` | 头顶净空（或 hc=true），距离≥2格 | hold∈{8,12,15}；ArcClipsWall 过滤；JumpMinCol=2 |
| pillar | `3 + rise × 6` | Standable，头顶净空（或 hc=true），rise>7 | leftClear 双列检查；rise≤7不生成 |
| bridge | `4 + col × 2 + penalty` | Standable，沿途头顶3格净空 | MaxBridge=25格；BridgeDtgThresh=8格免penalty |
| mine_right | `solidCount × 6 + 1` | canMineFrom，目标列非Standable，脚下有floor | mineDepth < maxMineDepth |
| mine_left | `solidCount × 6 + 1` | 同上（检查 cx-2 列） | 同上 |
| mine_down | `solidCount × 6 + 0.5` | canMineFrom，脚下无floor | 同上 |
| mine_up | `solidCount × 6` | canMineFrom，hc=false，头顶有solid | 同上；dst.hc=true |

**备注：**
- `solidCount`：目标范围内实际为 solid 的格数（air 格不计 cost）
- `riseBonus = max(0, rise-1) × 2`：jump 高度奖励
- `overhead = JumpOverheadMax × (1 - hold/maxHold)`：短跳惩罚，hold=15时为0，hold=8时为4×(1-8/15)≈1.87
- `BridgePenalty = max(0, 8-dtg) × 2`：桥接距地面近时的惩罚
- `canMineFrom = (Standable || mineNode) && mineDepth < maxMineDepth`

### 执行层限制（NavCoordinator）
| 参数 | 值 | 说明 |
|------|-----|------|
| StallFrames | 60帧 | pcx 连续不变60帧触发 replan |
| PillarThresh | 8格 | move 节点 rise>8 时强制走 pillar |
| ArriveX | 8px | bridge 到达判定距离 |
| BlacklistTTL | 3600帧（60s） | 失败节点黑名单有效期 |
| BlacklistMax | 20 | 黑名单满时 NavState=Failed |

### PlanTo vs Plan 的区别
- `Plan(sign)`：地表导航，有 fallback（heap耗尽→最远前进节点），单向扫描
- `PlanTo(wx,wy)`：指哪去哪，noFallback=true（heap耗尽→空路径），bidir=true（双向扫描）

---

## 2026-05-18 NavWand 调试日志机制

### 决策
NavWand 左键规划时，PathPlanner 自动向 `jump_trace.log` 写入规划过程和验证数据。无需额外激活，只要使用 NavWand 左键点击即触发。

### 日志位置
```
~/Library/Application Support/Terraria/tModLoader/TerraBlindLogs/jump_trace.log
```

### 日志提供的信息
- `[plan] goal=... start=...`：规划起点、终点、路径长度、总 cost
- `[verify] edge_emit type=jump/pillar ...`：每条 jump/pillar 边的参数（hold、startVx、wall_frames、ceil_frames）
- `[plan] jump edge wallclip`：被 ArcClipsWall 过滤掉的跳跃弧
- `[wand] target=... path=N`：NavWand 点击的目标和规划出的节点数
- `[nav] node[N]`：执行层进入每个节点时的状态
- `[nav] Replan`：触发重规划的时机和原因

### 查看方式
```bash
# 清空后测试
> "$HOME/Library/Application Support/Terraria/tModLoader/TerraBlindLogs/jump_trace.log"
# 查看规划+验证相关
grep -E "verify|plan\]|wand|wallclip" jump_trace.log
# 查看执行流
grep -E "node|Replan|stall|mine" jump_trace.log
```

---

## 2026-05-18 A* 节点签名扩展：head_clear 字段

### 决策
A* 节点从 `(cx, cy)` 扩展为 `(cx, cy, bool hc)`，`hc` 表示头顶2格是否已挖空。

### 原因
mine_up 边需要表达"原地挖头顶后状态改变"，但玩家坐标不变。若节点仍为 `(cx,cy)`，挖前和挖后是同一节点，A* 无法区分，会产生自环。扩展签名是最小侵入的解法。

### 排除方案
- **mine_up_then_jump 复合边**：把挖掘和跳跃合并为一条边，避免节点扩展。缺点：cost 估算不准，无法与独立 jump 边复用，执行层耦合。
- **world state 进节点签名**：记录所有已挖格。状态空间爆炸，不可行。

### 已知局限
- `hc=true` 仅在玩家未位移时有效。任何位移（move/fall/jump/mine_lr/mine_down）都将 dst.hc 设为 false。
- pillar 上升中途遇到头顶实心，SkillExecutor 仍会 Stop，由 NavCoordinator replan 处理，而不是在规划层预先处理。

---

## 2026-05-18 ArcClipsWall 起跳第一帧漏检修复

### 决策
`ArcClipsWall` 的过滤条件从 `prevVy >= 0f` 改为 `prevVy > 0f`。

### 根因
起跳第一帧 `prevVy=0`（初始状态），满足 `>= 0` 被跳过，但此时玩家头顶已进入天花板格。1格厚天花板恰好在起跳第一帧就被命中，导致穿墙规划。

### 验证
模拟 hold=8 startVx=0 起跳：f=0 时 `py=1776.99`，`tileY0=111`（天花板），`prevVy=0` 原本被跳过，修改后正确检测。

// ASSUMPTION: 下降阶段（prevVy>0）仍不检查侧面碰撞，仅检查头顶行 tileY0

---

## 2026-05-18 ArcClipsWall 天花板检测缺陷

### 现象
jump 边从 pillar 节点起跳（startVx=0），实际穿过一层方块，但 `ceil_frames=0`（规划层认为无碰撞）。

### 根因
`ArcClipsWall`（PathPlanner.cs 第163行）：
- 只在 `prevVy < 0`（上升阶段）检查，下降阶段不检查
- 只检查 `tileY0`（头顶第一行），未检查 `tileY0-1`
- 接近顶点时 `prevVy` 可能已 `>=0`，导致顶点附近的方块被跳过

### 当前状态
未修复。属于规划层已知缺陷，不影响大多数地形，极端情况（低天花板跳跃）会产生穿墙规划。

// FRAGILE: ArcClipsWall 不检测下降阶段碰撞，顶点附近实心块可能被漏检

---

## 2026-05-18 碰撞箱双列 predicate 改造（第1-2步）

### 决策
- fall 边生成条件改为双列：`cx` 和 `cx+1` 两列下方都无 floor 才生成
- move 边头顶检查改为双列：`(nx,ny-1/2)` 和 `(nx+dx,ny-1/2)` 都要净空

### 原因
玩家碰撞箱宽20px跨2列，单列 predicate 会产生"单格缝隙可下落"的误判，导致规划出玩家实际无法执行的 fall 边和 move 边。

### 排除方案
- **Standable 双列**：改动范围最大，影响所有边的源节点判断，风险高，列为第3步待做。

### 已知局限
- Move 边头顶检查目前用 `nx+dx` 作为第二列，仅在单步移动时正确。斜向移动（dx=±1,dy=±1）的第二列计算可能有偏差，待验证。

---

## 2026-05-17 mine 边 cost 对标实际时间

### 决策
- `MineCostPerTile = 6`（挖1格≈走6格）
- `pillar cost = 3 + rise * 6`（pillar 1格≈走6格）

### 数据依据
- 10秒内：行走约135格，挖掘约22格，bridge 放砖约同行走
- walk 1格 = 0.074s，mine 1格 = 0.455s，比例 ≈ 6.1×
- pillar 10秒上升约24格，比例 ≈ 5.6×，取整为6

### 已知局限
- 数据基于裸玩家，有速度 buff 时比例会变（已有 WARN 日志）
- bridge cost 未重新对标，沿用旧值

---

## 2026-05-16 mine 边并入 foreach 循环

### 决策
mine_right/left/down 边从独立的 `if (canMine)` 块合并进 move/fall 的 `foreach` 循环，遇到 `Solid(nx,ny)` 时判断是否生成 mine 边。

### 原因
原实现三个独立块代码重复，与 move/fall 边结构不一致。合并后4个方向统一处理，新增方向只需加 case。

### mineNodes 机制
mine 边终点加入 `mineNodes`，`canMineFrom = Standable || mineNodes.Contains`，允许 mine 节点继续展开 mine 边实现串联挖掘。

### maxMineDepth 限制
`maxMineDepth = |goalX-pcx| + |goalY-feetY| + 8`，防止 A* 在实心区域无限展开。动态计算而非固定值，避免截断深目标路径。
