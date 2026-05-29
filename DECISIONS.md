# TerraBlind 决策记录

---

## 2026-05-23 Terraria 水平移动物理（源码确认）

### 来源
直接阅读 `Terraria.decompiled.cs` 反编译源码（1.4.5.4），`Player.HorizontalMovement` 区段。

### 四种分支

**1. 按方向键，|vx| < maxRunSpeed（正常加速）：**
- 若反向有初速（|vx| > runSlowdown）：先 `vx += runSlowdown`（消耗反向），再 `vx += runAcceleration`
- 否则直接 `vx += runAcceleration`（+0.08/帧）
- 上限 clamp 到 maxRunSpeed=3.0

**2. 按方向键，maxRunSpeed < |vx| < accRunSpeed（超速段）：**
- 仅在空中且有加速配件（向日葵等）时触发
- `vx += runAcceleration * 0.2f`（+0.016/帧，极慢）

**3. 松手 + 在地面（vy==0）：**
- `|vx| > runSlowdown(0.2)`：每帧减 0.2
- `|vx| <= 0.2`：直接归零

**4. 松手 + 在空中（vy!=0）：**
- `|vx| > runSlowdown*0.5(0.1)`：每帧减 0.1
- `|vx| <= 0.1`：直接归零

### 关键结论
- 空中松手减速是 **0.1/帧**，不是 0，也不是 0.2
- PhysicsSimulator 现有代码空中松手未正确实现此减速（待修复）
- 反向加速时同一帧先消耗 runSlowdown 再加 runAcceleration，净效果 +0.28/帧（地面）

### 对跳跃精度的影响
空中松手 vx 会缓慢衰减（0.1/帧），若 mf 结束后还有多帧飞行，模拟误以为 vx 恒定，实际落点会比模拟偏近。

---

## 2026-05-22 人类玩家导航能力的五个前提（feat/realtime-jump-control 分支方向）

### 大前提：目标导向，不是路径跟踪
人类的目标是"到达那里"，不是"按这条路走"。路径是手段，偏了就实时纠正，不是重新规划。

### 小前提一：物理直觉准确
人类物理模型（游戏训练出的直觉）与游戏物理完全一致。PhysicsSimulator 现在基本对齐，这个前提接近满足。

### 小前提二：感知实时（最关键）
每帧都在感知当前速度、位置、周围地形。执行层决策基于当前状态，不是规划时的状态。现在的问题是规划和执行是两个时刻，中间有割裂。

### 小前提三：关键节点特殊处理
危险跳跃（坑旁边、岩浆上方）会特别谨慎——减速、对齐、确认再跳。不是所有动作一视同仁。

### 小前提四：纠偏即时且局部（最关键）
偏了不是重新规划整条路，而是就地纠正当前动作。现在的 deviated → replan 是全局重规划，代价太高。

### 小前提五：粒度动态
平坦地面全速跑不需要思考每格。危险地形放慢精细控制。现在所有节点粒度一样。

### 当前进展（feat/realtime-jump-control）
- 实现了小前提二：Jump 状态改为每帧用真实 position+velocity 模拟弧线，找到合适时机再起跳
- 实现了部分小前提四：反向 vx 时等待而不是立即起跳
- 未解决：PhysicsSimulator 模拟结果与游戏实际仍有系统性偏差（simDist=0 但实际偏 9 格）
- 未实现：小前提三、五

---

## 2026-05-20 jump 边规划-执行偏差的完整影响因素

### 问题背景
jump 边反复 deviated+replan，分析日志后整理出所有影响规划与执行一致性的因素。

### 影响因素一览

**规划层（BuildJumpEdges / PathPlanner）**

| 因素 | 当前行为 | 潜在误差来源 |
|------|---------|------------|
| `inferredVx`（起点） | 读 `p.velocity.X` | 规划时静止，执行时已起跑，vx 不同 |
| `inferredVx`（非1起点） | 由 prevAction 推断：pillar/mine→0，jump→prevEndVx，其余→`sign*MaxRun` | 推断链有误差累积；`sign` 方向改变时 infer 为 0 |
| `jumpVx`（反向跳） | `inferredVx * jsign < 0` 时置 0 | 执行时实际 vx 不为 0，轨迹不符 |
| `PhysicsSimulator.Step` | 近似模型，已于 2026-05-20 对齐游戏逻辑 | StepUp 在降落阶段误触，产生极短弧线（见下方 bug） |
| `ArcClipsWall` | 只检查上升阶段头顶，下降段漏检 | 产生穿墙规划 |
| 节点签名无 vx | `(cx, cy, hc)` 不含 vx | 同一节点不同入口速度生成不同弧线，节点被共享导致弧线不对 |

