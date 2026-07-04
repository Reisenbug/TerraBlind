# TerraBlind 项目总体状态

> 2026-07-03 更新(与 Fable 5 的战略讨论产出)。给未来的 agent 和自己:**先读这个,再干活**。
> 上一版(纯寻路阶段)的核心知识已压缩保留在第五、六节。

## 一、战略转向(2026-07-03,用户拍板)

**寻路降级,LLM 层升级。** 原话:"I'm tired of debugging path-finding. Fuck this." 结论:

- 寻路只是 LLM 的**手**,不需要优雅、不需要最优,只需要**够用**。不再主动修寻路 bug,
  只修**挡住上层任务**的。每次修都要问:这真的挡路了吗?
- 项目的新前沿是 **LLM second player**:玩家在游戏里给 AI 队友下自然语言指令,
  AI 分解、执行、用人话汇报。LLM 不可或缺的四个位置(全部已开工):
  1. **指令理解**:自然语言 → 机器目标(开放指令空间,手写解析器覆盖不完)
  2. **战略规划**:Terraria 世界知识(配方/进度/深度)→ 任务图,叶子是原语调用
  3. **对话**:澄清、汇报、协商——"像队友"的全部所在
  4. **语义异常处理**:寻路报 walled_in/loop_unresolved 时,LLM 决定任务级改道
     (绕路?先升级镐?问玩家?)——正是结构层解决不了的类3谎言的最上层兜底
- **LLM 只住在秒级慢层,永远不碰帧和格子。** 层级:LLM(任务)→ 场/规划(路线)→ 执行器(帧)。

## 二、当前架构(三层,已跑通竖切)

```
玩家: /tb <指令>  (游戏聊天)
  ↓ AgentChat.cs 入队
[mod HTTP 桥, 127.0.0.1:17878]
  GET /instruction   agent 轮询指令
  POST /say          agent → 游戏聊天(<TB> 前缀)
  POST /find_tiles   {name:"Iron",n,max_dist} 按 TileID 名找最近方块(环扫,按距排序)
  POST /nav_recede   {gx,gy} Bellman receding nav(与 K 键同引擎)
  GET /nav_recede_done  {done,status,reason: walled_in|loop_unresolved|stopped|timeout}
  POST /nav_recede_stop
  POST /mine /craft /fight /state /interact /loot_all ... (原语早已齐全,见 HttpServerSystem.cs)
  ↑ HTTP
[agent: /Users/lhy/Documents/Terraria-Agent/scripts/second_player.py]
  openai 兼容接口;配置读 .env: SECOND_PLAYER_API_URL/MODEL/KEY,回落 COMMANDER_*
  (当前 deepseek-chat @ SJTU。不许硬编码任何一家 provider——用户在 .env 里定)
  无 thinking,普通 chat completions + function calling
  工具: get_state / find_tiles / nav_to(阻塞轮询,失败带原因码) / mine / say
  跨指令滚动对话记忆(HISTORY_MAX_MSGS=60);最终文字回复自动转发游戏聊天
```

已验证:`/tb 你好` → LLM 回话;`/tb 向右走100格` → nav 调用。中文编码、代理绕行
(urllib 必须 ProxyHandler({}) 绕过 http_proxy,SJTU 网络环境)、断线重连都已修。

## 三、接下来的路(按序)

1. **竖切打穿**:`/tb 去挖10个铁矿` 端到端跑通(find_tiles→nav→mine→验证背包→汇报)。
   期间暴露的寻路 bug,只修挡路的。
2. **工具面扩充**(按需,不预建):craft(配方)、chest/loot、fight、place、
   背包查询(get_state 已含)。每个工具描述写清"何时用",别只写"是什么"。
3. **失败语义化**:nav 的原因码 → prompt 里教 LLM 翻译成人话+备选方案。
   这是4号能力的落点,也是寻路 bug 的新出口:修不动就让 TB 说"我过不去,要不…"。
4. **prompt 迭代**:system prompt 在 second_player.py 里,中文。观察实际对话打磨:
   汇报节奏(开始/关键进展/完成)、何时问何时自主、Terraria 知识注入。
