using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerraBlind;

public sealed class AgentUi : ModSystem
{
    delegate void UpdateUi(UserInterface ui, GameTime time);
    delegate void UpdateUiHook(UpdateUi original, UserInterface ui, GameTime time);

    sealed record Element(UIElement Control, Rectangle Bounds, string Text);
    sealed record Panel(UserInterface Interface, UIState State, Element[] Elements);
    sealed record Scene(string Id, int World, ulong Tick, Point Image, Point Screen, float UiScale, Panel[] Panels);
    sealed record Click(Panel Panel, Element Element, Scene Scene, Point Position, CancellationToken Token)
    {
        public bool Pressed;
        public readonly TaskCompletionSource<JsonObject> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    static readonly Dictionary<UserInterface, ulong> surfaces = new();
    static readonly FieldInfo clickDelay = typeof(UserInterface).GetField("_clickDisabledTimeRemaining", BindingFlags.Instance | BindingFlags.NonPublic);
    static TaskCompletionSource<JsonObject> capture;
    static Scene scene;
    static Click click;

    public override void Load()
    {
        if (!Main.dedServ)
            MonoModHooks.Add(typeof(UserInterface).GetMethod(nameof(UserInterface.Update)), (UpdateUiHook)Update);
    }

    public override void OnWorldUnload()
    {
        surfaces.Clear(); scene = null;
        capture?.TrySetResult(Error("ui_world_changed")); capture = null;
        click?.Result.TrySetResult(Error("ui_world_changed")); click = null;
    }

    static JsonObject Error(string error) => new() { ["error"] = error };
    static bool Available => AgentNetwork.CanAutomate && !Main.gameMenu && !Manual.On;

    static string Label(UIElement element) => element switch { UIText text => text.Text, UITextPanel<string> panel => panel.Text, _ => null };

    static IEnumerable<Element> Elements(UIElement parent)
    {
        foreach (var child in parent.Children)
        {
            Rectangle box = child.GetDimensions().ToRectangle();
            if (box.Width > 0 && box.Height > 0)
                yield return new Element(child, box, Label(child));
            foreach (var element in Elements(child)) yield return element;
        }
    }

    internal static async Task<JsonObject> Capture(CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        TaskCompletionSource<JsonObject> request = null;
        try
        {
            var pending = await NativeAgent.OnMain(() =>
            {
                if (!Available) return Task.FromResult(Error("ui_unavailable"));
                if (capture != null) return Task.FromResult(Error("ui_capture_busy"));
                capture = request = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return capture.Task;
            }, limit.Token);
            return await pending.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Error("ui_capture_timeout"); }
        finally
        {
            if (request != null) Main.QueueMainThreadAction(() => { if (capture == request) capture = null; });
        }
    }

    public override void PostDrawInterface(SpriteBatch spriteBatch)
    {
        if (capture == null) return;
        var result = capture; capture = null;
        try
        {
            if (!Available) { result.TrySetResult(Error("ui_unavailable")); return; }
            var device = Main.instance.GraphicsDevice;
            int width = device.PresentationParameters.BackBufferWidth, height = device.PresentationParameters.BackBufferHeight;
            var pixels = new Color[width * height];
            device.GetBackBufferData(pixels);
            float resize = Math.Min(1f, Math.Min(1600f / width, 1000f / height));
            var size = new Point(Math.Max(1, (int)(width * resize)), Math.Max(1, (int)(height * resize)));
            if (size.X != width || size.Y != height)
            {
                var resized = new Color[size.X * size.Y];
                for (int y = 0; y < size.Y; y++)
                for (int x = 0; x < size.X; x++) resized[y * size.X + x] = pixels[(y * height / size.Y) * width + x * width / size.X];
                pixels = resized;
            }
            using var texture = new Texture2D(device, size.X, size.Y);
            texture.SetData(pixels);
            using var png = new MemoryStream();
            texture.SaveAsPng(png, size.X, size.Y);
            var panels = surfaces.Where(pair => Main.GameUpdateCount - pair.Value <= 2 && pair.Key.CurrentState != null)
                .Select(pair => new Panel(pair.Key, pair.Key.CurrentState, Elements(pair.Key.CurrentState).Take(128).ToArray()))
                .Where(panel => panel.Elements.Length != 0).ToArray();
            scene = new Scene(Guid.NewGuid().ToString("N"), Main.worldID, Main.GameUpdateCount, size,
                new Point(width, height), Main.UIScale, panels);
            var panelList = new JsonArray();
            for (int i = 0; i < panels.Length; i++)
            {
                var panel = panels[i];
                panelList.Add(new JsonObject { ["id"] = i, ["type"] = panel.State.GetType().Name,
                    ["controls"] = new JsonArray(panel.Elements.Select(element => (JsonNode)Describe(element, scene)).ToArray()) });
            }
            result.TrySetResult(new JsonObject { ["image_id"] = AgentImages.Register(png.ToArray()), ["snapshot_id"] = scene.Id,
                ["width"] = size.X, ["height"] = size.Y, ["panels"] = panelList });
        }
        catch (Exception error) { result.TrySetResult(new JsonObject { ["error"] = "ui_capture_failed", ["detail"] = error.Message }); }
    }

