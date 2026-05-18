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

## 2026-05-18 规划失败的所有已知条件

以下任何一条成立，`PlanTo` 返回空路径（`path=[]`）。

### 1. 搜索范围限制
- 目标超出 A* 扫描范围：`|goalX-pcx| > GoalRangeFwd(60)` 或 `|goalY-feetY| > AStarScanUp/Down(50)`
- `PlanTo` 用 `bidir=true`，xMin/xMax 各扩展60格，goalSet 额外扩展5格，但仍有上限

### 2. 目标格不可命中
- 目标格不是 Standable 且不在 bridgeNodes/mineNodes 路径上
- 目标在 Occupied 格（树干、藤蔓等）：`Standable` 返回 false
- 目标悬空（下方无 floor）且周围没有 pillar/bridge 能到达的节点

### 3. HeuristicWeight=3 导致搜索偏向
- weighted A* 优先展开 h 小的节点
- 需要先横向移动再 pillar/bridge 的路径，横移过程中 h 增大，被推后展开
- 若 visited 预算内未展开到正确路径，返回空路径
// FRAGILE: HeuristicWeight>1 会导致需要"先绕路"的路径在有限搜索内找不到

### 4. mine 边深度限制
- `maxMineDepth = |goalX-pcx| + |goalY-feetY| + 8`
- 若实际需要挖掘的格数超过此值，mine 边不再展开
- 全实心区域且目标距离较远时容易触发

### 5. jump 边过滤
- `JumpMinCol=2`：水平距离<2格的 jump 不生成
- `ArcClipsWall`：弧线碰到实心块（已知缺陷：只检查上升阶段头顶）
- 头顶有实心且 `hc=false`：`canJump=false`，需要先 mine_up

### 6. pillar 边限制
- `rise <= 7`：低于7格的 pillar 不生成（由 jump 覆盖）
- leftClear 检查：`cx-1` 和 `cx` 两列整段净空，有任何实心就不生成
- pillar 只能同列（cx不变）上升，目标格若在 cx±1 列，需要额外 bridge/move

### 7. move 边限制
- 目标格必须 Standable，除非是 mineNode 或 bridgeNode
- 头顶两列（ny-1, ny-2）有实心则不生成
- 2格高缝隙（ny-1/ny-2 某列有实心）不可通过

### 8. bridge 边限制
- `MaxBridge=25`：单段 bridge 最长25格
- 沿途头顶3格有实心则中断
- cost 大幅提高后（base=10, perCol=4），仅在无其他选择时使用

---

## 2026-05-18 执行层各动作实现方式

### 控制信号来源
所有控制信号在 `StateSnapshotPlayer.cs` 的 `PreUpdate` hook 里每帧注入，优先级：MineCoordinator > SkillExecutor > NavCoordinator。

### 各动作具体实现

| 动作 | 状态 | 代码位置 | 实现方式 |
|------|------|---------|---------|
| move | `NavState.Move` | NavCoordinator.cs:706 | `p.controlRight/Left = true`，到达判定：`feetLeft <= target.Wx <= feetRight` |
| fall | `NavState.Fall` | NavCoordinator.cs:748 | `p.controlRight/Left = true` + `p.controlDown = true`（穿平台），落地判定：`prevVY>=0 && onGround` |
| jump | `NavState.Jump` | NavCoordinator.cs:767 | `ResimJump` 重新模拟，`ReplaySystem.Load(frames)` 回放，落地后 `vy==0` 完成 |
| pillar | `NavState.PillarAlign → Pillar` | NavCoordinator.cs:914 | Align：移动到 `target.Wx*16+8` 中心；Pillar：`SkillExecutor.StartPillarJump`，回放 43帧固定序列循环直到 `feetY <= targetWy` |
| bridge | `NavState.Bridge` | NavCoordinator.cs:834 | `ReplaySystem` 回放自构造帧（move+useItem+平台slot），到达 `targetCX` 后完成 |
| mine_right | `NavState.Mine` | NavCoordinator.cs:1029 | `SmartCursorWanted_Mouse=true`，光标右方160px同高，`controlRight=true`，`itemTime==0`时`controlUseItem=true`，`pcx>=target.Wx`完成 |
| mine_left | `NavState.Mine` | NavCoordinator.cs:1029 | 同上，光标左方160px，`controlLeft=true`，`pcx<=target.Wx`完成 |
| mine_down | `NavState.MineAlign → Mine` | NavCoordinator.cs:1029 | Align同pillar；Mine：光标下方160px，`feetY>=target.Wy && vy==0`完成 |
| mine_up | `NavState.Mine` | NavCoordinator.cs:1029 | 光标上方160px，done条件：头顶 `(pcx,feetY-2/3)` 和 `(pcx+1,feetY-2/3)` 全为air |

### 关键 API
```csharp
p.controlRight/Left/Down = true;   // 方向键
p.controlUseItem = true;            // 左键（需 itemTime==0 才有效）
p.controlJump = true;               // 跳跃（需边缘触发）
p.selectedItem = slot;              // 切换槽位
Main.SmartCursorWanted_Mouse = true/false;  // 智能光标
Main.mouseX/mouseY = ...;           // 鼠标屏幕坐标
ReplaySystem.Load(frames);          // 加载帧序列回放
SkillExecutor.StartPillarJump(dirRight, targetWy);  // 启动pillar
```

// ASSUMPTION: p.controlUseItem 仅在 itemTime==0 时触发新挥舞，持续 true 不会重复触发
// FRAGILE: ReplaySystem 回放期间 NavCoordinator 不应再注入控制，否则冲突

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

## 2026-05-18 ArcClipsWall 侧面墙误判为天花板（已知限制）

### 现象
玩家紧贴右侧墙（`tileX1+1` 列有实心），向右跳时 ArcClipsWall 把墙侧面判定为天花板，过滤掉本来可行的 jump 边。实际游戏里玩家能通过"先上升到墙顶高度再水平移动"跳上去。

### 根因
`ArcClipsWall` 检查碰撞箱双列（`tileX0` 到 `tileX1`），但 `SimulateJump` 从第一帧就全速向右（`startVx = sign * MaxRun`），弧线上升阶段右列（`tileX1`）提前进入墙体范围，触发误判。

真实可行轨迹是"先垂直上升超过墙顶高度，再水平移动"，但当前模型无法模拟分段时序输入。

### 为什么不修
修改 ArcClipsWall 排除前沿列会放过真正的侧面碰撞（玩家真的会被墙挡住的情况），安全边界难以界定。

### 影响
紧贴实心墙旁边的 jump 边被过度过滤，A* 被迫绕路（往反方向走几格再跳）。

// FRAGILE: ArcClipsWall 用全速 startVx 模拟，无法表达延迟水平移动的轨迹

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
