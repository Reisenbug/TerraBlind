# 内置 Agent

默认使用本机 Codex 后端。先安装 Codex CLI 并执行 `codex login`；mod 在首次收到任务时自动启动 `codex app-server`，不需要 Python 或额外终端窗口。在模组配置 → TerraBlind → 内置 Agent 中：

- 使用 Codex 后端：默认开启。
- Codex 可执行文件路径：留空自动寻找；找不到时填写完整路径。macOS 会优先查找常见安装位置，避开 HUD 启动包装器。
- Codex 模型名：留空沿用本机 Codex 默认模型，推理强度使用 low。它不使用下面的 API 模型名。
- 回复语言风格：可填写角色的说话方式。

Codex 使用自己已有的本机认证，不读取 mod 的 Agent API Key。ChatGPT 登录按该账号的套餐权限运行；若 Codex 本身用 API Key 登录，则沿用其 API 认证。它不会自动转用旧 API 后端。登录失败时在终端执行 `codex login`，再重新发任务。

关闭“使用 Codex 后端”后，使用原有 OpenAI 兼容 API 后端，需要填写：

- Agent API Key：通用模型服务商的密钥。
- Agent Base URL：OpenAI 兼容根地址，例如 `https://api.openai.com/v1`；也接受完整的 `/chat/completions` 地址。
- Agent 模型名：服务商的准确模型 ID，必须支持 Chat Completions 工具调用。

这些设置是 ClientSide，只保存在本机 ModConfigs。Jev 仍使用单独的 TypeSafe Key。API Key 不写进源码、不通过多人协议同步。

```text
/tb 帮我合成一个工作台，有不清楚的就问我
/tb 十个
/tb stop
/tb status
```

Agent 提问时，下一条 `/tb` 是当前任务的回答。执行中收到其他指令会取消旧动作，把新要求交回同一段对话处理。`/tb stop` 和 `/halt` 都会停止当前 agent。离开世界会取消任务并清空本次世界对话。

模型顶层入口为 `run_script/get_state/say/ask`。完整游戏 API 的名称、说明与参数定义放在系统上下文中，游戏操作由代码里的 `tool(...)` 调用；原分发器和权限检查保留。Codex 与 API 后端使用同一入口，不再把所有游戏操作作为平级模型函数暴露。

“自行寻找装备、合理搭配、自己准备战斗”授权Agent决定常规路线、装备选择、准备数量和开打门槛；明确的装备禁令、地形权限、保护区和停止要求仍优先。技术未知先查询，不把日常选择退回玩家。明确交付任务缺数量、目标实质歧义或需要越过权限时仍须询问。

Boss已有独立实时控制器，`boss_status`和`get_state.boss_control`暴露开关、许可、当前状态及在场Boss。`set_combat({boss_control:true})`开启原有Jev走位和自动攻击，false关闭；auto_dodge/auto_attack只修改许可，二者不能混淆。开启前先释放其他动作输入，不用普通fight或PvP工具维持Boss战。需要可用TypeSafe Key；状态只报告模组配置中是否有Key，其他来源未检查，不输出Key。Agent停止或离开世界时恢复Agent修改前的控制器开关。该接口不负责召唤、穿戴装备或场地建设，也不保证获胜；普通fight本身具备局部避让和射界处理，旧“只定点攻击”描述已更正。

`python3 tests/Agent/Engine/run.py --planning-readonly` 启动独立客户端，允许模型查询真实世界副本、配方、原版编号和Wiki，所有动作在分发前由白名单拒绝；不执行模型提出的下一步。测试起点100血、三件铜工具，关闭测试进程的Concessions补给，不改正式设置。2026-10-31 Astra/low完成7次查询，未尝试或执行修改操作；自行提出先搜刮最近箱子(2057,317)，受阻改试(2162,328)，给出武器、准备门槛及Boss控制器启用方案，questions为空。报告 `/tmp/terrablind-agent-engine-1793460893321055000/agent-planning-result.json`。前一轮沿用模组9999木材补给，提出先合成工作台，不能当纯开荒起点。实际执行、装备穿戴和老人对话召唤仍未验证；模型准确指出当前API未明确提供后两项接口。

跨段脚本传入稳定的 `workflow`，使用 `step(id, fn)` 保存有独立恢复意义的执行步骤。宿主缓存完成步骤的JSON返回值；同名步骤再次出现只读缓存，不重放动作。失败或取消时未完成步骤为uncertain，不能直接重跑；先读取 `get_state.script_workflows` 或 `script_progress`，核对实际状态，再以当前revision明确retry或complete。核对记录附实际随身库存和永久血量，但不替代任务条件验收，也不提供崩溃事务或任意副作用的恰好一次保证。JS变量不保留，只有步骤结果、最近12次调用和最后段落结果由宿主保存；退出世界清空，最多8个workflow、每个128个步骤。

脚本返回 `{status:'yield', completed, blocked, next}` 可要求模型根据新信息续写。宿主只对本轮新yield的workflow续接，完成步骤增加才算进展；无进展最多额外一次，之后paused。`status:'done'` 与 `status:'blocked'` 不自动续接，不等于宿主已验证最终游戏目标。进度读取和调用日志不增加用于核对的revision；没有运行中step时可在续写段落中核对uncertain步骤。

