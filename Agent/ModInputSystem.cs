using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class ModInputSystem : ModSystem
{
    delegate void ReadTriggers(Player player, TriggersSet triggers);
    delegate void ReadTriggersHook(ReadTriggers original, Player player, TriggersSet triggers);

    sealed class PressRequest(string key, int frames, int world, CancellationToken token)
    {
        public readonly string Key = key;
        public readonly int Frames = frames, World = world;
        public readonly CancellationToken Token = token;
        public readonly TaskCompletionSource<JsonObject> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Held;
    }

    static PressRequest pending;

    public override void Load()
    {
        if (Main.dedServ) return;
        MonoModHooks.Add(typeof(PlayerLoader).GetMethod(nameof(PlayerLoader.ProcessTriggers)),
            (ReadTriggersHook)Apply);
    }

    public override void OnWorldUnload()
    {
        var request = pending;
        pending = null;
        request?.Done.TrySetResult(new JsonObject { ["error"] = "mod_input_world_unloaded" });
    }

    internal static JsonObject List(string mod, int offset)
    {
        var keys = PlayerInput.Triggers.Current.KeyStatus.Keys.Where(key => key.Contains('/')
            && (string.IsNullOrEmpty(mod) || key.StartsWith(mod + "/", StringComparison.Ordinal)))
            .OrderBy(key => key, StringComparer.Ordinal).ToArray();
        offset = Math.Max(0, offset);
        return new JsonObject { ["keys"] = new JsonArray(keys.Skip(offset).Take(40).Select(key => (JsonNode)JsonValue.Create(key)).ToArray()),
            ["offset"] = offset, ["total"] = keys.Length };
    }

    internal static async Task<JsonObject> Press(string key, int frames, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var start = await NativeAgent.OnMain(() =>
            {
                if (frames is < 1 or > 120 || !key.Contains('/') || !PlayerInput.Triggers.Current.KeyStatus.ContainsKey(key))
                    return (Request: (PressRequest)null, Error: "invalid_mod_key");
                if (pending is { Held: 0 } stale && stale.Token.IsCancellationRequested) pending = null;
                if (pending != null) return (Request: (PressRequest)null, Error: "mod_input_busy");
                if (Manual.On || !AgentNetwork.CanAutomate || Main.gameMenu)
                    return (Request: (PressRequest)null, Error: "mod_input_unavailable");
                var request = new PressRequest(key, frames, Main.worldID, deadline.Token);
                pending = request;
                return (Request: request, Error: (string)null);
            }, deadline.Token);
            if (start.Error != null) return new JsonObject { ["error"] = start.Error };
            return await start.Request.Done.Task.WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new JsonObject { ["error"] = "mod_input_timeout" }; }
    }

    static void Apply(ReadTriggers original, Player player, TriggersSet triggers)
    {
        var request = pending;
        if (request == null || player != Main.LocalPlayer) { original(player, triggers); return; }
        string key = request.Key;
        var pack = PlayerInput.Triggers;
        var current = triggers.KeyStatus;
        var old = pack.Old.KeyStatus;
        var pressed = pack.JustPressed.KeyStatus;
        var released = pack.JustReleased.KeyStatus;
        if (!current.TryGetValue(key, out bool physical) || !old.ContainsKey(key) || !pressed.ContainsKey(key) || !released.ContainsKey(key))
        {
            pending = null;
            request.Done.TrySetResult(new JsonObject { ["error"] = "mod_input_binding_removed" });
            original(player, triggers);
            return;
        }
        bool aborted = request.Token.IsCancellationRequested || Manual.On || player.dead
            || request.World != Main.worldID || !AgentNetwork.CanAutomate;
        bool heldBefore = request.Held > 0;
        bool hold = !aborted && request.Held < request.Frames;
        bool physicalOld = old[key], physicalPress = pressed[key], physicalRelease = released[key];
        try
        {
            current[key] = physical || hold;
            old[key] = physicalOld || heldBefore;
            pressed[key] = current[key] && !old[key];
            released[key] = !current[key] && old[key];
            original(player, triggers);
        }
        finally
        {
            current[key] = physical; old[key] = physicalOld;
            pressed[key] = physicalPress; released[key] = physicalRelease;
        }
        if (hold) { request.Held++; return; }
        pending = null;
        request.Done.TrySetResult(new JsonObject { ["ok"] = !aborted, ["key"] = key, ["frames"] = request.Held,
            ["outcome"] = aborted ? "interrupted" : "dispatched" });
    }
}
