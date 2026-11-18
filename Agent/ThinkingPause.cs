using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class ThinkingPause : ModSystem
{
    static long frozenAt, frozenTicks, started;
    static int worldFrames, transitions;
    static string reason;
    static readonly JsonArray history = new();
    static Vector2 previousPosition;
    static int uncoveredFrames, uncoveredRun, longestUncoveredRun;
    static bool positionKnown;
    bool manualSwitch;
    public static bool Frozen => frozenAt != 0;
    public static double FrozenMilliseconds => FrozenDuration.TotalMilliseconds;
    static TimeSpan FrozenDuration => TimeSpan.FromSeconds((frozenTicks
        + (Frozen ? Stopwatch.GetTimestamp() - frozenAt : 0)) / (double)Stopwatch.Frequency);

    public override void Load()
    {
        if (Main.dedServ) return;
        NativeAgent.FrozenTime = () => FrozenDuration;
        On_Main.CanPauseGame += CanPause;
    }

    public override void Unload()
    {
        On_Main.CanPauseGame -= CanPause;
        SetFrozen(null);
        NativeAgent.FrozenTime = () => TimeSpan.Zero;
    }

    public override void OnWorldLoad()
    {
        frozenAt = frozenTicks = 0;
        worldFrames = transitions = 0;
        reason = null; manualSwitch = false;
        history.Clear(); started = Stopwatch.GetTimestamp();
        uncoveredFrames = uncoveredRun = longestUncoveredRun = 0;
        positionKnown = false;
        AgentTools.WorldFramesRequired = 0;
    }

    public override void OnWorldUnload()
    {
        SetFrozen(null);
        DiagLog.Write("[agent_freeze_summary] " + Snapshot());
    }

    public override void UpdateUI(GameTime gameTime)
    {
        if (Main.gamePaused && Frozen && TerraBlind.ToggleManual?.JustPressed == true)
        {
            Manual.Toggle();
            manualSwitch = true;
        }
    }

    public override void PostUpdateEverything()
    {
        worldFrames++;
        if (AgentTools.WorldFramesRequired > 0) AgentTools.WorldFramesRequired--;
        var player = Main.LocalPlayer;
        bool stationary = positionKnown && Vector2.DistanceSquared(previousPosition, player.position) <= .0025f;
        bool uncovered = !Main.gameMenu && !Manual.On && !player.dead
            && (NativeAgent.WorkPending || AgentTools.Actions.HasActive || AgentTools.ControlSession.Active)
            && stationary && player.itemAnimation == 0 && player.itemTime == 0 && player.reuseDelay == 0
            && !ActExecutor.IsActive && !FightCoordinator.IsActive && !PvpDuel.Active && !PlayerCombat.Active
            && NativeAgent.WorldWaiters == 0 && AgentTools.WorldFramesRequired == 0;
        uncoveredRun = uncovered ? uncoveredRun + 1 : 0;
        if (uncovered) uncoveredFrames++;
        longestUncoveredRun = Math.Max(longestUncoveredRun, uncoveredRun);
        if (uncoveredRun == 7)
            DiagLog.Write($"[agent_uncovered_frames] tick={Main.GameUpdateCount} run={uncoveredRun} tool={AgentTools.ControlSession.Tool} nav={RecedingNav.Active} field_ready={RecedingNav.FieldReady} mine={MineCoordinator.IsActive} use_item={ItemUseCoordinator.IsActive} status={NativeAgent.Status}");
        previousPosition = player.position; positionKnown = true;
    }

    bool CanPause(On_Main.orig_CanPauseGame original)
    {
        bool existingPause = original();
        string waiting = WaitingReason();
        SetFrozen(existingPause ? null : waiting);
        bool switchFrame = manualSwitch;
        manualSwitch = false;
        return existingPause || waiting != null || switchFrame;
    }

    static string WaitingReason()
    {
        if (Main.dedServ || Main.netMode != 0 || Main.gameMenu || Manual.On
            || !Config.I.AgentEnabled || !Config.I.AgentFreezeWhileThinking || !AgentNetwork.CanAutomate) return null;
        var player = Main.LocalPlayer;
        if (player == null || !player.active || player.dead) return null;
        if (!NativeAgent.WorkPending && !AgentTools.Actions.HasActive && !AgentTools.ControlSession.Active) return null;
        if (player.itemAnimation > 0 || player.itemTime > 0 || player.reuseDelay > 0
            || player.velocity.Y != 0 || Math.Abs(player.velocity.X) > .05f || AgentTools.WorldFramesRequired > 0
            || NativeAgent.WorldWaiters > 0 || ActExecutor.IsActive) return null;
        if (RecedingNav.Active || StateSpacePlanner.ExecRunning || MineCoordinator.IsActive || ItemUseCoordinator.IsActive
            || PlaceAnywhere.IsRunning || TreasureGrab.IsRunning || HouseBuilder.IsRunning
            || FightCoordinator.IsActive || PvpDuel.Active || PlayerCombat.Active || FrameProgram.Active
            || AgentTools.ContactEscapeActive) return null;
        return NativeAgent.WorkPending ? "agent_thinking" : "script_handoff";
    }

    static void SetFrozen(string next)
    {
        if (next == reason) return;
        long now = Stopwatch.GetTimestamp();
        if (Frozen) { frozenTicks += now - frozenAt; frozenAt = 0; }
        reason = next;
        if (next != null) frozenAt = now;
        transitions++;
        var entry = new JsonObject { ["elapsed_ms"] = started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            ["tick"] = Main.GameUpdateCount, ["frozen"] = Frozen, ["reason"] = next,
            ["world_frames"] = worldFrames, ["frozen_ms"] = FrozenDuration.TotalMilliseconds };
        history.Add(entry);
        while (history.Count > 32) history.RemoveAt(0);
        DiagLog.Write("[agent_freeze] " + entry);
    }

    public static JsonObject Snapshot() => new()
    {
        ["supported"] = Main.netMode == 0, ["frozen"] = Frozen, ["reason"] = reason,
        ["elapsed_ms"] = started == 0 ? 0 : Stopwatch.GetElapsedTime(started).TotalMilliseconds,
        ["frozen_ms"] = FrozenDuration.TotalMilliseconds, ["world_frames"] = worldFrames,
        ["uncovered_stationary_frames"] = uncoveredFrames, ["longest_uncovered_run_frames"] = longestUncoveredRun,
        ["transitions"] = transitions, ["recent"] = history.DeepClone()
    };

    public override void PostDrawInterface(SpriteBatch batch)
    {
        if (!Frozen || Main.gameMenu) return;
        Utils.DrawBorderString(batch, $"[TB 冻结] 思考与交接  {Stopwatch.GetElapsedTime(frozenAt).TotalSeconds:0.0}s",
            new Vector2(Math.Max(16, Main.screenWidth - 360), 110), Color.LightSkyBlue, .8f);
    }
}
