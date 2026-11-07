using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jint;

namespace TerraBlind;

public static class AgentScript
{
    public static void DescribePrograms(JsonArray tools)
    {
        var navigation = tools.First(t => t["function"]?["name"]?.ToString() == "nav_to")["function"];
        navigation["description"] = navigation["description"].ToString()
            + "默认travel进入请求路点附近的交接范围；mine接近待挖实心目标，interact按交互距离接近目标，stand搜索精确落脚位置。";
        var run = tools.First(t => t["function"]?["name"]?.ToString() == "run_script")["function"];
        run["parameters"]["properties"]["background"] = new JsonObject { ["type"] = "boolean",
            ["description"] = "配合task_node启动持续后台脚本并立即返回句柄；Agent可同时查询和准备后续。脚本内直接顺序调用动作，不嵌套action_start。" };
        run["description"] = run["description"].ToString()
            + "多动作连续执行可用background:true，脚本拥有主动作句柄，内部直接tool('nav_to',...)、tool('gather',...)及条件循环；action_control暂停/取消，原workflow保留。运行中改当前脚本需先暂停，后续节点可直接修改。";
        var plan = tools.First(t => t["function"]?["name"]?.ToString() == "task_plan")["function"];
        plan["description"] = plan["description"].ToString()
            + "DAG后台动作成功结束后，宿主按真实条件验收所属节点，并自动运行current_step中下一个就绪节点的已保存script，无需模型再次run_script。"
            + "动作成功但节点条件尚未满足时，宿主自动续跑该节点原workflow；用step保存句柄，查询已完成动作后执行下一项，不重复启动。"
            + "可在赶路时用task_plan_patch准备后续节点；显式action_queue优先。危险事件、暂停、取消或脚本主动yield/wake时交回模型。脚本返回后既无动作也未达成条件会报告execution_gap，不空转。";
        var queue = tools.First(t => t["function"]?["name"]?.ToString() == "action_queue")["function"];
        queue["parameters"]["properties"]["tool"]["enum"].AsArray().Add("run_script");
        queue["description"] = queue["description"].ToString()
            + "下一段需要现场查询、条件选择或多个连续动作时，tool用run_script，args为{task_node:同一节点id}。"
            + "宿主读取该节点已保存的script，前置验收后立即后台执行，模型可继续思考。脚本内直接tool('nav_to',args)、tool('gather',args)等顺序执行；"
            + "脚本本身已持有排队句柄，不能嵌套action_start。action_control可以暂停、取消该脚本；续接使用同一workflow，uncertain先核对。yield/wake会交回观察，不视为节点已完成。";
        foreach (var (tool, field) in new[] { ("task_plan", "steps"), ("task_plan_patch", "add_nodes") })
        {
            var definition = tools.First(t => t["function"]?["name"]?.ToString() == tool)["function"];
            var conditions = definition["parameters"]["properties"][field]["items"]["properties"]["conditions"]["items"]["properties"];
            conditions["kind"]["enum"].AsArray().Add("biome");
            conditions["name"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("jungle", "desert", "snow", "ocean", "dungeon", "corruption", "crimson", "hallow") };
            conditions["depth"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("any", "sky", "surface", "below_surface", "underground", "cavern", "underworld"), ["default"] = "any" };
            definition["description"] = definition["description"].ToString()
                + "biome条件按原生人物群系与深度验收，count=1，name为群系名，depth=below_surface涵盖地下层和洞穴层。结束时身处某群系的要求使用biome条件，可在群系内任意位置验证。";
            definition["parameters"]["properties"][field]["items"]["properties"]["conditions"]["items"]["properties"]["type"] = new JsonObject
            {
                ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                    new JsonObject { ["type"] = "string", ["minLength"] = 1 }),
                ["description"] = "真实编号或完整原版英文内部名称。宿主在建计划时解析名称并保存编号，可直接使用已知名称，无需先单独调用game_catalog；名称片段或歧义会拒绝。"
            };
        }
        var fight = tools.First(t => t["function"]?["name"]?.ToString() == "fight")["function"];
        fight["description"] = "仅控制攻击，不改变移动；可与nav_to并行，不需暂停原导航。挖掘、放置使用物品时攻击让位；结束攻击不停止导航。" + fight["description"];
        if (fight["parameters"]?["properties"]?["movement"] is JsonObject movement)
            movement["description"] = "旧参数已不控制移动；移动由nav_to或独立移动指令负责。";
        var programs = tools.First(t => t["function"]?["name"]?.ToString() == "script_program")["function"];
        programs["parameters"]["properties"]["operation"]["enum"].AsArray().Add("example");
        programs["description"] = programs["description"].ToString()
            + "list包含内置技能模板(template:true,revision:0)和已保存技能。get读取模板源码，填写config后save(expected_revision:0)，再以返回版本执行；模板不是实机验证通过的技能。navigate_observe保持导航与观察并行；collect_detour采集后恢复原路线；use_life_crystals验证永久生命增长。"
            + "example返回可编辑JS示例，name为空列出名称；sample_tile_variants演示按现场变体试采、保存所得、同句柄恢复及事件交回。示例不执行；先读源码，按任务修改config与策略，再save和run_script。";
        var events = tools.First(t => t["function"]?["name"]?.ToString() == "script_events")["function"];
        events["parameters"]["properties"]["max_events"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1,
            ["maximum"] = 16, ["description"] = "每次读取数量，默认4。remaining_events表示队列剩余数；可继续读取，未读事件不会丢弃。" };
        var watch = tools.First(t => t["function"]?["name"]?.ToString() == "watch")["function"];
        watch["parameters"]["properties"]["loot_chests_in_reach"] = new JsonObject { ["type"] = "boolean",
            ["description"] = "提前授权在交互范围内立即开箱并loot，保持原移动；默认false。必须订阅nearby_chest。复用原陷阱/锁/占用检查，核对实际入包；不绕路、不整理或丢弃背包。false关闭；重新开启可重试本轮已处理箱子。" };
        watch["description"] = watch["description"].ToString()
            + "另支持nearby_drop(附近可见掉落，坐标单位格，需用nearby_drops刷新)和nearby_npc_killed(仅单人原生死亡事件，不证明谁击杀、不保证有掉落)。script本地处理的事件不消耗模型唤醒额度，显式decision_point仍受额度限制。额度耗尽后的机会仍记录在pending/history，wake_deferred=true表示未主动中断；可用script_events读取，行动前重新核对现场。不代表已拾取。";
        var parameters = tools.First(t => t["function"]?["name"]?.ToString() == "run_script")["function"]["parameters"];
        parameters["properties"]["tool"] = new JsonObject { ["type"] = "string", ["minLength"] = 1,
            ["description"] = "单次游戏API调用的名称；配合args使用，不必写JS。与code/program三选一，经过相同权限和参数校验。" };
        parameters["properties"]["args"] = new JsonObject { ["type"] = "object", ["description"] = "tool对应的参数，先用game_api读取定义。" };
        parameters["properties"]["program"] = new JsonObject { ["type"] = "string", ["description"] = "已保存程序名称；与code/tool三选一。" };
        parameters["properties"]["expected_revision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1,
            ["description"] = "使用program时必填，必须匹配刚读取的源码版本。" };
        parameters["properties"]["event_delivery"] = new JsonObject { ["type"] = "string",
            ["enum"] = new JsonArray("agent", "script"),
            ["description"] = "默认agent遇事件交回模型；script由代码调用script_events消费事件，长动作被事件暂停时返回handle/events供代码处理。显式decision_point仍交回模型。" };
        parameters["required"] = new JsonArray();
    }

    public static JsonArray EntryTools(JsonArray tools) => new(tools.Where(t =>
        t["function"]?["name"]?.ToString() is "run_script" or "get_state" or "say" or "ask" or "game_api" or "terrain_map")
        .Select(t => t.DeepClone()).ToArray());

    public static string Instructions(string system, JsonArray tools) =>
        "单次游戏API用run_script({tool:名称,args:参数})；组合行为用run_script提交JavaScript，代码里用tool(name,args)。"
        + "顶层提供run_script、get_state、say、ask、game_api、terrain_map。terrain_map直接调用会收到局部地图图片与世界坐标说明，可在导航持续运行时观察布局并准备施工或路线约束。其余游戏API通过run_script中的tool调用。"
        + "逐次行为、条件和循环应写在代码里，读取真实返回后推进；不能只登记计划或口头承诺。\n"
        + "玩家要求沿途择机行动时，把它作为执行条件：出发前配置watch，按已查证的类型观察；用稳定workflow和step保存原action_start句柄。用script_events读取机会，opportunity_assess提供现场依据，由你选择拿或跳过并保存理由；插入前暂停原句柄，核实实际所得后恢复同一句柄。不能用先抵达终点再回头搜集代替沿途处理。"
        + "按当前可执行的一步查询接口和知识，不必先查清所有后续材料来源才行动；已读取的定义可复用，已有返回值直接用于下一步。已确定的查询、判断、动作和验证尽量组合在同一段脚本，遇未知情况再带证据交回审阅。\n"
        + system
        + RuntimeInstructions
        + "\n长任务采用DAG：task_plan传mode:'dag'，节点包含id、depends_on、script和conditions。共享前置只建一个节点；依赖表示前置已验收，不代表输入互斥。先查当前必要知识，再建立可执行节点；未知后续行为可用调查脚本返回缺失事实，不能猜来源或假造完成条件。"
        + "首个行动的目的地、必要条件和验收已明确时，先建立最小DAG，再从所属节点启动后台动作并立即返回；不要等全部配方、后续资源和完整路线查完才出发，也不要先启动无所属节点的动作再补建DAG。导航运行期间可查询知识、观察现场并用task_plan_patch添加后续节点；保留当前节点和原句柄，不为只读调查暂停导航。最终目标的验收条件从初始计划保留，后续细化不能遗漏原要求。导航的逐格移动、挖掘和搭桥交给导航执行器，不用模型反复生成逐格修路脚本替代导航。"
        + "长任务优先用run_script({task_node:id,background:true})执行ready_steps中的节点，宿主返回后台句柄并保存稳定workflow。此模式下节点script直接用tool('nav_to',参数)、tool('gather',参数)等顺序执行，step保存已完成效果；Agent同时观察和准备后继。action_control管理宿主返回的句柄。普通前台脚本可用action_start启动独立后台动作并保存返回句柄，再用action_query观察。task_step verify读取真实状态验收节点。"
        + "多个ready节点可共同推进，但不轮流抢移动：默认继续当前动作，出现具体顺路收益、阻塞或危险才切换。切换前暂停原主动作并保存理由；插入结束核实所得，回到原节点恢复句柄。独立防身可与移动并行，不把所有怪物变成必须完成的前置任务。"
        + "新事实需要修改方案时用task_plan_patch的nodes更新未完成节点script/depends_on，或add_nodes插入有实际完成条件的新机会/恢复节点，两者可同批提交，带expected_revision和reason；共享已有前置，保留workflow与已完成step，不重建计划。不要为开箱把采集节点脚本改成只有开箱；新机会单独成节点，原节点保留原意图与句柄。脚本改动不会清除uncertain，仍须核对。最终验收检查所有末端节点，必须把最终仍需持有的物品等要求写入末端conditions。\n"
        + "\n以下只是游戏API用途索引，不是参数定义。写代码前用game_api({names:[本次需要的工具名]})读取完整参数和限制；同一会话已查过的定义可复用，不要猜测工具名、参数或坐标单位。查询可批量进行，不需要先写脚本。\n"
        + string.Join("\n", tools.Where(t => t["function"]?["name"]?.ToString() is not ("run_script" or "ask"))
            .Select(t => t["function"]["name"] + ": " + t["function"]["description"]?.ToString().Split('。')[0]));

    public static JsonObject DescribeApi(JsonArray tools, JsonObject args)
    {
        var names = args["names"]?.AsArray().Select(n => n.GetValue<string>()).Distinct().ToArray();
        if (names == null || names.Length == 0) throw new ArgumentException("names需要至少一个工具名。");
        var functions = tools.Select(t => t["function"]).ToArray();
        var missing = names.Where(n => !functions.Any(f => f["name"].ToString() == n)).ToArray();
        if (missing.Length > 0) return new JsonObject { ["error"] = "unknown_game_api",
            ["unknown"] = new JsonArray(missing.Select(n => (JsonNode)JsonValue.Create(n)).ToArray()),
            ["available"] = new JsonArray(functions.Select(f => f["name"].DeepClone()).ToArray()) };
        return new JsonObject { ["tools"] = new JsonArray(names.Select(n => functions.First(f => f["name"].ToString() == n).DeepClone()).ToArray()),
            ["detail"] = "仅接口定义，不会向外层tools注册这些函数。单次调用run_script({tool:名称,args:参数})；组合行为用run_script({code:源码})，源码内tool(name,args)。" };
    }

    public const string RuntimeInstructions =
        "\n源码是同步JavaScript函数体，tool()直接返回结果；不使用顶层await，不返回Promise。"
        + "find_biome负责辨认群系方向和大致位置，实际路线结合特殊知识、当前位置和现场环境选择。进入地下丛林优先利用find_descent/descent_route查证的主道，这是原有算法计算的高效下降道路。沿主道利用现有洞穴前进；遇红木、神庙、蜂巢等阻挡时，结合现场选择挖掘、绕行或接回主道，以行动效率为依据。"
        + "需要复用或修改的行为先用script_program保存源码，再用run_script的program、expected_revision和稳定workflow运行。"
        + "将script_program作为可持续积累的技能库：任务开始先list并get相关技能，优先复用适用的已保存程序。把重复的行进、短支线采集和恢复行为抽成参数化JS；源码写明适用条件、配置、完成证据和失败返回。实际跑通后保存验证条件与结果，失败则修订同一技能，不把未经执行的程序宣称为验证通过。不保存旧世界坐标或句柄作为下次任务的默认参数。"
        + "长导航写入节点script，以run_script({task_node:id,background:true})启动，立即使用宿主返回的句柄进行并行观察和后续准备。后台脚本顺序调用动作，连续主路线串联已查证路点；普通机会通过passive_events观察，由策略决定插入行动，短程躲避由独立实时控制器响应。"
        + "永久生命上限低于160时，生命水晶具有更高优先级；获得后及时使用并核对永久上限增加，除非玩家明确要求保留。优先级不代表无视地形或为遥远水晶无限绕路。"
        + "workflow.watch_policy保留该工作流最后成功设置的watch参数，是历史策略而非当前生效状态。续接先对照get_state.watch，按当前目标选择恢复或修改观察范围；需要恢复时用tool('watch', policy)，不会自动覆盖你新设的策略。"
        + "需要事件驱动的快速反应时使用event_delivery:script，程序用script_events消费订阅事件、用jev_judge根据当前事实判断；模型不必为每次事件重新生成整段代码。"
        + "有沿途收集要求时，先用recipe确认所需物品和数量、game_catalog确认编号，再查证来源。game_catalog的matches[].type是对应kind的编号；物品编号、NPC编号和方块编号不能混用，名称相似也不证明掉落关系。NPC材料先用npc_drop_sources查询注册掉落及条件；植物来源与变体查wiki或依据实际采集证据，不能把材料名当TileID、把生长基底当材料来源，也不能猜npc_types或frame。未知来源应报告未查证，不得假装已经配置正确。"
        + "按查证的来源配置watch：方块和工作站使用nearby_tile+tile_types，箱子使用nearby_chest，已掉落材料使用nearby_drop；发现候选不代表必定取得目标物品。"
        + "普通机会可加入watch.passive_events，只通知不自动打断；配合action_start和script_events边执行边评估，决定值得插入时再action_control pause，避免每发现一株植物就重启导航。"
        + "target_acquired是目标物品库存增加，不代表数量达标；持续采集时可同时订阅并加入passive_events，让增量通知不自动暂停采集。是否插入行动由JS按进度、现场和机会决定，完成仍须核对实际数量，危险事件独立处理。"
        + "script_events若本次暂停了动作，action返回暂停清理后的handle/status/observation，事件evidence仍是触发时的历史快照。等待或操作后用action.observation或新查询核对血量与位置，不要复用等待前的值；action为null仅表示本次未暂停，当前动作状态用action_query核对，running表示仍在执行。"
        + "机会交付时可附assessment，使用它的tick、障碍和route_relation作判断；navigation保存发现时原导航句柄和目的地。每批最多评估6个，其余assessment_deferred=true仍是未评估，不代表安全。缺少评估或人物、地形已改变时再opportunity_assess(x,y,destination原目的地)，结合当前库存、任务需求和已测障碍取舍；ok只表示查询完成，即使有岩浆或陷阱也可为true，不是安全、可达或值得拿的结论。直线距离不是可达证明。"
        + "危险取舍要检查每项kind、坐标和on_direct_corridor，不能用hazards.length等于某个观测数量作为安全条件；列表可能截断，未列出不代表不存在。走廊外的危险也可能影响接近或返程，必要时进一步观察。"
        + "把每次选择及原因保存在稳定step结果中；跳过不能写成已收集。多格物体可能发多个tile_cell事件，按现场物体合并处理。"
        + "step单个结果上限8000字符；只存选择理由、目标坐标、handle、outcome和实际所得等必要证据，不复制工具返回中整份task_plan、历史和背包。需要完整动作证据时用action_query读取原handle，不能用截断的记录冒充完成证明。"
        + "print用于诊断，每条最多1000字符；log_truncations列出被截断的日志及原长度。截断后未出现的字段不代表不存在，不能据此下结论。把决策所需字段从工具结果中提取后return，避免print整份状态或多份查询；JSON字符串直接print即可，不会再次转义。"
        + "已选定局部采集目标时给gather传source_area包住该物体和预计掉落位置，防止默认最近来源选择重新选回已跳过的目标；来源区域不是导航路径限制。"
        + "插入动作前保存并暂停原action句柄，插入后核对实际库存增量或箱子返回got；失败保留证据，不记成功。继续时resume同一原句柄，而不是新建重复目标。"
        + "找回原句柄：get_state.action_tasks是数组，每项含handle/tool/args/status；按原任务参数匹配，不能随便选第一个gather。script_progress({workflow,action:'get'})直接返回steps数组，step保存的action_start结果位于steps[i].value.handle，不存在顶层gather_handle或original_gather_handle。step.value里的status是当时的快照，当前状态用action_query({handle})核对。先读取真实结构，不得把猜错字段当作句柄丢失。"
        + "fight仅占攻击通道，不控制移动。导航途中防身可action_start fight（短seconds、有限max_dist），无需暂停nav_to；结束或取消fight不停止原导航，挖掘、放置时攻击自动让出物品使用权。不要为普通防身停下赶路或反复追求清光刷新的敌怪；需要绕行、撤离或追击时独立选择移动目标。暂停导航不取消并行攻击，停止攻击用其自己的handle。"
        + "Agent可以修改resume的args，或cancel已经暂停的原意图；不值得的机会可直接跳过并继续，不能无条件见宝必拿。"
        + "开箱、获得装备或inventory_full后可用inventory_review批量检查背包、功能装备和trash垃圾桶，结合item_info、当前任务配方及玩家保留要求选择换装、整理或丢弃，不按售价或防御单项固定评分。用equip_item穿戴、inventory_swap整理、inventory_drop丢整叠到地上、inventory_trash移整叠到垃圾桶；先暂停原动作，使用新鲜槽位证据，核对实际结果再恢复原handle。地面物品可能被捡回；垃圾桶会覆盖旧垃圾，必须同时提供expected_trash。收藏物品不可丢弃。"
        + "长动作返回outcome:interrupted不是完成；保存返回的handle和events，核对现场后暂停/恢复同一动作。不要把中断结果直接作为成功step。"
        + "动作返回的events已从事件队列取走，下一次script_events不会重放；必须先处理该返回值中的危险事件，再决定是否resume。空事件队列不代表现场安全。"
        + "low_health仅通知并唤醒观察，当前动作继续执行，包括本地JS事件模式和模型思考期间。不得仅因低血量暂停、取消动作、原地等回血或空yield；边执行边调整后续方案。只有任务对死亡有明确特殊要求时，才按该要求另行处理。"
        + "恢复失败任务时读取最新现场；治疗核对真实库存及使用后效果，窒息须脱困，保留原句柄和已完成step。恢复策略应有实际执行分支，不能把低血量设置成先停下等待达到回血阈值的前置条件。"
        + "get_state.healing_items直接列出背包前50格中healLife>0的非空物品及slot、stack、base_heal_life、consumable，找直接治疗物品不必逐格item_info。基础治疗值不是当前可用或实际回血承诺；还需看potion_delay_ticks并验证use_item结果，不包含仅提供再生buff的物品。"
        + "last_automatic_heal记录默认生存反射最近一次实际回血或治疗冷却变化的tick和前后值；默认代码可能已用掉药水，不能照旧槽位重复用药。它是历史证据，继续查看当前healing_items、hp和potion_delay_ticks；可通过control禁止survival，但需自行承担恢复控制。"
        + "get_state.health_dynamics是同一tick的原生血量、死亡状态、life_regen_raw和life_regen_count_raw；regen_direction只说明当前引擎持续回血/掉血方向，不证明安全或预测未来血量。结合buffs剩余时间和last_damage.tick区分持续掉血与新受击，撤离后仍掉血不必然需要再次换位；判断能否等待由你的策略决定，继续观察新威胁并保留原任务。raw字段是引擎原始单位，不是每秒伤害。"
        + "get_state.depth提供游戏原生高度层标志sky/surface/underground/cavern/underworld和世界分层格坐标。结合player.biome判断所处环境，不能仅凭方块类型或猜测坐标断言不在地下；深度符合也不证明每个植物变体都能掉落所需材料。"
        + "gather返回source_unavailable时，用相同targets/source_area调用只读gather_observe确认当前库存和来源，不要反复启动gather探测。出现值得获取的新来源后可恢复原handle；否则决定换区域、执行其他步骤或交回观察。候选不是可达或安全证明，仍需opportunity_assess。"
        + "gather返回source_needs_review时，source_error.observations记录连续移除方块却没有目标库存增长的证据。先核查材料来源、变体和拾取条件，修正原handle的targets/source_area后再恢复；不要把挖掉方块当作收集成功，也不要原样重跑整批采集。"
        + "gather返回combat_obstructed表示连续射界受阻且目标未掉血；读取last_result.result里的目标格坐标、武器和血量证据，先观察地形、换位或改武器，不要原样重试。clear_line只是当前直线碰撞检查，不预测弹道或证明可达。"
        + "gather返回combat_out_of_range表示敌怪超出40格战斗范围，执行层没有向敌怪当前位置导航。last_result.result提供实时距离、双方格坐标、血量和射界；根据地形选择人物可站的位置，必要时用npc_combat_observe测量，再导航并resume原handle。飞行敌怪坐标不是人物站位，射界通畅也不等于在攻击范围内。"
        + "普通敌怪受阻可用npc_combat_observe，npc/npc_type取原candidate的who/npc_type。站位候选不是路径或安全承诺；先检查危险，再尝试导航，抵达后刷新NPC射界和身份，最后显式resume失败的原采集句柄。"
        + "默认站位探针没有通畅射界不代表所有位置都不可用。可依据已观察的地形选择其他支撑格，通过npc_combat_observe.stance_cells测量；核对requested_stances的supported/body_fits/shot_line及支撑形状，勿把空中射线通畅当作可以站立。"
        + "观察发现危险、判断失败或局部调整仍无进展时调用decision_point交回审阅。审阅时读取原程序及workflow进度，修改源码版本后续接；保留已完成step和原句柄，uncertain先核对，不从头重放。"
        + "decision_point的evidence可携带刚测到的关键字段、捕获tick、原handle、已尝试动作及候选方案；后续模型会收到reported_evidence，不要只写‘观察完请再观察’并丢弃变量中的结果。这是脚本报告而非宿主验证，不可代替task_step或新的现场检查。"
        + "decision_point默认暂停当前动作；确需让有时限的防身等动作在模型审阅时继续，可传continue_action为当前active句柄。模型会继续审阅而非等待该动作结束；下一轮须观察并完成输入交接，不能直接启动冲突动作。这不绕过权限、死亡或后续危险事件。"
        + "事件本地消费不占模型唤醒次数，显式decision_point仍受限；源码可跨启动保存，workflow进度只保留本世界。"
        + "run_script.tool_counts记录本段全部工具请求，action_control按pause/resume/cancel分开；trace仅保留最近12次。get_state.script_workflows各workflow的last_execution保留上一段的tool_counts、tool_calls、elapsed_ms和failure，跨轮续接时先核对；script_progress的last_segment也保留这些证据。请求不代表成功。若只查询或暂停而未推进目标，必须补上依据观察结果执行的分支或说明真实阻塞，不能把脚本正常结束当作已经采取恢复行动。";

    public static async Task<JsonObject> Run(string source, int seconds,
        Action<string, JsonObject> validate,
        Func<string, JsonObject, CancellationToken, Task<string>> execute,
        Func<CancellationToken, Task> stop, CancellationToken token, ScriptProgress progress = null,
        Func<CancellationToken, Task<JsonObject>> observeEvent = null)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 16000)
            throw new ArgumentException("code必须是1..16000字符的JavaScript函数体，使用return返回结果。");
        progress?.Begin();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        var clock = Stopwatch.StartNew();
        var trace = new Queue<JsonNode>();
        var logs = new JsonArray();
        var logTruncations = new JsonArray();
        var toolCounts = new JsonObject();
        int calls = 0;
        bool inTool = false;
        Exception hostFailure = null;
        JsonObject wake = null;
        JsonObject lastFailure = null;
        string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
        JsonObject Report(string error, string detail, JsonNode value = null, int? omittedOutputChars = null)
        {
            var result = new JsonObject
            {
                ["ok"] = error == null, ["error"] = error, ["detail"] = detail,
                ["value"] = value, ["tool_calls"] = calls, ["elapsed_ms"] = clock.ElapsedMilliseconds,
                ["tool_counts"] = toolCounts.DeepClone(),
                ["trace"] = new JsonArray(trace.ToArray()), ["logs"] = logs, ["log_truncations"] = logTruncations,
                ["failure"] = lastFailure?.DeepClone()
            };
            if (omittedOutputChars.HasValue) result["output_limit"] = new JsonObject
                { ["characters"] = omittedOutputChars.Value, ["limit"] = 16000, ["body_completed"] = true, ["value_omitted"] = true };
            progress?.End(result);
            if (progress != null) result["workflow"] = progress.Summary();
            return result;
        }
        try
        {
            return await Task.Run(() =>
            {
                using var engine = new Engine(options =>
                {
                    options.Strict().LimitMemory(32_000_000).MaxStatements(50000)
                        .CancellationToken(budget.Token);
                    options.Host.StringCompilationAllowed = false;
                    options.Constraints.MaxRecursionDepth = 64;
                    options.Constraints.MaxArraySize = 100000;
                    options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(100);
                });
                engine.SetValue("__gameTool", new Func<string, string, string>((name, json) =>
                {
                    budget.Token.ThrowIfCancellationRequested();
                    if (observeEvent != null)
                    {
                        wake ??= observeEvent(budget.Token).GetAwaiter().GetResult();
                        if (wake != null) throw hostFailure = new InvalidOperationException("host_event_pending");
                    }
                    if (name is "run_script" or "ask") throw new InvalidOperationException("脚本内禁止嵌套run_script或ask；需要决策时return交回模型。");
                    if (json == null || json.Length > 32000) throw new ArgumentException("工具参数超过32000字符。");
                    var args = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("工具参数必须是JSON对象。");
                    try { validate(name, args); }
                    catch (ArgumentException error)
                    {
                        var validationFailure = new JsonObject { ["tool"] = name, ["args"] = args.DeepClone(),
                            ["evidence"] = new JsonObject { ["error"] = "invalid_tool_arguments", ["detail"] = error.Message,
                                ["executed"] = false, ["tool_schema"] = (error.Data["tool_schema"] as JsonNode)?.DeepClone() } };
                        if (lastFailure == null) lastFailure = validationFailure;
                        else lastFailure["validation_failure"] = validationFailure;
                        throw;
                    }
                    if (calls >= 100) throw hostFailure = new InvalidOperationException("脚本达到100次工具调用上限。");
                    calls++;
                    string activity = name == "action_control" ? name + "." + args["command"]
                        : name == "script_program" ? name + "." + args["operation"] : name;
                    toolCounts[activity] = (toolCounts[activity]?.GetValue<int>() ?? 0) + 1;
                    var entry = new JsonObject { ["step"] = calls, ["tool"] = name, ["args"] = Clip(json, 400) };
                    trace.Enqueue(entry);
                    if (trace.Count > 12) trace.Dequeue();
                    long started = clock.ElapsedMilliseconds;
                    inTool = true;
                    string result = execute(name, args, budget.Token).GetAwaiter().GetResult();
                    progress?.Record(name, args, result);
                    inTool = false;
                    JsonObject toolResult = null;
                    try { toolResult = JsonNode.Parse(result) as JsonObject; }
                    catch (System.Text.Json.JsonException) { }
                    if (toolResult != null)
                    {
                        if (toolResult["error"] != null || toolResult["ok"]?.ToString() == "false")
                        {
                            var facts = new JsonObject();
                            foreach (string field in new[] { "error", "detail", "outcome", "active_handle", "name_candidates", "requested_name", "targets", "source_rules", "source_error" })
                                if (toolResult[field] != null) facts[field] = toolResult[field].DeepClone();
                            lastFailure = new JsonObject { ["tool"] = name, ["args"] = args.DeepClone(), ["evidence"] = facts };
                        }
                        else if (lastFailure?["tool"]?.ToString() == name
                            && (name != "action_control" || JsonNode.DeepEquals(lastFailure["args"], args))) lastFailure = null;
                    }
                    if (name == "decision_point" || toolResult?["host_yield"]?.GetValue<bool>() == true)
                    {
                        if (name == "decision_point" && toolResult?["error"] != null)
                            throw hostFailure = new InvalidOperationException(toolResult.ToJsonString());
                        wake = toolResult;
                        throw hostFailure = new InvalidOperationException("decision_requested");
                    }
                    budget.Token.ThrowIfCancellationRequested();
                    entry["elapsed_ms"] = clock.ElapsedMilliseconds - started;
                    entry["result"] = Clip(result, 800);
                    if (result.Length > 256000) throw new InvalidOperationException("工具结果超过256000字符，请缩小查询范围。");
                    try { JsonNode.Parse(result); return result; }
                    catch (System.Text.Json.JsonException) { return JsonValue.Create(result).ToJsonString(); }
                }));
                engine.SetValue("__gamePrint", new Action<string>(text =>
                {
                    budget.Token.ThrowIfCancellationRequested();
                    if (logs.Count >= 20) throw new InvalidOperationException("脚本最多print 20次。");
                    if (text?.Length > 1000) logTruncations.Add(new JsonObject
                        { ["index"] = logs.Count, ["original_chars"] = text.Length, ["retained_chars"] = 1000 });
                    logs.Add(Clip(text ?? "null", 1000));
                }));
                engine.SetValue("__stepEnter", new Func<string, string>(id =>
                    (progress ?? throw new InvalidOperationException("step需要run_script.workflow。 ")).Enter(id).ToJsonString()));
                engine.SetValue("__stepComplete", new Action<string, string>((id, json) =>
                    progress.Complete(id, JsonNode.Parse(json)?["value"])));
                string wrapper = """
                    (function(call, log, enter, complete) {
                        delete globalThis.__gameTool;
                        delete globalThis.__gamePrint;
                        delete globalThis.__stepEnter;
                        delete globalThis.__stepComplete;
                        const stringify = JSON.stringify;
                        const parse = JSON.parse;
                        const tool = (name, args = {}) => {
                            if (name === 'step') throw new Error('step是JS内置函数，不是游戏API。请直接写step(id, () => valueOrAction)，不要写tool("step", ...)。修正调用并沿用原workflow，已完成步骤不会重做。');
                            const result = parse(call(name, stringify(args)));
                            if (name !== 'get_state' || result === null || typeof result !== 'object') return result;
                            return new Proxy(result, {
                                get(target, key, receiver) {
                                    if (typeof key === 'string' && key !== 'then' && key !== 'toJSON' && !Reflect.has(target, key))
                                        throw new Error('get_state.' + key + ' 不存在；请使用实际结构，根字段：' + Object.keys(target).join(','));
                                    return Reflect.get(target, key, receiver);
                                }
                            });
                        };
                        const print = value => log(typeof value === 'string' ? value : stringify(value));
                        const step = (id, action) => {
                            const saved = parse(enter(id));
                            if (!saved.execute) return saved.value;
                            const value = action();
                            if (value && typeof value.then === 'function') throw new Error('step不支持Promise');
                            const json = stringify({value});
                            if (json.length > 8000) throw new Error('step返回值超过8000字符');
                            complete(id, json);
                            return value;
                        };
                        const value = (function() {
                    """ + source + """

                        })();
                        if (value && typeof value.then === 'function') throw new Error('使用同步tool调用，不支持Promise返回');
                        const result = stringify({value});
                        return result;
                    })(__gameTool, __gamePrint, __stepEnter, __stepComplete)
                    """;
                var output = engine.Evaluate(wrapper).AsString();
                if (hostFailure != null) throw hostFailure;
                budget.Token.ThrowIfCancellationRequested();
                if (output.Length > 16000)
                    return Report(lastFailure == null ? "script_result_too_large" : "tool_failed",
                        "脚本主体已执行，但返回值超过16000字符，已省略；这不是任务完成或动作回滚。先用action_query/get_state/script_progress核对实际效果与原句柄，只返回所需字段，不要重跑已执行动作。",
                        omittedOutputChars: output.Length);
                var value = JsonNode.Parse(output)?["value"]?.DeepClone();
                return Report(lastFailure != null && value == null ? "tool_failed" : null,
                    lastFailure != null && value == null ? "工具失败未处理；返回完整失败证据以修复。" : "脚本执行结束；任务是否完成仍由task_step verify检查。", value);
            }, token);
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            progress?.End(new JsonObject { ["error"] = "cancelled", ["detail"] = "执行被取消，未完成步骤需要核对实际状态。" });
            throw new OperationCanceledException(token);
        }
        catch (Exception error)
        {
            if (wake != null)
                return Report(lastFailure == null ? null : "tool_failed",
                    lastFailure == null ? "宿主事件或主动决策点交回模型；未完成step需核对。"
                        : "交回模型时仍有未解决的工具失败；读取failure，不得把请求或脚本文字当作执行成功。", new JsonObject
                { ["status"] = "yield", ["wake"] = wake.DeepClone() });
            string cleanup = null;
            if (inTool)
            {
                try { await stop(token); }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    progress?.End(new JsonObject { ["error"] = "cancelled", ["detail"] = "清理期间被取消，步骤需要核对。" });
                    throw new OperationCanceledException(token);
                }
                catch (Exception failure) { cleanup = failure.Message; }
            }
            var result = Report(budget.IsCancellationRequested || error is TimeoutException ? "script_timeout" : "script_failed",
                Clip(error.GetType().Name + ": " + error.Message, 2000));
            if (cleanup != null) result["cleanup_error"] = cleanup;
            return result;
        }
    }
}
