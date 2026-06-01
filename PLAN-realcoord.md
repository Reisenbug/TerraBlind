# 方案:A* node 携带真实坐标,消除整格反推

## 问题根源

A* 规划是整格的。每条边算出真实落点 `EndState.Px/Py` 后,**取整成 `(lx,ly)`**(BuildJumpEdges:345-346),
下一跳又用 `CanonicalPx(cx,sub)` / `(cy+1)*16-height` **从整格反推假 px/py**(327-329)。

→ 每跳一次,真实坐标被"取整→反推"洗掉一次。半砖/斜坡的半格信息在反推时假设"站整格顶"而丢失。

这是半砖/斜坡一系列 bug(feetY 偏移、move 1.5格、pillar 差半格、落地检测)的**共同根源**:
规划用整格反推的假坐标,执行用真实坐标,两者差半~1格。

## 核心思路

**不动 A* 框架(node 仍是整格 `(wx,wy,hc,sub)`,visited/g/key 不变)**,
但给每个 node 挂一个"到达时的真实 px/py",边生成时**用携带的真实坐标当起点,不再反推**。

整格 node 负责"去重/收敛"(A* 正常工作),真实坐标负责"物理精度"(半砖斜坡自动对)。

## 改动清单

### 1. 新增:node→真实坐标字典
```
var nodeRealPos = new Dictionary<(int,int,bool,SubPx), (float px, float py)>();
```
key 同 node。存"以最优 g 到达该 node 时的真实落点"。

### 2. 起点初始化真实坐标
```
nodeRealPos[startNode] = (p.position.X, p.position.Y);  // 真实当前位置
```

### 3. 每条边落点时,存真实坐标(与 g 更新同步)
所有 action 边(jump/move/fall/pillar/jump_x/jump_y/bridge/mine)在 `g[ekey] = cost` 处,
同时 `nodeRealPos[ekey] = (realPx, realPy)`。
- jump 类:用 `result.EndState.Px/Py`(已算出)
- move/fall:从起点真实坐标 + 整格位移推真实落点
- pillar/jump_y:垂直,真实落点 = 起点px, 目标格顶py

### 4. 边生成起点改用携带的真实坐标(关键)
`BuildJumpEdges` 等的 startPx/startPy(327-329):
```
// 旧:isStartNode ? 真实 : CanonicalPx(反推)
// 新:nodeRealPos.TryGetValue(node, out var rp) ? rp : 反推(兜底)
```
所有 action 的边生成同样改:从 `nodeRealPos[当前node]` 取真实起点。

### 5. 执行端用真实落点(第二阶段,可选)
路径 JSON 每个 node 输出真实 px/py,NavCoordinator 执行时目标用真实坐标而非整格。
第一阶段可不做(执行已实时闭环,规划准了执行偏差自然小)。

## 分步实施(每步可独立编译+测)

- **Step 1**:加 `nodeRealPos` 字典 + 起点初始化 + jump 边存真实落点 + jump 边起点用真实坐标。
  其他 action 暂时仍反推(混用,但 jump 链路先准)。测:斜坡/半砖上的 jump 不再差半格。
- **Step 2**:move/fall 边接入真实坐标。测:斜上 move 在半砖上正确。
- **Step 3**:pillar/jump_y 接入。测:pillar 在斜坡上 reached_height 正确。
- **Step 4**(可选):执行端用真实落点,JSON 带 px/py。

## 风险

- **同整格多真实坐标去重**:同一 `(wx,wy,hc,sub)` 被多条边到达,真实落点不同。A* 取 g 最小那条的真实坐标。
  可能错过"g 稍大但真实坐标更利于后续"的解。预期影响小(整格去重本就有损),需观察。
- **每个 action 边生成都要改起点**,易漏。分 action 逐步改,漏的退回反推兜底,不会崩。
- **CanonicalPx/反推保留作兜底**(node 无真实坐标记录时),不删,降低风险。

## 不解决的

- 这是规划精度问题。规划能力边界(跳不到的高度、撞墙跳)不在此列。
- FromPx 亚像素(node 身份漂移)是独立问题,本方案不碰 node 身份,不解决它。
  但两者可先后做。

## 为什么值得做

逐个补 action 没完没了(每个动作各踩半格坑)。本方案让"真实坐标全程不丢",
一次性消除半砖/斜坡对**所有** action 的半格干扰,根治这一类。
比推倒重写小得多(A* 框架/node/key 全留),比补丁治本。
