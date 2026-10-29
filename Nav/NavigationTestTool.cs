using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class NavigationTestTool : ModSystem
{
    public bool Enabled;
    Point? start, goal;
    bool left, right, run, stop;
    string status = "选择起点和终点";

    public override void OnWorldLoad()
    {
        Enabled = Terraria.Program.LaunchParameters.TryGetValue("-tbdevrole", out string role) && role == "navigation";
        start = goal = null;
        left = right = run = stop = false;
        status = "选择起点和终点";
    }

    public override void OnWorldUnload() { Enabled = false; start = goal = null; }

    public override void PostUpdateInput()
    {
        if (Main.dedServ || Main.gameMenu) return;
        bool clickLeft = Main.mouseLeft && !left, clickRight = Main.mouseRight && !right;
        bool pressRun = Main.keyState.IsKeyDown(Keys.D8), pressStop = Main.keyState.IsKeyDown(Keys.D9);
        bool begin = pressRun && !run, halt = pressStop && !stop;
        left = Main.mouseLeft; right = Main.mouseRight; run = pressRun; stop = pressStop;
        if (!Enabled || Main.drawingPlayerChat || Main.editSign || Main.editChest) return;
        if (halt) { Halt.All(); status = "已停止"; }
        bool shift = Main.keyState.IsKeyDown(Keys.LeftShift) || Main.keyState.IsKeyDown(Keys.RightShift);
        if (shift && (Main.mapFullscreen || !Main.LocalPlayer.mouseInterface))
        {
            Vector2 tile = Main.mapFullscreen
                ? Main.mapFullscreenPos + (new Vector2(Main.mouseX, Main.mouseY) - new Vector2(Main.screenWidth, Main.screenHeight) / 2) / Main.mapFullscreenScale
                : Main.MouseWorld / 16;
            var point = new Point((int)System.Math.Floor(tile.X), (int)System.Math.Floor(tile.Y));
            if (WorldGen.InWorld(point.X, point.Y, 20) && (clickLeft || clickRight))
            {
                Halt.All();
                if (clickLeft) start = point; else goal = point;
                status = clickLeft ? "已选起点" : "已选终点";
                DiagLog.Write($"[nav-test-select] start={start} goal={goal}");
            }
        }
        if (begin) Begin();
    }

    void Begin()
    {
        if (Main.netMode != NetmodeID.SinglePlayer) { status = "选点传送测试仅限单人世界"; return; }
        if (start is not Point from || goal is not Point to) { status = "请先选齐起点、终点"; return; }
        var player = Main.LocalPlayer;
        if (player.dead) { status = "请先复活"; return; }
        var position = new Vector2(from.X * 16 + 8 - player.width / 2f, from.Y * 16 - player.height);
        if (!VanillaContactRegion.SafeBody(position, player.width, player.height))
        {
            status = "起点人物空间被挡住或有危险，请点落脚面的顶端";
            return;
        }
        Halt.All(); NativeAgent.Stop();
        player.Teleport(position, TeleportationStyleID.RodOfDiscord);
        player.velocity = Vector2.Zero;
        player.fallStart = (int)(position.Y / 16);
        Main.mapFullscreen = false;
        VanillaNavBridge.Enabled = false; VanillaNavBridge.RegionsEnabled = false;
        RecedingNav.Start(to.X, to.Y, RecedingNav.Mode.Snap);
        status = "旧主干寻路已启动；9 停止";
        DiagLog.Write($"[nav-test-start] start={from} goal={to} backend=legacy");
    }

    Vector2 ScreenPoint(Point tile)
    {
        var feet = new Vector2(tile.X + .5f, tile.Y);
        return (Main.mapFullscreen
            ? (feet - Main.mapFullscreenPos) * Main.mapFullscreenScale + new Vector2(Main.screenWidth, Main.screenHeight) / 2
            : Vector2.Transform(feet * 16 - Main.screenPosition, Main.GameViewMatrix.TransformationMatrix)) / Main.UIScale;
    }

    public override void PostDrawInterface(SpriteBatch batch)
    {
        if (!Enabled || Main.gameMenu) return;
        Utils.DrawBorderString(batch, "寻路测试：Shift+左键 起点 / Shift+右键 终点 / 8 开始 / 9 停止", new Vector2(24, 160), Color.Cyan, .8f);
        Utils.DrawBorderString(batch, $"起点 {start?.ToString() ?? "未选"} → 终点 {goal?.ToString() ?? "未选"}  {status}", new Vector2(24, 184), Color.White, .8f);
        if (start is Point a) Utils.DrawBorderString(batch, "起", ScreenPoint(a), Color.Lime);
        if (goal is Point b) Utils.DrawBorderString(batch, "终", ScreenPoint(b), Color.Yellow);
    }
}

public sealed class NavigationTestCommand : ModCommand
{
    public override CommandType Type => CommandType.Chat;
    public override string Command => "navtest";
    public override string Description => "开关起终点选点测试工具";
    public override void Action(CommandCaller caller, string input, string[] args)
    {
        var tool = ModContent.GetInstance<NavigationTestTool>();
        tool.Enabled = !tool.Enabled;
        caller.Reply(tool.Enabled ? "选点测试已开启：Shift+左键起点，Shift+右键终点，8开始，9停止。" : "选点测试已关闭。");
    }
}
