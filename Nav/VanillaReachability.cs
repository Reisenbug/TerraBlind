using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class VanillaReachability : ModSystem
{
    JsonObject snapshot, anchor, result, forecast;
    Task<JsonObject> pending;
    CancellationTokenSource cancellation;
    bool selecting, previousMouse;
    int layer = 15, selected = -1;
    int[] controls = Array.Empty<int>();
    long tick;
    VanillaPrediction.RegionPlayback playback;
    readonly Rectangle slider = new(24, 200, 360, 18);
    public string Status { get; private set; } = "未探索";

    public override void Load()
    {
        if (Main.dedServ) return;
        On_Player.ResetControls += ResetInput;
        On_TriggersSet.CopyInto += CopyInput;
    }
    void ResetInput(On_Player.orig_ResetControls original, Player player) { original(player); Apply(player); }
    void CopyInput(On_TriggersSet.orig_CopyInto original, TriggersSet triggers, Player player) { original(triggers, player); Apply(player); }
    void Apply(Player player)
    {
        if (snapshot == null || player != Main.LocalPlayer) return;
        if (playback != null) VanillaPrediction.ApplyInput(player, playback.At(tick + 1));
        else if (Main.mouseLeft && !Main.drawingPlayerChat)
        { player.controlUseItem = false; player.mouseInterface = true; }
    }
    void Cancel()
    {
        var source = cancellation; cancellation = null;
        source?.Cancel();
        if (pending != null) _ = pending.ContinueWith(task => { _ = task.Exception; source?.Dispose(); }, TaskScheduler.Default);
        else source?.Dispose();
        pending = null;
    }
    public void Clear()
    {
        Cancel(); snapshot = anchor = result = forecast = null; playback = null;
        selected = -1; controls = Array.Empty<int>(); Status = "已关闭可达范围";
        AxisLock.Release("reachability-replay");
    }
    public void Start()
    {
        Clear(); Halt.All(); NativeAgent.Stop();
        var player = Main.LocalPlayer;
        if (Main.netMode != 0 || player.dead) throw new InvalidOperationException("请在单人世界存活时使用。");
        snapshot = VanillaFrame.Capture(player, VanillaLocalPlanner.SnapshotArea(player, player.position), true);
        anchor = VanillaPrediction.Observe(player, false);
        cancellation = new CancellationTokenSource(); selecting = false; layer = 15;
        pending = VanillaNavigationDemo.Instance.Observe(new JsonObject { ["command"] = "reachability",
            ["snapshot"] = snapshot.DeepClone() }, cancellation.Token);
        Status = "探索 90 帧可达状态中；不会自动操控人物";
    }
    IEnumerable<JsonNode> Visible()
    {
        if (result == null) return Enumerable.Empty<JsonNode>();
        if (layer == 0) return new[] { result["nodes"][0] };
        return result["layers"][layer - 1]["nodes"].AsArray().Select(id => result["nodes"][id.GetValue<int>()]);
    }
    static Vector2 Point(JsonNode node) => new(node["x"].GetValue<float>(), node["y"].GetValue<float>());
    void Select(int id)
    {
        Cancel(); selected = id; forecast = null;
        var sequence = new List<int>();
        var branch = new Stack<int>();
        for (var node = result["nodes"][id]; node["parent"].GetValue<int>() >= 0; node = result["nodes"][node["parent"].GetValue<int>()])
            branch.Push(node["keys"].GetValue<int>());
        foreach (int keys in branch) sequence.AddRange(Enumerable.Repeat(keys, 6));
        controls = sequence.ToArray();
        if (controls.Length == 0) { Status = "起点：0 帧"; return; }
        selecting = true; cancellation = new CancellationTokenSource();
        pending = VanillaNavigationDemo.Instance.Observe(new JsonObject { ["command"] = "replay",
            ["snapshot"] = snapshot.DeepClone(), ["controls"] = new JsonArray(controls.Select(key => JsonValue.Create(key)).ToArray()),
            ["capture_end"] = true }, cancellation.Token);
        Status = $"复查所选 {controls.Length} 帧轨迹";
    }
    public void Replay()
    {
        if (forecast == null || pending != null) throw new InvalidOperationException("先点击一个可达点，等轨迹复查完成。");
        var player = Main.LocalPlayer;
        if (!VanillaAsyncMovement.StateMatches(player, anchor, true, out _, out string mismatch)
            || !VanillaFrame.ContextMatches(snapshot, player, out mismatch, forecast["frames"].AsArray()))
            throw new InvalidOperationException("起始状态已经变化，请重新 /vnav explore：" + mismatch);
        if (!AxisLock.Take("reachability-replay", Ax.Move | Ax.Jump | Ax.Vertical | Ax.Use, () => playback != null))
            throw new InvalidOperationException("人物控制权被其他动作占用。");
        playback = new VanillaPrediction.RegionPlayback(new JsonObject { ["controls"] = new JsonArray(controls.Select(key => JsonValue.Create(key)).ToArray()),
            ["use_slots"] = new JsonArray(controls.Select(_ => JsonValue.Create(-1)).ToArray()),
            ["frames"] = forecast["frames"].DeepClone(), ["anchor"] = anchor.DeepClone() }, tick);
        Status = "正在实际回放所选轨迹";
    }
    public override void PostUpdateEverything()
    {
        if (Main.dedServ || snapshot == null || Main.gameMenu) return;
        tick++;
        try
        {
            if (playback != null)
            {
                if (!playback.Matches(Main.LocalPlayer, tick, out string difference))
                { playback = null; AxisLock.Release("reachability-replay"); Status = "实际回放偏离：" + difference; }
                else if (playback.Complete(tick))
                { playback = null; AxisLock.Release("reachability-replay"); Status = "实际回放完成，逐帧状态一致"; }
                return;
            }
            if (pending?.IsCompleted == true)
            {
                var response = pending.GetAwaiter().GetResult(); pending = null; cancellation.Dispose(); cancellation = null;
                if (selecting)
                {
                    if (response["outcome"]?.GetValue<string>() != "complete") throw new InvalidOperationException("所选轨迹复查失败");
                    var frames = response["frames"].AsArray();
                    var endpoint = Point(frames[frames.Count - 1]) + new Vector2(Main.LocalPlayer.width / 2f, Main.LocalPlayer.height);
                    if (Vector2.DistanceSquared(endpoint, Point(result["nodes"][selected])) > .01f)
                        throw new InvalidOperationException("回放终点与探索结果不一致");
                    forecast = response; Status = $"选中 {controls.Length} 帧轨迹；/vnav replay 实际回放";
                }
                else
                {
                    result = response; layer = Math.Min(15, result["layers"].AsArray().Count);
                    var point = Visible().OrderByDescending(node => Vector2.DistanceSquared(Point(node), Point(result["nodes"][0]))).FirstOrDefault();
                    Status = "探索完成";
                    if (point != null) Select(point["id"].GetValue<int>());
                }
            }
            bool click = Main.mouseLeft && !previousMouse; previousMouse = Main.mouseLeft;
            if (result == null || Main.drawingPlayerChat) return;
            var mouse = Main.MouseScreen / Main.UIScale;
            if (Main.mouseLeft && slider.Contains(mouse.ToPoint()))
            {
                int nextLayer = Math.Clamp((int)Math.Round((mouse.X - slider.X) / slider.Width * result["layers"].AsArray().Count), 0, result["layers"].AsArray().Count);
                if (nextLayer != layer)
                {
                    Cancel(); layer = nextLayer; selected = -1; forecast = null;
                    controls = Array.Empty<int>(); Status = "点击可达点查看轨迹";
                }
                return;
            }
            if (click && mouse.Y > 230)
            {
                var point = Visible().OrderBy(node => Vector2.DistanceSquared(Point(node), Main.MouseWorld)).FirstOrDefault();
                if (point != null && Vector2.DistanceSquared(Point(point), Main.MouseWorld) <= 20 * 20) Select(point["id"].GetValue<int>());
            }
        }
        catch (Exception error) { Cancel(); playback = null; AxisLock.Release("reachability-replay"); Status = "探索失败：" + error.GetBaseException().Message; }
    }
    public override void PostDrawTiles()
    {
        if (Main.dedServ || Main.gameMenu || result == null) return;
        var batch = Main.spriteBatch;
        batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone, null, Main.GameViewMatrix.TransformationMatrix);
        foreach (var node in Visible())
        {
            bool chosen = node["id"].GetValue<int>() == selected;
            var point = Point(node) - Main.screenPosition;
            batch.Draw(TextureAssets.MagicPixel.Value, point, null, chosen ? Color.White : node["jumps"].GetValue<int>() > 0 ? Color.Cyan : Color.Orange,
                0, new Vector2(.5f), chosen ? 7f : 3f, SpriteEffects.None, 0);
        }
        if (forecast?["frames"] is JsonArray frames)
        {
            Vector2 previous = Point(result["nodes"][0]);
            foreach (var frame in frames)
            {
                var next = Point(frame) + new Vector2(Main.LocalPlayer.width / 2f, Main.LocalPlayer.height);
                var delta = next - previous;
                batch.Draw(TextureAssets.MagicPixel.Value, previous - Main.screenPosition, null, Color.Yellow * .7f,
                    delta.ToRotation(), Vector2.Zero, new Vector2(delta.Length(), 1.5f), SpriteEffects.None, 0);
                previous = next;
            }
        }
        batch.End();
    }
    public override void PostDrawInterface(SpriteBatch batch)
    {
        if (snapshot == null || Main.gameMenu) return;
        batch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(16, 142, 600, 94), Color.Black * .8f);
        Utils.DrawBorderString(batch, "90 帧可达探索：" + Status, new Vector2(24, 148), Color.White, .75f);
        string detail = result == null ? "6 种输入，每段 6 帧；合并近似状态，每层最多保留 256 个" :
            $"{layer * 6} 帧  青：仍有额外跳跃  橙：已用完  耗时 {result["elapsed_ms"].GetValue<double>() / 1000:0.00}s  截去 {result["dropped"]} 个状态";
        Utils.DrawBorderString(batch, detail, new Vector2(24, 174), Color.White, .65f);
        batch.Draw(TextureAssets.MagicPixel.Value, slider, Color.DimGray);
        batch.Draw(TextureAssets.MagicPixel.Value, new Rectangle(slider.X + layer * slider.Width / 15 - 3, slider.Y, 6, slider.Height), Color.White);
        Utils.DrawBorderString(batch, "0                 拖动时间轴                 90 帧", new Vector2(24, 218), Color.White, .6f);
    }
    public override void OnWorldUnload() => Clear();
    public override void Unload()
    {
        Clear();
        if (Main.dedServ) return;
        On_Player.ResetControls -= ResetInput; On_TriggersSet.CopyInto -= CopyInput;
    }
}