`action_start({tool,args})` 支持gather/nav_to/fight，立即返回句柄；对应高层执行器在后台运行。`action_query({handle,wait_ms})` 返回任务状态、当前hp/dead/像素位置/类型化库存、工具结果和上次尝试证据；等待最多1000毫秒，查询不重启动作。`action_control({handle,command,args?})` 支持pause/cancel/resume。暂停等待旧动作和输入清理完毕，角色输入一次只归一个任务；其他操作返回action_busy。恢复复用句柄并增加attempt，先读取现场再进入原高层工具；gather保留目标总库存，nav_to/fight重新判断当前现场，不重放旧按键。可整体更新args。已完成任务拒绝恢复；暂停、取消、失败任务均须明确resume。句柄属于当前玩家指令，脚本正常返回不取消，stop/新指令/离开世界取消；退出世界清空，最多32个句柄。查询新状态不会覆盖实际暂停时的证据。默认战斗及导航规则、装备适配和工具自身超时仍适用。

需要监控危险时，脚本启动动作后循环查询并检查实际hp等条件，条件触发后暂停、保存最小业务结果并yield；下一段核对现场后恢复。实时反应速度取决于查询间隔和游戏帧，不把这个循环当逐帧避弹控制器。step返回值最多8000个未转义JSON字符，应保存handle、状态、计数等最小字段，不复制整份get_state或任务计划。完成动作仍需验证任务条件。

`python3 tests/Agent/Engine/run.py --continuation` 不调用模型：真实采集0→3时第一段暂停并yield，第二段跳过已完成启动步骤，复用同一handle恢复到10。2026-10-28 实测报告 `/tmp/terrablind-agent-engine-1793191226792824000/agent-engine-result.json`，两段分别5891/9968毫秒，attempt=2；仅一次预置10颗水晶，导航为teleport_stub。`--workflow-live` 使用Astra/low在测试配置中生成代码，过程中测试模组把血量降至80一秒再恢复100，验证模型根据真实观测暂停、跨段续接、同句柄恢复和最终库存；会消耗模型额度，生产默认模型不变。

2026-10-28 `--workflow-live` 修复后实测通过，报告 `/tmp/terrablind-agent-engine-1793191779968386000/agent-live-result.json`，65.774777秒。模型自行编写两个脚本段落：首次在实际库存3、HP80时暂停；随后确认HP100，复用句柄恢复至库存10、attempt=2，宿主item条件验证通过，计划completed、agent idle。宿主保存track/plan/handle/pause/resume五个完成步骤，模型没有重启任务。首次联调暴露了读取进度导致版本失效、中文JSON转义造成步骤限额不一致，以及后续查询覆盖暂停现场三个问题；已有对应回归检查。仅测试临时场地中的采集与危险监控，导航仍用传送替代；移动/战斗句柄的取消与恢复由独立测试覆盖，不能宣称复杂地形实机验收。客户端功能报告后仍出现缺失声音资源异常，测试进程已退出。

`run_script` 让模型一次提交 JavaScript 函数体，在 mod 内用 Jint 4.17.0 执行，不依赖外部 Node/Python。`tool(name,args={})` 串行等待现有游戏工具返回原生 JS 对象/数组，支持变量、if、for、while、return；`print(value)` 记录调试输出。接口仍使用原工具的名称、参数、权限及完成验证，不直接暴露 Terraria 对象、文件、Shell 或任意网络。它是完整 JS 语法配游戏工具接口，不是自行定义语法的 DSL，也不执行 TypeScript。例子：

```javascript
const found = tool('find_tiles', {name:'Containers', n:5, max_dist:100});
if (found.error) return {needs_decision:true, found};
const failures = [];
for (const chest of found.tiles) {
    const taken = tool('collect_treasure', {x:chest.x, y:chest.y});
    if (taken.error) { failures.push({chest, taken}); continue; }
    const recipes = tool('recipe', {name:'ThornChakram'});
    if (recipes.error) return {needs_decision:true, recipes, failures};
    if (recipes.recipes.some(r => r.can_craft_now)) {
        return tool('craft', {name:'ThornChakram', amount:1});
    }
}
return {needs_decision:true, failures};
```

脚本结束只表示代码执行结束，不能替代 `task_step verify`。工具返回 error/ok/outcome 仍要显式检查；失败不回滚已经完成的动作，不自动重放脚本。脚本变量不跨调用保留，任务续接仍读取宿主 task_plan 的真实进度。默认限时120秒，最大180秒，最多100次工具调用、50000条语句、32MB解释器分配、64层递归；代码最多16000字符，返回 JSON 最多16000字符，保留最近12次子调用及最多20条 print。解释器限制是协作式检查，不能抢占阻塞的宿主代码。脚本不支持异步 Promise 返回，禁止嵌套 run_script 和脚本内 ask；需要询问时 return 证据，由模型在脚本外处理。超时取消在途工具并收尾，父任务取消由原有停止机制处理，避免旧脚本误停新任务。

构建会从固定 NuGet 版本恢复 Jint/Acornima 并复制到 lib 后打包进 `.tmod`；生成的 DLL 不提交，授权文本保存在 lib/THIRD_PARTY_LICENSES.txt。正常玩家只需要完整 `.tmod`。`python3 tests/Agent/Engine/run.py --script` 不调用模型：一次脚本采集预置10颗水晶，按真实血量循环使用并验证300血，再验证1秒超时会停止600帧动作。接近由传送替代、角色免伤，不能视为导航或模型自主编写的验收。2026-10-28 实测第一段14次工具调用、19610毫秒、永久血量300且 verified=true；超时段1036毫秒后动作已停。客户端退出仍有既有音效资源错误，功能检查通过不代表退出无异常。

Codex 使用本地 stdio JSON-RPC，由 mod 管理子进程。每个世界使用独立于编程会话的临时 thread，工作目录为当前保存根目录下的 `TerraBlindAgent`；对话保留到离开世界、关闭 AI 身份／内置 Agent 或后台进程退出。模型、路径或人格变更在下条任务生效，并建立新会话。该进程配置禁用 shell、插件、应用、子代理、记忆、技能发现及网页搜索，并在建 thread 时禁用现有 MCP 服务；使用游戏指令、空环境列表及只读沙箱。全局 Codex 配置和登录不被修改。

