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
TB_CASEBOOK_RESIDENCY=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_SHIMMER=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_GRAPPLE=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_SHAFT=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_HONEY=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_SKY=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_REFORGE=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_SOULS=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_ALTARS=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
TB_CASEBOOK_ORECHAIN=1 TB_TASK_CASEBOOK=/tmp/tb-task-casebook-bundle.json \
  python3 tests/Agent/Engine/run.py --control --world /path/to/Checks.wld
~~~

储物场景跑 26、28、09。社交场景跑 34 和单件饰品的 13。原版右键场景额外跑 30，使用原版回弹挡板判定。矿车场景跑 18，记录车速、下车与蘑菇入包。重铸可能耗尽次数，此时验收物品是否归还。

Storage runs cases 26, 28, and 09. Social interactions runs 34 and tries 13 against one accessory. The world-interaction fixture runs 30 and reads the vanilla bumper predicate. The house fixture runs 02, builds from materials and checks native housing. The minecart fixture runs 18 and records speed, dismount, and mushroom pickup. Reforging can hit the roll limit, in which case the fixture checks that the accessory was returned. The runner copies the world into an isolated save and prints each result as `TASK_CASE`.

19 和 20 各跑有跳与无跳两次，报告写入 `task-casebook-rail.json`。记录实际按键、羽落、上车、接触铁轨和车速。当前 tModLoader 1.4.4.9 的无左右输入对照里，两项都已上轨，水平速度保持为零。

Cases 19 and 20 each run with and without a jump and write `task-casebook-rail.json`. It records applied inputs, Featherfall, mounting, rail contact, and speed. Both reached the rails in the current tModLoader 1.4.4.9 fixtures. Horizontal speed remained zero with left and right inputs released.

入住场景跑 35 和 36，记录夜间 NPC 实际瞬移、原版晶塔居民校验由不满足变为满足，以及角色传送到目标晶塔。

The residency fixture runs 35 and 36. It records native NPC relocation at night, the change from insufficient to sufficient pylon residency, and player arrival at the destination pylon.

微光场景跑 31 和 33。从干燥地面探查附近的微光，再单独从干燥岸边投掷木剑，核对原版分解生成的七个木材全部入包。测试全程监测角色是否接触微光。

The Shimmer fixture runs 31 and 33. It locates a nearby pool from dry ground, then separately throws a Wooden Sword from the bank and checks that all seven Wood from native decrafting were collected. It monitors the player for Shimmer contact throughout.

钩爪场景跑 21 和 32。以钩爪移动到两个锚点，检查最终挂点；另一轮对比同一记伤害在空手站立与挂住钩爪时造成的位移。

The grapple fixture runs 21 and 32. It traverses two anchors and checks the final native attachment, then compares displacement from the same hit while standing and while latched.

竖井场景跑 22 和 23，各有一个两格宽对照。对照中角色下井、在同一列挖掉三格并下移 48 像素。一格宽的直井中，钩爪挂住后角色仍停在入口，脚本报告阻塞。

The shaft fixture runs 22 and 23 with two-tile-wide controls. The controls entered the shaft, removed three tiles from one column, and descended 48 pixels. In the one-tile-wide straight shaft, the hook latched while the player stayed at the entrance, and the scripts reported the blockage.

蜂蜜场景跑 24。角色先用空桶舀蜂蜜，受伤后在脚边倒出，获得原版蜂蜜增益，再把蜂蜜收回桶里。逐帧核对伤害、接触蜂蜜和桶的状态，验收脚边无剩余液体。

The honey fixture runs 24. The character fills an empty bucket, pours honey at its feet after taking damage, gains the native Honey buff, and scoops up the honey. The fixture samples damage, honey contact, and bucket state per frame and requires a dry end state.

天域箱场景跑 25。脚本查询两个重叠的指定空域，识别并去重天域箱，使用重力药水升至云层，再倒置移动和取物。实机核对两个天域箱清空、两件物品入包和普通木箱原样保留。场景覆盖一条有连续云层顶面的路线。

The sky fixture runs 25. The script scans two overlapping sky regions, deduplicates Skyware Chests, drinks a Gravitation Potion, and travels and loots while inverted. It checks both Skyware Chests are emptied, both items are collected, and a Wooden Chest is untouched. The route has a continuous cloud ceiling.

重铸场景跑 13，使用经典角色的五个已穿戴饰品槽。跨批次保留已刷好的饰品，并核对最终词缀、原装备、实际扣款及清空的光标和重铸槽。另跑一次预算不足对照，核对零扣款。

The reforge fixture runs 13 across five equipped slots on a Classic character. It keeps finished accessories across batches and verifies final prefixes, original item types, actual spending, and empty cursor and reforge slots. A separate insufficient-budget attempt verifies zero spending.

魂类场景跑 06。夹具构建地下神圣和地下腐化两处狩猎区，生成蝙蝠作为真实敌怪。脚本验收到场群系，击杀和拾取走原版流程，最终核对背包中两种魂各至少 9 个。

The soul fixture runs 06 with underground Hallow and Corruption hunting areas and spawned Cave Bats. The script checks the current biome and uses native combat, drops, and pickup. Acceptance requires at least nine of each soul in the inventory.

祭坛场景验收 07 的前段。用圣锤敲掉九个真实祭坛，通过原版祭坛计数和全图矿物格数确认新三矿生成。随后仍需验收采矿和制作链。

The altar fixture verifies the first part of 07. It smashes nine native altars with the Pwnhammer and confirms each ore tier was generated using the native altar counter and world tile counts. Mining and crafting remain to be verified.

采矿场景单独运行 07 的后段，从熔岩镐和原版矿物格开始。当前实机完成了钴镐，秘银入包 97/100，随后因导航无进展退出。验收仍要求实际制作两级镐、砧、熔炉及整套护甲。

The mining fixture runs the second part of 07 from a Molten Pickaxe and native ore tiles. The current run crafted the Cobalt Pickaxe and collected 97 of 100 Mythril Ore before navigation returned no progress. The acceptance gate still requires both upgraded pickaxes, the placed anvil and forge, and the complete armor set.

| 当前验证范围 / Current coverage | 编号 / IDs |
| --- | --- |
| 已在隔离实机验证 / Verified in isolated game | 02, 06, 09, 13, 18, 21, 24–26, 28, 30, 31–36 |
| 有执行脚本，完整场景待验收 / Executable with scene-specific inputs, gameplay acceptance pending | 07, 37 |
| 有分段验证或缺口探针，完整目标仍阻塞 / Partial execution or blocker probes | 01, 03, 04, 05, 08, 10–12, 14–17, 19, 20, 22, 23, 27, 29, 38–40 |
