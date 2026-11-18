# 内置 Agent

TerraBlind 在模组内运行游戏 Agent。模型调查世界、规划目标并编写 JavaScript，游戏宿主执行动作、处理现场事件并核验结果。

## 上手

1. 安装并启用 TerraBlind。版本与下载见[项目首页](../README.md#开始使用)。
2. 在模组配置的「内置 Agent」中启用 Agent 并选择后端。
3. 带 10 个木材进入世界，发送 `/tb 帮我制作一个工作台`，完成后查看背包。

| 后端 | 配置 |
| --- | --- |
| 本机 Codex（默认） | 安装并登录 Codex CLI。模组收到任务后启动 `codex app-server`。找不到 CLI 时，在「Codex 可执行文件路径」中填写完整路径。可在模组配置中指定模型。 |
| OpenAI 兼容 API | 关闭「使用 Codex 后端」，填写 API Key、Base URL 和模型名。模型须支持 Chat Completions 工具调用。 |
| Jev | 需要独立的 TypeSafe API Key。在模组配置中填写，按需开启战斗功能。 |

Agent 的本机 API 配置保存在客户端。多人模式下，由 AI 客户端启用 `AiClient`，绑定后由所有者发送指令。

~~~text
/tb status
/tb stop
/tb /goal 去丛林收集生命水晶，顺路搜刮箱子
/tb /goal status
/tb /goal pause
/tb /goal resume
/ai list
/ai <玩家编号> bind
/ai <玩家编号> task <自然语言指令>
~~~

`/goal` 使用 Codex 后端。任务进度和动作句柄属于当前世界会话。Agent 提问时，用原指令入口回答。

## 执行机制

1. **规划。** Agent 查询地形、背包、配方等事实，用 DAG 表达任务依赖与完成条件。
2. **行动。** 每个节点可保存 JavaScript 行为。模型准备后续步骤期间，已经开始的后台动作可继续运行。
3. **处理变化。** 脚本可为事件注册处理逻辑。执行层在失败时保留动作参数、结果和现场证据，供 Agent 调整计划或续接动作。
4. **验证。** 游戏宿主检查背包物品、永久生命、装备、位置或击杀等实际条件，再标记节点完成并推进其依赖项。

## 脚本接口

脚本在模组内执行，`tool(name, args)` 等待游戏工具并返回结构化结果。下面的脚本查询附近箱子并尝试搜刮第一个候选，返回实际的工具结果。

~~~javascript
const found = tool('find_tiles', {name: 'Containers', n: 1, max_dist: 30});
if (found.error) return found;
const chest = found.tiles?.[0];
if (!chest) return {found: false};
return tool('collect_treasure', {x: chest.x, y: chest.y});
~~~

| 接口 | 用途 |
| --- | --- |
| `task_plan`、`task_plan_patch`、`task_step` | 创建与调整计划，查询和验收节点 |
| `action_start`、`action_query`、`action_control` | 在后台运行、查询、暂停与续接动作 |
| `action_queue` | 预排后继动作，由宿主在前序完成并释放操作后交接 |
| `on(...)`、`watch`、`task_policy` | 响应现场变化，设置任务期间复用的反应 |
| `script_program`、`skill(...)` | 保存、修订和复用带版本的 JavaScript 技能 |
| `jev_judge` | 让 Jev 从脚本提供的候选中作出快速判断 |

工具的完整参数见 [Tools.json](Tools.json)。模型可读的运行约定见 [System.txt](System.txt)。

## 原版制作与 Mod 按键 / Native crafting and mod keybinds

| 接口 / API | 用法 / Usage |
| --- | --- |
| `craft_recipes` → `craft_recipe` | 打开背包后查询可用配方索引，制作一次并把成品留在鼠标上。Open the inventory, look up an available recipe index, and craft once onto the cursor. |
| `craft_into_inventory` | 用原版配方批量制作并存进背包。旧脚本中的 `craft` 调用应改用此入口。Batch-craft into the inventory using vanilla recipes. Migrate old `craft` calls to this tool. |
| `mod_input_list` → `mod_input` | 按 Mod 内部名查询已注册的按键，再用 `ModName/KeyName` 按下并释放。List registered keybinds by internal mod name, then press and release a binding by `ModName/KeyName`. |

`mod_input` 走 tModLoader 的 `ModPlayer.ProcessTriggers`，支持按下、保持和释放。Mod 用 `KeybindLoader.RegisterKeybind` 注册的输入会自动被发现。派发回执只确认按键已交给游戏，实际效果请查游戏状态。

`mod_input` delivers press, hold, and release through tModLoader's `ModPlayer.ProcessTriggers`. Bindings registered through `KeybindLoader.RegisterKeybind` are discovered automatically. Check game state to verify the mod's response.

~~~javascript
const bindings = tool('mod_input_list', {mod: 'MyMod'});
const key = bindings.keys.find(key => key === 'MyMod/ActivateAbility');
return key ? tool('mod_input', {key, frames: 1}) : {error: 'binding_not_found'};
~~~

## 储物容器 / Storage containers

先用原版交互打开容器，再用 `slot_state` 读取其 ID。世界箱子、小猪存钱罐、保险箱、护卫熔炉和虚空袋共用这套接口。Open the container through normal game interaction, then read its ID with `slot_state`. This applies to world chests, Piggy Banks, Safes, Defender's Forges, and Void Vaults.

| 操作 / Operation | 调用 / Call |
| --- | --- |
| 全部取出 / Loot All | `loot_all({expected_container})` |
| 全部存入、快速堆叠、补货、整理 / Deposit All, Quick Stack, Restock, Sort Items | `container_action({action, expected_container})`，`action` 分别是 `deposit_all`、`quick_stack`、`restock`、`sort_items`。 |
| 重命名世界箱子 / Rename a world chest | `container_action({action:'rename', expected_container, name})`，空字符串恢复默认名称。An empty name resets it. |
| 切换虚空袋自动拾取 / Toggle Void Vault auto-pickup | `container_action({action:'toggle_void_vacuum', expected_container})` |
| 单槽操作 / Click an individual slot | `slot_click` with `section:'container'` and the slot, cursor, and container values from `slot_state`. 用对应槽位的原版左右键语义。 |

多人游戏里，改名沿用原版时机，在关闭或切换箱子时提交。In multiplayer, the game sends the new name when the chest is closed or switched, as it does for a manual rename.

~~~javascript
const open = tool('slot_state', {section: 'container', slot: 0});
if ('error' in open) return open;
return tool('container_action', {action: 'quick_stack', expected_container: open.container});
~~~

## 验证

~~~sh
dotnet run --project tests/Agent/AgentTests.csproj
./check.sh
~~~

离线测试覆盖计划、事件、脚本和后端协议，编译检查需要 tModLoader 构建环境。

原生客户端验证使用独立存档。先构建 Agent 开发包，再为引擎测试指定一个可复制的世界：

~~~sh
python3 scripts/dev_client.py build --role agent
python3 tests/Agent/Engine/run.py --world "/path/to/World.wld" --continuation
~~~

引擎测试启动器面向 macOS，从当前检出目录的 Agent 开发构建中读取 `.tmod`，并把 `--world` 指定的 `.wld` 复制进独立存档。`--continuation` 使用预置资源验证采集句柄的暂停与恢复。带 `--live` 等真实模型模式的测试会消耗模型额度，具体模式见 `--help`。

一次受控原生客户端联调中，模型处理了箱子事件，搜刮后恢复原采集句柄，宿主核验最终库存为 10 颗生命水晶并确认箱中战利品已入包。资源和箱子由测试预置，移动用传送替代。自然地图的长任务须单独实测导航、生存与执行衔接。