取消任务时，mod 立即停止游戏动作，再向 Codex 发送 `turn/interrupt` 并最多等待 3 秒完成收尾；无响应时终止该子进程，下一次任务重建会话。退出世界或关闭 AI 身份时终止该子进程。进程异常会报告失败，不自动重放可能已有副作用的任务。动态工具接口为实验 API，目前按本机 Codex 0.163.0 协议验证。

多人模式：AI 客户端开启 `AiClient`，绑定后由所有者发送 `/ai <编号> task <自然语言>`；提问的回答也走这个入口。回复回传给绑定的所有者。普通玩家客户端不运行 AI 工具。

迁移了 `second_player.py` 的 23 个工具：状态查询、Wiki、寻找地形与方块、导航、采矿、物品使用、放置、交互、战斗、开箱取物、合成、配方、台账、动作原语、询问和说话。增加 `set_combat`，可禁止默认自动走位或攻击，许可持续到再次修改或离开世界。

原脚本的 find 模板、`agent_loop.py` 预生成计划分支、导航时自动绕路捡箱子没有移植；现在统一使用逐步工具调用循环。原有 mod 速通流程保留独立入口。

步骤受阻必须附有宿主记录的实际动作失败或搜索无结果。查询名称拼错、成功查到候选但模型不确定，都不构成阻塞证据。成功恢复后步骤回到 active，旧失败保留在 previous_failure；工具返回的 task_plan 会同步更新。计划创建只接受步骤说明与完成条件，不能通过参数写入宿主的失败证据或历史。

宿主在动作返回和状态查询时，读取当前步骤完成条件的真实值。永久血量100→120等阶段内提升也计入 progress_revision，允许继续下一轮；观察本身不会完成步骤，完成仍需 verify。每项条件只计超过此前最高值且不超过目标值的进展，反复丢弃/拾取或囤积超量资源不会无限刷新续接机会。没有真实条件进展时仍最多额外续接一次。

`game_catalog` 从当前加载的原版 ItemID/NPCID/TileID 常量查询内部英文名和编号，支持名称片段，精确匹配优先，最多12条。kind 必须明确区分物品、NPC与方块；查询结果不代表已经持有或击杀。它不查询模组注册名、中文译名、掉落或召唤规则，不修改世界，也不消耗模型额度。

也可用数字编号反查原版名称。物品结果的 `places_tile` 来自主线程读取的游戏物品放置定义，含方块编号和内部名称；不可放置时为 null。它不证明挖掉该方块会掉落原物品，也不证明世界中存在该方块。引擎检查验证 DirtBlock 对应 Dirt、LifeCrystal 不可放置；不能用此关系反查生命水晶来源。累计14组定向检查。

方块查询还匹配 `MapHelper` 与 `Lang.GetMapObjectName` 提供的当前语言地图图例，返回 `display_names`，按显示名精确匹配优先。没有地图标签的方块仍只能按内部名或编号查询；这不是完整掉落数据库。Heart 与 LifeCrystalBoulder 的地图图例同为 Life Crystal，查询必须保留两项，不能把排在第一项视为来源已确认。

工具异常会以 tool_exception 和具体原因返回，并记录在计划中。等待动作期间，状态查询报错或被拒绝会停止该动作；停止异常或被拒绝也会一并报告，不伪称停止成功。取消请求仍向上传递，不写入可能已被新指令接管的计划。

多阶段目标使用 `task_plan` 建立有序步骤，再用 `task_step verify` 验证当前步骤。宿主检查永久血上限 `statLifeMax`、随身背包中指定 type 的数量、建计划后的指定 NPC 击杀次数；不会使用临时血量加成、物品名称或历史 Boss 进度替代。击杀证据目前只支持单人，联机计划中的此条件会被拒绝。完成步骤是历史里程碑，最终步骤需要同时包含最终仍应满足的条件。导航受阻可 `task_step block` 保存原因，动作结果和验证实际值会留在 `get_state.task_plan`。模型结束回复后，若计划尚未完成且未阻塞，宿主携带新状态自动续接；未推进步骤时最多额外续接一次，再无进展进入 `paused`，每次步骤验证推进会重置这一次机会。再次发继续指令沿用游标。持续PvP仍沿用原本的事件唤醒。当前计划只保存在本次世界内存中，离开世界清空，不支持重启恢复。尚未通过真实“300血、荆棘旋刃、蜂王”全流程验收。

`fight_player` 按玩家编号原地瞄准并使用快捷栏武器。状态包含其他玩家及指令发送者的编号、位置、血量、队伍和 PvP 状态。工具会开启自身 PvP 并保持；目标必须也开启 PvP，且不能是同一非零队伍。它不追人，也不保证命中；报告的是目标实际前后血量。目标死亡、关闭 PvP、离开范围或收到停止指令会结束攻击。

`chop_wood` 接受本次需要新增的普通木材 `count` 和原话限制 `constraints`。一次委托内由代码扫描附近成熟普通树、定位树根、接近、选择斧头、砍伐并靠近掉落物；Jev 根据候选和失败现场选择下一棵树。仅真实背包中的 Wood 增量计入目标，不把树倒或已有库存算作本次收获。需要已有 TypeSafe Key。

采木接近阶段在计划派发前拒绝挖掘、垫方块、搭桥和跳放路线，以免为砍树改动其他地形；当前不保证能找到替代的无损路线。树木选择的语义限制由 Jev 根据候选附近家具等可见事实判断，不等于完整的房屋识别。扫描范围为水平 120 格、上下 48 格，每次提供最近 12 个候选，每个目标最多尝试两次，每轮检查 180 秒预算，总计最多 16 次选择。接近和拾取各最多 20 秒，挥斧最多 8 秒；遇到限制或预算返回已有进度，交回 Agent。`[harvest]` 日志记录候选、Jev 耗时、接近和挥斧结果。实机速度和成功率仍待验收。