5. **记忆**(远期):跨会话共享记忆、命名地点("我们上次那个洞")。
6. **寻路兜底方案(如 Bellman 继续拖后腿)——轨道模式**:把 MazeWand 的线当轨道字面
   执行,下一线格在哪就走/挖/垒到那格,一格一格,不模拟不选边。失败模式塌缩成
   "能否进入相邻一格"。无聊但接近保证。用户已认可此方案为备胎。

## 四、硬规则(血泪换的,violate 前先想三遍)

寻路层(见 memory,依然有效):
1. **一套 cost**:选边 g ≡ H(s)−H(s'),绝不手编第二套 per-action 成本。
2. **不准禁退**:防循环靠诚实,不靠禁止回头/重访。
3. **stuck 必须结构上不可能**:选不出就走安全步挪一格(EXPAND-EMPTY 已有 escape step)。
4. **一切方案带不精确 fallback**:不假设精确对齐,读真实状态。
5. **手写附加项必须封顶在真实 H 落差量级之下**。

agent 层(本次新增):
6. **provider 由用户定**:agent 代码读 env,绝不默认/硬编码某家 API。
7. **agent 代码住 Terraria-Agent 仓库**,mod 仓库只放 C# 桥。
8. **TB 对玩家说中文**;mod C# 代码注释用英文。
9. **玩家看不到 TB 的任何动作**——不 say 等于没发生,失败绝不假装成功。

流程规矩:build 只能游戏内 `/build TerraBlind` 回复 1;commit/push 需用户同意,
英文 conventional commits;日志在 tModLoader/TerraBlindLogs/(jump_trace.log + runs/)。

## 五、寻路知识压缩(改寻路前必读)

**Bellman 从来没错,错的永远是喂给它的世界。** 三类死循环谎言:

| 类 | 谎言 | 修复状态 |
|---|---|---|
| 1 | 代价撒谎:手写项盖过真实H落差 | 已修:g=ΔH + 地形改动帧数×0.5封顶15 |
| 2 | 标签撒谎:落点是 replan 读不到的幽灵态 | 已修:SettleNode + StandCell body-fit |
| 3 | 能力撒谎:场定价了身体做不到的转移 | 逐类修:可挖性/竖3行/斜砖6px/垫脚第4行/横向宽度均已入场;写回自愈(D* Lite)未做 |

类2/3的险恶:每步都完美HIT,attention 全盲 → 防循环根本手段是消灭谎言,不是加惩罚。

已落地机制:循环电击器(bestH 停滞20 replan→环上边+200衰减罚,3次无效→停机dump;
坠落等非自愿位移 H 暴涨>200 会重定基线防误伤)、执行 watchdog(软/硬双钟)、
EXPAND-EMPTY 安全脱困步、STARVED-EXPAND 自动 SegDiag 诊断、EmitPlace STALL-WHY 遥测。

身体几何常数:玩家 20px 宽(跨2-3列)、42px 高(3行包络48px,仅 **6px 富余**——
斜砖/半砖在胸头行或垫脚都会吃光它)、单跳约6格、站立格约定 (px+10)/16, (py+41)/16。

未验证的最新修复(2026-07-03 后半):位移重定基线、安全脱困步、宽度净空、
goalSnapCap=2(drift-replan 目标被 snap 传送>2行则弃腿重选)。跑 L-run 验证。

## 六、试过且丢弃的(别再试)

- **local-action-graph 重写**:整体推倒,神秘 bug,已删("好的设计不应该出 bug")。
- **2-cell lookahead**:深谷里第二层同样出不去,退化为无用。
- **surcharge 常数**(40/3):单常数在"防乱挖"和"不饿死必要挖"间无解 → 帧数化。
- **手编 per-action 成本**(place=120 等):golden rule 之前的循环之根。
- **AlignScale=120**:否决真实降H;tie-break 项必须小(现18)。
- **在 mod 仓库里放 agent 代码 / 默认 Claude API**:用户明确否决。
