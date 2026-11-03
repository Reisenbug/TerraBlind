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
        watch["parameters"]["properties"]["events"]["items"]["enum"].AsArray().Add("nearby_drop");
        watch["parameters"]["properties"]["events"]["items"]["enum"].AsArray().Add("nearby_npc_killed");
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
        t["function"]?["name"]?.ToString() is "run_script" or "get_state" or "say" or "ask")
        .Select(t => t.DeepClone()).ToArray());

    public static string Instructions(string system, JsonArray tools) =>
        "游戏操作通过run_script提交JavaScript代码，代码里用tool(name,args)调用下面的游戏API。"
        + "顶层只提供run_script、get_state、say、ask；其余API不是顶层工具，不要直接发起函数调用。"
        + "逐次行为、条件和循环应写在代码里，读取真实返回后推进；不能只登记计划或口头承诺。\n"
        + system
        + RuntimeInstructions
        + "\n以下是脚本内tool可调用的完整游戏API定义（name、description、parameters），不是执行结果：\n"
        + new JsonArray(tools.Where(t => t["function"]?["name"]?.ToString() is not ("run_script" or "ask"))
            .Select(t => t["function"].DeepClone()).ToArray()).ToJsonString();

    public const string RuntimeInstructions =
        "\n源码是同步JavaScript函数体，tool()直接返回结果；不使用顶层await，不返回Promise。"
        + "需要复用或修改的行为先用script_program保存源码，再用run_script的program、expected_revision和稳定workflow运行。"
        + "需要事件驱动的快速反应时使用event_delivery:script，程序用script_events消费订阅事件、用jev_judge根据当前事实判断；模型不必为每次事件重新生成整段代码。"
        + "有沿途收集要求时，先查game_catalog确定材料/工作站的tile编号，用watch的nearby_tile+tile_types以及nearby_chest/nearby_drop订阅。"
        + "普通机会可加入watch.passive_events，只通知不自动打断；配合action_start和script_events边执行边评估，决定值得插入时再action_control pause，避免每发现一株植物就重启导航。"
        + "发现机会先opportunity_assess(x,y,destination原目的地)，结合当前库存、任务需求和已测障碍取舍；直线距离不是可达证明。"
        + "把每次选择及原因保存在稳定step结果中；跳过不能写成已收集。多格物体可能发多个tile_cell事件，按现场物体合并处理。"
        + "step单个结果上限8000字符；只存选择理由、目标坐标、handle、outcome和实际所得等必要证据，不复制工具返回中整份task_plan、历史和背包。需要完整动作证据时用action_query读取原handle，不能用截断的记录冒充完成证明。"
        + "已选定局部采集目标时给gather传source_area包住该物体和预计掉落位置，防止默认最近来源选择重新选回已跳过的目标；来源区域不是导航路径限制。"
        + "插入动作前保存并暂停原action句柄，插入后核对实际库存增量或箱子返回got；失败保留证据，不记成功。继续时resume同一原句柄，而不是新建重复目标。"
        + "暂停会释放该动作的攻击和移动，不代表仍在防身。敌怪附近需要连续只读观察时，可明确action_start fight临时防身（短seconds、有限max_dist），观察放在try中，finally用action_control cancel该防身句柄并等待释放，再导航、用物品或resume原任务；不能让两个动作同时持有输入。防身会改变人物和敌怪位置，行动前刷新现场；不保证安全，不可绕过攻击/闪避禁令或忽略危险事件。"
        + "Agent可以修改resume的args，或cancel已经暂停的原意图；不值得的机会可直接跳过并继续，不能无条件见宝必拿。"
        + "长动作返回outcome:interrupted不是完成；保存返回的handle和events，核对现场后暂停/恢复同一动作。不要把中断结果直接作为成功step。"
        + "动作返回的events已从事件队列取走，下一次script_events不会重放；必须先处理该返回值中的危险事件，再决定是否resume。空事件队列不代表现场安全。"
        + "恢复失败任务前先刷新get_state并处理低血量、持续受伤或窒息，再处理射界等任务障碍。治疗须核对实际库存和使用后的血量；无药可依据现场选择撤离休整或其他方案，不能只反复yield。撤离和换位也可能被事件中断，保留新旧句柄及返回events，勿把中断误判为寻路不可达。"
        + "get_state.healing_items直接列出背包前50格中healLife>0的非空物品及slot、stack、base_heal_life、consumable，找直接治疗物品不必逐格item_info。基础治疗值不是当前可用或实际回血承诺；还需看potion_delay_ticks并验证use_item结果，不包含仅提供再生buff的物品。"
        + "gather返回source_unavailable时，用相同targets/source_area调用只读gather_observe确认当前库存和来源，不要反复启动gather探测。出现值得获取的新来源后可恢复原handle；否则决定换区域、执行其他步骤或交回观察。候选不是可达或安全证明，仍需opportunity_assess。"
        + "gather返回combat_obstructed表示连续射界受阻且目标未掉血；读取last_result.result里的目标格坐标、武器和血量证据，先观察地形、换位或改武器，不要原样重试。clear_line只是当前直线碰撞检查，不预测弹道或证明可达。"
        + "普通敌怪受阻可用npc_combat_observe，npc/npc_type取原candidate的who/npc_type。站位候选不是路径或安全承诺；先检查危险，再尝试导航，抵达后刷新NPC射界和身份，最后显式resume失败的原采集句柄。"
        + "默认站位探针没有通畅射界不代表所有位置都不可用。可依据已观察的地形选择其他支撑格，通过npc_combat_observe.stance_cells测量；核对requested_stances的supported/body_fits/shot_line及支撑形状，勿把空中射线通畅当作可以站立。"
        + "观察发现危险、判断失败或局部调整仍无进展时调用decision_point交回审阅。审阅时读取原程序及workflow进度，修改源码版本后续接；保留已完成step和原句柄，uncertain先核对，不从头重放。"
        + "decision_point默认暂停当前动作；确需让有时限的防身等动作在模型审阅时继续，可传continue_action为当前active句柄。模型会继续审阅而非等待该动作结束；下一轮须观察并完成输入交接，不能直接启动冲突动作。这不绕过权限、死亡或后续危险事件。"
        + "事件本地消费不占模型唤醒次数，显式decision_point仍受限；源码可跨启动保存，workflow进度只保留本世界。"
        + "run_script.tool_counts记录本段全部工具请求，action_control按pause/resume/cancel分开；trace仅保留最近12次。请求不代表成功。若只查询或暂停而未推进目标，必须补上依据观察结果执行的分支或说明真实阻塞，不能把脚本正常结束当作已经采取恢复行动。";

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
        var toolCounts = new JsonObject();
        int calls = 0;
        bool inTool = false;
        Exception hostFailure = null;
        JsonObject wake = null;
        JsonObject lastFailure = null;
        string Clip(string text, int limit) => text.Length <= limit ? text : text[..limit] + "…";
        JsonObject Report(string error, string detail, JsonNode value = null)
        {
            var result = new JsonObject
            {
                ["ok"] = error == null, ["error"] = error, ["detail"] = detail,
                ["value"] = value, ["tool_calls"] = calls, ["elapsed_ms"] = clock.ElapsedMilliseconds,
                ["tool_counts"] = toolCounts.DeepClone(),
                ["trace"] = new JsonArray(trace.ToArray()), ["logs"] = logs,
                ["failure"] = lastFailure?.DeepClone()
            };
            progress?.End(result);
            if (progress != null) result["workflow"] = progress.Snapshot();
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
                    validate(name, args);
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
                            foreach (string field in new[] { "error", "detail", "outcome", "name_candidates", "requested_name", "targets" })
                                if (toolResult[field] != null) facts[field] = toolResult[field].DeepClone();
                            lastFailure = new JsonObject { ["tool"] = name, ["args"] = args.DeepClone(), ["evidence"] = facts };
                        }
                        else if (lastFailure?["tool"]?.ToString() == name) lastFailure = null;
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
                        const print = value => log(stringify(value));
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
                        if (result.length > 16000) throw new Error('返回结果超过16000字符');
                        return result;
                    })(__gameTool, __gamePrint, __stepEnter, __stepComplete)
                    """;
                var output = engine.Evaluate(wrapper).AsString();
                if (hostFailure != null) throw hostFailure;
                budget.Token.ThrowIfCancellationRequested();
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
                return Report(null, "宿主事件或主动决策点交回模型；未完成step需核对。", new JsonObject
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