模型请求在后台执行。游戏工具通过主线程队列直接调用共享分发器，不经过本机 HTTP，也不需要 Python。动作完成后返回当前位置、背包真实增减和台账进度。工具参数错误返回模型修正；网络或鉴权错误停止任务并报告。

旧 API 后端每个任务最多执行 40 轮模型调用，并压缩旧的大块工具结果；Codex 后端的对话与模型执行循环交给 Codex 自身管理。以下离线测试使用协议替身，不产生模型费用：

```sh
dotnet run --project tests/Agent/AgentTests.csproj
./check.sh
```

编译和离线测试不能代替实机验证。建议依次测试纯聊天、询问并回答、合成、导航中停止，以及“Boss 战不自动走位”的许可覆盖。

真实客户端定向检查：先 `python3 scripts/dev_client.py build --role agent`，再运行 `python3 tests/Agent/Engine/run.py`。它复制 `/tmp/terrablind-descent-world/Worlds/World.wld` 到唯一临时目录，沿用用户画面和按键设置，只加载正式包与测试模组。预置生命水晶、合成材料和铁砧，通过正式 AgentTools 验证吃水晶、合成荆棘旋刃、历史击杀不计入、本次 NPC 死亡回调、非实心生命水晶的导航参数适配，以及实际挥镐采集、掉落物入包后再使用。蜂王由测试代码瞬杀，不能视为 AI 战斗胜利，也没有测试自主收集远处材料。成功标记 `AGENT_ENGINE_COMPLETE checks=9 model_calls=0 controlled_fixture=true`，结果与日志位于输出的临时目录。测试结束由启动器关闭自己创建的进程，不注册自动重启服务。

附加 `--live` 会在上述检查后清空随身背包、还原100永久血与铜制初始工具，向真实 Codex 发送300血、荆棘旋刃、蜂王任务，最多运行180秒，消耗当前账号模型额度。测试场地仍是检查后的世界（含铁砧和历史击杀），不是原始开荒验收。它在结束时写 `agent-live-result.json`，捕获成功不代表任务成功，必须检查其中的真实状态、计划与日志。

当前 live 场地会重新清空附近地面上方空间与旧掉落物，放置铁砧和10颗可实际采集的生命水晶，关闭背包界面。不会直接给 agent 水晶或替它使用；只隔离验证第一阶段100→300血的执行。尚未预置完整的材料与召唤资源链，不能视为整条任务的受控验收。

离线测试共 70 组，结束标记为 `AGENT_CHECKS_COMPLETE groups=70`。直接链接生产的 Agent 协议、脚本解释器、宿主工作流、后台动作句柄、调度、网络、PlayerCombat、WoodHarvest、Gather 和 FightCoordinator。覆盖脚本入口及API元数据、组合与分支、逐次采集与行为顺序、跨段续接与核对、暂停/取消/恢复、输入归属和过期清理、Boss控制开关与许可分离、资源限额、工具参数及权限、真实条件验证、有限续接、采集预算、NPC 类型过滤、弹幕避让、射界恢复、名称解析与宝藏接近后取物的调用顺序；使用替身的部分不能视为客户端验收。

`dotnet run --project tests/Agent/AgentTests.csproj -- --routing-benchmark gpt-6-astra 2` 比较正式脚本入口与测试专用混合入口，不改变正式路由，会消耗模型额度。后者额外直接暴露use_item/gather/action_start/action_query/action_control，其余API元数据及系统规则相同，仅替换路由说明。两轮顺序交错、每项新建会话、固定low；模拟库存和危险，不测试游戏物理。三个场景：使用已有一颗水晶；收集10颗；每颗后说“叮”，库存3时注入HP80，须交回后由宿主恢复HP再继续。低血量期间继续采集标为失败，不由模拟器自动替agent避险。完成状态由库存、永久血、逐次顺序和危险期间行为检查，不采用模型自述。使用生产Jint、ScriptProgress、ActionTasks和TaskPlan，未实现的其他游戏工具明确返回错误。

2026-10-31 单次矩阵12个样本均完成目标，每组仅2次。原始代码、调用顺序、回复和逐项用量：`/var/folders/2p/90f7gl9d32g9227t1r_cd9nh0000gn/T/terrablind-routing-bfafefe7c08941e1879cb92fcb23a0ed/summary.json`，同目录每例JSON包含完整事件。没有启动游戏，没有改正式入口。

| 场景 | 脚本/混合成功 | 平均秒数 脚本/混合 | 平均输出tokens 脚本/混合 | 用量增长次数 脚本/混合 |
|---|---|---|---|---|
| 使用一颗 | 2/2、2/2 | 11.22 / 11.87 | 99.5 / 86.5 | 2 / 2 |
| 收集10颗 | 2/2、2/2 | 19.80 / 21.81 | 360.5 / 323.5 | 3 / 3.5 |
| 逐颗＋遇险停止续接 | 2/2、2/2 | 40.03 / 46.13 | 1083.5 / 869 | 4.5 / 4 |

平均累计输入tokens（含缓存）分别为86293.5/88183.5、144396/169120、269089/257293；其中缓存输入分别42752/43904、88256/115392、196544/175488。不能直接将总tokens当费用。用量来自本机app-server的thread/tokenUsage/updated，重复累计值去重；增长次数可观察模型响应往返，但不是服务端含内部重试的精确API请求次数。宿主RunAsync回合在短/采集场景均1次，组合场景均2次；顶层工具往返分别1/1、2/2.5、2.5/2。耗时包含新会话初始化及服务延迟；样本小、上下文缓存与冷启动都有影响，不作显著性结论。

