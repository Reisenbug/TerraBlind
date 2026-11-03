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
        var watch = tools.First(t => t["function"]?["name"]?.ToString() == "watch")["function"];
        watch["description"] = watch["description"].ToString()
            + "另支持nearby_drop(附近可见掉落，坐标单位格，需用nearby_drops刷新)和nearby_npc_killed(仅单人原生死亡事件，不证明谁击杀、不保证有掉落)。script本地处理的事件不消耗模型唤醒额度，显式decision_point仍受额度限制。不代表已拾取。";
        var parameters = tools.First(t => t["function"]?["name"]?.ToString() == "run_script")["function"]["parameters"];
        parameters["properties"]["program"] = new JsonObject { ["type"] = "string", ["description"] = "已保存程序名称；与code二选一。" };
        parameters["properties"]["expected_revision"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1,
            ["description"] = "使用program时必填，必须匹配刚读取的源码版本。" };
        parameters["properties"]["event_delivery"] = new JsonObject { ["type"] = "string",
            ["enum"] = new JsonArray("agent", "script"),
            ["description"] = "默认agent遇事件交回模型；script由代码调用script_events消费事件，长动作被事件暂停时返回handle/events供代码处理。显式decision_point仍交回模型。" };
        parameters["required"] = new JsonArray();
    }

    public static JsonArray EntryTools(JsonArray tools) => new(tools.Where(t =>
        t["function"]?["name"]?.ToString() is "run_script" or "get_state" or "say" or "ask" or "game_api")
        .Select(t => t.DeepClone()).ToArray());

    public static string Instructions(string system, JsonArray tools) =>
        "游戏操作通过run_script提交JavaScript代码，代码里用tool(name,args)调用下面的游戏API。"
        + "顶层只提供run_script、get_state、say、ask、game_api；其余API不是顶层工具，不要直接发起函数调用。"
        + "逐次行为、条件和循环应写在代码里，读取真实返回后推进；不能只登记计划或口头承诺。\n"
        + system
        + RuntimeInstructions
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
            ["detail"] = "仅接口定义，不是执行结果。通过run_script里的tool(name,args)执行游戏操作。" };
    }

    public const string RuntimeInstructions =
        "\n源码是同步JavaScript函数体，tool()直接返回结果；不使用顶层await，不返回Promise。"
        + "需要复用或修改的行为先用script_program保存源码，再用run_script的program、expected_revision和稳定workflow运行。"
        + "需要事件驱动的快速反应时使用event_delivery:script，程序用script_events消费订阅事件、用jev_judge根据当前事实判断；模型不必为每次事件重新生成整段代码。"
        + "有沿途收集要求时，先用recipe确认所需物品和数量、game_catalog确认编号，再查证来源。game_catalog的matches[].type是对应kind的编号；物品编号、NPC编号和方块编号不能混用，名称相似也不证明掉落关系。NPC材料先用npc_drop_sources查询注册掉落及条件；植物来源与变体查wiki或依据实际采集证据，不能把材料名当TileID、把生长基底当材料来源，也不能猜npc_types或frame。未知来源应报告未查证，不得假装已经配置正确。"
        + "按查证的来源配置watch：方块和工作站使用nearby_tile+tile_types，箱子使用nearby_chest，已掉落材料使用nearby_drop；发现候选不代表必定取得目标物品。"
        + "普通机会可加入watch.passive_events，只通知不自动打断；配合action_start和script_events边执行边评估，决定值得插入时再action_control pause，避免每发现一株植物就重启导航。"
        + "发现机会先opportunity_assess(x,y,destination原目的地)，结合当前库存、任务需求和已测障碍取舍；ok只表示查询完成，即使有岩浆或陷阱也可为true，不是安全、可达或值得拿的结论。直线距离不是可达证明。"
        + "把每次选择及原因保存在稳定step结果中；跳过不能写成已收集。多格物体可能发多个tile_cell事件，按现场物体合并处理。"
        + "step单个结果上限8000字符；只存选择理由、目标坐标、handle、outcome和实际所得等必要证据，不复制工具返回中整份task_plan、历史和背包。需要完整动作证据时用action_query读取原handle，不能用截断的记录冒充完成证明。"
        + "print用于诊断，每条最多1000字符；log_truncations列出被截断的日志及原长度。截断后未出现的字段不代表不存在，不能据此下结论。把决策所需字段从工具结果中提取后return，避免print整份状态或多份查询；JSON字符串直接print即可，不会再次转义。"
        + "已选定局部采集目标时给gather传source_area包住该物体和预计掉落位置，防止默认最近来源选择重新选回已跳过的目标；来源区域不是导航路径限制。"
        + "插入动作前保存并暂停原action句柄，插入后核对实际库存增量或箱子返回got；失败保留证据，不记成功。继续时resume同一原句柄，而不是新建重复目标。"
        + "暂停会释放该动作的攻击和移动，不代表仍在防身。敌怪附近需要连续只读观察时，可明确action_start fight临时防身（短seconds、有限max_dist），观察放在try中，finally用action_control cancel该防身句柄并等待释放，再导航、用物品或resume原任务；不能让两个动作同时持有输入。防身会改变人物和敌怪位置，行动前刷新现场；不保证安全，不可绕过攻击/闪避禁令或忽略危险事件。"
        + "Agent可以修改resume的args，或cancel已经暂停的原意图；不值得的机会可直接跳过并继续，不能无条件见宝必拿。"
        + "开箱、获得装备或inventory_full后可用inventory_review批量检查背包和功能装备，结合item_info、当前任务配方及玩家保留要求选择换装、整理或丢弃，不按售价或防御单项固定评分。用equip_item穿戴、inventory_swap整理、inventory_drop丢整叠；先暂停原动作，使用新鲜槽位证据，核对实际结果再恢复原handle。丢到地上的物品仍可能被捡回，收藏物品不可丢弃。"
        + "长动作返回outcome:interrupted不是完成；保存返回的handle和events，核对现场后暂停/恢复同一动作。不要把中断结果直接作为成功step。"
        + "动作返回的events已从事件队列取走，下一次script_events不会重放；必须先处理该返回值中的危险事件，再决定是否resume。空事件队列不代表现场安全。"
        + "恢复失败任务前先刷新get_state并处理低血量、持续受伤或窒息，再处理射界等任务障碍。治疗须核对实际库存和使用后的血量；无药可依据现场选择撤离休整或其他方案，不能只反复yield。撤离和换位也可能被事件中断，保留新旧句柄及返回events，勿把中断误判为寻路不可达。"
        + "恢复条件必须配有执行分支：由你依据现场选择回血目标和等待预算；判断当前位置可休息后，在预算内用script_events({wait_ms:1000})等待并消费事件，再get_state核对血量、死亡/窒息和新伤害。继续受伤或现场改变就中止休息并调整方案；条件满足后在同一段代码继续处理原任务障碍或resume原handle，而不是再decision_point要求模型做已经明确的观察。预算耗尽仍未恢复时，带上血量变化、已等时长和原handle交回。没有确认可休息的位置不能直接套等待循环；缺少可查询的证据应先调用观察工具，不能只把‘需要观察/回血’存成一个立即yield的程序。"
        + "get_state.healing_items直接列出背包前50格中healLife>0的非空物品及slot、stack、base_heal_life、consumable，找直接治疗物品不必逐格item_info。基础治疗值不是当前可用或实际回血承诺；还需看potion_delay_ticks并验证use_item结果，不包含仅提供再生buff的物品。"
        + "last_automatic_heal记录默认生存反射最近一次实际回血或治疗冷却变化的tick和前后值；默认代码可能已用掉药水，不能照旧槽位重复用药。它是历史证据，继续查看当前healing_items、hp和potion_delay_ticks；可通过control禁止survival，但需自行承担恢复控制。"
        + "get_state.health_dynamics是同一tick的原生血量、死亡状态、life_regen_raw和life_regen_count_raw；regen_direction只说明当前引擎持续回血/掉血方向，不证明安全或预测未来血量。结合buffs剩余时间和last_damage.tick区分持续掉血与新受击，撤离后仍掉血不必然需要再次换位；判断能否等待由你的策略决定，继续观察新威胁并保留原任务。raw字段是引擎原始单位，不是每秒伤害。"
        + "get_state.depth提供游戏原生高度层标志sky/surface/underground/cavern/underworld和世界分层格坐标。结合player.biome判断所处环境，不能仅凭方块类型或猜测坐标断言不在地下；深度符合也不证明每个植物变体都能掉落所需材料。"
        + "gather返回source_unavailable时，用相同targets/source_area调用只读gather_observe确认当前库存和来源，不要反复启动gather探测。出现值得获取的新来源后可恢复原handle；否则决定换区域、执行其他步骤或交回观察。候选不是可达或安全证明，仍需opportunity_assess。"
        + "gather返回source_needs_review时，source_error.observations记录连续移除方块却没有目标库存增长的证据。先核查材料来源、变体和拾取条件，修正原handle的targets/source_area后再恢复；不要把挖掉方块当作收集成功，也不要原样重跑整批采集。"
        + "gather返回combat_obstructed表示连续射界受阻且目标未掉血；读取last_result.result里的目标格坐标、武器和血量证据，先观察地形、换位或改武器，不要原样重试。clear_line只是当前直线碰撞检查，不预测弹道或证明可达。"
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