**执行层（NavCoordinator / ResimJump）**

| 因素 | 当前行为 | 潜在误差来源 |
|------|---------|------------|
| ResimJump 起跳 vx | 读实际 `p.velocity.X` | 与规划时的 `inferredVx` 不同 |
| ResimJump vx 与 sign 反向 | 生成反向弧线帧，无法到达目标 | 执行层没有拒绝/等待机制 |
| replay path（node.Frames）| 直接回放规划帧，不检查实际 vx | 实际 vx 与规划假设不符时轨迹偏离 |
| `align_diff` | 玩家中心与 src tile 中心可能有 2-3 格偏差 | 起点偏差叠加 vx 偏差，放大落点误差 |
| deviated 阈值 | jump: `|dx|>3 OR |dy|>2` | dy 阈值 2 较严，±1 格的正常误差不触发，但 StepUp 误差会触发 |

**物理参数（PhysicsSimulator.Params）**

| 因素 | 当前行为 | 潜在误差来源 |
|------|---------|------------|
| maxRunSpeed | 从 `p.maxRunSpeed` 读取 | buff 下 3.63 vs 规划旧值 3.0，弧线水平跨度不同 |
| accRunSpeed | 从 `p.accRunSpeed` 读取（2026-05-20 修复） | boot 加速区间，超速后弧线与普通 vx 不同 |
| gravity | 从 `p.gravity` 读取 | 配件可减半，影响弧线高度和落点 |
| StepUp | 2026-05-20 引入，降落段误触 | 降落阶段 `vy>=0` 时误判 1 格台阶为着地，产生极短弧线（已知 bug，待修） |

### 当前已知 bug（2026-05-20）

**StepUp 降落段误触**：`Step` 里条件 `vx != 0f && vy >= 0f` 在下降阶段满足，遇到 1 格台阶时 StepUp 抬高玩家并清零 vel.Y，TileCollision 随即判定 hitFloor=true，模拟提前终止。表现：hold=15 的跳跃只跨 2 格。

// FRAGILE: StepUp 条件应限制为"接近地面时"，而非整个下降阶段。修法待定：可在 hitFloor 后额外校验脚底是否真的有 floor，或只在最后 N 帧启用 StepUp。

### 根本矛盾
规划层用**静态推断的 vx** 生成弧线，执行层用**运行时实际 vx** 起跳。两者在以下情况会不一致：
1. 规划时静止，执行时已加速（起点 vx 推断问题）
2. 上一跳向左落地，下一跳向右，规划用 0，执行用 -3（反向 vx 问题）
3. buff 导致 maxRunSpeed 与规划假设不符（已部分修复）
4. PhysicsSimulator 近似误差（2026-05-20 减小，StepUp bug 待修）

记录关键设计决策、排除方案和已知局限。按时间倒序追加。

---

## 2026-05-19 A* 节点签名引入 vxBucket

### 决策
节点签名从 `(cx, cy, hc)` 扩展为 `(cx, cy, hc, vxBucket)`，vxBucket 为 int，范围 -2..+2。

### 原因
原签名不区分入口速度，导致两类 bug：
1. 规划出全速才能到达的 jump 落点，但执行时静止起跳跳不到，触发 deviated+replan 死循环
2. 漏掉 vx=0 时弧线更陡能跳上的目标（垂直高度更高但水平距离更短）

### vxBucket 离散化方案
5桶：`-2=NegFull, -1=NegHalf, 0=Zero, +1=PosHalf, +2=PosFull`

归桶边界（零偏向）：
- `|vx| < 0.75` → 0（Zero）
- `0.75 ≤ |vx| < 2.25` → ±1（Half，带符号）
- `|vx| ≥ 2.25` → ±2（Full，带符号）

基于 MaxRun=3.0，Half=1.5，边界在 0.75 和 2.25。