混合入口只在短任务选择了零脚本，采集任务两次仍都生成两段脚本。脚本组两次组合场景分别出现重复task_step验证、返回内容超过16000字符，最终目标仍完成；混合组没有工具失败。事件时间检查未发现采集/使用操作重叠。这组结果没有证明直接入口能稳定提速，也没有证明写脚本是短任务的主要成本；重复计划、检查与大结果返回值得优先单独测量。基准本身的消费、逐次顺序及危险判定也有不调用模型的回归检查。

`dotnet run --project tests/Agent/AgentTests.csproj -- --script-model` 是可选真实模型脚本验收，会消耗 Codex 额度，不启动游戏。使用生产系统提示词及其中五项工具定义（run_script/get_state/gather/say/game_catalog），以模拟库存隔离执行细节。模型自行生成代码，分别完成收集10颗，以及每收集一颗立即说“叮”再收下一颗。必须实际在脚本内调用采集，逐次行为的库存序列必须恰好为1..10；只有写脚本建计划不算通过。原始代码、调用层级、返回与回复保存到输出的临时目录。通过标记 `SCRIPT_MODEL_COMPLETE checks=2 mock_world=true` 只证明这两种意图在该模拟工具集上的模型编写与执行，不证明真实采集、完整工具集下的选择或任意任务规划。该入口不会在普通离线测试中调用。

2026-10-28 此入口两项实测通过，沿用本机默认模型（当时配置 gpt-6-astra），effort=low；并非游戏配置的模型验收。逐颗任务由模型生成一个脚本，内部21次工具调用（一次库存查询、10次采集与10次say），未提供现成代码。`dotnet run --project tests/Agent/AgentTests.csproj -- --script-replay <per-item.json>` 可原样重放该次模型代码，不再调用模型：第四次采集失败时仅前三次说话；一次取得两颗时不伪称完成逐颗行为，立即返回。两种重放均通过，标记 `SCRIPT_REPLAY_COMPLETE checks=2 mock_world=true model_calls=0`。仍不代表真实游戏采集链或其他模型通过。

`--script-model-full [模型名]` 使用当前生产入口（四个顶层函数加完整游戏API元数据），默认 gpt-6-luna；仍是模拟库存，不开游戏。简单总量目标不强制写脚本，逐颗行为必须在代码中执行。模拟器额外接受track，其余未实现工具返回not_simulated，不伪造成功。2026-10-28 对照：此前全平级工具下Luna批量收齐后只说一次，失败；改用脚本入口后Luna的简单任务通过，但逐颗任务未调用工具便声称缺少tool，仍失败。同一脚本入口Astra两项通过（每项一个脚本）。不能据此声称Luna稳定可用；生产默认模型未改变。真实游戏中的模型代码执行仍待验收。

`python3 tests/Agent/Engine/run.py --model-script <per-item.json>` 为上述模型代码的真实引擎重放入口，会启动独立客户端，不再调用模型。要求输入恰好包含一个run_script，原样提取code交给正式AgentTools执行；临时场地放置10颗生命水晶，初始库存0、永久血100，测试传送替代导航。采集、掉落、入包均走游戏，监听实际AgentChat.Say并记录当时库存。只有库存10、血上限仍100、代码返回completed=10且“叮”对应库存严格为1..10才通过；模型原始代码和实际结果保留在报告中。入口完成编译尚不代表该游戏检查已经通过。

2026-10-28 真实引擎重放通过，报告 `/tmp/terrablind-agent-engine-1793189470567463000/agent-engine-result.json`。输入为完整API脚本入口下Astra生成的逐颗脚本，模型输出与实际执行code逐字符相同，SHA256 `a693ff06123ef6ab5f5cf42b6ab3381174b0d0ac0140aef0837c4e3aba26839c`。22次工具调用、18042毫秒，真实库存0→10；实际say对应库存1..10，永久血100。预置10颗水晶仅一次，不补放；没有再次调用模型。标记 `AGENT_MODEL_SCRIPT_COMPLETE checks=2 model_calls=0 placed_crystals=10 inventory=10 announcements=10 hp=100 navigation=teleport_stub`。这证明模型生成的循环、库存分支和逐次行为能通过正式API执行，不证明自然地图探索、Luna可靠性或任意任务均可完成。功能报告产生后客户端仍出现macOS原生退出异常，不能称退出过程正常；测试进程已结束。

普通 NPC 的 FightCoordinator 在允许 auto_dodge 时，比较24帧内停留、左右移动与起跳的预测碰撞风险，并继续攻击。使用当前弹幕速度、extraUpdates 和附近敌人速度做短时外推，按当前跑速、加速度、重力和方块碰撞估计移动；不推演敌人后续 AI、复杂弹道或完整饰品物理，也不求全局路径。它不调用模型，不能视为已接入 PvP 的完整 Jev 决策。

当前射界受阻时，同一短时比较还考虑取得射界，碰撞代价优先于射界代价；跳跃估计包含持续按住跳跃的12帧。对受方块碰撞的弹丸，直线被挡时停止攻击；ContentSamples 中 tileCollide=false 的弹丸不套此限制。这仍不是完整弹道遮挡模拟，不保证抛物线、曲线或特殊模组武器的判断正确。

`python3 tests/Agent/Engine/run.py --hornets` 创建42格宽、12格高的封闭空场，给300血、500投刀，不给免伤或额外装备；自然刷怪关闭。黄蜂正常运行 AI，依次生成3只，每只预留2秒行动再开始攻击，每轮最多30秒，不重置角色血量或清掉前一轮毒刺。实际 NPC 死亡回调确认击杀，记录敌对弹丸、移动帧、受伤和背包消耗。2026-10-28 两次单次实测：站桩基线全部击杀、累计受伤170；新增避让后全部击杀、累计受伤46、结束256血。随机行动未做严格配对或多轮统计，不证明稳定无伤、复杂地形战斗或自主采集毒刺。

