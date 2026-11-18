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