### 各边类型出口 vxBucket
| 边类型 | 出口 vxBucket |
|--------|--------------|
| move 单格 | 入口 + accRun×5.5帧归桶 |
| fall | 入口（空中无摩擦） |
| jump | 入口（空中无摩擦） |
| pillar | 0（落顶静止） |
| bridge | ±1（始终 Half，不论入口和桥长）// ASSUMPTION: bridge 放砖走走停停，保守归 Half |
| mine_* | 0（挖掘间歇导致速度归零） |

### bridge 始终计为 Half 的理由
bridge 执行时玩家边走边放砖，存在周期性停顿，速度难以精确推算。保守归 ±Half（bsign 决定符号）避免高估出口速度导致后续 jump 边选择错误落点。

### 起点 vxBucket
`Plan`/`PlanTo` 入口处读 `p.velocity.X` 归桶，不再默认全速。

### jump 边生成
每个节点按当前 vxBucket 的实际速度 `BucketVx(vxb)` 生成跳跃弧，同时生成正向和反向跳。原硬编码 `-sign * MaxRunSpeed` 的反向跳 bug 一并修复。

### 节点数膨胀
理论上限 5 vxBucket × 2 hc = 10×。实际估计 5-8×，因为 hc=true 极少，不同 vxBucket 的 jump 落点差异会引入新节点。P2.1 验收阈值：closed set size 不超过 8× 原值。

// FRAGILE: vxBucket 推算基于单格加速模型，不考虑地面减速（runSlowdown=0.2）和空中 vx 保持。实际误差在1桶以内，可接受。

---

## 2026-05-19 规划层关键 predicate 设计决策

### Occupied 格的处理
`Occupied = HasTile && !Solid && !Platform`，包括树干、藤蔓、植物、火把等。

游戏里玩家可以自由穿过 Occupied 格，不阻碍移动。规划层不应把 Occupied 当障碍。

**当前规则：**
- `Standable` **不**排除 Occupied——玩家可以站在有树干/藤蔓的格子上（只要下方有 floor）
- pillar 边单独保留 `Occupied` 过滤——平台砖不能放在树干上（`if (Occupied(cx, topY)) continue`）
- move/fall/jump 边不受 Occupied 影响

// ASSUMPTION: Occupied 格在游戏里对玩家移动完全透明，不产生碰撞

### 平台穿越（fall through platform）
fall 边生成条件用 `IsBlock`（实心块），不用 `IsFloor`（包含平台）。

玩家站在平台上可以按下穿越，执行层 Fall 状态已有 `p.controlDown = true`。
规划层 fall 边从平台格生成，A* 可以规划"穿平台下落"的路径。

// ASSUMPTION: 平台穿越在执行层依赖 controlDown，规划层只负责生成 fall 边

### mine 边终点可以是实心格（mineNode）
mine_down 的终点 `(cx, cy+1)` 在当前世界是实心格，但挖完后变成空气。
A* 把这类节点加入 `mineNodes`，允许继续展开邻居边（包括 move/fall/mine）。

**debug 提示：** 路径里出现"move 到实心格"时，查是否是 mineNode——不一定是 bug。

### step-up 的已知限制
`BuildJumpEdges` 额外生成 `(lx, ly-1)` 的 step-up 落点，但：
- 起跳点离台阶太远时，模拟落点 `ly` 距台阶超过1格，step-up 实际不会发生
- 紧贴右侧墙起跳时，ArcClipsWall 会误判台阶侧面为天花板，过滤掉本来可行的边
- step-up 需要水平移动触发，原地跳（lx==cx）不生成 step-up 落点

### visited=1 的 debug 方法
`visited=1` 说明起点节点出队后邻居全被过滤，heap 立刻为空。
排查顺序：
1. 起点 `feetY` 是否调整正确（起点校正第265行）
2. 起点周围格子是否全被 Standable/Solid/Occupied 过滤掉
3. 起点是否在特殊地形（平台上方、树干内、悬空）

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

---

## 2026-05-23 PhysicsSimulator 空中松手 vx 衰减修复

### 问题
PhysicsSimulator.Step 松手分支只处理地面（`else if (s.Grounded)`），空中松手 vx 完全恒定。
实际游戏空中松手每帧衰减 `runSlowdown * 0.5 = 0.1`，导致模拟落点系统性偏远。

### 修复
在 `else if (s.Grounded)` 后加 `else` 分支，空中松手每帧 `vx -= 0.1`（方向敏感，到 0 截断）。

### 验证
dx=1..7 向右跳精度测试，修复前系统性偏差 -1~-2，修复后全部 ±1 以内，dx=1..4,6 精确命中。

