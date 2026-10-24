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
