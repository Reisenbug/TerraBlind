# 游戏内原版移动入口验收

`/vnav` 以前方鼠标位置为落脚点；`/vnav X Y` 使用格子坐标，`/vnav stop` 停止，`/vnav status` 查看状态。目前仅支持单人本地世界和原版移动装备，青线显示已接受的预测轨迹，黄点显示脚部目标。

完整构建 TerraBlind 会生成并内置最小推演模组 `Assets/VanillaWorker.bin`。游戏命令自行启动临时无界面进程，不需要外部 Python 常驻。同一世界复用进程，离开世界时清理。

验收构建：

```
dotnet build TerraBlind.csproj '-p:ExtraBuildModFlags=-tmlsavedirectory /tmp/terrablind-demo-app'
dotnet build tests/Demo/Demo.csproj '-p:ExtraBuildModFlags=-tmlsavedirectory /tmp/terrablind-demo-app'
python3 tests/Demo/run.py --show
```

这会打开独立图形窗口，加载完整 TerraBlind 和仅用于验收的 Demo 模组；人物、世界和配置位于 `/tmp/terrablind-demo-app`。演示场地有防水边界，并关闭测试场地的NPC生成。它调用正式命令，分别只装备沙暴瓶和天使翅膀，登上同一个12格高台。不是自然丛林或多人验收。

`demo-result.json` 保存结果，`trace-0.json`、`trace-1.json` 保存人物、跳跃状态、拒绝原因及耗时，`demo-0.png`、`demo-1.png` 为实际绘制画面。`--show` 在成功或失败后保留窗口；默认模式结束后清理自己的进程。`--wings` 仅复现翅膀场景。

启动器读取正常 tModLoader 的 `config.json` 和 `input profiles.json` 到临时目录，保留画面、语言、音量、缩放和按键，仅关闭临时存档的云保存。原配置不写回。

`--descent` 使用 `world.py` 生成的固定种子自然地图，测试沙暴瓶、赫尔墨斯靴、钴护盾及星怒，不装备翅膀。携带蜘蛛网作为落地保护候选，放置仍受原版规则限制。此模式不关闭怪物生成。`descent-trace.json` 记录位置、血量、跳跃及计划耗时；`jump_trace.log` 中的 `[engine-nav-timing]` 记录快照、推演、往返、状态恢复/物理推进/观察/清理耗时，以及零输入帧计数。零输入帧包括主动等待，不能直接当作计算阻塞帧。

`--continuity` 在同一自然地图导航到出生点右侧180格的地面，跳过丛林/地狱路线准备，用于快速复现动作停顿。构建的两个模组放在 `/tmp/terrablind-continuity-check/Mods`，其余启动步骤相同。`[engine-nav-timing]` 的 `starved_frames`、`longest_starvation` 仅统计接受首份计划之后动作队列耗尽的帧数；不能用它们证明角色没有被地形卡住或没有主动等待。此检查通过不等于抵达地狱。
