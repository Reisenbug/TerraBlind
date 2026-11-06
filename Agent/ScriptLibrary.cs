using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TerraBlind;

public sealed class ScriptLibrary
{
    static readonly object gate = new();
    readonly string directory;
    public ScriptLibrary(string directory) => this.directory = directory;
    static readonly string[] Templates = { "navigate_observe", "collect_detour", "use_life_crystals", "sample_tile_variants" };

    static JsonObject Template(string name, string detail, string code) => new()
    {
        ["name"] = name, ["found"] = true, ["revision"] = 0, ["template"] = true,
        ["validation"] = "example_not_live_verified", ["detail"] = detail, ["code"] = code
    };

    public static JsonObject Example(string name)
    {
        if (name == "navigate_observe") return Template(name,
            "配置destination世界格坐标，写入所属DAG节点script，以run_script({task_node:id,background:true})启动。宿主立即返回后台句柄；脚本直接执行导航，Agent同时观察和准备后继。step保留实际完成效果。",
            """
            const config = null;
            if(!config) throw new Error('Fill config.destination with a verified destination');
            const result=step('navigation',()=>{
              const r=tool('nav_to',config.destination);
              if(r.error||r.done!==true) throw new Error(JSON.stringify(r));
              return {done:r.done,state:r.state};
            });
            return {status:'arrived',evidence:result,next:'Verify owning DAG node'};
            """);
        if (name == "collect_detour") return Template(name,
            "仅在已判断值得绕路后调用。配置route_handle和gather_args；须在拥有原导航的DAG节点中执行。保存原句柄、暂停、采集；多次运行同一workflow检查进度，采集完成后恢复原导航。不会自行认定所有宝物必拿。",
            """
            const config = null;
            if(!config) throw new Error('Fill config.route_handle and verified gather_args');
            const original=step('pause_route',()=>{
              const a=tool('action_query',{handle:config.route_handle});
              if(a.tool!=='nav_to'||!['running','paused'].includes(a.status)) throw new Error(JSON.stringify(a));
              if(a.status==='running'){
                const p=tool('action_control',{handle:a.handle,command:'pause'});
                if(p.error||p.status!=='paused') throw new Error(JSON.stringify(p));
              }
              return {handle:a.handle};
            });
            const collection=step('collect',()=>{
              const a=tool('action_start',{tool:'gather',args:config.gather_args});
              if(a.error||!a.handle) throw new Error(JSON.stringify(a));
              return {handle:a.handle};
            });
            const result=tool('action_query',{handle:collection.handle});
            if(result.status==='running') return {status:'running',handle:collection.handle,route_handle:original.handle};
            if(result.status!=='completed'||result.result?.outcome!=='enough')
              return {status:'needs_review',collection:result,route_handle:original.handle};
            const resumed=step('resume_route',()=>{
              const r=tool('action_control',{handle:original.handle,command:'resume'});
              if(r.error||r.status!=='running') throw new Error(JSON.stringify(r));
              return {handle:r.handle,status:r.status};
            });
            return {status:'collected_route_resumed',collection:result.result,route:resumed};
            """);
        if (name == "use_life_crystals") return Template(name,
            "配置target_health（例如160）。使用当前背包中的生命水晶，按永久上限核对；没有水晶就返回missing_crystals，不猜采集坐标。先暂停占用输入的动作；使用后由调用者恢复。",
            """
            const config = null;
            if(!config||!Number.isInteger(config.target_health)||config.target_health<100||config.target_health>400)
              throw new Error('Fill config.target_health between 100 and 400');
            let state=tool('get_state',{});
            while(state.permanent_max_health<config.target_health){
              const crystal=state.inventory_slots.find(i=>i.type===29&&i.stack>0);
              if(!crystal) return {status:'missing_crystals',permanent_health:state.permanent_max_health,target:config.target_health};
              const before=state.permanent_max_health;
              step('use_at_health_'+before,()=>{
                const r=tool('use_item',{slot:crystal.slot,x:Math.floor(state.player.pos.x/16),y:Math.floor(state.player.pos.y/16),duration_ticks:30});
                if(r.error||r.verified!==true) throw new Error(JSON.stringify(r));
                return {outcome:r.outcome,verified:r.verified};
              });
              state=tool('get_state',{});
              if(state.permanent_max_health<=before) return {status:'needs_review',reason:'permanent_health_did_not_increase',before,actual:state.permanent_max_health};
            }
            return {status:'target_met',permanent_health:state.permanent_max_health,next:'Verify permanent_health DAG condition'};
            """);
        if (name != "sample_tile_variants") return new JsonObject { ["available"] = new JsonArray(Templates.Select(n => (JsonNode)JsonValue.Create(n)).ToArray()) };
        return new JsonObject { ["name"] = name,
            ["detail"] = "可编辑示例，不会自动执行。填入已暂停或失败的gather句柄、目标物品及总数、查证的方块名、source_area和max_samples；保存后用稳定workflow和event_delivery:script执行。仅试采本区域，未达目标须继续规划；不推断Wiki样式与frame关系。",
            ["code"] = """
                const config = null;
                if(!config) throw new Error('Read current action and fill config before execution');
                const {handle,item_type,count,tile_name,source_area,max_samples}=config;
                if(!Number.isInteger(max_samples)||max_samples<1||max_samples>12) throw new Error('max_samples must be 1..12');
                const inventory=()=>tool('get_state',{}).inventory_slots.filter(i=>i.type===item_type).reduce((n,i)=>n+i.stack,0);
                const readEvents=()=>{
                  const notice=tool('script_events',{wait_ms:0,max_events:16});
                  if(notice.remaining_events) tool('decision_point',{reason:'Review remaining opportunities before sampling',evidence:{handle,notice}});
                  return notice.events.filter(e=>!(['action_finished','target_acquired'].includes(e.kind)&&e.evidence?.handle===handle));
                };
                const original=tool('action_query',{handle});
                if(original.tool!=='gather') throw new Error('Expected original gather handle');
                if(original.status==='running'){
                  const paused=tool('action_control',{handle,command:'pause'});
                  if(paused.error) throw new Error(JSON.stringify(paused));
                }
                const found=tool('find_tiles',{name:tile_name,n:1,source_area});
                const decisions=[];
                for(const variant of found.variants.slice(0,max_samples)){
                  if(inventory()>=count) break;
                  const c=variant.nearest;
                  const result=step('sample:'+handle+':'+c.x+':'+c.y+':'+c.frame_x+':'+c.frame_y,()=>{
                    const events=readEvents();
                    if(events.length) tool('decision_point',{reason:'Review observed events before sampling',evidence:{handle,events}});
                    const assessment=tool('opportunity_assess',{x:c.x,y:c.y});
                    if(!assessment.ok) tool('decision_point',{reason:'Source access unknown; choose approach or skip',evidence:{handle,candidate:c,assessment}});
                    const hazards=assessment.hazards.filter(h=>h.on_direct_corridor||Math.abs(h.x-c.x)<=1&&Math.abs(h.y-c.y)<=2);
                    if(hazards.length) return {decision:'skip',x:c.x,y:c.y,reason:'observed hazard near approach or target',hazards};
                    const before=inventory();
                    const resumed=tool('action_control',{handle,command:'resume',args:{targets:[{item_type,count,tile_names:[tile_name],
                      tile_frame_x:c.frame_x,tile_frame_y:c.frame_y}],source_area:{x:c.x-1,y:c.y-1,width:3,height:3}}});
                    if(resumed.error) throw new Error(JSON.stringify(resumed));
                    let action;
                    do{
                      action=tool('action_query',{handle,wait_ms:1000});
                      const events=readEvents();
                      if(events.length||action.status==='paused') tool('decision_point',{reason:'Review sample interruption',evidence:{handle,events,status:action.status}});
                    }while(action.status==='running');
                    const after=inventory();
                    if(action.status!=='completed'&&!['source_unavailable','source_needs_review'].includes(action.result?.outcome))
                      tool('decision_point',{reason:'Sample failed; inspect before another action',evidence:{handle,result:action.result,before,after}});
                    return {decision:'sample',x:c.x,y:c.y,frame_x:c.frame_x,frame_y:c.frame_y,before,after,gained:after-before,outcome:action.result?.outcome};
                  });
                  decisions.push(result);
                }
                const actual=inventory();
                return {handle,actual,wanted:count,target_met:actual>=count,decisions,next:actual>=count?'verify task condition':'review samples and choose remaining sources'};
                """ };
    }

