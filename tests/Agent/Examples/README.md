# 40 项游戏任务脚本 / 40 game-task scripts

这套脚本按原讨论顺序编号。每一项都能生成供游戏内 `run_script` 接收的 JSON。部分条目会执行并验收任务，部分只执行可安全验证的一段或读取现场，并返回具体阻塞项。没有原语支持的条件会显式返回 `blocked`。

The case IDs follow the original request order. Each emits a payload for the in-game `run_script` tool. A case may execute the requested behavior or probe the missing capability and return a blocker. A `blocked` receipt is a failed acceptance check.

~~~sh
node tests/Agent/Examples/task_cases.mjs list
node tests/Agent/Examples/task_cases.mjs audit
node tests/Agent/Examples/task_cases.mjs build 34-use-pylon fixture.json
~~~

晶塔例的 `fixture.json` 形如 `{"x":1392,"y":314}`。坐标来自当前世界的 `pylon_list`。其他任务的参数直接见对应函数。例中的路线、房屋、装备、材料和物品 ID 都需要按测试世界填写。

Use current-world coordinates and inventory values for each fixture. For example, case 34 takes `{"x":1392,"y":314}` from a current `pylon_list` result. `audit` checks syntax, tool IDs and the host source length cap. Actual execution uses the game's JS host. In the resulting receipt, verify `value.ok`, the underlying world evidence, and any task-specific constraints.

## 隔离验收 / Isolated checks

~~~sh
node tests/Agent/Examples/task_cases.mjs bundle > /tmp/tb-task-casebook-bundle.json
dotnet run --project tests/Agent/AgentTests.csproj -- --task-casebook /tmp/tb-task-casebook-bundle.json
TB_CASEBOOK_STORAGE=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_SOCIAL_INTERACTIONS=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_WORLD_INTERACT=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_HOUSE=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_CART=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_RAIL=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
~~~

储物场景跑 26、28、09。社交场景跑 34 和单件饰品的 13。原版右键场景额外跑 30，使用原版回弹挡板判定。矿车场景跑 18，记录车速、下车与蘑菇入包。重铸可能耗尽次数，此时验收物品是否归还。

Storage runs cases 26, 28, and 09. Social interactions runs 34 and tries 13 against one accessory. The world-interaction fixture runs 30 and reads the vanilla bumper predicate. The house fixture runs 02, builds from materials and checks native housing. The minecart fixture runs 18 and records speed, dismount, and mushroom pickup. Reforging can hit the roll limit, in which case the fixture checks that the accessory was returned. The runner copies the world into an isolated save and prints each result as `TASK_CASE`.

19 和 20 各跑有跳与无跳两次，报告写入 `task-casebook-rail.json`。记录实际按键、羽落、上车、接触铁轨和车速。当前 tModLoader 1.4.4.9 的无左右输入对照里，两项都已上轨，水平速度保持为零。

Cases 19 and 20 each run with and without a jump and write `task-casebook-rail.json`. It records applied inputs, Featherfall, mounting, rail contact, and speed. Both reached the rails in the current tModLoader 1.4.4.9 fixtures. Horizontal speed remained zero with left and right inputs released.

| 当前验证范围 / Current coverage | 编号 / IDs |
| --- | --- |
| 已在隔离实机验证 / Verified in isolated game | 02, 09, 13 (单件 / one accessory), 18, 26, 28, 30, 34 |
| 有执行脚本，完整场景待验收 / Executable with scene-specific inputs, gameplay acceptance pending | 06, 07, 31, 35, 36, 37 |
| 有分段验证或缺口探针，完整目标仍阻塞 / Partial execution or blocker probes | 01, 03, 04, 05, 08, 10–12, 14–17, 19–25, 27, 29, 32, 33, 38–40 |
