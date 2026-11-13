using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

internal static class AgentNavigationIntent
{
    internal readonly record struct Candidate(float X, float Y, int Frames, float Cost);
    sealed record Intent(string Mode, int Target, int TargetType, int GoalX, int GoalY,
        long Execution, long Revision, long Published, long Updated);
    static Intent current;
    static long sequence;

    internal static void Reset()
    {
        if (current != null) DiagLog.Write($"[navigation_intent_end] revision={current.Revision}");
        current = null;
    }

    internal static JsonObject Update(JsonObject args)
    {
        string mode = args["mode"]?.GetValue<string>();
        if (mode == "continue") { Reset(); return new JsonObject { ["mode"] = "continue" }; }
        if (mode is not ("withdraw" or "cover")) throw new ArgumentException("mode需要continue/withdraw/cover。");
        var denied = AgentTools.ControlSession.Check(ControlBehavior.Navigation, ControlChannels.Movement, true)
            ?? AgentTools.ControlSession.Check(ControlBehavior.Dodge, ControlChannels.Movement, true);
        if (denied != null) return denied;
        if (!RecedingNav.Active || AgentTools.ControlSession.Tool != "nav_to" || VanillaNavBridge.Enabled)
            return new JsonObject { ["error"] = "navigation_intent_requires_active_local_navigation" };
        int who = args["target"]?.GetValue<int>() ?? -1;
        if (who < 0 || who >= Main.npc.Length || Main.npc[who] is not { active: true, life: > 0, damage: > 0, friendly: false, townNPC: false, boss: false } npc)
            return new JsonObject { ["error"] = "navigation_intent_target_unavailable" };
        if (Vector2.Distance(Main.LocalPlayer.Center, npc.Center) > 25 * 16)
            return new JsonObject { ["error"] = "navigation_intent_target_out_of_range" };
        int x = args["destination"]?["x"]?.GetValue<int>() ?? throw new ArgumentException("需要destination.x。");
        int y = args["destination"]?["y"]?.GetValue<int>() ?? throw new ArgumentException("需要destination.y。");
        long execution = AgentTools.ControlSession.Snapshot()["execution"].GetValue<long>();
        long now = Stopwatch.GetTimestamp();
        bool same = current is { } old && old.Mode == mode && old.Target == who && old.TargetType == npc.type
            && old.GoalX == x && old.GoalY == y && old.Execution == execution
            && Stopwatch.GetElapsedTime(old.Updated).TotalSeconds < 2;
        current = new Intent(mode, who, npc.type, x, y, execution,
            same ? current.Revision : ++sequence, same ? current.Published : now, now);
        var result = new JsonObject { ["mode"] = mode, ["target"] = who, ["revision"] = current.Revision,
            ["execution"] = execution, ["expires_in_ms"] = 2000 };
        DiagLog.Write("[navigation_intent] " + result.ToJsonString());
        return result;
    }

    internal static long RevisionFor(int goalX, int goalY)
    {
        var intent = current;
        if (intent == null || intent.GoalX != goalX || intent.GoalY != goalY
            || !RecedingNav.Active || Main.LocalPlayer.dead || AgentTools.ControlSession.Tool != "nav_to"
            || AgentTools.ControlSession.Snapshot()["execution"].GetValue<long>() != intent.Execution
            || Stopwatch.GetElapsedTime(intent.Updated).TotalSeconds >= 2
            || AgentTools.ControlSession.Check(ControlBehavior.Navigation, ControlChannels.Movement, true) != null
            || AgentTools.ControlSession.Check(ControlBehavior.Dodge, ControlChannels.Movement, true) != null) return 0;
        var npc = Main.npc[intent.Target];
        if (npc is not { active: true, life: > 0, damage: > 0, friendly: false, townNPC: false, boss: false }
            || npc.type != intent.TargetType || Vector2.Distance(Main.LocalPlayer.Center, npc.Center) > 25 * 16) return 0;
        if (intent.Mode == "withdraw" && Vector2.Distance(Main.LocalPlayer.Center, npc.Center) >= 10 * 16) return 0;
        return intent.Revision;
    }

    internal static float Cost(string mode, float routeCost, float distanceTiles, bool lineClear)
        => routeCost + Math.Max(0, 10 - distanceTiles) * 8 + (mode == "cover" && lineClear ? 40 : 0);

    internal static int Select(int goalX, int goalY, IReadOnlyList<Candidate> candidates, int baseline)
    {
        if (RevisionFor(goalX, goalY) == 0 || baseline < 0) return baseline;
        var intent = current;
        var npc = Main.npc[intent.Target];
        var player = Main.LocalPlayer;
        float Score(Candidate c)
        {
            var enemy = npc.position + npc.velocity * Math.Min(30, c.Frames);
            var position = new Vector2(c.X, c.Y);
            float distance = Vector2.Distance(position + new Vector2(player.width, player.height) * 0.5f,
                enemy + new Vector2(npc.width, npc.height) * 0.5f) / 16;
            return Cost(intent.Mode, c.Cost, distance,
                intent.Mode == "cover" && Collision.CanHitLine(enemy, npc.width, npc.height, position, player.width, player.height));
        }
        int chosen = baseline;
        float best = Score(candidates[baseline]);
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Cost > candidates[baseline].Cost + 80) continue;
            float score = Score(candidates[i]);
            if (score < best) { best = score; chosen = i; }
        }
        DiagLog.Write("[navigation_intent_select] " + new JsonObject {
            ["revision"] = intent.Revision, ["mode"] = intent.Mode, ["target"] = intent.Target,
            ["tick"] = Main.GameUpdateCount, ["player_x"] = player.Center.X, ["player_y"] = player.Center.Y,
            ["target_x"] = npc.Center.X, ["target_y"] = npc.Center.Y,
            ["goal_x"] = goalX, ["goal_y"] = goalY, ["candidates"] = candidates.Count,
            ["changed"] = chosen != baseline, ["age_ms"] = Stopwatch.GetElapsedTime(intent.Published).TotalMilliseconds,
            ["baseline"] = new JsonObject { ["x"] = candidates[baseline].X, ["y"] = candidates[baseline].Y, ["cost"] = candidates[baseline].Cost },
            ["selected"] = new JsonObject { ["x"] = candidates[chosen].X, ["y"] = candidates[chosen].Y, ["cost"] = candidates[chosen].Cost },
            ["score"] = best }.ToJsonString());
        return chosen;
    }
}