`--hornets-wall` 在同一场地增加2格宽、5格高的石墙。2026-10-28 单次修复前后均击杀3只：受阻时仍攻击的帧数合计1001→12，总帧数2141→1426，均未受伤。修复后有主动移动与起跳；剩余12帧记录可能涉及同一帧内位置变化，尚未逐帧归因。该对比未固定敌人随机行动，不能视为统计性能结论。

`find_tiles` 在搜索前使用当前游戏地图显示名和原版编号解析名称。只有精确匹配且类型唯一时才转换为内部名，保持 n/max_dist 等参数不变，单次调用搜索入口；返回 requested_name/resolved_name。有多个精确类型时返回 ambiguous_tile_name 和 name_candidates，不执行搜索。只有近似匹配时保留旧入口行为，名称错误时附候选。`gather` 的方块源使用同一入口，不要求模型反复手动换名。这不建立物品到掉落来源的关系，也没有根据300血目标自动推导或执行采集。

单独验证批量执行：`python3 tests/Agent/Engine/run.py --batch`。跳过此前的单物件检查，一次放置10颗 Heart，初始背包没有水晶，调用一次正式 Gather.Run 和 GameGather；不再补放水晶、不调用模型。测试适配器在每个动作前传送到目标旁，标记 navigation=teleport_stub，隔离尚在另一工作区迭代的导航。真实挥镐、掉落与拾取仍由游戏执行，仅背包达到10颗才写 `AGENT_BATCH_COMPLETE`，限时90秒。它不验证导航、agent 自主查知识、选择来源、使用水晶或完整任务。

2026-10-26 该批量检查实测通过：10次动作，12201毫秒，真实水晶库存0→10，model_calls=0、navigation=teleport_stub。带真实导航的前一次检查约101秒仅取得7颗，未达标；两项结果不可混为真实导航通过。成功检查结束后客户端退出日志仍出现macOS原生异常，功能完成记录不等于退出过程正常。

`python3 tests/Agent/Engine/run.py --health-live` 单独向真实 agent 提交“永久生命上限提升到300”，初始100血、铜制工具，当前版本直接使用世界副本原有地形与水晶，不预放资源或清空落点区域。模型不知道方块内部名或具体操作步骤。测试模组只将 RecedingNav.Start 接近替换为传送，覆盖工具入口及取物流程的直接导航；角色测试期间免伤。查找、采集、使用、宿主续接和任务验证均使用正式代码，最多180秒，消耗模型额度。报告含真实永久血量、计划状态和 navigation=teleport_stub，不能把捕获报告等同通过。

按用户约定，测试传送直接把角色中心放到目标格中心，使用原版 Player.Teleport，并清零速度、重置 fallStart/fallStart2。不寻找可站点，不检查卡块，不清除方块或生成平台；卡块属于本轮排除的寻路问题。`--teleport-check` 使用世界原有最近生命水晶，检查精确坐标、传送前后周围方块未变、坠落计数重置、90帧后血量及距离排序，不再造地形。

find_tiles 保持原方形 max_dist 搜索范围，按输出的曼哈顿 dist 排序并取前 n 项；扫描不会在同圈尚未完成时截断，也会继续到后续圈不可能出现更近结果再停止。它给出空间距离，不保证导航代价最小。模型仍需按候选与真实失败证据选目标，不能把排序修复当作已验证模型会正确选择。

2026-10-27 单次真实模型检查通过：55.98秒，永久血量100→300，计划两步均以实际血量验证为 completed，agent 为 idle。模型选择 mine_vein 批量采矿，再用 act 连续使用水晶，没有使用 gather。仅证明本受控场景中的300血阶段；不证明自然地图探索、导航、丛林材料来源、荆棘旋刃或蜂王全流程。

`gather` 接受最多8种资源的目标随身库存，每项包含 `item_type`、`count`，以及由 agent 根据知识查询提供的 `tile_names`／`npc_types`。代码优先拾取所需掉落物，再选择附近目标敌人和方块；每个候选最多两次，总计最多32次动作，单次委托最多180秒。结束时以真实背包数量判断是否足够。没有候选只表示当前扫描未找到，不表示整个世界不存在。拾取会等待掉落物落稳并重新读取位置；接近仍依赖现有导航。

`npc_find` 查询当前活动实体；`fight` 可传 `npc_types` 限定目标；`collect_treasure` 先接近，再复用现有开箱取物流程。`npc_alive` 完成条件检查活动且存活的指定类型 NPC，不证明由自己召唤。普通 NPC 战斗仍使用简单快捷栏武器选择，不保证移动追击、刷怪或复杂地形战斗。`find_tiles` 返回 `near_hive_wall`，不再因临近蜂巢墙而静默隐藏目标。

当前引擎定向检查已扩展到13组，标记为 `AGENT_ENGINE_COMPLETE checks=13 model_calls=0 controlled_fixture=true`。新增蜂巢墙邻近目标查询、真实史莱姆与生命水晶的混合采集，以及空中掉落物落稳后拾取。场地和实体由测试预置，蜂王仍由测试代码瞬杀；这些结果不能证明自主探索、自然刷取材料或击败蜂王。尚缺通用场地建设、无候选时的区域搜索与完整任务实机验收。

可选真实 Codex 联调会消耗当前 Codex 登录账号的模型额度，但只提供模拟背包，不连接游戏：

```sh
dotnet run --project tests/Agent/AgentTests.csproj -- --codex-smoke
```

