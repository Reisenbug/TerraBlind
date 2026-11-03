using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TerraBlind;

public static partial class AgentTools
{
    public static bool ControlAllows(ControlBehavior behavior, ControlChannels channels, bool delegated = false)
        => ControlSession.Check(behavior, channels, delegated) == null;

    static async Task<JsonObject> ManageControl(JsonObject args, CancellationToken token)
    {
        string operation = Text(args, "operation");
        if (operation == "status")
            return await NativeAgent.OnMain(() => new JsonObject { ["overrides"] = Control.Snapshot(),
                ["execution"] = ControlSession.Snapshot(), ["actions"] = Actions.Summary() }, token);
        if (operation is not ("claim" or "deny" or "release")) throw new ArgumentException("operation需要claim/deny/release/status。");
        if (ControlSession.Active && !Actions.HasActive)
            return new JsonObject { ["error"] = "control_direct_action_busy", ["detail"] = "当前直接动作尚未清理，不能宣告接管。" };

        string id = Text(args, "id");
        if (operation == "release")
        {
            await NativeAgent.OnMain(() =>
            { if (!Control.Contains(id)) throw new ArgumentException("未知覆盖句柄。"); return true; }, token);
        }
        else
        {
            if (!Enum.TryParse<ControlLifetime>(Text(args, "scope"), true, out var lifetime) || !Enum.IsDefined(lifetime))
                throw new ArgumentException("scope需要action/task/persistent。");
            string handle = Text(args, "action");
            if (lifetime == ControlLifetime.Action && !Actions.Summary().Any(item => item["handle"]?.ToString() == handle
                && item["status"]?.ToString() is "running" or "paused"))
                throw new ArgumentException("action需要尚未结束的真实动作句柄。");
            string reason = Text(args, "reason");
            if (operation == "claim")
            {
                var channels = ControlChannels.None;
                foreach (var value in args["channels"]?.AsArray() ?? new JsonArray())
                    channels |= value?.ToString() switch { "movement" => ControlChannels.Movement,
                        "item_use" => ControlChannels.ItemUse, _ => throw new ArgumentException("未知控制通道。") };
                id = await NativeAgent.OnMain(() => Control.Claim(channels, lifetime, handle, reason), token);
            }
            else
            {
                var behaviors = (args["behaviors"]?.AsArray() ?? new JsonArray()).Select(value =>
                    Enum.TryParse<ControlBehavior>(value?.ToString(), true, out var behavior) && Enum.IsDefined(behavior)
                        ? behavior : throw new ArgumentException("未知自动行为。")).ToArray();
                (int X, int Y, int Width, int Height)? region = null;
                if (args["region"] is JsonObject area)
                    region = (area["x"].GetValue<int>(), area["y"].GetValue<int>(),
                        area["width"].GetValue<int>(), area["height"].GetValue<int>());
                id = await NativeAgent.OnMain(() => Control.Deny(behaviors, lifetime, handle, reason, region), token);
            }
        }
        var result = await Actions.Handoff(async valid =>
        {
            await NativeAgent.OnMain(() =>
            {
                if (!valid()) throw new OperationCanceledException();
                StopInputs();
                return true;
            }, token);
            while (await NativeAgent.OnMain(() => Terraria.Main.LocalPlayer.itemAnimation > 0, token))
                await Task.Delay(16, token);
            await NativeAgent.OnMain(() =>
            {
                if (!valid()) throw new OperationCanceledException();
                if (operation == "claim") Control.ConfirmReleased(id);
                if (operation == "release") Control.Release(id);
                return true;
            }, token);
        }, token);
        result["id"] = id;
        result["overrides"] = await NativeAgent.OnMain(Control.Snapshot, token);
        return result;
    }
}