    string FilePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 80
            || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-'))
            throw new ArgumentException("program名称只允许1..80个英文字母、数字、下划线或短横线。");
        return Path.Combine(directory, name + ".json");
    }

    public JsonObject Get(string name)
    {
        lock (gate)
        {
            string path = FilePath(name);
            if (File.Exists(path)) return JsonNode.Parse(File.ReadAllText(path)).AsObject();
            if (Templates.Contains(name))
            {
                var example = Example(name);
                example["found"] = true; example["revision"] = 0; example["template"] = true;
                example["validation"] = "example_not_live_verified";
                return example;
            }
            return new JsonObject { ["name"] = name, ["revision"] = 0, ["found"] = false };
        }
    }

    public JsonArray List()
    {
        lock (gate)
        {
            var saved = Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension) : Enumerable.Empty<string>();
            return new JsonArray(Templates.Concat(saved).Distinct().OrderBy(name => name)
                .Select(name => { var entry = Get(name); entry.Remove("code"); return (JsonNode)entry; }).ToArray());
        }
    }

    public JsonObject Save(string name, string code, int expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 16000)
            throw new ArgumentException("code必须是1..16000字符的JavaScript函数体。");
        lock (gate)
        {
            var previous = Get(name);
            int revision = previous["revision"].GetValue<int>();
            if (revision != expectedRevision)
                return new JsonObject { ["error"] = "program_revision_conflict", ["name"] = name,
                    ["expected_revision"] = expectedRevision, ["revision"] = revision };
            try
            {
                string wrapped = "(function() {\n" + code + "\n});";
                Jint.Engine.PrepareScript(wrapped);
                var script = new Acornima.Parser().ParseScript(wrapped);
                var function = (Acornima.Ast.FunctionExpression)((Acornima.Ast.ExpressionStatement)script.Body[0]).Expression;
                if (function.Body.Body.Any(node => node is Acornima.Ast.FunctionDeclaration)
                    && function.Body.Body.All(node => node is Acornima.Ast.FunctionDeclaration or Acornima.Ast.EmptyStatement))
                    return new JsonObject { ["error"] = "program_missing_invocation", ["name"] = name, ["revision"] = revision,
                        ["detail"] = "源码仅声明函数，没有执行它们。宿主执行的是函数体，不会自动调用main。",
                        ["hint"] = "在末尾显式调用入口，例如return main();，或直接编写执行语句。原版本未修改。" };
            }
            catch (Jint.ScriptPreparationException failure) when (failure.InnerException is Acornima.ParseErrorException)
            {
                var error = (Acornima.ParseErrorException)failure.InnerException;
                return new JsonObject { ["error"] = "program_invalid_syntax", ["name"] = name, ["revision"] = revision,
                    ["line"] = Math.Max(1, error.LineNumber - 1), ["column"] = error.Column,
                    ["detail"] = error.Message, ["hint"] = "使用同步JavaScript函数体和tool()；不支持顶层await。原版本未修改。" };
            }
            var saved = new JsonObject { ["name"] = name, ["found"] = true, ["revision"] = revision + 1, ["code"] = code };
            Directory.CreateDirectory(directory);
            string path = FilePath(name), temporary = path + ".tmp";
            File.WriteAllText(temporary, saved.ToJsonString());
            File.Move(temporary, path, true);
            return saved;
        }
    }

    public JsonObject Patch(string name, string oldText, string newText, int expectedRevision)
    {
        if (string.IsNullOrEmpty(oldText) || newText == null) throw new ArgumentException("patch需要非空old_text和new_text。");
        lock (gate)
        {
            var previous = Get(name);
            int revision = previous["revision"].GetValue<int>();
            if (revision != expectedRevision)
                return new JsonObject { ["error"] = "program_revision_conflict", ["name"] = name, ["revision"] = revision };
            string code = previous["code"]?.GetValue<string>();
            int start = code?.IndexOf(oldText, StringComparison.Ordinal) ?? -1;
            if (start < 0 || code.IndexOf(oldText, start + 1, StringComparison.Ordinal) >= 0)
                return new JsonObject { ["error"] = "program_patch_not_unique", ["name"] = name, ["revision"] = revision };
            return Save(name, code[..start] + newText + code[(start + oldText.Length)..], expectedRevision);
        }
    }
}