完成标记为 `CODEX_SMOKE_COMPLETE checks=4 mock_world=true`：检查真实工具调用、上下文保留、打断和打断后的连续对话。

### 装备与 NPC 对话

`watch` 登记当前指令的 nearby_chest/action_finished/low_health/player_died/inventory_full 事件；宿主每250毫秒读取现场，不请求模型。箱子仅检查半径内仍有物品的真实箱子实体。相同坐标、同一动作attempt去重，低血量/死亡/满包每指令一次，主动决策点和被动事件共用6次预算；重配不重置预算。事件发生后，在脚本下个工具边界停止后续操作，宿主等待当前模型轮结束，再暂停后台句柄并带事件、库存、任务及脚本进度启动下一轮。它不是任意指令中断，长时间同步工具仍需返回；需要观察的操作应使用后台句柄。

`decision_point` 主动交回且不执行后续语句；未完成step保留uncertain，不能直接重放。模型可核对后提交剩余代码，或用action_control resume args修改同一句柄的剩余参数。参数属于动作，不改默认设置或玩家权限。停止和新指令清理观察状态。77组离线检查包含生产宿主事件交接、暂停后重设目标并同handle恢复、停止后无新调用，以及脚本无法捕获事件异常后继续执行工具。尚待真实模型与客户端的事件流程验收。

### Codex goal 与事件流程验收

2026-11-01 晚间实机反馈修正：脚本与导航不再有120/180秒墙钟截止，Gather也取消隐式时限；保留JS语句/内存/调用次数上限、真实失败、死亡与玩家停止。旧seconds参数仅兼容，不限制长动作耗时。脚本直接调用nav_to/gather/fight/mine_vein/chop_wood也由宿主自动托管，正常保持串行等待，事件发生则暂停并提前交回；后续代码不执行，原句柄及未完成step保留。不再依赖模型登记watch。每250毫秒采样，状态含samples/last_observation，每20次写agent_observe日志，不按周期请求模型。6次机会事件预算不会关闭现场采样或吞掉完成/失败/死亡。

查询错误单独保存recovery，含原参数和完整name_candidates；仅print失败而隐式返回null的脚本标tool_failed，不再算成功。宿主最多补3轮有证据的修复，同参数失败3次后拒绝再次执行。正确查询消除对应旧失败。首次script_progress get不存在workflow返回found=false，不能对不存在的步骤retry/complete。run_script包装和只读action_query不再覆盖原动作证据。

丛林入口用完整天空列的第一块实心地面，检查3格宽/高人物空间和岩浆，按当前TileID.Sets.JungleBiome、原版buffScanAreaWidth/Height与SceneMetrics.JungleTileThreshold计算局部生态数量；不足则拒绝，不找地下小洞的最近单块草。独立引擎报告 `/tmp/terrablind-agent-engine-1793540635022563000/agent-engine-result.json`：选点(2905,275)，局部141/阈值140，扫描1680000格、124ms，传送测试后原生player.ZoneJungle=true，天空列检查通过。原世界只读扫描，只有测试人物被传送；不宣称真实导航已到达。

新普通指令Luna联调：`python3 tests/Agent/Engine/run.py --automatic-live`，未要求watch或后台句柄，只要求收10颗水晶并顺路搜箱子。报告 `/tmp/terrablind-agent-engine-1793541026863389000/agent-engine-result.json`：自动观察83次，箱子事件暂停、实际搜刮、同一句柄attempt=2恢复，最终库存10且原生goal完成，4个宿主模型轮次。导航仍由传送替代、10颗资源及中途箱子由测试有限预置；不是自然地图一条龙验收。新增回归后85组独立检查通过，包含未注册watch的同步导航中断、失败候选修复、超过旧seconds仍执行及玩家取消。

无模型的真实引擎脚本检查也通过：`python3 tests/Agent/Engine/run.py --script`，报告 `/tmp/terrablind-agent-engine-1793541390809299000/agent-engine-result.json`。seconds=1的脚本实际执行90帧动作后正常完成，并验证300永久血，标记 `AGENT_SCRIPT_COMPLETE checks=2 model_calls=0 hp=300 verified=true navigation=teleport_stub`。它验证游戏等待不会被旧脚本墙钟打断，不代表已经实测任意时长导航。

游戏中用 `/tb /goal 目标内容` 创建长期目标；`/tb /goal --tokens 预算 目标内容` 显式设置正整数token预算。`/tb /goal status` 查询，`/tb /goal pause` 或普通stop停止，`/tb /goal resume` 继续保留的目标。预算耗尽或已完成的目标不能普通resume。普通指令不会隐式创建Codex goal。离开世界仍清空游戏计划与句柄，不承诺重启游戏后续接。

实际调用Codex app-server的thread/goal/set与thread/goal/get，goal会话使用持久线程。服务器启用goals；游戏线程单独关闭原生自动续轮和模型内置goal工具，由游戏宿主调度同一目标的事件续接，防止原生自动轮次绕过输入交接或模型提前标完成。完成前重新读取最终条件，所有步骤已验收且没有后台动作占用输入才写native complete。模型回复结束、仅有启动成功、计划中的旧completed标记都不能替代最终现场。无进展只额外续接一次；每轮最多32次顶层游戏工具调用，脚本另有100次工具与时间限制。

原生goal的tokensUsed/timeUsedSeconds在该宿主调度方式下不会自动累计，保留原始值，不伪造。实际额度判断使用thread/tokenUsage/updated的累计totalTokens差额，另列host_tokens_used；暂停恢复不重置计数。包括缓存输入，不能等同费用。预算在收到遥测时中断，可能超过阈值一个模型响应，不能当服务端生成硬上限。达到限制写budgetLimited并停止角色动作，不标完成。异常保留blocked状态；用户停止保留paused状态。停止确认完成后再关闭所属后台连接。

