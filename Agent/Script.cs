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
    internal static readonly AsyncLocal<Reactions> CurrentReactions = new();
    internal static readonly AsyncLocal<CancellationToken> ReactionCancellation = new();

    internal sealed class Reactions
    {
        sealed record Request(JsonObject Event, TaskCompletionSource<JsonObject> Completion, ExecutionContext Context, CancellationToken Token);
        readonly object gate = new();
        readonly Dictionary<string, Func<JsonObject, JsonObject>> handlers = new();
        readonly Queue<Request> requests = new();
        TaskCompletionSource<bool> signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool servicing;
        Action retired;
        public bool Active { get; private set; } = true;
        public bool Handling { get; set; }
        public Func<string, JsonObject, CancellationToken, Task> BeforeAction;
        public bool TryEnter()
        {
            lock (gate)
            {
                if (!Active || servicing) return false;
                servicing = true; return true;
            }
        }
        public void Exit()
        {
            Action finish;
            lock (gate) { servicing = false; finish = retired; retired = null; }
            finish?.Invoke();
        }
        public void Retire(Action finish)
        {
            bool wait;
            lock (gate) { Active = false; wait = servicing; if (wait) retired = finish; }
            if (!wait) finish();
        }
        long lastTick, lastDecisionVersion;
        public bool TickDue(long decisionVersion = 0)
        {
            if (decisionVersion == lastDecisionVersion && Stopwatch.GetElapsedTime(lastTick).TotalMilliseconds < 250) return false;
            lastDecisionVersion = decisionVersion;
            lastTick = Stopwatch.GetTimestamp();
            return true;
        }
        public string[] Kinds { get { lock (gate) return handlers.Keys.ToArray(); } }
        public bool Has(string kind) { lock (gate) return handlers.ContainsKey(kind); }
        public void Remove(string kind) { lock (gate) handlers.Remove(kind); }
        public void Register(string kind, Func<JsonObject, JsonObject> handler)
        { lock (gate) handlers[kind] = handler; }

        public async Task<JsonObject> Dispatch(JsonObject notice, CancellationToken token)
        {
            var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            token.ThrowIfCancellationRequested();
            lock (gate) { requests.Enqueue(new Request(notice, completion, ExecutionContext.Capture(), token)); signal.TrySetResult(true); }
            var result = await completion.Task;
            token.ThrowIfCancellationRequested();
            return result;
        }

        public string Await(Task<string> task)
        {
            while (true)
            {
                Request request;
                Task ready;
                lock (gate)
                {
                    request = requests.TryDequeue(out var pending) ? pending : null;
                    if (requests.Count == 0) signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    ready = signal.Task;
                }
                if (request != null)
                {
                    if (request.Completion.Task.IsCompleted) continue;
                    if (request.Token.IsCancellationRequested) { request.Completion.TrySetCanceled(request.Token); continue; }
                    Handling = true;
                    try
                    {
                        ExecutionContext.Run(request.Context, _ =>
                        {
                            CurrentReactions.Value = this;
                            ReactionCancellation.Value = request.Token;
                            request.Completion.TrySetResult(handlers[request.Event["kind"].ToString()](request.Event));
                        }, null);
                    }
                    catch (Exception error) { request.Completion.TrySetException(error); }
                    finally { Handling = false; }
                    continue;
                }
                if (task.IsCompleted) return task.GetAwaiter().GetResult();
                Task.WhenAny(task, ready).GetAwaiter().GetResult();
            }
        }
    }

    internal static bool PreparationOnly(string source)
    {
        try
        {
            var ast = new Acornima.Parser().ParseScript("(function(){\n" + source + "\n});");
            int references = 0, queries = 0;
            bool dynamicOrAction = false;
            void Visit(Acornima.Ast.Node node)
            {
                if (node is Acornima.Ast.Identifier id)
                {
                    if (id.Name == "tool") references++;
                    if (id.Name == "skill") dynamicOrAction = true;
                }
                if (node is Acornima.Ast.CallExpression call && call.Callee is Acornima.Ast.Identifier { Name: "tool" })
                {
                    if (call.Arguments.Count > 0 && call.Arguments[0] is Acornima.Ast.StringLiteral name
                        && name.Value is "get_state" or "game_api" or "game_catalog" or "find_tiles" or "inspect_terrain"
                            or "terrain_map" or "opportunity_assess" or "nearby_drops" or "combat_assess" or "item_info" or "wiki_search" or "wiki_page"
                            or "recipe" or "script_program" or "jev_judge" or "find_biome" or "tile_names" or "find_descent" or "descent_route"
                            or "equipment_list" or "inventory_review" or "npc_find" or "npc_drop_sources" or "script_progress" or "script_report" or "house_site") queries++;
                    else dynamicOrAction = true;
                }
                foreach (var child in node.ChildNodes) Visit(child);
            }
            Visit(ast);
            return !dynamicOrAction && references == queries;
        }
        catch (Acornima.ParseErrorException) { return false; }
    }

    public static void DescribePrograms(JsonArray tools)
    {
        var useItem = tools.First(t => t["function"]?["name"]?.ToString() == "use_item")["function"];
        useItem["description"] = useItem["description"].ToString()
            + "途中反应消费当前gather目标时返回deferred_consumption，先让采集验收库存，采集结束后再使用；其它物品照常可用。";
        tools.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
        {
            ["name"] = "nav_intent",
            ["description"] = "更新当前导航的短时战术意图，包括gather、mine、collect_treasure内部的导航，原动作持续运行。withdraw增加与target敌怪的间距；cover偏向地形遮挡。意图绑定当前动作，持续2秒，可更新；敌怪消失、到达10格间距(withdraw)或动作结束后恢复原评分。continue清除意图。现有候选和物理校验负责执行。",
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            {
                ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("continue", "withdraw", "cover") },
                ["target"] = new JsonObject { ["type"] = "integer" },
                ["destination"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                    ["x"] = new JsonObject { ["type"] = "integer" }, ["y"] = new JsonObject { ["type"] = "integer" } }, ["required"] = new JsonArray("x", "y") }
            }, ["required"] = new JsonArray("mode") }
        } });
        tools.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
        {
            ["name"] = "task_policy",
            ["description"] = "整项任务共用的JS反应策略。先get读取版本，set提交初始化源码：用on注册处理函数、watch配置观察，可调用已保存技能；动作放在处理函数中。所有DAG节点及后台动作自动使用此策略，局部on优先覆盖同名事件。策略闭包状态跨节点保留。更新先初始化新策略，成功后切换，当前动作和已进入的旧处理继续；失败保持旧策略。clear关闭未来反应，已发生的游戏效果保留。新指令任务及离开世界清除策略。",
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
            {
                ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("get", "set", "clear") },
                ["code"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 15000 },
                ["expected_revision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 }
            }, ["required"] = new JsonArray("operation") }
        } });
        tools.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject
        {
            ["name"] = "combat_assess",
            ["description"] = "只读观察附近普通敌怪、弹幕和快捷栏武器检查。forecast:true配合context和destination后台推演候选。所有移动在同一comparison_frames内比较伤害、直线目标距离变化、匀速威胁相交和射界；follow_route只预测已知控制段，之后导航标记未知。safe_landing另检查换位动作的完整落地过程。act携combat_candidate引用表达移动与武器意图；宿主保持原动作运行，同时从当前位置重算该动作，通过校验后交接并逐帧检测偏离。follow_route组合用set_combat更新武器。",
            ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject
                { ["radius"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 60 },
                  ["forecast"] = new JsonObject { ["type"] = "boolean" }, ["context"] = new JsonObject { ["type"] = "string" },
                  ["destination"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                    ["x"] = new JsonObject { ["type"] = "integer" }, ["y"] = new JsonObject { ["type"] = "integer" } }, ["required"] = new JsonArray("x", "y") } } }
        } });
        tools.First(t => t["function"]?["name"]?.ToString() == "act")["function"]["parameters"]["properties"]["combat_candidate"] =
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                ["context"] = new JsonObject { ["type"] = "string" }, ["forecast_id"] = new JsonObject { ["type"] = "integer" },
                ["plan_id"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("context", "forecast_id", "plan_id"),
                ["description"] = "combat_assess候选引用；宿主用经过状态校验的剩余按键序列替换steps，同时选择组合武器。占用移动通道，自动攻击继续。" };
        var findTiles = tools.First(t => t["function"]?["name"]?.ToString() == "find_tiles")["function"];
        findTiles["parameters"]["properties"]["include_locked"] = new JsonObject { ["type"] = "boolean", ["default"] = false,
            ["description"] = "查箱子时可设true以包含上锁箱；结果locked给出当前锁状态。" };
        findTiles["description"] = findTiles["description"].ToString() + "include_locked:true可发现上锁箱，返回locked当前锁状态。";
        var treasure = tools.First(t => t["function"]?["name"]?.ToString() == "collect_treasure")["function"];
        treasure["description"] = treasure["description"].ToString() + "上锁箱复用原版右键交互检查钥匙与解锁条件并消耗钥匙，再开箱取物；chest_was_locked记录采集前锁状态。";
        var navigation = tools.First(t => t["function"]?["name"]?.ToString() == "nav_to")["function"];
        navigation["description"] = navigation["description"].ToString()
            + "默认travel进入请求路点附近的交接范围；mine接近待挖实心目标，interact按交互距离接近目标，stand搜索精确落脚位置。";
        var run = tools.First(t => t["function"]?["name"]?.ToString() == "run_script")["function"];
        run["parameters"]["properties"]["background"] = new JsonObject { ["type"] = "boolean",
            ["description"] = "配合task_node启动后台原脚本或临时代码，立即返回句柄。活动goal中的节点动作脚本默认后台；background:false可显式等待。只读准备脚本直接返回。" };
        run["description"] = run["description"].ToString()
            + "多动作连续执行可用background:true，脚本拥有主动作句柄，内部直接tool('nav_to',...)、tool('gather',...)及条件循环；action_control暂停/取消，原workflow保留。运行中可准备下一段源码；立即改变正在执行的动作时先暂停。"
            + "临时恢复可传task_node、code及独立workflow，动作仍归属该节点，step检查点单独保存，原节点script和workflow保留；无检查点的临时代码执行后释放workflow槽位，返回workflow_retained=false，结果保留在本次返回中。临时脚本支持后台执行，Agent可同时观察、暂停或修订后继。task_node也可与tool/args组合执行所属节点下的单工具，原script和workflow保留。";
        run["description"] = run["description"].ToString()
            + "普通动作失败直接返回失败结果，JS可在同轮按现场执行已准备的恢复分支；step仍会拒绝把失败记录为完成。"
            + "只有一个就绪DAG节点时，带code和独立workflow的临时恢复自动归属该节点，返回task_node；多个就绪节点时显式指定。";
        var inventoryWatch = tools.First(t => t["function"]?["name"]?.ToString() == "watch")["function"];
        inventoryWatch["description"] = inventoryWatch["description"].ToString()
            + "action_finished、target_acquired和inventory_full始终为状态通知，当前动作继续执行；失败和显式订阅的库存增长唤醒并行观察。普通沿途机会每15秒合并交付一批后台观察，JS可即时读取完整事件；危险和失败独立交付。JS处理工具结果后继续，切换控制用action_control或decision_point。";
        var plan = tools.First(t => t["function"]?["name"]?.ToString() == "task_plan")["function"];
        plan["description"] = plan["description"].ToString()
            + "节点可提供on_failure（JS函数体），在赶路时提前准备失败恢复；task_plan_patch nodes仅修改on_failure时保持当前动作运行，空串移除。动作失败后宿主直接执行一次已保存恢复，使用独立一次性上下文，不占持久workflow名额；failure是原动作的完整action_query结果（含handle/tool/args/attempt/result），原script与step进度保留。核验修复结果后在同段JS恢复failure.handle；未覆盖的失败返回{status:'needs_review',handle:failure.handle,reason:'具体原因'}交回模型。模型直接调用action_query读取完整失败。死亡、窒息和其他主动决策事件独立处理。"
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
            conditions["kind"]["enum"].AsArray().Add("tiles");
            conditions["width"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 4096 };
            conditions["height"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 4096 };
            conditions["initially_locked"] = new JsonObject { ["type"] = "boolean",
                ["description"] = "chests_looted专用：true只计数采集前上锁、解锁后箱内物品实际入包的新箱子。" };
            conditions["name"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("jungle", "desert", "snow", "ocean", "dungeon", "corruption", "crimson", "hallow") };
            conditions["depth"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("any", "sky", "surface", "below_surface", "underground", "cavern", "underworld"), ["default"] = "any" };
            definition["description"] = definition["description"].ToString()
                + "biome条件按原生人物群系与深度验收，count=1，name为群系名，depth=below_surface涵盖地下层和洞穴层。结束时身处某群系的要求使用biome条件，可在群系内任意位置验证。"
                + "施工使用tiles条件：x/y为矩形左上世界格，width/height为大小，count=面积且面积<=4096，type为原版方块编号或内部名称，-1为空格。宿主逐格读取真实世界验收，不规则图形可拆成若干矩形条件。"
                + "chests_looted可设initially_locked:true，宿主核对采集前上锁、采集后解锁且战利品入包；每个箱子只计一次。";
            definition["parameters"]["properties"][field]["items"]["properties"]["conditions"]["items"]["properties"]["type"] = new JsonObject
            {
                ["anyOf"] = new JsonArray(new JsonObject { ["type"] = "integer", ["minimum"] = -1 },
                    new JsonObject { ["type"] = "string", ["minLength"] = 1 }),
                ["description"] = "真实编号或完整原版英文内部名称。宿主在建计划时解析名称并保存编号，可直接使用已知名称，无需先单独调用game_catalog；名称片段或歧义会拒绝。"
            };
        }
        foreach (var (tool, field) in new[] { ("task_plan", "steps"), ("task_plan_patch", "add_nodes"), ("task_plan_patch", "nodes") })
            tools.First(t => t["function"]?["name"]?.ToString() == tool)["function"]["parameters"]["properties"][field]["items"]["properties"]["on_failure"]
                = new JsonObject { ["type"] = "string", ["maxLength"] = 16000,
                    ["description"] = "预先准备的失败恢复JS函数体；failure为失败动作完整结果。可在执行中更新；空串移除。" };
        var fight = tools.First(t => t["function"]?["name"]?.ToString() == "fight")["function"];
        fight["description"] = "仅控制攻击，不改变移动；可与nav_to并行，不需暂停原导航。挖掘、放置使用物品时攻击让位；结束攻击不停止导航。" + fight["description"];
        if (fight["parameters"]?["properties"]?["movement"] is JsonObject movement)
            movement["description"] = "旧参数已不控制移动；移动由nav_to或独立移动指令负责。";
        var programs = tools.First(t => t["function"]?["name"]?.ToString() == "script_program")["function"];
        programs["parameters"]["properties"]["operation"]["enum"].AsArray().Add("example");
        programs["description"] = programs["description"].ToString()
            + "list包含内置技能模板(template:true,revision:0)和已保存技能。get读取模板源码，填写config后save(expected_revision:0)，再以返回版本执行；模板不是实机验证通过的技能。navigate_observe保持导航与观察并行；collect_detour采集后恢复原路线；use_life_crystals验证永久生命增长；recover_pressure_plate可按权限和当前工具配置后写入节点on_failure。"
            + "example返回可编辑JS示例，name为空列出名称；gather_route在来源暂不可见或本区域采尽时，按预备路点立即续采；sample_tile_variants演示按现场变体试采、保存所得、同句柄恢复及事件交回。示例不执行；先读源码，按任务修改config与策略，再save和run_script。";
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
        t["function"]?["name"]?.ToString() is "run_script" or "get_state" or "say" or "ask" or "game_api" or "terrain_map" or "action_query" or "script_program")
        .Select(t => t.DeepClone()).ToArray());

    public static string Instructions(string system, JsonArray tools) =>
        "单次API用run_script({tool:名称,args:参数})；组合行为提交同步JavaScript，以tool(name,args)读取结果、循环和执行。"
        + "顶层提供run_script、get_state、say、ask、game_api、terrain_map、action_query、script_program。action_query直接读取动作证据，script_program直接读取或修改技能源码；两者返回完整对象，组合行为时也可在JS内调用。terrain_map可直接返回现场图片。\n"
        + system
        + RuntimeInstructions
        + "\n以下是API用途索引。首次使用前game_api({names:[所需工具名]})批量查询完整定义，已读定义可复用。\n"
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
        "\n运行约定：源码是同步JS函数体，tool直接返回对象；step保存可独立核验的小效果，跨段变量存于step返回值。"
        + "每step结果限8000字符，return限16000字符；print每条限1000字符，log_truncations记录截断。只返回决策字段，完整执行轨迹按report_id用script_report查询。tool_counts表示请求次数，效果用真实状态核验。"
        + "\nDAG执行：第一步目的地与验收明确后建立最小计划，从ready节点后台启动；最终要求从初始conditions保留，末端包含仍需持有物品。调查、后继script和on_failure在行进期间补齐，前置通过宿主验收后自动派发。依赖表示已验收，输入互斥由执行器管理。"
        + "\n途中反应：先用on(kind,event=>{...;return {handled:true};})注册本次脚本的处理函数，再执行主任务。watch订阅nearby_tile/nearby_chest/nearby_drop/nearby_npc_killed/inventory_full等现场事件；tick每250ms提供一次本地检查机会，适合检查受伤、威胁和武器，combat_assess提供事实。导航及gather内部导航会执行处理函数，原脚本和主动作句柄保持；只读和背包操作保持路线运行，需要移动/挖掘/放置时交接控制，结束后按当前位置重启原目的地。处理函数可调用tool/skill/step，步骤与主任务隔离。返回handled:false交回Agent；未注册事件沿用原交付。注册仅本次脚本有效，重新执行源码会使用新处理函数。"
        + "整趟任务的资源、背包、压力板和战斗反应先挂到task_policy，随后开始首段行动；节点script只推进主任务，局部on用于该节点的特别处理。task_policy覆盖当前节点、后继节点、恢复脚本和独立后台动作；不依赖后继节点开始才生效。event.execution给出本次实际工具及args，event.task_node为所属节点，可据此决定顺路采集与返回。赶路时更新同一策略，注册与新策略初始化保持当前动作运行。task_reactions模板可用于整趟任务，reactive_travel用于单段。"
        + "快速现场判断可在tick里用jev_judge的channel/context后台发起，读取ready中的request_id、input_state和result后按现场执行，等待期间原动作继续。script_program example decision_reactions提供weapon/movement/opportunity三个可独立启停的可编辑示例，decisions数组选择维度，instruction给当前意图；保存为技能后可在task_policy中用skill调用。"
        + "组合策略时先skill注册基础处理，再on(kind,handler,{append:true})追加该事件的处理；依注册顺序执行，handled:false交回审阅或retry:true重试时结束本次处理链。on默认替换同名处理。task_policy只写skill调用和增补代码，完整技能保存在script_program。"
        + "挖掘、use_item、place_at和act执行期间同样投递事件：只读判断立即运行，处理函数的物品或移动动作在当前原子动作完成后交接；原动作结果先保存，随后主脚本继续。长段输入拆成有明确世界结果的短动作，让策略及时接手。"
        + "on('action_failed',event=>...)接收tool/args/result；查证并完成修复后返回{retry:true}，宿主仅立即重试原调用一次，原失败保留记录；压力板、背包满等可在本地处理。处理失败返回证据给主脚本或显式decision_point。技能reactive_travel提供机会、背包、压力板、荆棘和战斗策略的可编辑示例。"
        + "task_plan_patch带expected_revision和reason，nodes更新现节点，add_nodes插入机会或恢复节点；保留原意图、workflow、handle和step id。当前片段正常yield后续跑已保存新版本；already_running保留原执行，派发中的handle可为空。"
        + "父脚本运行时由它续接；脚本已退出且主动作独立运行时，action_queue在当前handle后排同节点run_script，args填task_node。"
        + "\n重复观察用get_state的fields选择当前判断所需字段，例如{fields:['player','inventory_slots','permanent_max_health']}；首次建立现场认识再读完整状态。连续采集用JS循环和实际库存/永久血量验证，下一目标提前准备在当前脚本或后继节点中。"
        + "player.biome_flags是原生群系独立标志，多个可同时为true；player.biome保留旧显示名称，判定混合群系用biome_flags，ground_tiles给出脚下实际方块坐标和类型。"
        + "\n程序与技能：script_program list/get查询现有技能和runtime证据，save/patch修订同一程序，用program、expected_revision、workflow执行。"
        + "skill(name,expected_revision,input,key)读input，共享tool/print/step与100次工具预算；稳定key隔离本次调用的step，不同目标用不同key。启动时固定版本，新版下一段生效。skill_calls记录实际版本、哈希、参数、耗时；returned表示函数返回，完成另行验收。"
        + "\n导航：find_biome给群系方向和大致位置；实际路线结合当前位置与特殊知识。地下丛林使用find_descent/descent_route查证的高效主道，按洞穴和阻挡选择局部处理。逐格移动、挖掘、搭桥交给执行器。"
        + "\n事件：按已查证来源配置watch，方块/工作站用nearby_tile+tile_types，箱子nearby_chest，掉落物nearby_drop。workflow.watch_policy是上次设置记录，当前策略看get_state.watch。"
        + "passive_events只记录，background_events请求并行观察并保持行动；event_delivery:script用script_events消费，必要时jev_judge做本地判断。target_acquired是实际库存增长通知，当前动作继续；遵守玩家保留/使用要求，消费后更新剩余库存目标。"
        + "script_events.action提供本次暂停后的最新observation；evidence是触发时快照，action:null表示本次未暂停。动作返回的events已出队，先处理它们；空队列只说明当前没有待交付事件。"
        + "assessment附tick、障碍、route_relation及原navigation，每批最多6项；assessment_deferred表示未评估。opportunity_assess的ok仅表示查询成功，按hazards的kind、坐标、on_direct_corridor及接近/返程地形作判断，观测可能截断。"
        + "结合库存、任务和取得成本选择机会，理由存step；多格事件按物体合并，取得用真实库存或got核验。插入时暂停并保存原handle，按需resume(args)或cancel，独立防身可与移动并行。"
        + "\n恢复：script_progress get返回steps数组，原句柄在steps[i].value.handle，当前状态用action_query。uncertain核对后再retry/complete；repeatable_navigation:true允许根据no_progress现场修正后续接原step，历史失败保留。"
        + "on_failure接收完整failure，局部修复后同段JS恢复原handle。decision_point携带关键evidence、tick、原handle、尝试和候选，reported_evidence是脚本报告；默认暂停，continue_action指定允许审阅期间继续的当前句柄，后续仍需核对交接。"
        + "interrupted保留handle/events，按事件恢复；失败恢复保留已完成效果。get_state.script_workflows.last_execution及script_progress.last_segment保留上段工具次数、耗时和failure。"
        + "\n资源：game_catalog matches[].type按kind区分编号，places_tile仅证明可放置关系，exact:false须查证；frame和实际掉落核验来源。NPC材料可查npc_drop_sources，npc_find只列当前已生成NPC。"
        + "get_state.depth是原生高度层，与player.biome共同判环境。gather source_error.observations记录移除无增量证据，combat_obstructed/out_of_range的last_result.result给距离、射界、目标和伤害。"
        + "npc_combat_observe用candidate.who/npc_type，stance_cells可测其他落脚格；stance_probes的weapon_types按当前快捷栏武器许可、弹药/魔力、射程及射界测量，距离单位为格。combat_approach技能可选站位并导航，抵达后刷新身份和射界；路线安全由导航另行验证。clear_line不预测弹道。"
        + "\n整理与生存：查询和选择在行进中完成；需要交接时同段JS暂停、使用新鲜type/prefix/stack操作、核验、finally恢复。healing_items列直接治疗物，实际可用看potion_delay_ticks；last_automatic_heal是历史。"
        + "低血量仅唤醒观察，当前动作继续。health_dynamics的regen_direction说明引擎当下趋势，raw不是每秒伤害；last_damage.tick区分新伤。hp_at_hook不保证扣血后血量，查当前player.hp。"
        + "contact_escape_active仅在auto_dodge许可下启用；survival_escape_reobserve表示原动作未执行且位置已变。navigation_contact_escape需结合contact_escape实际起终、落脚和危险修复局部路线，再续接原目标。"
        + "每次调用推进任务或解决未知；已知动作在JS连续执行，新事实需要模型时带证据交回。后台观察没有新决定可结束本轮，当前动作保持运行。"
        + "\n学习未知操作：act(observe:true)返回真实输入、弹射物轨迹、伤害归属及observation.id。比较短试验后用script_program保存可复用方法，结论引用实际试验ID，技能执行时读取现场参数并检验效果。";

    public static async Task<JsonObject> Run(string source, int seconds,
        Action<string, JsonObject> validate,
        Func<string, JsonObject, CancellationToken, Task<string>> execute,
        Func<CancellationToken, Task> stop, CancellationToken token, ScriptProgress progress = null,
        Func<CancellationToken, Task<JsonObject>> observeEvent = null, JsonArray programs = null, Action<JsonObject> onResult = null,
        Action<string> registerReaction = null)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 16000)
            throw new ArgumentException("code必须是1..16000字符的JavaScript函数体，使用return返回结果。");
        progress?.Begin(source);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        var clock = Stopwatch.StartNew();
        var trace = new Queue<JsonNode>();
        var logs = new JsonArray();
        var logTruncations = new JsonArray();
        var toolCounts = new JsonObject();
        var skillCalls = new JsonArray();
        var skills = (programs ?? new JsonArray()).ToDictionary(p => p["name"].ToString(), p => p.AsObject());
        int calls = 0;
        int mainCalls = 0, handlerCalls = 0, reactionCount = 0;
        int mainSkillCalls = 0, handlerSkillCalls = 0;
        ScriptProgress handlerProgress = null;
        Reactions session = null;
        var reactionReports = new Queue<JsonNode>();
        bool inTool = false;
        bool inputEffects = false;
        Exception hostFailure = null;
        JsonObject wake = null;
        JsonObject lastFailure = null;
        string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
        JsonObject Report(string error, string detail, JsonNode value = null, int? omittedOutputChars = null)
        {
            foreach (var call in skillCalls.Where(call => call["status"]?.ToString() == "running"))
            {
                call["status"] = "interrupted";
                call["elapsed_ms"] = clock.ElapsedMilliseconds - call["started_ms"].GetValue<long>();
                call["tool_calls"] = calls - call["tool_calls_before"].GetValue<int>();
                call["result"] = new JsonObject { ["error"] = error ?? "skill_did_not_return" };
            }
            var result = new JsonObject
            {
                ["ok"] = error == null, ["error"] = error, ["detail"] = detail,
                ["value"] = value, ["tool_calls"] = calls, ["elapsed_ms"] = clock.ElapsedMilliseconds,
                ["input_effects"] = inputEffects,
                ["tool_counts"] = toolCounts.DeepClone(),
                ["reaction_count"] = reactionCount, ["reactions"] = new JsonArray(reactionReports.ToArray()),
                ["skill_calls"] = skillCalls,
                ["trace"] = new JsonArray(trace.ToArray()), ["logs"] = logs, ["log_truncations"] = logTruncations,
                ["failure"] = lastFailure?.DeepClone()
            };
            if (omittedOutputChars.HasValue) result["output_limit"] = new JsonObject
                { ["characters"] = omittedOutputChars.Value, ["limit"] = 16000, ["body_completed"] = true, ["value_omitted"] = true };
            progress?.End(result);
            if (progress != null) result["workflow"] = progress.Summary();
            onResult?.Invoke(result);
            return result;
        }
        try
        {
            return await Task.Run(() =>
            {
                var reactions = session = new Reactions();
                CurrentReactions.Value = reactions;
                using var engine = new Engine(options =>
                {
                    options.Strict().LimitMemory(32_000_000).MaxStatements(50000)
                        .CancellationToken(budget.Token);
                    options.Host.StringCompilationAllowed = false;
                    options.Constraints.MaxRecursionDepth = 64;
                    options.Constraints.MaxArraySize = 100000;
                    options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(100);
                });
                engine.SetValue("__gameOn", new Action<string, Jint.Native.JsValue>((kind, handler) =>
                {
                    if (kind is not ("nearby_tile" or "nearby_chest" or "nearby_drop" or "nearby_npc_killed"
                        or "inventory_full" or "target_acquired" or "low_health" or "honey_slowdown" or "tick" or "action_failed"))
                        throw new ArgumentException("on不支持此事件：" + kind);
                    if (reactions.Handling) throw new InvalidOperationException("事件处理期间不能注册处理函数。");
                    reactions.Register(kind, notice =>
                    {
                        engine.Constraints.Reset();
                        try { return JsonNode.Parse(engine.Invoke(handler, notice.ToJsonString()).AsString())?.AsObject(); }
                        finally { engine.Constraints.Reset(); }
                    });
                    registerReaction?.Invoke(kind);
                }));
                engine.SetValue("__gameHandling", new Action<bool, string>((value, facts) =>
                {
                    reactions.Handling = value;
                    if (value)
                    {
                        handlerCalls = handlerSkillCalls = 0; reactionCount++;
                        handlerProgress = new ScriptProgress(); handlerProgress.Begin();
                    }
                    else if (handlerProgress != null)
                    {
                        handlerProgress.End(new JsonObject { ["value"] = JsonNode.Parse(facts) });
                        reactionReports.Enqueue(handlerProgress.Summary());
                        if (reactionReports.Count > 8) reactionReports.Dequeue();
                        handlerProgress = null;
                    }
                }));
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
                    if ((reactions.Handling ? handlerCalls++ : mainCalls++) >= 100)
                        throw hostFailure = new InvalidOperationException("脚本或单次事件处理达到100次工具调用上限。");
                    calls++;
                    string activity = name == "action_control" ? name + "." + args["command"]
                        : name == "script_program" ? name + "." + args["operation"] : name;
                    toolCounts[activity] = (toolCounts[activity]?.GetValue<int>() ?? 0) + 1;
                    var entry = new JsonObject { ["step"] = calls, ["tool"] = name, ["args"] = Clip(json, 400) };
                    trace.Enqueue(entry);
                    if (trace.Count > 12) trace.Dequeue();
                    long started = clock.ElapsedMilliseconds;
                    inputEffects |= AgentControlSession.ActionTool(name) || name == "control";
                    inTool = true;
                    string result = reactions.Await(execute(name, args, budget.Token));
                    (reactions.Handling ? handlerProgress : progress)?.Record(name, args, result);
                    inTool = false;
                    JsonObject toolResult = null;
                    try { toolResult = JsonNode.Parse(result) as JsonObject; }
                    catch (System.Text.Json.JsonException) { }
                    if (toolResult != null)
                    {
                        if (ScriptProgress.Failed(toolResult))
                        {
                            var facts = new JsonObject();
                            foreach (string field in new[] { "error", "detail", "outcome", "done", "status", "reason", "state", "pos", "progress_idle_seconds", "stationary_seconds", "distance_pixels", "best_distance_pixels", "active_handle", "name_candidates", "requested_name", "targets", "source_rules", "source_error", "last_result", "placed", "wanted" })
                                if (toolResult[field] != null) facts[field] = toolResult[field].DeepClone();
                            if (toolResult["cells"] is JsonArray cells && cells.Count > 0) facts["last_cell"] = cells[^1]?.DeepClone();
                            lastFailure = new JsonObject { ["tool"] = name, ["args"] = args.DeepClone(), ["evidence"] = facts };
                            foreach (var call in skillCalls.Where(call => call["status"]?.ToString() == "running"))
                                call["last_failure"] = new JsonObject { ["tool"] = name, ["error"] = toolResult["error"]?.DeepClone(),
                                    ["reason"] = toolResult["reason"]?.DeepClone(), ["outcome"] = toolResult["outcome"]?.DeepClone() };
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
                    if (logs.Count >= 20)
                    {
                        if (reactions.Handling) logs.RemoveAt(0);
                        else throw new InvalidOperationException("脚本最多print 20次。");
                    }
                    if (text?.Length > 1000) logTruncations.Add(new JsonObject
                        { ["index"] = logs.Count, ["original_chars"] = text.Length, ["retained_chars"] = 1000 });
                    logs.Add(Clip(text ?? "null", 1000));
                    DiagLog.Write("[script_log] " + Clip(text ?? "null", 1000));
                }));
                engine.SetValue("__stepEnter", new Func<string, string>(id =>
                    ((reactions.Handling ? handlerProgress : progress) ?? throw new InvalidOperationException("step需要run_script.workflow。 ")).Enter(id).ToJsonString()));
                engine.SetValue("__stepComplete", new Action<string, string>((id, json) =>
                    (reactions.Handling ? handlerProgress : progress).Complete(id, JsonNode.Parse(json)?["value"])));
                engine.SetValue("__skillEnter", new Func<string, string, string, int>((name, input, key) =>
                {
                    budget.Token.ThrowIfCancellationRequested();
                    int count = reactions.Handling ? ++handlerSkillCalls : ++mainSkillCalls;
                    if (count > 64) throw hostFailure = new InvalidOperationException($"单次脚本或事件的技能调用为{count}次，上限64次。");
                    if (input.Length > 8000) throw hostFailure = new InvalidOperationException($"技能input为{input.Length}字符，上限8000字符。");
                    if (skillCalls.Count >= 64 && skillCalls.All(call => call["status"]?.ToString() != "running")) skillCalls.Clear();
                    var saved = skills[name];
                    skillCalls.Add(new JsonObject { ["name"] = name, ["revision"] = saved["revision"].DeepClone(),
                        ["source_sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                            System.Text.Encoding.UTF8.GetBytes(saved["code"].ToString()))),
                        ["input"] = JsonNode.Parse(input), ["key"] = key, ["status"] = "running",
                        ["started_ms"] = clock.ElapsedMilliseconds, ["tool_calls_before"] = calls });
                    return skillCalls.Count - 1;
                }));
                engine.SetValue("__skillExit", new Action<int, string>((index, outcome) =>
                {
                    var call = skillCalls[index];
                    call["status"] = "returned";
                    call["elapsed_ms"] = clock.ElapsedMilliseconds - call["started_ms"].GetValue<long>();
                    call["tool_calls"] = calls - call["tool_calls_before"].GetValue<int>();
                    call["result"] = JsonNode.Parse(outcome);
                }));
                string skillSource = string.Join(",", skills.Select(pair =>
                {
                    string code = pair.Value["code"].ToString();
                    try { Engine.PrepareScript(ScriptLibrary.FunctionSource(code)); }
                    catch (ScriptPreparationException error)
                    {
                        code = "throw new Error(" + System.Text.Json.JsonSerializer.Serialize(
                            $"program_invalid_syntax: {pair.Key} revision={pair.Value["revision"]}: {error.Message}") + ");";
                    }
                    return System.Text.Json.JsonSerializer.Serialize(pair.Key) + ":{revision:" + pair.Value["revision"]
                        + ",run:function(input){\n" + code + "\n}}";
                }));
                string wrapper = """
                    (function(call, log, enter, complete, enterSkill, exitSkill, register, setHandling) {
                        delete globalThis.__gameTool;
                        delete globalThis.__gamePrint;
                        delete globalThis.__stepEnter;
                        delete globalThis.__stepComplete;
                        delete globalThis.__skillEnter;
                        delete globalThis.__skillExit;
                        delete globalThis.__gameOn;
                        delete globalThis.__gameHandling;
                        const stringify = JSON.stringify;
                        const parse = JSON.parse;
                        const handlers = new Map();
                        let handling = false;
                        const dispatch = event => {
                            handling = true;
                            setHandling(true, '{}');
                            let value;
                            try { value = handlers.get(event.kind)(event); return value; }
                            finally { handling = false; setHandling(false, stringify(value) ?? '{}'); }
                        };
                        const on = (kind, handler, options = {}) => {
                            if (typeof handler !== 'function') throw new Error('on需要事件名和处理函数');
                            const previous = handlers.get(kind);
                            handlers.set(kind, options.append && previous ? event => {
                                const first = previous(event);
                                if (first?.handled === false || first?.retry === true) return first;
                                return {...first, ...handler(event)};
                            } : handler);
                            register(kind, json => stringify(dispatch(parse(json))) ?? '{}');
                        };
                        const tool = (name, args = {}) => {
                            if (name === 'step') throw new Error('step是JS内置函数，不是游戏API。请直接写step(id, () => valueOrAction)，不要写tool("step", ...)。修正调用并沿用原workflow，已完成步骤不会重做。');
                            let result = parse(call(name, stringify(args)));
                            if (!handling && handlers.has('action_failed') && result &&
                                (result.error || result.ok === false || ['interrupted','interrupted_by_death','no_progress','inventory_full'].includes(result.outcome))) {
                                const decision = dispatch({kind:'action_failed',tool:name,args,result});
                                if (decision?.retry === true) result = parse(call(name, stringify(args)));
                            }
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
                        let stepPrefix = '';
                        const step = (id, action) => {
                            id = stepPrefix + id;
                            const saved = parse(enter(id));
                            if (!saved.execute) return saved.value;
                            const value = action();
                            if (value && typeof value.then === 'function') throw new Error('step不支持Promise');
                            const json = stringify({value});
                            if (json.length > 8000) throw new Error('step返回值超过8000字符');
                            complete(id, json);
                            return value;
                        };
                        const programs = {
                    """ + skillSource + """
                        };
                        const skill = (name, revision, input = {}, key = name) => {
                            if (!Object.hasOwn(programs, name)) throw new Error('unknown_saved_skill: ' + name);
                            const program = programs[name];
                            if (program.revision !== revision) throw new Error('program_revision_conflict: ' + name + ' actual=' + program.revision);
                            if (typeof key !== 'string' || !key.length) throw new Error('skill key needs a stable nonempty string');
                            const previous = stepPrefix;
                            stepPrefix += key + '/';
                            const index = enterSkill(name, stringify(input), stepPrefix);
                            try {
                                const value = program.run(input);
                                if (value && typeof value.then === 'function') throw new Error('skill不支持Promise');
                                const facts = {};
                                if (value && typeof value === 'object')
                                    for (const field of ['status','reason','error','outcome'])
                                        if (Object.hasOwn(value, field) && typeof value[field] === 'string') facts[field] = value[field].slice(0,192);
                                exitSkill(index, stringify(facts));
                                return value;
                            } finally { stepPrefix = previous; }
                        };
                        const value = (function() {
                    """ + source + """

                        })();
                        if (value && typeof value.then === 'function') throw new Error('使用同步tool调用，不支持Promise返回');
                        const result = stringify({value});
                        return result;
                    })(__gameTool, __gamePrint, __stepEnter, __stepComplete, __skillEnter, __skillExit, __gameOn, __gameHandling)
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
            Report("cancelled", "执行被取消，未完成步骤需要核对实际状态。");
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
                    Report("cancelled", "清理期间被取消，步骤需要核对。");
                    throw new OperationCanceledException(token);
                }
                catch (Exception failure) { cleanup = failure.Message; }
            }
            var result = Report(budget.IsCancellationRequested || error is TimeoutException ? "script_timeout" : "script_failed",
                Clip(error.GetType().Name + ": " + error.Message, 2000));
            if (cleanup != null) result["cleanup_error"] = cleanup;
            return result;
        }
        finally { if (session != null) session.Retire(() => { }); }
    }
}
