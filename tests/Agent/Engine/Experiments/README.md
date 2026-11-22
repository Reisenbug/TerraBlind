# 场地实验 / Arena experiments

场地实验默认清除测试范围内的液体、关闭刷怪并清理已有敌怪。场地四周封闭。若液体、敌怪或开放边界是实验变量，在本项说明中标明。录制和实机运行关闭音乐，保留游戏音效。

Arena experiments clear liquids in the test area, suppress natural spawns, clear existing hostile NPCs, and close the perimeter. Document any liquids, enemies, or open boundaries required by the experiment. Mute music and keep game sound effects audible during execution and recording.

启动独立客户端前，将 `arena.config.json` 复制到测试保存目录并命名为 `config.json`。保留 `LastLaunchedVersion`，游戏依赖它判断是否读取音量。录制若使用自己的配置，保留音效音量并在启动前将测试副本的 `VolumeMusic` 设为 `0`。

Copy `arena.config.json` to the isolated save as `config.json` before launching. Retain `LastLaunchedVersion` so Terraria reads the audio values. For recordings with custom settings, set `VolumeMusic` to `0` in the test copy and retain the chosen sound effects volume.