    static JsonObject Describe(Element element, Scene snapshot)
    {
        float x = snapshot.UiScale * snapshot.Image.X / snapshot.Screen.X;
        float y = snapshot.UiScale * snapshot.Image.Y / snapshot.Screen.Y;
        var bounds = element.Bounds;
        return new JsonObject { ["type"] = element.Control.GetType().Name, ["text"] = element.Text,
            ["bounds"] = new JsonArray((int)(bounds.X * x), (int)(bounds.Y * y), (int)(bounds.Width * x), (int)(bounds.Height * y)) };
    }

    static bool Current(Scene snapshot)
    {
        var buffer = Main.instance.GraphicsDevice.PresentationParameters;
        return snapshot.World == Main.worldID && Main.GameUpdateCount - snapshot.Tick <= 300
            && snapshot.Screen.X == buffer.BackBufferWidth && snapshot.Screen.Y == buffer.BackBufferHeight && snapshot.UiScale == Main.UIScale;
    }

    internal static async Task<JsonObject> ClickAt(string snapshotId, int panelId, int x, int y, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var task = await NativeAgent.OnMain(() =>
            {
                if (!Available) return Task.FromResult(Error("ui_unavailable"));
                if (click != null) return Task.FromResult(Error("ui_click_busy"));
                if (scene == null || scene.Id != snapshotId || !Current(scene)) return Task.FromResult(Error("ui_snapshot_stale"));
                if (panelId < 0 || panelId >= scene.Panels.Length || x < 0 || x >= scene.Image.X || y < 0 || y >= scene.Image.Y)
                    return Task.FromResult(Error("ui_target_invalid"));
                var position = new Point((int)(x * scene.Screen.X / (scene.Image.X * scene.UiScale)),
                    (int)(y * scene.Screen.Y / (scene.Image.Y * scene.UiScale)));
                var panel = scene.Panels[panelId];
                if (!surfaces.ContainsKey(panel.Interface) || panel.Interface.CurrentState != panel.State)
                    return Task.FromResult(Error("ui_panel_changed"));
                var control = panel.State.GetElementAt(position.ToVector2());
                var element = panel.Elements.FirstOrDefault(candidate => candidate.Control == control);
                if (element == null) return Task.FromResult(Error("ui_target_changed"));
                click = new Click(panel, element, scene, position, limit.Token);
                return click.Result.Task;
            }, limit.Token);
            return await task.WaitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Error("ui_click_timeout"); }
    }

    static void Update(UpdateUi original, UserInterface ui, GameTime time)
    {
        if (ui.CurrentState != null && ui.CurrentState.GetType().DeclaringType != typeof(AgentPanel) && !Main.gameMenu)
            surfaces[ui] = Main.GameUpdateCount;
        var pending = click;
        if (pending == null || pending.Panel.Interface != ui) { original(ui, time); return; }
        bool valid = !pending.Token.IsCancellationRequested && Available && !Main.mouseLeft && Current(pending.Scene)
            && ui.CurrentState == pending.Panel.State
            && pending.Panel.State.GetElementAt(pending.Position.ToVector2()) == pending.Element.Control
            && pending.Element.Control.GetDimensions().ToRectangle() == pending.Element.Bounds
            && Label(pending.Element.Control) == pending.Element.Text;
        if (!valid && !pending.Pressed)
        {
            click = null; pending.Result.TrySetResult(Error("ui_target_changed"));
            original(ui, time); return;
        }
        if (!pending.Pressed && clickDelay?.GetValue(ui) is double delay && delay > time.ElapsedGameTime.TotalMilliseconds)
        { original(ui, time); return; }
        var input = (Main.mouseX, Main.mouseY, Main.mouseLeft, Main.mouseLeftRelease, Main.hasFocus);
        try
        {
            Main.mouseX = valid ? pending.Position.X : -1;
            Main.mouseY = valid ? pending.Position.Y : -1;
            Main.mouseLeft = valid && !pending.Pressed;
            Main.mouseLeftRelease = !pending.Pressed;
            Main.hasFocus = true;
            Main.LocalPlayer.mouseInterface = true;
            original(ui, time);
        }
        finally { (Main.mouseX, Main.mouseY, Main.mouseLeft, Main.mouseLeftRelease, Main.hasFocus) = input; }
        if (valid && !pending.Pressed) { pending.Pressed = true; return; }
        click = null;
        pending.Result.TrySetResult(valid
            ? new JsonObject { ["ok"] = true, ["outcome"] = "dispatched", ["control"] = pending.Element.Control.GetType().Name }
            : Error("ui_target_changed"));
    }

    public override void PostUpdateEverything()
    {
        foreach (var ui in surfaces.Where(pair => Main.GameUpdateCount - pair.Value > 2).Select(pair => pair.Key).ToArray())
            surfaces.Remove(ui);
        if (click is { Pressed: false } pending && pending.Token.IsCancellationRequested) click = null;
        if (click is { Pressed: true } held && !surfaces.ContainsKey(held.Panel.Interface))
        { click = null; held.Result.TrySetResult(Error("ui_panel_closed")); }
    }
}