---

## 2026-05-24 分層規劃架構（待實現）

### 設計方向
- **大致路徑**：現有 A* 降採樣，只保留 action 變化點（jump/mine/pillar 起點）作為關鍵節點，move/fall 壓縮丟棄
- **關鍵節點識別**：action 變化點——jump/mine/pillar/bridge 每個都是關鍵節點
- **節點間 A* 範圍**：budget-based，展開 N 個節點截斷，平坦地形快，複雜地形不爆
- **局部失敗兜底**：升級全量 A*

### 整體架構
```
全量粗 A*（只生成關鍵節點序列）
    ↓
當前關鍵節點間：局部精細 A*（budget 限制）
    ↓
偏了/找不到 → 局部重規劃 → 失敗則全量重規劃
```

### 動機
- 全量 A* 在某些位置性能影響大
- 滿足大前提（目標導向）：每個局部段都從當前真實狀態重規劃，不依賴固定路徑序列
- 地形變化/偏移自然體現在下一段局部規劃中

---

## 2026-05-24 plat_up / plat_jump 新邊類型設計

### 場景一：plat_up（垂直上升，替代 pillar）
- 跳躍到最高點時在腳底放 1 塊平台，上升 6-7 格只需 1 塊
- 前提：最高點附近 CanPlacePlatformAt 為 true（需要背景牆）、有平台物品
- cost 比 pillar 低
- 執行：從 SimulateJump frames 找 MinPy 對應幀插入 UseItem

### 場景二：plat_jump（水平穿越懸空，替代 bridge）
- 跳躍落地前在落點腳下放 1 塊平台
- 前提：落點腳下 CanPlacePlatformAt 為 true、有平台物品
- cost 比 bridge 低
- 執行：從 SimulateJump frames 找落地前對應幀插入 UseItem

### 測試方式
先實現 /test_plat_up 和 /test_plat_jump HTTP 端點，直接從當前位置執行，
不需要 A* 規劃，確認放置時機正確後再接入 A*。

### 規劃假設
A* 規劃時假設平台會放好。執行失敗（背景牆不夠、放置失敗）→ deviated → 局部重規劃。
與現有 pillar/bridge 處理方式一致。

---

## 2026-05-29 根因调查：FromPx 亚像素归一化导致 replan storm

### 现象
自由探索/导航中反复出现 `replan storm`，玩家原地不动（如 `Replan at (2835,244) state=Move` 反复触发）。
表面看是"执行偏离导致死循环"，实际根因在规划层的格位映射。

### 根因
`PathPlanner.FromPx`（17-23 行）把宽 20px（横跨两列）的玩家归一化到单个 `(cx, sub)`，
但 **cx（中心格）和"占用哪两列"不是一一对应**。亚像素漂移会让节点身份跳变。

实测数据（玩家仅左移 ~15px，占用列几乎没变，A* 节点跳变 3 次）：
```
px=45356  cols=[2834,2835]  -> cx=2835 sub=L
px=45349  cols=[2834,2835]  -> cx=2834 sub=R   占列没变，cx/sub 却变了
px=45343  cols=[2833,2835]  -> cx=2834 sub=C   碰到第3列，归为 C
px=45340  cols=[2833,2834]  -> cx=2834 sub=L
```
A* 把 `(2835,L)`/`(2834,R)`/`(2834,C)`/`(2834,L)` 当成 4 个不同节点，
为"玩家其实没移动"的亚像素位置生成多余的 move 边 → 执行层无法对齐 → 反复 replan → storm。

### 关键事实（调查中逐一证伪的错误假设）
- 跳跃执行不是开环录像，是实时闭环（NavState.Jump grounded 阶段用真实 vx 选 hold，命中才起跳）
- 落点容差不严（jump Dx=3/Dy=2），偏 1 格不会触发 deviate
- A* 不生成"同格 (0,0) 换 sub"的边（方向集无 (0,0)）；问题在 FromPx 把同一物理位置映射到不同 (cx,sub)
- sub 进 node key 的真实用途只有 3 处，全是挖掘/起跳列判断（ColsOccupied 711、shaft col0 561、CanonicalPx 327），
  其余几十处只是透传