2026-11-01 当前81组离线检查通过。新增原生goal协议替身检查、宿主/goal命令续接、额度中断、32次连续失败截止、最终条件重新测量、事件结束保留证据且清除观察、同句柄恢复及停止。真正的协议验收分别运行：

```sh
dotnet run --project tests/Agent/AgentTests.csproj -- --goal-smoke
dotnet run --project tests/Agent/AgentTests.csproj -- --goal-control-smoke
python3 tests/Agent/Engine/run.py --watch-live
python3 tests/Agent/Engine/run.py --decision-live
```

这些可选入口会消耗模型额度，均使用gpt-6-astra/low，生产默认Luna未改。goal-smoke通过真实Codex持久目标的active→拒绝无证据完成→complete，世界为替身；报告 `/var/folders/2p/90f7gl9d32g9227t1r_cd9nh0000gn/T/terrablind-goal-protocol-593fd719826e4732a74f70af2ce723f9/result.json`。goal-control-smoke两项通过：真实模型的阻塞工具被取消后目标paused，无第二轮；1token阈值收到14455token遥测后中断，原生状态budgetLimited；报告 `/var/folders/2p/90f7gl9d32g9227t1r_cd9nh0000gn/T/terrablind-goal-controls-495102272c834dc9a2fa6711123ca6d0/result.json`。两者不是游戏行为验收。

真实客户端联合验收报告 `/tmp/terrablind-agent-engine-1793513272195296000/agent-engine-result.json`：有限10颗预置Heart、铜镐，初始目标库存5；实际取得2颗后生成一个装有金币的箱子；被动nearby_chest事件暂停旧采集，模型实际搜刮并保存独立step，然后主动decision_point交回；下一段把原句柄目标改为10，恢复采集；action_finished再唤醒，实际库存10且原箱子已空、金币入包，计划与原生goal均完成。相同句柄仅2个attempt，旧attempt为paused；三个事件各一次，结束后events为空。通过标记 `AGENT_WATCH_LIVE_RESULT inventory=10 looted=True resumed=True woke=True explicit=True native_goal=True`。引擎测试包SHA256 `f30e7c7826fcafe3050c3a44c02679cfbfea45ccf46f8c296c246e001b2dfc10`。

该次4个宿主模型轮次、4段模型工具代码；原生rollout有8个token_usage_record/response_id。累计inputTokens=998198，其中cachedInputTokens=793856，outputTokens=956，不能说只有4次模型推理或直接换算费用。完整代码、工具输出、事件与用量保留在 `/Users/lhy/.codex/sessions/2026/11/01/rollout-2026-11-01T14-08-14-01a195b9-0a0a-7a33-ae08-2db4798e90dc.jsonl` 及测试目录TerraBlindLogs。移除了把action_query嵌套记录回计划，以及决策点把整个旧状态嵌入workflow造成的证据膨胀，但完整游戏上下文仍较大，不声称额度成本已充分优化。

导航仍由测试传送替代，场地与箱子由测试生成；真实挖掘、掉落、拾取、开箱与任务验收由正式代码完成。这不证明自然地图探索、任意任务、Luna可靠性或300血/荆棘旋刃/蜂王整链成功。功能报告通过后，测试客户端终止阶段仍记录macOS原生退出异常，不能称干净退出。

`equipment_list` 和状态中的 `equipment_slots` 返回原版功能装备槽0..9：头、衣、裤、饰品，以及各槽是否解锁。`equip_item` 在背包0..49和指定装备槽之间交换物品，必须提供查询得到的 `expected_inventory`、`expected_equipment`（type/prefix/stack）。用空背包槽交换可以卸下装备。调用原生 ItemSlot 校验与点击逻辑，遵守部位、重复饰品、翅膀冲突、锁定槽及模组装备钩子；光标有物品或正在使用物品时拒绝。装备效果在下一次玩家更新生效。暂不覆盖时装、染料、坐骑、钩爪和模组新增槽。

任务完成条件新增 `equipped`，按真实已解锁功能槽中的物品类型计数；背包、时装及锁定槽不计入。它证明已穿戴，不替代防御、机动性等效果与战斗准备程度的检查。

`npc_dialogue_status` 查询当前对话、实际本地化按钮文字和商店状态。`npc_chat` 的 open 通过原生 NPC 交互打开对话，choose 接受当前按钮1或2与 `expected_text`，close 关闭对话。NPC编号、类型、距离和按钮文字均核对；超距离应先接近。选择经过原生对话处理与 NPCLoader 点击钩子，未硬编码特定 Boss。`button_dispatched` 仅表示点击已送达，模组仍可拦截；召唤后须用真实 NPC 条件验证。打开商店不包含购买能力。多人沿用原生交互与装备同步路径，尚未进行双客户端验收。

`python3 tests/Agent/Engine/run.py --interactions` 不调用模型。受控场景给头盔、云朵瓶并生成老人和商人，验证穿脱及真实防御/二段跳效果、装备完成条件、错误部位、过期快照、重复饰品、锁定槽、过期对话按钮、模组点击拦截、夜间原生召唤、距离限制、商店与关闭。2026-11-01 实测19项通过，标记 `AGENT_INTERACTIONS_COMPLETE checks=19 model_calls=0 equipped_verified=true equipment_effects=true native_dialogue=true boss_summoned=1 merchant_shop=true range_guard=true`。报告目录 `/tmp/terrablind-agent-engine-1793466410582972000`。这不证明自主寻找装备或击败骷髅王。

新增回归后，当前离线测试共72组，标记 `AGENT_CHECKS_COMPLETE groups=72`。其中装备/对话的离线适配器明确拒绝实际操作；上述原生行为由独立客户端检查，不由替身模拟成功。
