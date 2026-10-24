using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class VanillaDescentRun : ModSystem
{
    public static VanillaDescentRun Instance => ModContent.GetInstance<VanillaDescentRun>();
    public bool Active { get; private set; }
    public string Status { get; private set; } = "idle";
    Task<JsonObject> planning;
    JsonObject route;
    int phase;

    public void Start()
    {
        if (Main.netMode != NetmodeID.SinglePlayer || Main.ActiveWorldFileData.IsCloudSave)
            throw new InvalidOperationException("目前只支持单人本地世界。");
        Active = true; phase = 0; route = null; Status = "计算丛林入口及地狱终点";
        planning = Task.Run(HttpServerSystem.NavigationDescentGoals);
    }

    public void Stop()
    {
        Active = false; planning = null;
        RecedingNav.Stop();
    }

    public override void PostUpdateEverything()
    {
        if (!Active) return;
        try
        {
            if (Main.LocalPlayer.dead) { Status = "failed:dead"; Stop(); return; }
            if (phase == 0)
            {
                if (!planning.IsCompleted) return;
                route = planning.GetAwaiter().GetResult(); planning = null;
                if (route["found"]?.GetValue<bool>() != true) throw new InvalidOperationException(route.ToJsonString());
                RecedingNav.Start(route["entrance"]["x"].GetValue<int>(), route["entrance"]["y"].GetValue<int>());
                phase = 1; Status = "前往丛林入口"; return;
            }
            if (RecedingNav.Active) return;
            if (RecedingNav.LastStop != "done") throw new InvalidOperationException("nav:" + RecedingNav.LastStop);
            if (phase == 1)
            {
                RecedingNav.Start(route["hell_x"].GetValue<int>(), route["hell_y"].GetValue<int>());
                phase = 2; Status = "从丛林入口下地狱"; return;
            }
            if (Main.LocalPlayer.Bottom.Y / 16 < Main.UnderworldLayer) throw new InvalidOperationException("nav_done_outside_underworld");
            Status = "done"; Active = false; Main.NewText("已从丛林入口抵达地狱落脚点。");
        }
        catch (Exception error) { Status = "failed:" + error.GetBaseException().Message; Stop(); Main.NewText(Status); }
    }

    public override void OnWorldUnload() { Stop(); VanillaNavBridge.Enabled = false; }
}