### 待解（修法未定）
重新设计"玩家格位 + 占用列"的表示，使其对亚像素漂移稳定。候选方向：
- A：node key 去掉 sub 维度，挖掘时按进入方向现算占用列（改动大，g/prev/nodeEndVx/verifyData/mineDepth 全部 key 要改）
- B：禁止纯换 sub 的边（但实测表明边来自 FromPx 映射跳变，非 A* 造边，B 可能不对症）
- 需进一步确认 cx 该怎么定义（中心格？还是按占用列锚定），未拍板

---

## 2026-05-29 失败盘点 + 水里寻路两个待查问题

### 失败盘点（扫 24M jump_trace.log）
执行类失败频次：jump deviated 85、jump_x deviated 79、replan storm 88、jump_y deviated 23、JumpAlign timeout 10。
unreachable 293（多为目标本就不可达，不算执行失败）。
Replan 按 state：Idle 500、Move 158、Jump 118、JumpX 78、JumpY 21、Bridge 17。

jump deviated 偏离分布：水平 dx 集中在 -2~+1（准），**垂直 dy 严重发散 -30~+21**，
正偏(够不到掉回下层)与负偏(跳过头)双峰。→ 执行失败大头是垂直高度失控，不是水平。

### 澄清：+8/+9 格 jump 边不是 bug
原怀疑规划生成超上限(≤7)的跳跃边。核查 PhysicsSimulator.Params.FromPlayer：
水里 grav=0.1、js=3.005、长按 hold 到 30 帧，**水里确实能跳 8-9 格**，物理建模正确。
edge_emit 里 +8/+9 边来自水中场景，非陆地乐观误生成。
（陆地纯 jump 上限由 SimulateJump 的 Landed 隐式约束，无显式 ≤7 常量。）

### 水里待查问题 1：wet 抖动触发 replan 风暴（已确认现象）
玩家在水面附近，p.wet 在 True/False 间反复抖动，日志大量：
`[nav] wet changed →True → replan` / `→False → replan` 来回横跳。
每次 wet 切换都 replan（水陆物理不同需重算），水线边缘抖动 → replan 风暴 → storm。
候选修法：wet 状态加滞回/去抖，或只在"稳定进入/离开水"时才 replan。

### 水里待查问题 2：模拟物理 ×0.5 是否正确（存疑，需查源码）
执行期 jump fire 日志：水里 js=6.01 grav=0.2（Player.jumpSpeed / p.gravity 原始值）。
模拟期 Params.FromPlayer：水里 grav=0.2*0.5=0.1、js=6.01*0.5=3.005（又各打 0.5）。
疑点：模拟对水里 js/grav 各 ×0.5，是否与 Terraria 引擎实际水中物理一致？
若不一致 → 水里模拟弧线 ≠ 真实弧线 → 落点偏。
待办：查反编译源码确认 Terraria 水中 jumpSpeed/gravity 实际作用方式，再定 ×0.5 对错。

### 优先级（数据驱动，未拍板）
两个水里问题 + 已记录的 FromPx 亚像素问题（见上一条），三者都指向 replan storm。
FromPx（Move 原地 158 次）与 wet 抖动是 storm 两大来源，建议优先。

---

## 2026-05-29 根因坐实：实时起跳门槛 bestDistCx==0 过严，差1格永不起跳

### 复现（用户量身造的地形）
玩家站 (1762,183)，脚下半砖，goal=(1766,179)。执行后**始终不跳**，玩家 vx=0、px 恒定，原地卡死。

### 坐实证据（[rt] 实时跳跃日志，连续 8 秒不变）
```
tick=30/60/.../240: bestHold=15 bestSimCx=1765 bestSimCy=179 target=(1766,179) bestDistCx=1 px=28188 vx=0
```
- 模拟最佳落点 bestSimCy=179 **垂直完美命中**
- bestSimCx=1765，目标 x=1766 → **水平差 1 格（bestDistCx=1）**
- vx=0、px 恒定 → 玩家站着没动，从不 fire

### 根因
NavCoordinator NavState.Jump 的 grounded 起跳判定（~1109 行）：
`if (bestHold == 0 || bestDistCx > 0) { 微调走位; return; }` —— 只有 **bestDistCx==0**（水平落点精确命中目标格）才 fire。
但此跳的物理最优落点就是 1765（差目标 1 格），任何 hold 都到不了精确的 1766
→ bestDistCx 恒 ≥1 → 起跳条件永不满足 → 始终不跳 → 卡死。

