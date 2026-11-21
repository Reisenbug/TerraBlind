using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameInput;
using Terraria.Localization;
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
    static int decisionFrames, decisionIdleFrames, decisionIdleRun, longestDecisionIdleRun;
    static long stationarySince;
    static bool stationaryLogged;
    static string stationaryReason;
    static bool positionKnown;
    bool manualSwitch;
    bool dragonLensWasPaused;
    const string ExplorationPauseEvent = "exploration_paused";
    const string DragonLensPauseKey = "DragonLens/Pause";
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
        reason = null; manualSwitch = false; dragonLensWasPaused = false;
        history.Clear(); started = Stopwatch.GetTimestamp();
        uncoveredFrames = uncoveredRun = longestUncoveredRun = 0;
        decisionFrames = decisionIdleFrames = decisionIdleRun = longestDecisionIdleRun = 0;
        stationarySince = 0;
        stationaryLogged = false;
        stationaryReason = null;
        positionKnown = false;
        AgentTools.WorldFramesRequired = 0;
    }

    public override void OnWorldUnload()
    {
        dragonLensWasPaused = false;
        SetFrozen(null);
        DiagLog.Write("[agent_freeze_summary] " + Snapshot());
    }

    public override void PostUpdateInput()
    {
        if (Main.gameMenu || Main.netMode != 0 || !Main.LocalPlayer.active) return;

        bool paused = FreezeSystem.IsFrozen;
        var pressed = PlayerInput.Triggers.JustPressed.KeyStatus;
        if (paused && Main.hasFocus && !Main.drawingPlayerChat
            && pressed.TryGetValue(DragonLensPauseKey, out bool toggle) && toggle && FreezeSystem.Unfreeze())
        {
            pressed[DragonLensPauseKey] = false;
            paused = false;
        }
        if (paused == dragonLensWasPaused) return;
        dragonLensWasPaused = paused;
        if (!paused)
        {
            Main.NewText(Language.GetTextValue("Mods.TerraBlind.Exploration.Unpaused"), 120, 220, 180);
            return;
        }

        var player = Main.LocalPlayer;
        var signal = new JsonObject
        {
            ["event"] = ExplorationPauseEvent,
            ["world_id"] = Main.worldID,
            ["tick"] = Main.GameUpdateCount,
            ["x"] = (int)(player.Center.X / 16),
            ["y"] = (int)(player.Bottom.Y / 16),
            ["hp"] = player.statLife,
            ["agent_status"] = NativeAgent.Status,
            ["tool"] = AgentTools.ControlSession.Tool,
            ["navigation_active"] = RecedingNav.Active,
            ["navigation_state"] = NavCoordinator.State.ToString(),
            ["navigation_reason"] = NavCoordinator.FailReason,
            ["world_frozen"] = true
        }.ToJsonString();
        Mod.Logger.Info(signal);
        DiagLog.WriteEvent(signal);
        HttpServerSystem.PushEvent(ExplorationPauseEvent, signal);
        NativeAgent.Stop();
        if (RecedingNav.Active || ActExecutor.IsActive || FightCoordinator.IsActive) AgentTools.StopInputs();
        Main.NewText(Language.GetTextValue("Mods.TerraBlind.Exploration.Paused"), 255, 220, 120);
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
        bool deciding = NativeAgent.AwaitingDecision && !Main.gameMenu && player.active && !player.dead && !Manual.On;
        if (deciding) decisionFrames++;
        bool decisionMoving = positionKnown && !stationary && (RecedingNav.Active || ActExecutor.IsActive
            || AgentTools.ControlSession.PrimaryActive || StateSpacePlanner.IsActive || FrameProgram.Active || AgentTools.ContactEscapeActive);
        bool decisionIdle = deciding && !decisionMoving && player.itemAnimation == 0 && player.itemTime == 0;
        decisionIdleRun = decisionIdle ? decisionIdleRun + 1 : 0;
        if (decisionIdle) decisionIdleFrames++;
        longestDecisionIdleRun = Math.Max(longestDecisionIdleRun, decisionIdleRun);
        if (decisionIdleRun == 61)
            DiagLog.Write($"[agent_decision_idle] tick={Main.GameUpdateCount} frames={decisionIdleRun} status={NativeAgent.Status}" +
                $" continuity={AgentTools.ExecutionContinuity?["reason"]} owner={AgentTools.ControlSession.Tool}" +
                $" action={AgentTools.Actions.PrimaryHandle} nav_goal={RecedingNav.DisplayGoal} field_ready={RecedingNav.FieldReady}" +
                $" at=({player.Center.X / 16:0.0},{player.Center.Y / 16:0.0})");
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
        ObserveStationary(stationary && !Main.gameMenu && !Manual.On && player.active && !player.dead
            && player.itemAnimation == 0 && player.itemTime == 0
            && (NativeAgent.WorkPending || AgentTools.Actions.HasActive || AgentTools.ControlSession.Active || RecedingNav.Active));
        previousPosition = player.position; positionKnown = true;
    }

    static void ObserveStationary(bool waiting)
    {
        if (!waiting)
        {
            stationarySince = 0; stationaryReason = null; stationaryLogged = false; return;
        }
        if (stationarySince == 0) stationarySince = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(stationarySince).TotalSeconds < .5) return;
        stationaryReason = RecedingNav.Active ? NavigationWait()
            : ActExecutor.IsActive ? "Script input is active; no displacement"
            : HouseBuilder.IsRunning ? "Construction is active"
            : MineCoordinator.IsActive || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning ? "Waiting for item action"
            : AgentTools.ControlSession.Tool is string tool ? "Waiting on " + tool
            : NativeAgent.WorkPending ? NativeAgent.Status
            : "Waiting for the next prepared action";
        if (stationaryLogged || Stopwatch.GetElapsedTime(stationarySince).TotalSeconds < 2) return;
        stationaryLogged = true;
        DiagLog.Write($"[agent_stationary] reason={stationaryReason} nav_goal={RecedingNav.DisplayGoal} status={NativeAgent.Status}");
    }

    static string NavigationWait()
    {
        if (!string.IsNullOrEmpty(RecedingNav.MovementWaitOwner)) return "Movement held by " + RecedingNav.MovementWaitOwner;
        if (!RecedingNav.FieldReady) return "Building navigation field";
        if (LegacyNativePrediction.Computing) return "Simulating a movement segment";
        if (TrapEscape.Busy) return "Searching for a local escape";
        if (MineCoordinator.IsActive || ItemUseCoordinator.IsActive || PlaceAnywhere.IsRunning) return "Navigation is changing terrain";
        if (StateSpacePlanner.ExecRunning) return "Executing route segment; no displacement";
        return VanillaNavBridge.Enabled ? "Navigation: " + VanillaNavBridge.Stage : "Selecting the next route segment";
    }

    internal static string StationaryLabel => stationaryReason == null ? null
        : $"Wait {Stopwatch.GetElapsedTime(stationarySince).TotalSeconds:0.0}s · {stationaryReason}";

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
        ["decision_frames"] = decisionFrames, ["decision_idle_frames"] = decisionIdleFrames,
        ["longest_decision_idle_run_frames"] = longestDecisionIdleRun,
        ["transitions"] = transitions, ["recent"] = history.DeepClone()
    };

    public override void PostDrawInterface(SpriteBatch batch)
    {
        if (!Frozen || Main.gameMenu) return;
        Utils.DrawBorderString(batch, $"[TB 冻结] 思考与交接  {Stopwatch.GetElapsedTime(frozenAt).TotalSeconds:0.0}s",
            new Vector2(Math.Max(16, Main.screenWidth - 360), 110), Color.LightSkyBlue, .8f);
    }
}
