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
    internal static string FunctionSource(string code) => "(function(input) {\n" + code + "\n});";
    static readonly string[] Templates = { "navigate_observe", "collect_detour", "use_life_crystals", "sample_tile_variants", "observe_boss_control", "recover_pressure_plate", "recover_navigation", "safety_reactions", "combat_approach", "inventory_space", "gather_route", "reactive_travel", "task_reactions", "decision_travel", "decision_reactions", "joint_reactions", "joint_combat", "tactical_reactions", "travel_maneuver", "travel_intent" };
    static readonly string[] BuiltIns = { "recover_navigation", "inventory_space", "travel_intent", "joint_combat", "safety_reactions", "task_reactions", "combat_approach", "gather_route", "use_life_crystals" };

    static JsonObject Template(string name, string detail, string code) => new()
    {
        ["name"] = name, ["found"] = true, ["revision"] = 0, ["template"] = true,
        ["callable"] = BuiltIns.Contains(name),
        ["validation"] = "example_not_live_verified", ["detail"] = detail, ["code"] = code
    };

    public static JsonObject Example(string name)
    {
        if (name == "combat_approach") return Template(name,
            "选择有射界的交战站位。input含npc、npc_type、可选min_distance_tiles（默认4格）。复用npc_combat_observe的原版射程、当前武器许可及支撑测量，检查落脚危险，导航到站位后刷新目标及射界。一次最多移动到一个站位；返回ready才可继续攻击或恢复原采集。可修改并保存同名技能。",
            """
            const query={npc:input.npc,npc_type:input.npc_type};
            const scene=tool('npc_combat_observe',query);
            if(scene.error) return {status:'needs_review',reason:scene.error};
            if(scene.current_weapon_types?.length) return {status:'ready',weapon_types:scene.current_weapon_types};
            const positions=scene.terrain.stance_probes.filter(p=>p.body_fits&&p.weapon_types?.length
              &&p.target_distance_tiles>=(input.min_distance_tiles??4))
              .sort((a,b)=>a.player_distance_tiles-b.player_distance_tiles);
            for(const p of positions){
              const x=p.support_x,y=p.support_y-1;
              const safety=tool('opportunity_assess',{x,y});
              if(!safety.ok||safety.hazards.some(h=>Math.abs(h.x-x)<=2&&Math.abs(h.y-y)<=3)) continue;
              const moved=tool('nav_to',{x,y,mode:'stand'});
              if(!moved.done) return {status:'needs_review',reason:'stance_unreached',x,y,movement:moved};
              const current=tool('npc_combat_observe',query);
              return {status:current.current_weapon_types?.length?'ready':'needs_review',
                reason:current.error??(current.current_weapon_types?.length?null:'target_or_shot_changed'),
                npc:input.npc,npc_type:input.npc_type,x,y,weapon_types:current.current_weapon_types??[]};
            }
            return {status:'needs_review',reason:'no_observed_firing_position',npc:input.npc,npc_type:input.npc_type};
            """);
        if (name == "safety_reactions") return Template(name,
            "默认任务反应：近敌时用原生推演候选联合选择短程走位与武器，覆盖导航和等待模型期间。复核并拆除可达压力板与近身接触伤害物，验证后续接。近期受击且血量下降时，Jev结合当前任务、冷却、敌人与地形判断是否用已有回城道具脱险。遵守权限；可修改此JS并用task_policy替换。",
            """
            let survivalRequest=null,movementRequest=null,movementProgram=null,idleDestination=null;
            const movementContext='survival-combat:'+Date.now();
            on('tick',event=>{
              const c=tool('combat_assess',{radius:15}),p=c.player;
              let destination=event.execution?.tool==='nav_to'?event.execution.args:null;
              const idle=event.execution?.tool==='idle';
              if(destination) idleDestination=null;
              if(!c.boss_present&&(destination||idle)&&(c.targets?.some(n=>n.contact_damage>0&&n.distance_tiles<12)||c.projectiles?.length)){
                if(idle) destination=idleDestination??={x:Math.floor(p.pos.x/16),y:Math.floor(p.pos.y/16)};
                movementProgram??=tool('script_program',{operation:'get',name:'joint_combat'});
                const goal=tool('get_state',{fields:['codex_goal']}).codex_goal;
                const intent=skill('joint_combat',movementProgram.revision,{context:movementContext+':'+(idle?'idle:':'route:')+destination.x+':'+destination.y,
                  destination,instruction:goal?.native?.objective??'Continue the current task while staying alive.',last_request:movementRequest});
                movementRequest=intent.request_id??movementRequest;
              }
              if(c.boss_present||p.hp>p.max_hp*.75||!c.recent_damage||c.tick-c.recent_damage.tick>120) return {handled:true};
              const s=tool('get_state',{fields:['player','inventory_slots','healing_items','codex_goal']});
              const recall=s.inventory_slots.find(i=>[50,2350,3199].includes(i.type)&&i.stack>0);
              if(!recall||s.player.hp>s.player.max_hp*.75) return {handled:true};
              const r=tool('jev_judge',{channel:'survival',context:'task-survival',interval_ms:500,max_age_ms:1000,
                state:{objective:s.codex_goal?.native?.objective,elapsed_seconds:s.codex_goal?.host_wall_elapsed_seconds,
                  combat:c,player:s.player,healing_items:s.healing_items,terrain:tool('inspect_terrain',{radius:8}),recall,
                  coordinates:'Player positions are world pixels; enemy x/y and terrain cells are world tiles (16 pixels). Terrain rows start at terrain.origin; x right, y down.'},questions:{response:{type:'choice',
                  instructions:'Should this player continue the current task or recall to survive the immediate threats? Respect the objective and death constraints. Recall uses the shown item and returns to spawn; the task resumes from there. Consider health, potion cooldown, incoming damage, terrain and escape options.',
                  criteria:{continue:'Continue the task with current defense and movement.',recall:'Use the available recall item now to escape.'}}}});
              if(r.status!=='ready'||r.request_id===survivalRequest) return {handled:true};
              survivalRequest=r.request_id;
              print({decision:'survival',request:r.request_id,choice:r.result.answers.response.choice,model_ms:r.result.elapsed_ms});
              if(r.result.answers.response.choice==='recall') tool('use_item',{slot:recall.slot,
                x:Math.floor(s.player.pos.x/16),y:Math.floor(s.player.pos.y/16),duration_ticks:120});
              return {handled:true};
            });
            on('action_failed',event=>{
              const nav=event.result.last_result?.result?.navigation??event.result.navigation??event.result;
              if(!['navigation_near_trap','navigation_contact_escape'].includes(nav.error)) return {retry:false};
              const program=tool('script_program',{operation:'get',name:'recover_navigation'});
              return skill('recover_navigation',program.revision,{error:nav.error,tile:nav.contact_escape?.predicted_tile,
                traps:nav.trap_tiles,allow_trigger_removal:true});
            });
            return {registered:true};
            """);
        if (name == "inventory_space") return Template(name,
            "内置背包腾位技能，可直接调用revision=0。input含protected_types、trash_types和drop_types。保留收藏、工具和受保护物品，仅按当前策略回收或丢出，操作前核对物品快照，返回是否实际有空位。保存同名修订可替换实现，调用时取当前revision。",
            """
            const bag=tool('inventory_review');
            if(bag.empty_slots.length) return true;
            for(const item of bag.inventory){
              if(item.favorited||item.pick||item.axe||item.hammer||(input.protected_types??[]).includes(item.type)) continue;
              const expected={type:item.type,prefix:item.prefix,stack:item.stack};
              let result;
              if((input.trash_types??[]).includes(item.type)
                &&(bag.trash.type===0||(input.trash_types??[]).includes(bag.trash.type)))
                result=tool('inventory_trash',{slot:item.slot,expected,expected_trash:bag.trash});
              else if((input.drop_types??[]).includes(item.type)) result=tool('inventory_drop',{slot:item.slot,expected});
              else continue;
              if(!result.error) return tool('inventory_review').empty_slots.length>0;
            }
            return false;
            """);
        if (name == "recover_navigation") return Template(name,
            "内置局部恢复技能，可直接调用revision=0。input含error、tile或traps、allow_trigger_removal。核对近身非实心接触伤害物或获准拆除的压力板，使用实际镐子并验证方块变化；retry只在修复验证后返回true。保存同名修订可替换实现，调用时取当前revision。",
            """
            const contact=input.error==='navigation_contact_escape';
            const targets=contact?(input.tile?[input.tile]:[]):input.traps??[];
            if(!targets.length||!contact&&(input.error!=='navigation_near_trap'||!input.allow_trigger_removal
              ||targets.some(t=>t.kind!=='pressure_plate'))) return {retry:false,reason:'uncovered_failure'};
            const pick=tool('inventory_review').inventory.filter(i=>i.pick>0).sort((a,b)=>b.pick-a.pick)[0];
            if(!pick) return {retry:false,reason:'no_pickaxe'};
            const same=(a,b)=>['x','y','type',...(contact?[]:['frame_x','frame_y'])].every(k=>a[k]===b[k]);
            for(const target of targets){
              const scene=tool('inspect_terrain',{radius:8});
              const observed=(contact?scene.near_body_tiles:scene.trap_tiles).find(t=>same(t,target));
              if(!observed){
                const checked=tool('opportunity_assess',{x:target.x,y:target.y});
                if(checked.ok&&(!checked.target.has_tile||checked.target.tile_type!==target.type)) continue;
                return {retry:false,reason:'hazard_evidence_changed'};
              }
              if(contact&&(!observed.touch_damage||observed.solid||observed.falling))
                return {retry:false,reason:'hazard_evidence_changed'};
              const result=tool('use_item',{slot:pick.slot,x:target.x,y:target.y,strict:true,duration_ticks:16});
              if(result.error) return {retry:false,reason:result.error};
              const checked=tool('opportunity_assess',{x:target.x,y:target.y});
              if(!checked.ok||checked.target.has_tile&&checked.target.tile_type===target.type)
                return {retry:false,reason:'hazard_not_removed'};
            }
            return {retry:true,removed:targets.length};
            """);
        if (name == "travel_intent") return Template(name,
            "行进战术意图实验。input含context、destination、instruction、baseline、last_request。后台判断continue/withdraw/cover，nav_intent让原导航在下一次选边读取当前意图；等待时继续原导航。返回request_id供调用者续接。",
            """
            if(!Number.isInteger(input.destination?.x)||!Number.isInteger(input.destination?.y)) return {status:'no_navigation_destination'};
            const c=tool('combat_assess',{radius:25,terrain_radius:8});
            const target=c.targets.find(n=>n.attackable&&n.contact_damage>0&&n.distance_tiles<12);
            if(c.boss_present||!target){tool('nav_intent',{mode:'continue'});return {status:'no_threat',request_id:input.last_request};}
            const state={instruction:input.instruction,destination:input.destination,target:{who:target.who,type:target.type},combat:c,
              coordinates:'Positions and distances are tiles except player.pos/size_pixels and all velocities, which are pixels or pixels per tick.',
              execution:'The existing navigation chooses a physically supported edge using the intent. Attacks continue. Withdrawal ends at 10 tiles separation. Cover tests terrain line of sight; enemy attacks may penetrate terrain.'};
            const r=input.baseline?{status:'ready',request_id:c.tick,input_state:state,result:{answers:{response:{choice:
              target.distance_tiles<6?'withdraw':'continue'}}}}:tool('jev_judge',{channel:'travel_intent',context:input.context,
              interval_ms:1000,max_age_ms:2000,state,questions:{response:{type:'choice',instructions:
                'Which navigation intent best supports state.instruction while handling the current nearby threat? Consider health, enemy movement, attack range and the terrain cells. combat.navigation_options reports the last inspected candidate set at its recorded tick for its target. changes_edge and score_gain describe whether each intent found a better edge; use this feedback when the target matches. Choose continue when the inspected alternatives offer no improvement.',
                criteria:{continue:'Keep following the destination while attacking.',
                  withdraw:'Temporarily increase separation from the selected nearby enemy while attacking; then continue the journey.',
                  cover:'Prefer a reachable edge with terrain across the selected enemy sight line, while continuing toward the destination.'}}}});
            if(r.status==='pending'||r.request_id===input.last_request) return {status:r.status,request_id:input.last_request};
            if(r.status==='failed'){print({decision:'travel_intent',error:r.result.error});return {request_id:r.request_id};}
            const observed=r.input_state.target,choice=r.result.answers.response.choice;
            if(!c.targets.some(n=>n.who===observed.who&&n.type===observed.type&&n.contact_damage>0)) return {request_id:r.request_id,status:'target_changed'};
            const applied=tool('nav_intent',{mode:choice,target:observed.who,destination:input.destination});
            print({decision:'travel_intent',request:r.request_id,choice,model_ms:r.result.elapsed_ms,
              request_age_ms:r.request_age_ms,ready_wait_ms:r.ready_wait_ms,result:applied});
            return {request_id:r.request_id,status:applied.error??'published',result:applied};
            """);
        if (name == "travel_maneuver") return Template(name,
            "赶路移动短策略。input含context、destination、instruction、weapon_order，可传combat/terrain、baseline、last_request、intent、last_forecast。意图保留2秒，匹配最新带共同路线前缀的原生候选；接管时核验人物、地形及分岔期限。比较继续导航、加快穿越、拉开敌距、进入方块遮挡；比较均为相同帧段。pending时继续原动作。返回request_id、intent、last_forecast、applied供调用者续接。",
            """
            const observed=input.combat??tool('combat_assess',{radius:25});
            if(observed.boss_present||!observed.targets.some(n=>n.contact_damage>0)&&!observed.projectiles.length)
              return {status:'no_threat'};
            const memo={request_id:input.last_request,intent:input.intent,last_forecast:input.last_forecast,applied:false};
            const c=tool('combat_assess',{radius:25,forecast:true,route_handoff:true,context:input.context,destination:input.destination}),f=c.forecast;
            if(f.status!=='ready') return {status:f.status,reason:f.reason};
            if(f.prefix_source==='idle_inputs') return {...memo,status:'awaiting_route_inputs'};
            const summarize=m=>{
              const ts=m.exposure.targets;
              const start=ts.length?Math.min(...ts.map(t=>t.start_distance_tiles)):null;
              const end=ts.length?Math.min(...ts.map(t=>t.end_distance_tiles)):null;
              const plans=f.plans.filter(p=>p.movement===m.id&&p.movement_available);
              const usable=plans.some(p=>p.target_checks.some(t=>t.line_or_reach_passed))
                ?plans.filter(p=>p.target_checks.some(t=>t.line_or_reach_passed)):plans;
              const rank=p=>{const i=(input.weapon_order??[]).indexOf(p.weapon_type);return i<0?999:i;};
              const plan=usable.sort((a,b)=>rank(a)-rank(b))[0];
              return {movement:m.id,plan_id:plan?.id,weapon_type:plan?.weapon_type,safe_landing:m.safe_landing,
                comparison_frames:m.comparison_frames,comparison_complete:m.comparison_complete,
                route_progress_tiles:m.route_progress_tiles,nearest_enemy_start_tiles:start,nearest_enemy_end_tiles:end,
                enemy_separation_delta_tiles:start==null?null:end-start,exposure:m.exposure,
                linear_threat_overlap:m.linear_threat_overlap,path_samples_pixels:m.path_samples_pixels};
            };
            const baseline=summarize(f.movements.find(m=>m.id==='follow_route'));
            const candidates=f.movements.filter(m=>m.id!=='follow_route'&&m.safe_landing&&m.comparison_complete)
              .map(summarize).filter(m=>m.plan_id).map(m=>({...m,cover_gain:m.exposure.targets.reduce((n,t)=>{
                const original=baseline.exposure.targets.find(b=>b.who===t.who);
                return n+(original?.end_terrain_line_clear&&!t.end_terrain_line_clear?1:0)
                  -(!original?.end_terrain_line_clear&&t.end_terrain_line_clear?1:0);
              },0)}));
            const best=(list,score)=>list.sort((a,b)=>score(b)-score(a))[0]??null;
            const options={
              advance:best(candidates.filter(m=>m.route_progress_tiles>baseline.route_progress_tiles+0.5),m=>m.route_progress_tiles),
              withdraw:best(candidates.filter(m=>m.nearest_enemy_end_tiles!=null
                &&m.nearest_enemy_end_tiles>baseline.nearest_enemy_end_tiles+0.5),m=>m.nearest_enemy_end_tiles),
              cover:best(candidates.filter(m=>m.cover_gain>0),m=>m.cover_gain*100+m.route_progress_tiles)};
            if(!Object.values(options).some(Boolean)) return {status:'no_better_maneuver',forecast_id:f.id};
            const applyIntent=()=>{
              const intent=memo.intent,choice=intent.choice,plan=options[choice],started=Date.now();
              if(choice!=='continue'&&(!plan||memo.last_forecast===f.id)) return {...memo,status:'awaiting_current_maneuver'};
              let result={outcome:'continue'};
              if(choice!=='continue'){
                memo.last_forecast=f.id;
                result=tool('act',{steps:[{keys:['right'],until:{frames:1}}],
                  combat_candidate:{context:input.context,forecast_id:f.id,plan_id:plan.plan_id}});
              }
              print({decision:'maneuver',request:intent.request,choice,movement:plan?.movement,weapon_type:plan?.weapon_type,
                decision_forecast_id:intent.decision_forecast_id,current_forecast_id:f.id,execution_forecast_id:result.execution_forecast_id,
                model_ms:intent.model_ms,request_age_ms:intent.request_age_ms,ready_wait_ms:intent.ready_wait_ms,
                elapsed_ms:Date.now()-started,action:result.error??result.outcome,error:result.error,reason:result.reason,
                age_frames:result.age_frames,handoff_frames:result.handoff_frames,
                observed:result.observed,expected:result.expected,forecast_abort:result.forecast_abort??null});
              memo.applied=!result.error&&choice!=='continue';
              if(!result.error) memo.intent=null;
              return {...memo,error:result.error};
            };
            if(memo.intent&&Date.now()<=memo.intent.expires&&options[memo.intent.choice]&&memo.last_forecast!==f.id)
              return applyIntent();
            const state={instruction:input.instruction,destination:input.destination,combat:c,terrain:tool('inspect_terrain',{radius:8}),
              sample_tick:f.sample_tick,current_tick:c.tick,forecast_age_frames:f.age_frames,
              sample_player_pixels:f.sample_player,sample_targets:f.sample_targets,sample_projectiles:f.sample_projectiles,
              baseline,options,comparison_scope:f.scope,forecast_id:f.id,handoff_frames:f.handoff_frames,
              coordinates:'Player positions/path samples are pixels; destination and distances are tiles. Velocities are pixels per tick.'};
            delete state.combat.forecast;
            let r;
            if(input.baseline){
              const imminent=baseline.linear_threat_overlap.npc_object_frames+baseline.linear_threat_overlap.projectile_object_frames>0;
              const choice=imminent&&options.cover?'cover':imminent&&options.withdraw?'withdraw':options.advance?'advance':'continue';
              r={status:'ready',request_id:f.id,input_state:state,result:{answers:{response:{choice}}}};
            }else r=tool('jev_judge',{channel:'maneuver',context:input.context,interval_ms:2000,max_age_ms:2000,state,
              questions:{response:{type:'choice',instructions:'Which available short strategy best fits this journey and the observed combat situation?',
                criteria:{continue:'Keep the current navigation and defend while moving.',
                  advance:'Take the available faster progress maneuver, then resume navigation.',
                  withdraw:'Take the available maneuver that increases separation from enemies, then resume navigation.',
                  cover:'Take the available maneuver that puts terrain across more enemy sight lines, then resume navigation. Attack penetration depends on enemy abilities.'}}}});
            if(r.status==='failed'&&r.request_id!==input.last_request){
              print({decision:'maneuver',request:r.request_id,error:r.result.error});
              return {...memo,request_id:r.request_id};
            }
            const fresh=r.status==='ready'&&r.request_id!==input.last_request;
            if(fresh){
              memo.request_id=r.request_id;
              memo.intent={choice:r.result.answers.response.choice,request:r.request_id,expires:Date.now()+2000,
                decision_forecast_id:r.input_state.forecast_id,model_ms:r.result.elapsed_ms,
                request_age_ms:r.request_age_ms,ready_wait_ms:r.ready_wait_ms};
            }
            if(!memo.intent||Date.now()>memo.intent.expires) return {...memo,intent:null,status:r.status};
            return applyIntent();
            """);
        if (name == "tactical_reactions") return Template(name,
            "赶路短策略实验。input含instruction、weapon_order及可选baseline；注册tick/action_failed。Jev后台判断当前阻力：继续赶路、最多48帧处理近处威胁、拆除近身非实心接触伤害物。提供maneuver_skill_revision时，移动交给保存的travel_maneuver技能。复核当前目标后交接，保留原导航；print记录策略、耗时、血量和位移。baseline使用相同动作做规则对照。",
            """
            const context='tactics:'+Date.now(),history=[];
            let used=null,serial=0,maneuver={};
            const scene=()=>{
              const combat=tool('combat_assess',{radius:25,terrain_radius:8}),terrain=combat.terrain;
              delete combat.terrain;
              const obstacle=terrain.near_body_tiles.find(t=>t.touch_damage>0&&!t.solid&&!t.falling
                &&!terrain.trap_tiles.some(h=>h.x===t.x&&h.y===t.y));
              const bounds=terrain.player_bounds;
              const grounded=combat.player.velocity.y===0&&terrain.near_body_tiles.some(t=>t.solid
                &&t.y===bounds.bottom+1&&t.x>=bounds.left&&t.x<=bounds.right);
              const threats=combat.targets.filter(n=>n.attackable&&n.contact_damage>0&&n.distance_tiles<=8
                &&n.weapons.some(w=>w.has_ammo&&w.mana_available&&w.line_or_reach_passed));
              return {combat,terrain,obstacle,grounded,threats};
            };
            const run=event=>{
              const destination=event.execution?.tool==='nav_to'?event.execution.args:event.tool==='nav_to'?event.args:null;
              if(!destination) return {handled:true};
              const before=scene();
              if(before.combat.boss_present) return {handled:true};
              if(input.intent_skill_revision!=null&&!before.obstacle){
                const currentContext=context+':'+destination.x+':'+destination.y;
                if(maneuver.context!==currentContext) maneuver={context:currentContext};
                const r=skill('travel_intent',input.intent_skill_revision,{context:currentContext,destination,
                  instruction:input.instruction,baseline:input.baseline,last_request:maneuver.request_id});
                maneuver={...r,context:currentContext};
                return {handled:true};
              }
              if(input.maneuver_skill_revision!=null&&!before.obstacle){
                const currentContext=context+':'+destination.x+':'+destination.y;
                if(maneuver.context!==currentContext) maneuver={context:currentContext};
                const r=skill('travel_maneuver',input.maneuver_skill_revision,{context:currentContext,
                  destination,instruction:input.instruction,weapon_order:input.weapon_order,baseline:input.baseline,
                  combat:{boss_present:before.combat.boss_present,targets:before.combat.targets.map(n=>({contact_damage:n.contact_damage})),
                    projectiles:before.combat.projectiles.map(p=>({damage:p.damage}))},last_request:maneuver.request_id,
                  intent:maneuver.intent,last_forecast:maneuver.last_forecast});
                if(r.request_id!=null) maneuver={...r,context:currentContext};
                return {handled:true};
              }
              if(!before.obstacle&&!before.threats.length) return {handled:true};
              const state={instruction:input.instruction,destination,combat:before.combat,terrain:before.terrain,
                coordinates:'player pos/size and all velocities: pixels or pixels per tick; destination, terrain and enemy/projectile x/y: tiles (16 pixels each)',
                available:{none:true,nearby_enemy:before.grounded&&before.threats.length>0,contact_terrain:!!before.obstacle},
                enemy:before.threats[0]??null,obstacle:before.obstacle??null,history};
              let r;
              if(input.baseline) r={status:'ready',request_id:++serial,input_state:state,result:{answers:{response:{choice:
                state.available.contact_terrain?'contact_terrain':state.available.nearby_enemy?'nearby_enemy':'none'}}}};
              else r=tool('jev_judge',{channel:'tactics',context:context+':'+destination.x+':'+destination.y,
                interval_ms:2000,max_age_ms:2000,state,questions:{response:{type:'choice',instructions:
                  'Which available nearby constraint most interferes with carrying out state.instruction and reaching the destination? Consider the cost of interrupting travel and the recent outcomes. Attacks continue during travel. An enemy intervention lasts at most 48 ticks, then travel resumes.',
                  criteria:{none:'Continue travelling while defending.',nearby_enemy:'Briefly address the observed nearby reachable enemy from supported ground, with existing attack and evasion controls.',
                    contact_terrain:'Remove the observed nearby non-solid contact-damage obstacle, then continue travelling.'}}}});
              if(r.status==='pending'||r.request_id===used) return {handled:true};
              used=r.request_id;
              const started=Date.now(),choice=r.result?.answers?.response?.choice;
              const record={decision:'tactics',request:r.request_id,choice,request_age_ms:r.request_age_ms,
                model_ms:r.result?.elapsed_ms,ready_wait_ms:r.ready_wait_ms,hp_before:before.combat.player.hp};
              let result={outcome:'continue'},retry=false;
              if(r.status==='failed') result={error:r.result.error};
              else if(choice==='contact_terrain'){
                const observed=r.input_state.obstacle,current=scene().obstacle;
                if(!observed||!current||['x','y','type'].some(k=>observed[k]!==current[k])) result={error:'obstacle_changed'};
                else {
                  const pick=tool('inventory_review').inventory.filter(w=>w.pick>0).sort((a,b)=>b.pick-a.pick)[0];
                  result=pick?tool('use_item',{slot:pick.slot,x:current.x,y:current.y,strict:true,duration_ticks:16}):{error:'no_pickaxe'};
                  if(pick&&!result.error){
                    const checked=tool('opportunity_assess',{x:current.x,y:current.y});
                    result=checked.ok&&(!checked.target.has_tile||checked.target.tile_type!==current.type)
                      ?{...result,outcome:'removed'}:{...result,error:'contact_not_removed'};
                  }
                  retry=result.outcome==='removed'&&!result.error;
                }
              }else if(choice==='nearby_enemy'){
                const current=scene(),enemy=current.threats.find(n=>n.who===r.input_state.enemy?.who);
                if(!current.grounded||!enemy) result={error:'enemy_or_support_changed'};
                else {
                  const weapons=enemy.weapons.filter(w=>w.has_ammo&&w.mana_available&&w.line_or_reach_passed);
                  const weapon=(input.weapon_order??[]).map(type=>weapons.find(w=>w.type===type)).find(Boolean)??weapons[0];
                  result=tool('set_combat',{weapon_type:weapon.type});
                  for(let i=0;i<3&&!result.error;i++){
                    const now=scene();
                    if(!now.grounded||!now.threats.some(n=>n.who===enemy.who)) break;
                    result=tool('act',{steps:[{keys:[],until:{frames:16}}]});
                  }
                }
              }
              const after=tool('combat_assess',{radius:25});
              Object.assign(record,{elapsed_ms:Date.now()-started,outcome:result.outcome,error:result.error,
                hp_after:after.player.hp,dx_pixels:after.player.pos.x-before.combat.player.pos.x,
                dy_pixels:after.player.pos.y-before.combat.player.pos.y,retry});
              print(record);history.push(record);if(history.length>4) history.shift();
              return {handled:true,retry};
            };
            on('tick',run);
            on('action_failed',event=>{
              if(event.tool!=='nav_to') return {handled:false};
              if(event.result.error==='navigation_contact_escape') return run(event);
              const traps=event.result.trap_tiles;
              if(event.result.error!=='navigation_near_trap'||!input.allow_trigger_removal
                ||!traps?.length||traps.some(t=>t.kind!=='pressure_plate')) return {handled:false};
              const same=(a,b)=>['x','y','type','frame_x','frame_y'].every(k=>a[k]===b[k]);
              const pick=tool('inventory_review').inventory.filter(w=>w.pick>0).sort((a,b)=>b.pick-a.pick)[0];
              if(!pick) return {handled:false};
              for(const trap of traps){
                if(!tool('inspect_terrain').trap_tiles.some(t=>same(t,trap))) return {handled:false};
                const r=tool('use_item',{slot:pick.slot,x:trap.x,y:trap.y,strict:true});
                if(r.outcome!=='removed'||tool('inspect_terrain').trap_tiles.some(t=>same(t,trap))) return {handled:false};
              }
              return {handled:true,retry:true};
            });
            return {registered:true};
            """);
        if (name == "joint_combat") return Template(name,
            "内置共享战斗决策，可直接调用revision=0；joint_reactions传combat_skill_revision。input含context、destination、instruction、last_request及interval_ms/max_age_ms。候选后台计算，pending期间原动作继续。返回request_id和applied供调用者去重；宿主执行前核对轨迹，执行期间检测偏离。",
            """
            const c=tool('combat_assess',{radius:25,forecast:true,context:input.context,destination:input.destination});
            const f=c.forecast;
            if(c.boss_present||f.status!=='ready'||f.plans.length<2) return {status:f.status,reason:f.reason};
            if(!c.targets.some(n=>n.contact_damage>0)&&!c.projectiles.length) return {status:'no_threat'};
            const criteria=Object.fromEntries(f.plans.map(p=>[p.id,{movement:p.movement,weapon_type:p.weapon_type}]));
            const movements=f.movements.map(({engine_ms,roundtrip_ms,queue_ms,...m})=>m);
            const weapons=[...new Map(c.targets.flatMap(n=>n.weapons).map(({line_or_reach_passed,...w})=>[w.type,w])).values()];
            const r=tool('jev_judge',{channel:'combat',context:input.context,interval_ms:input.interval_ms??750,
              max_age_ms:input.max_age_ms??2000,state:{instruction:input.instruction??'',destination:input.destination,
                current_tick:c.tick,current_player_pixels:c.player,targets:c.targets.map(({weapons,...n})=>n),projectiles:c.projectiles,
                sample_targets:f.sample_targets,sample_projectiles:f.sample_projectiles,
                weapons,recent_damage:c.recent_damage,
                forecast_id:f.id,sample_tick:f.sample_tick,age_frames:f.age_frames,sample_player_pixels:f.sample_player,
                comparison_frames:f.comparison_frames,
                scope:f.scope,known_route_frames:f.known_route_frames,prefix_source:f.prefix_source,walk_intent:f.walk_intent,
                movements,plans:f.plans},
              questions:{response:{type:'choice',instructions:
                'Which available movement and weapon combination best avoids incoming damage while carrying out state.instruction?',criteria}}});
            if(r.status==='pending'||r.request_id===input.last_request) return {status:r.status};
            const record={decision:'combat',request:r.request_id,request_age_ms:r.request_age_ms,ready_wait_ms:r.ready_wait_ms,model_ms:r.result.elapsed_ms};
            if(r.status==='failed'){
              print({...record,error:r.result.error});return {request_id:r.request_id,error:r.result.error};
            }
            const plan=r.input_state.plans.find(p=>p.id===r.result.answers.response.choice);
            let applied;
            if(!plan?.movement_available) applied={error:'landing_unresolved'};
            else if(plan.movement==='follow_route') applied=plan.weapon_type>0?tool('set_combat',{weapon_type:plan.weapon_type}):{};
            else applied=tool('act',{steps:[{keys:['right'],until:{frames:1}}],combat_candidate:{
              context:input.context,forecast_id:r.input_state.forecast_id,plan_id:plan.id}});
            print({...record,choice:r.result.answers.response.choice,action:applied.error??applied.outcome??'follow_route',
              reason:applied.reason,decision_forecast_id:applied.decision_forecast_id,execution_forecast_id:applied.execution_forecast_id});
            return {request_id:r.request_id,applied:!applied.error,weapon_type:plan?.weapon_type,error:applied.error??null};
            """);
        if (name is "reactive_travel" or "task_reactions" or "decision_travel" or "decision_reactions" or "joint_reactions") return Template(name,
            "task_reactions和decision_reactions注册整趟任务策略，目的地从event.execution的实际导航读取；travel版本另需destination并执行单段导航。input包含resources（item_type/count/tile_type/tile_name及可选frame_x/frame_y）、weapon_order、trash_types/drop_types、max_detour_tiles、health_reserve、contact_pass_types、allow_trigger_removal。decision版本默认启用weapon/movement/opportunity三维后台判断，decisions数组可单独启停作对照；instruction给当前意图，decision_interval_ms、decision_max_age_ms和decision_max_displacement_tiles调整更新及应用条件。判断期间原动作继续，只有执行具体干预才暂停并恢复。print记录每次采用的request_id、选择、耗时和动作结果。资源来源提前查证，策略源码可随现场修改。",
            "const policyOnly=" + (name is "task_reactions" or "decision_reactions" or "joint_reactions" ? "true" : "false") + ";\n"
            + (name == "task_reactions" ? "const safety=tool('script_program',{operation:'get',name:'safety_reactions'});skill('safety_reactions',safety.revision);\n" : "")
            + "const defaultDecisions=" + (name == "joint_reactions" ? "['combat','opportunity']" : name.StartsWith("decision_") ? "['weapon','movement','opportunity']" : "[]") + ";\n" + """
            const config=typeof input==='undefined'?null:input;
            if(!config||!policyOnly&&!config.destination||!Array.isArray(config.resources)||!Array.isArray(config.weapon_order)
                ||!Number.isFinite(config.max_detour_tiles)||!Number.isFinite(config.health_reserve))
              throw new Error('Fill destination, resources, weapon_order, max_detour_tiles and health_reserve');
            const handled=()=>({handled:true});
            const state=fields=>tool('get_state',{fields});
            const originalCombat=state(['agent']).agent;
            const dimensions=new Set(config.decisions??defaultDecisions);
            const decisionContext=String(Date.now());
            const queuedOpportunities=defaultDecisions.includes('opportunity')||dimensions.has('opportunity');
            const usedDecisions={},opportunities=new Map();
            let approvedOpportunity=false,activeOpportunity=null;
            const judge=(dimension,context,scene,instructions,criteria)=>{
              const r=tool('jev_judge',{channel:dimension,context,state:scene,
                interval_ms:config.decision_interval_ms??750,max_age_ms:config.decision_max_age_ms??2000,
                questions:{response:{type:'choice',instructions,criteria}}});
              if(r.status==='pending'||usedDecisions[dimension]===r.request_id) return null;
              if(r.status==='failed'){
                usedDecisions[dimension]=r.request_id;
                print({decision:dimension,request:r.request_id,error:r.result.error});
                return null;
              }
              return r;
            };
            const accept=(dimension,r,action)=>{
              usedDecisions[dimension]=r.request_id;
              print({decision:dimension,request:r.request_id,choice:r.result.answers.response.choice,
                request_age_ms:r.request_age_ms,model_ms:r.result.elapsed_ms,action});
            };
            const count=type=>state(['inventory_counts']).inventory_counts[type]??0;
            const protectedTypes=new Set([...config.resources.map(r=>r.item_type),...config.weapon_order,...(config.protected_types??[])]);
            const makeSpace=()=>{
              const program=tool('script_program',{operation:'get',name:'inventory_space'});
              return skill('inventory_space',program.revision,{protected_types:[...protectedTypes],
                trash_types:config.trash_types,drop_types:config.drop_types});
            };
            const worthwhile=(point,event)=>{
              const destination=event.execution?.tool==='nav_to'?event.execution.args:config.destination;
              const a=tool('opportunity_assess',{x:point.x,y:point.y,...(destination?{destination}:{})});
              if(!a.ok||a.approach_direct_distance>config.max_detour_tiles
                  ||destination&&(a.route_relation?.extra_direct_distance??Infinity)>config.max_detour_tiles) return false;
              return !blockedDetour(a,point)&&(approvedOpportunity||!a.hazards.some(h=>h.on_direct_corridor&&['lava','falling_block','trap_tile'].includes(h.kind)));
            };
            const blockedDetour=(assessment,point)=>assessment.hazards?.find(h=>h.kind==='trap_tile'
              &&Math.max(Math.abs(h.x-point.x),Math.abs(h.y-point.y))<=4);
            const collect=(resource,point,drop,event)=>{
              const actual=count(resource.item_type);
              if(actual>=resource.count) return handled();
              if(queuedOpportunities&&!approvedOpportunity){
                opportunities.set('resource:'+resource.item_type+':'+point.x+':'+point.y,{resource,point,drop});
                return handled();
              }
              if(actual>=resource.count||!worthwhile(point,event)) return handled();
              if(!makeSpace()) return {handled:false,reason:'inventory_policy_needs_review'};
              const target={item_type:resource.item_type,count:Math.min(resource.count,actual+1)};
              if(!drop){
                target.tile_names=[resource.tile_name];
                if(resource.frame_x!=null) target.tile_frame_x=resource.frame_x;
                if(resource.frame_y!=null) target.tile_frame_y=resource.frame_y;
              }
              const r=tool('gather',{targets:[target],source_area:{x:point.x-2,y:point.y-2,width:6,height:6}});
              return {handled:r.outcome==='enough',result:r.outcome};
            };
            on('nearby_tile',event=>{
              const tile=event.evidence;
              const resource=config.resources.find(r=>r.tile_type===tile.tile_type
                &&(r.frame_x==null||r.frame_x===tile.frame_x)&&(r.frame_y==null||r.frame_y===tile.frame_y));
              return resource?collect(resource,tile,false,event):handled();
            });
            on('nearby_drop',event=>{
              const observed=event.evidence;
              const drop=tool('nearby_drops',{radius:60}).drops.find(d=>d.who===observed.who
                &&d.generation===observed.generation&&d.item_type===observed.item_type);
              if(!drop||drop.liquid_type===1&&drop.liquid_amount>0) return handled();
              const resource=config.resources.find(r=>r.item_type===drop.item_type);
              return resource?collect(resource,drop,true,event):handled();
            });
            on('nearby_chest',event=>{
              if(queuedOpportunities){
                opportunities.set('chest:'+event.evidence.x+':'+event.evidence.y,{point:event.evidence,chest:true});
                return handled();
              }
              if(!worthwhile(event.evidence,event)) return handled();
              if(!makeSpace()) return {handled:false,reason:'inventory_policy_needs_review'};
              const r=tool('collect_treasure',{x:event.evidence.x,y:event.evidence.y});
              print({opportunity:'chest',point:event.evidence,verified:r.new_chest_loot_verified===true,error:r.error,outcome:r.outcome});
              return {handled:true,outcome:r.new_chest_loot_verified===true?'looted':'deferred',reason:r.error??r.outcome};
            });
            on('inventory_full',()=>({handled:makeSpace()}));
            on('action_failed',event=>{
              if(event.result.outcome==='inventory_full') return {retry:makeSpace()};
              const nav=event.result.last_result?.result?.navigation??event.result.navigation??event.result;
              if(!['navigation_contact_escape','navigation_near_trap'].includes(nav.error)) return {retry:false};
              const program=tool('script_program',{operation:'get',name:'recover_navigation'});
              return skill('recover_navigation',program.revision,{error:nav.error,tile:nav.contact_escape?.predicted_tile,
                traps:nav.trap_tiles,allow_trigger_removal:config.allow_trigger_removal});
            });
            let combatChoice='',decisionWeapon=originalCombat.combat_weapon_type,jointChosen=false;
            on('tick',event=>{
              const destination=event.execution?.tool==='nav_to'?event.execution.args:config.destination;
              const forecastContext=decisionContext+':'+destination?.x+':'+destination?.y;
              const combat=tool('combat_assess',{radius:25});
              if(dimensions.size&&combat.boss_present) return handled();
              const attacker=combat.recent_damage?.attacker;
              const recent=attacker&&combat.tick-combat.recent_damage.tick<=120;
              const threats=combat.targets.filter(n=>n.attackable&&(n.distance_tiles<=8
                ||recent&&n.who===attacker.npc_index&&n.type===attacker.npc_type));
              threats.sort((a,b)=>(recent&&a.who===attacker.npc_index?-1:0)-(recent&&b.who===attacker.npc_index?-1:0)
                ||a.distance_tiles-b.distance_tiles);
              let choice=null;
              for(const target of threats){
                const weapon=config.weapon_order.map(type=>target.weapons.find(w=>w.type===type
                  &&w.has_ammo&&w.mana_available&&w.line_or_reach_passed)).find(Boolean);
                if(weapon){choice={target,weapon};break;}
              }
              const terrain=tool('inspect_terrain',{radius:10});
              let scene;
              if(dimensions.size){
                const player=combat.player;
                const body=player.size_pixels;
                const spatialCombat={...combat,player:{hp:player.hp,max_hp:player.max_hp,
                  x:(player.pos.x+body.width/2)/16,y:(player.pos.y+body.height/2)/16,
                  width:body.width/16,height:body.height/16,
                  vx:player.velocity.x*60/16,vy:player.velocity.y*60/16,
                  controls:player.controls,extra_jump_available:player.extra_jump_available,wing_time:player.wing_time},
                  targets:combat.targets.map(({vx_pixels_per_tick,vy_pixels_per_tick,...n})=>
                    ({...n,vx:vx_pixels_per_tick*60/16,vy:vy_pixels_per_tick*60/16})),
                  projectiles:(combat.projectiles??[]).map(({vx_pixels_per_tick,vy_pixels_per_tick,...p})=>
                    ({...p,vx:vx_pixels_per_tick*60/16,vy:vy_pixels_per_tick*60/16}))};
                scene={instruction:config.instruction??'',destination,combat:spatialCombat,terrain,
                  coordinates:'World tiles; x right, y down. Entity x/y are centers; vx/vy are tiles per second. Terrain coordinates are tile cells.'};
              }
              const encounter=combat.targets.filter(n=>n.contact_damage>0).map(n=>n.who+':'+n.type).join(',');
              if(dimensions.has('weapon')&&encounter){
                const weapons=new Map();
                for(const target of combat.targets) for(const weapon of target.weapons)
                  if(target.attackable&&weapon.has_ammo&&weapon.mana_available&&weapon.line_or_reach_passed) weapons.set(weapon.type,weapon);
                if(weapons.size===1){
                  const type=weapons.keys().next().value;
                  if(type!==decisionWeapon&&!tool('set_combat',{weapon_type:type}).error) decisionWeapon=type;
                }else if(weapons.size>1){
                  const criteria=Object.fromEntries([...weapons].map(([type,{line_or_reach_passed,...weapon}])=>['weapon_'+type,weapon]));
                  const r=judge('weapon',decisionContext,scene,
                    'Which available weapon best handles these enemies while the player continues the journey?',criteria);
                  if(r){
                    const type=Number(r.result.answers.response.choice.slice(7));
                    if(!weapons.has(type)) accept('weapon',r,'discarded_weapon_unavailable');
                    else if(type!==decisionWeapon){
                      const applied=tool('set_combat',{weapon_type:type});
                      if(!applied.error) decisionWeapon=type;
                      accept('weapon',r,applied.error??'weapon_type:'+type);
                    } else accept('weapon',r,'unchanged');
                  }
                }
              }
              const fallback=config.weapon_order.find(type=>combat.targets.some(n=>n.weapons.some(w=>w.type===type&&w.has_ammo&&w.mana_available)))
                ??originalCombat.combat_weapon_type;
              const key=choice?choice.weapon.type+':'+choice.target.who:'preferred:'+fallback;
              if((!dimensions.has('weapon')&&(!dimensions.has('combat')||!jointChosen)||!encounter)&&key!==combatChoice){
                const r=tool('set_combat',choice?{auto_attack:true,weapon_type:choice.weapon.type}
                  :{auto_attack:originalCombat.auto_attack,weapon_type:fallback});
                if(r.error) return {handled:false,reason:r.error};
                combatChoice=key;
              }
            """ + (name == "joint_reactions" ? """
              if(dimensions.has('combat')&&destination){
                const r=skill('joint_combat',config.combat_skill_revision,{context:forecastContext,destination,
                  instruction:config.instruction,last_request:usedDecisions.combat,
                  interval_ms:config.decision_interval_ms,max_age_ms:config.decision_max_age_ms},'combat');
                if(r.request_id) usedDecisions.combat=r.request_id;
                if(r.applied){decisionWeapon=r.weapon_type;jointChosen=true;}
              }
            """ : "") + """
              if(dimensions.has('movement')&&destination&&(encounter||combat.projectiles?.length)){
                const player=scene.combat.player;
                const movementScene={...scene,combat:{...scene.combat,
                  targets:scene.combat.targets.map(({weapons,...n})=>({...n,dx:n.x-player.x,dy:n.y-player.y})),
                  projectiles:scene.combat.projectiles.map(p=>({...p,dx:p.x-player.x,dy:p.y-player.y}))}};
                const r=judge('movement',decisionContext+':'+destination.x+':'+destination.y,movementScene,
                  'Which movement best avoids enemy contact and projectile damage over the next half second, considering the local terrain? Attacks can continue during every movement. The journey resumes after the response.',
                  {follow_route:'Keep the currently observed movement keys in combat.player.controls while navigation continues.',
                    left:'Move left for 12 frames, then resume the journey.',right:'Move right for 12 frames, then resume the journey.',
                    jump_left:'Move left and hold jump for 12 frames, then resume the journey.',
                    jump_right:'Move right and hold jump for 12 frames, then resume the journey.'});
                if(r){
                  const answer=r.result.answers.response.choice;
                  const dx=scene.combat.player.x-r.input_state.combat.player.x;
                  const dy=scene.combat.player.y-r.input_state.combat.player.y;
                  if(dx*dx+dy*dy>(config.decision_max_displacement_tiles??6)**2)
                    accept('movement',r,'discarded_displacement');
                  else {
                    accept('movement',r,answer);
                    if(answer!=='follow_route'){
                      const keys=[answer.endsWith('left')?'left':'right'];
                      if(answer.startsWith('jump_')) keys.push('jump');
                      const began=Date.now();
                      const moved=tool('act',{steps:[{keys,until:{frames:12}}]});
                      print({decision_motion_result:answer,elapsed_ms:Date.now()-began,result:moved.outcome??moved.error});
                      return handled();
                    }
                  }
                }
              }
              if(queuedOpportunities&&opportunities.size){
                const px=combat.player.pos.x/16,py=combat.player.pos.y/16;
                const [id,opportunity]=activeOpportunity&&opportunities.has(activeOpportunity)
                  ?[activeOpportunity,opportunities.get(activeOpportunity)]:[...opportunities].sort((a,b)=>
                  Math.hypot(a[1].point.x-px,a[1].point.y-py)-Math.hypot(b[1].point.x-px,b[1].point.y-py))[0];
                const assessment=tool('opportunity_assess',{x:opportunity.point.x,y:opportunity.point.y,
                  ...(destination?{destination}:{})});
                if(opportunity.resource&&count(opportunity.resource.item_type)>=opportunity.resource.count
                  ||!assessment.ok||opportunity.resource&&!opportunity.drop&&(!assessment.target.has_tile
                  ||assessment.target.tile_type!==opportunity.resource.tile_type))
                  opportunities.delete(id);
                else if(blockedDetour(assessment,opportunity.point)){
                  print({opportunity:id,outcome:'deferred',reason:'trap_at_target',hazard:blockedDetour(assessment,opportunity.point)});
                  opportunities.delete(id);activeOpportunity=null;
                }
                else if(assessment.approach_direct_distance<=25){
                  activeOpportunity=id;
                  let take=opportunity.take;
                  if(dimensions.has('opportunity')){
                    if(take==null){
                    const r=judge('opportunity',decisionContext+':'+id,{...scene,opportunity,assessment,resources:config.resources,
                      collection_budget:{approach_tiles:config.max_detour_tiles,extra_distance_tiles:config.max_detour_tiles},
                      inventory:state(['inventory_counts']).inventory_counts},
                      'Is this opportunity worth collecting when passing nearby on this journey, given the resource goals, route, threats and inventory? Collection starts within collection_budget.',
                      {take:'Collect this opportunity when within the approach budget, then resume the journey.',pass:'Continue the journey and leave this opportunity.'});
                    if(r){
                      accept('opportunity',r,r.result.answers.response.choice);
                      take=opportunity.take=r.result.answers.response.choice==='take';
                    }
                    }
                  } else if(assessment.approach_direct_distance<=config.max_detour_tiles) take=worthwhile(opportunity.point,event);
                  if(take&&assessment.approach_direct_distance>config.max_detour_tiles) take=null;
                  if(take!=null){opportunities.delete(id);activeOpportunity=null;}
                  if(take){
                    approvedOpportunity=true;
                    try {
                      if(opportunity.chest){
                        if(makeSpace()) tool('collect_treasure',{x:opportunity.point.x,y:opportunity.point.y});
                      } else collect(opportunity.resource,opportunity.point,opportunity.drop,event);
                    } finally {approvedOpportunity=false;}
                    return handled();
                  }
                } else activeOpportunity=null;
              }
              const thorn=destination&&terrain.near_body_tiles.find(t=>(config.contact_pass_types??[]).includes(t.type)
                &&t.touch_damage>0&&(t.in_body||t.x===terrain.player_bounds.right+1&&destination.x>t.x
                  ||t.x===terrain.player_bounds.left-1&&destination.x<t.x));
              if(thorn&&combat.player.hp-thorn.touch_damage>=config.health_reserve){
                const direction=destination.x>=combat.player.pos.x/16?'right':'left';
                const r=tool('act',{steps:[{keys:[direction],until:{frames:6}}]});
                if(r.error) return {handled:false,reason:r.error};
              }
              return handled();
            """ + (name == "task_reactions" ? "},{append:true});\n" : "});\n") + """
            const tileTypes=[...new Set(config.resources.map(r=>r.tile_type).filter(Number.isInteger))];
            tool('watch',{events:[...(tileTypes.length?['nearby_tile']:[]),'nearby_drop','nearby_chest','inventory_full'],
              tile_types:tileTypes,
              radius:25,loot_chests_in_reach:!queuedOpportunities});
            if(policyOnly) return {registered:true};
            try { return step('travel',()=>tool('nav_to',config.destination)); }
            finally { tool('set_combat',{auto_attack:originalCombat.auto_attack,weapon_type:originalCombat.combat_weapon_type}); }
            """);
        if (name == "gather_route") return Template(name,
            "内置参数化采集技能，可直接调用revision=0；input包含gather_args和当前世界查证的waypoints（最多8项，每项x/y及可选mode/source_area）。先收集当前位置可用来源，未达到真实库存目标且当前来源耗尽时，立即前往下一个预备位置并继续；其他失败保留证据交回。DAG按真实条件完成验收。",
            """
            const config=typeof input==='undefined'?null:input;
            if(!config?.gather_args?.targets||!Array.isArray(config.waypoints)||config.waypoints.length>8)
              throw new Error('input needs gather_args.targets and up to eight observed waypoints');
            const targets=config.gather_args.targets;
            const counts=()=>{
              const inventory=tool('get_state',{fields:['inventory_counts']}).inventory_counts;
              return targets.map(t=>({type:t.item_type,wanted:t.count,
                actual:inventory[t.item_type]??0}));
            };
            let args=config.gather_args;
            const visited=[];
            for(let index=0;index<=config.waypoints.length;index++){
              const current=counts();
              if(current.every(t=>t.actual>=t.wanted)) return {status:'collected',counts:current,visited};
              const result=tool('gather',args);
              if(result.outcome==='enough'){
                const actual=counts();
                if(actual.every(t=>t.actual>=t.wanted)) return {status:'collected',counts:actual,visited};
                return {status:'needs_review',reason:'inventory_changed',counts:actual,result};
              }
              if(!['source_unavailable','source_area_exhausted'].includes(result.outcome))
                return {status:'needs_review',reason:'collection_blocked',result,visited};
              const point=config.waypoints[index];
              if(!point) return {status:'needs_review',reason:'prepared_sources_exhausted',result,visited};
              const travel=tool('nav_to',{x:point.x,y:point.y,mode:point.mode??'travel'});
              if(travel.done!==true) return {status:'needs_review',reason:'approach_blocked',travel,visited};
              visited.push({x:point.x,y:point.y});
              args={...config.gather_args,source_area:point.source_area};
            }
            """);
        if (name == "recover_pressure_plate") return Template(name,
            "写入DAG节点on_failure的局部恢复示例。先按任务权限填写allow_trigger_removal、实际pick_slot/pick_type；宿主提供failure。依据原动作失败证据和当前机关变体拆除压力板，核验后恢复原句柄；未覆盖或修复失败交回审阅。原脚本与采集进度保留。",
            """
            const config = null;
            if(!config||config.allow_trigger_removal!==true||typeof failure==='undefined')
              return {status:'needs_review',reason:'trigger_removal_policy_required'};
            const result=failure.result?.value?.blocked??failure.result?.value?.collection??failure.result;
            const nav=result?.last_result?.result?.navigation??result?.navigation??result;
            if(nav?.error!=='navigation_near_trap'||!nav.trap_tiles?.length||nav.trap_tiles.some(t=>t.kind!=='pressure_plate'))
              return {status:'needs_review',reason:'uncovered_failure',handle:failure.handle};
            const inventory=tool('get_state',{fields:['inventory_slots']}).inventory_slots;
            if(!inventory.some(i=>i.slot===config.pick_slot&&i.type===config.pick_type&&i.stack>0))
              return {status:'needs_review',reason:'configured_pick_changed'};
            const same=(a,b)=>a.x===b.x&&a.y===b.y&&a.type===b.type&&a.frame_x===b.frame_x&&a.frame_y===b.frame_y;
            let removed=0;
            for(const trap of nav.trap_tiles){
              const current=tool('inspect_terrain',{}).trap_tiles;
              if(!current.some(t=>same(t,trap))) return {status:'needs_review',reason:'trap_evidence_changed',trap};
              const r=tool('use_item',{slot:config.pick_slot,x:trap.x,y:trap.y});
              if(r.outcome!=='removed') return {status:'needs_review',reason:'trigger_not_removed',result:r};
              if(tool('inspect_terrain',{}).trap_tiles.some(t=>same(t,trap)))
                return {status:'needs_review',reason:'trigger_still_present',trap};
              removed++;
            }
            const resumed=tool('action_control',{handle:failure.handle,command:'resume'});
            return {removed,handle:resumed.handle,status:resumed.status,error:resumed.error};
            """);
        if (name == "observe_boss_control") return Template(name,
            "Boss控制器已交接后，在所属DAG节点中观察。config.boss_type可限定Boss，required_axes可设horizontal/vertical。每秒读取即时预览，所需移动轴缺失、危险事件或Boss离场就交回模型，保留控制器运行；离场后由宿主条件验收击败事实。可按现场修改观察条件。",
            """
            const config = null;
            if(!config) throw new Error('Fill config.boss_type and required_axes from the combat plan');
            const required=config.required_axes??[];
            if(required.some(axis=>!['horizontal','vertical'].includes(axis))) throw new Error('required_axes needs horizontal/vertical');
            for(let sample=0;sample<40;sample++) {
              const state=tool('boss_status');
              const bosses=(state.active_bosses??[]).filter(b=>config.boss_type==null||b.type===config.boss_type);
              if(!bosses.length) return {status:'verify',reason:'boss_absent',next:'Verify actual defeat condition in owning DAG node'};
              const snapshot=state.movement_snapshot;
              if(state.input_gate||!state.movement_enabled||!state.movement_permitted)
                return {status:'yield',reason:'controller_unavailable',controller:state};
              if(snapshot?.source==='live_preview') {
                const options=snapshot.options??[];
                const missing=required.filter(axis=>!options.some(option=>(axis==='horizontal'?/Left|Right/:/Up|Down/).test(option.direction)));
                if(missing.length) return {status:'yield',reason:'movement_axis_unavailable',missing,bosses,movement:snapshot};
              }
              const observed=tool('script_events',{wait_ms:1000,max_events:8});
              const danger=(observed.events??[]).filter(event=>['low_health','suffocating','player_died','boss_appeared'].includes(event.kind));
              if(danger.length) return {status:'yield',reason:'combat_observation',events:danger,bosses};
            }
            return {status:'yield',reason:'combat_review_interval',next:'Inspect damage, equipment and continued controller state'};
            """);
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
            "内置生命水晶技能，可直接调用revision=0，通过skill(name,revision,{target_health,gather_args},key)传入目标与可选采集配置。入参采用当前世界已查证的来源和区域。所属DAG后台节点连续采集、使用生命水晶并核对永久生命；每颗使用后继续下一颗，采集需要审阅时返回证据。仅使用背包水晶时提供target_health即可。",
            """
            const config = typeof input==='undefined'?null:input;
            if(!config||!Number.isInteger(config.target_health)||config.target_health<100||config.target_health>400)
              throw new Error('Pass input.target_health between 100 and 400');
            const observe=()=>tool('get_state',{fields:['player','inventory_slots','permanent_max_health']});
            let state=observe();
            while(state.permanent_max_health<config.target_health){
              let crystal=state.inventory_slots.find(i=>i.type===29&&i.stack>0);
              if(!crystal){
                if(!config.gather_args) return {status:'missing_crystals',permanent_health:state.permanent_max_health,target:config.target_health};
                const collection=tool('gather',config.gather_args);
                if(collection.outcome!=='enough') return {status:'needs_review',collection,permanent_health:state.permanent_max_health};
                state=observe();
                crystal=state.inventory_slots.find(i=>i.type===29&&i.stack>0);
                if(!crystal) return {status:'needs_review',reason:'collection_inventory_changed',collection};
              }
              const before=state.permanent_max_health;
              step('use_at_health_'+before,()=>{
                const r=tool('use_item',{slot:crystal.slot,x:Math.floor(state.player.pos.x/16),y:Math.floor(state.player.pos.y/16),duration_ticks:30});
                if(r.error||r.verified!==true) throw new Error(JSON.stringify(r));
                return {outcome:r.outcome,verified:r.verified};
              });
              state=observe();
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
                const inventory=()=>tool('get_state',{fields:['inventory_counts']}).inventory_counts[item_type]??0;
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

    public JsonObject Get(string name, bool includeRuntime = true)
    {
        lock (gate)
        {
            string path = FilePath(name);
            if (File.Exists(path))
            {
                var entry = JsonNode.Parse(File.ReadAllText(path)).AsObject();
                entry["callable"] = true;
                string runtime = Path.Combine(directory, "runtime", name + ".json");
                if (includeRuntime && File.Exists(runtime)) entry["runtime"] = JsonNode.Parse(File.ReadAllText(runtime));
                return entry;
            }
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
                .Select(name =>
                {
                    var entry = Get(name); entry.Remove("code");
                    if (entry["runtime"] is JsonArray versions)
                    {
                        entry["runtime"] = versions.LastOrDefault(v => v["revision"]?.ToString() == entry["revision"]?.ToString())?.DeepClone();
                        entry["runtime"]?.AsObject().Remove("recent");
                    }
                    return (JsonNode)entry;
                }).ToArray());
        }
    }

    public JsonArray Callable()
    {
        lock (gate)
            return new JsonArray(BuiltIns.Concat(Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension)
                : Enumerable.Empty<string>()).Distinct().OrderBy(name => name)
                .Select(name => (JsonNode)Get(name, includeRuntime: false)).ToArray());
    }

    public void RecordCalls(JsonArray calls, string world)
    {
        lock (gate)
        {
            foreach (var group in calls.GroupBy(call => call["name"].ToString()))
            {
                if (!File.Exists(FilePath(group.Key))) continue;
                string folder = Path.Combine(directory, "runtime"), path = Path.Combine(folder, group.Key + ".json");
                var versions = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)).AsArray() : new JsonArray();
                foreach (var call in group)
                {
                    var version = versions.FirstOrDefault(v => v["revision"]?.ToString() == call["revision"]?.ToString()
                        && v["source_sha256"]?.ToString() == call["source_sha256"]?.ToString())?.AsObject();
                    if (version == null)
                    {
                        version = new JsonObject { ["revision"] = call["revision"].DeepClone(),
                            ["source_sha256"] = call["source_sha256"].DeepClone(), ["calls"] = 0, ["returned"] = 0,
                            ["interrupted"] = 0, ["review_returns"] = 0, ["calls_with_tool_failure"] = 0,
                            ["total_elapsed_ms"] = 0L, ["recent"] = new JsonArray() };
                        versions.Add(version);
                    }
                    version["calls"] = version["calls"].GetValue<int>() + 1;
                    string status = call["status"].ToString();
                    if (status is "returned" or "interrupted") version[status] = version[status].GetValue<int>() + 1;
                    if (call["result"]?["status"]?.ToString() == "needs_review")
                        version["review_returns"] = version["review_returns"].GetValue<int>() + 1;
                    if (call["last_failure"] != null)
                        version["calls_with_tool_failure"] = version["calls_with_tool_failure"].GetValue<int>() + 1;
                    version["total_elapsed_ms"] = version["total_elapsed_ms"].GetValue<long>() + call["elapsed_ms"].GetValue<long>();
                    var sample = new JsonObject { ["utc"] = DateTime.UtcNow.ToString("O"), ["world"] = world };
                    foreach (string field in new[] { "status", "elapsed_ms", "tool_calls", "result", "last_failure" })
                        if (call[field] != null) sample[field] = call[field].DeepClone();
                    var recent = version["recent"].AsArray(); recent.Add(sample);
                    while (recent.Count > 4) recent.RemoveAt(0);
                }
                while (versions.Count > 6) versions.RemoveAt(0);
                Directory.CreateDirectory(folder);
                File.WriteAllText(path + ".tmp", versions.ToJsonString());
                File.Move(path + ".tmp", path, true);
            }
        }
    }

    public JsonObject Save(string name, string code, int expectedRevision)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 16000)
            throw new ArgumentException("code必须是1..16000字符的JavaScript函数体。");
        lock (gate)
        {
            var previous = Get(name, includeRuntime: false);
            int revision = previous["revision"].GetValue<int>();
            if (revision != expectedRevision)
                return new JsonObject { ["error"] = "program_revision_conflict", ["name"] = name,
                    ["expected_revision"] = expectedRevision, ["revision"] = revision };
            if (previous["template"]?.GetValue<bool>() != true && previous["code"]?.ToString() == code)
            {
                previous["unchanged"] = true;
                return previous;
            }
            try
            {
                string wrapped = FunctionSource(code);
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
            var previous = Get(name, includeRuntime: false);
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