### 关键矛盾
起跳门槛要求落点零误差（==0），但落地容差 BehaviorContract.ExitTol["jump"] = Dx=3。
**起跳门槛(0) 比落地容差(3) 还严** —— "落点差1格但完全可接受"的跳跃永远不被触发。

### 修法（未实施）
起跳判定改为 `bestDistCx <= 落地容差(或一个小阈值如1)` 即可 fire，而非要求 ==0。
与落地 Deviated 容差对齐，避免"能落、却因门槛过严不敢跳"的死锁。
注意：半砖在此非直接根因（垂直 bestSimCy 命中正常），但半砖地形更容易撞上"落点差1格"的情形。

---

## 2026-05-29 根因坐实并修复：斜坡落地检测失效导致 Jump 哑死锁

### 复现 + 坐实证据（diag-state 临时日志，连续不变）
玩家跳过头落到 ◢ 斜坡上 (2750,211)，target=(2747,212) 在背后：
```
State=Jump feet=(2750,211) vx=0 vy=0 pathIdx=3 target=(2747,212) stall=0   (持续不变)
```
顶着右墙、完全静止、不跳、不 replan、日志不刷新 = 哑死锁。

### 根因
airborne 落地检测 `if (_prevVY > 0f && grounded)`：要求"上一帧在下落(vy>0)"。
但 ◢/◣ 斜坡上，Terraria 的 Collision.SlopeCollision 落地时**直接把 vy 吸附归零**，
没有经历正 vy 的下落帧 → _prevVY>0 永不成立 → 落地永不检测 → 卡在 airborne 分支每帧
controlRight; return。且 Jump 状态被 stall 检测排除(779行 stalledX 条件)，永不超时 replan → 永久死锁。

### 修复（已验证成功）
1. 加 `_jumpLeftGround` 标志：airborne 中 vy<0(真离地) 时置真；起跳时重置 false。
   落地判定改为 `grounded && (_prevVY > 0f || _jumpLeftGround)` —— 斜坡吸附零速也能判落地。
2. 兜底：grounded 重瞄阶段若 target 落在 _jumpSign 反方向(跳过头/目标在背后) → Replan()，
   避免实时重瞄只朝 _jumpSign 模拟、永远够不到背后目标的死调。

### 关联坑
- 斜坡/半砖在 `/state` tile flags 原先不可见(都被标 solid)，本次新增 128 位标记 slope/halfblock，
  ASCII 调试可见(commit 见 StateSnapshotPlayer)。这是定位本 bug 的前提。
- 同类已修：起跳门槛 bestDistCx==0 过严(70e20bc/d2edd2d)。两者都属"该跳却卡住"，但机制不同。

---

## 2026-05-30 根因坐实并修复：半砖上斜向 move 实为 1.5 格爬升，应改 jump

### 复现 + 坐实（diag-state）
玩家站半砖 (3381,247，其下 248 是半砖)，goal (3399,241)。卡死：
```
State=Move feet=(3381,247) vx=0 vy=0 pathIdx=0 target=(3382,246) act=move stall 反复涨到~50重置
```
node[0] `move (3381,247)->(3382,246)` 上升1格(整格坐标)，但执行端顶墙走不上去。

### 根因
站半砖时脚比站整格低 ~0.5 格(陷进半砖)。到上方 +1 格目标的真实爬升 = 1.5 格。
move 靠 StepUp 只能上 1 格整 → 上不去 1.5 格。但规划用整格坐标只看到"+1"，
照样生成 move 边 → 执行 stall 循环。

附带发现：规划端 feetY 与执行端 FeetY() 对半砖处理不一致(规划 line453 feetY--到247，
执行停在248) → 即使坐标对齐后核心问题仍在(1.5格)。

### 修复（已验证 pass）
1. NavCoordinator.FeetY()：加 `if (IsHalfBrick(pcx, fy)) fy--`，与规划端对齐(都认半砖上方空格)。
2. PathPlanner move 展开：斜上 move 若起点下方是半砖 `if (dy==-1 && dx!=0 && IsHalfBrick(cx,cy+1)) continue`
   → 不生成 move 边，A* 改用 jump 边(jump 能上 1.5 格)。

### 调试关键
`/state` tile flags 128 位(slope/halfblock)实测打脸两次错误假设：半砖实际位置、目标vs起点下方。
无实测数据必改错地方。关联 DECISIONS 半砖系列、FromPx 亚像素。
