using System;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class NpcDialogue : ModSystem
{
    sealed class Request
    {
        public int Npc, Type, Button;
        public string Label;
        public CancellationToken Token;
        public readonly TaskCompletionSource<JsonObject> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    static readonly Action<Main, Rectangle> Hover = typeof(Main).GetMethod("HoverOverNPCs", BindingFlags.Instance | BindingFlags.NonPublic)
        .CreateDelegate<Action<Main, Rectangle>>();
    static int capturedNpc = -1, capturedType = -1;
    static string firstLabel = "", secondLabel = "";
    static Request pending;
    static bool injected;

    public override void Load()
    {
        if (Main.dedServ) return;
        On_Main.DrawNPCChatButtons += Buttons;
        On_Main.GUIChatDrawInner += Draw;
    }

    static NPC Target(int index, int type)
    {
        if (index < 0 || index >= Main.maxNPCs || !Main.npc[index].active || Main.npc[index].type != type)
            throw new InvalidOperationException("npc_changed");
        var p = Main.LocalPlayer;
        var reach = new Rectangle((int)p.Center.X - Player.tileRangeX * 16, (int)p.Center.Y - Player.tileRangeY * 16,
            Player.tileRangeX * 32, Player.tileRangeY * 32);
        if (Main.gameMenu || !p.active || p.dead || !reach.Intersects(Main.npc[index].Hitbox)) throw new InvalidOperationException("npc_out_of_reach");
        return Main.npc[index];
    }

    public static JsonObject Snapshot()
    {
        int index = Main.LocalPlayer.talkNPC;
        bool current = index >= 0 && index < Main.maxNPCs && Main.npc[index].active;
        var buttons = new JsonArray();
        if (current && capturedNpc == index && capturedType == Main.npc[index].type)
        {
            if (!string.IsNullOrWhiteSpace(firstLabel)) buttons.Add(new JsonObject { ["button"] = 1, ["text"] = firstLabel });
            if (!string.IsNullOrWhiteSpace(secondLabel)) buttons.Add(new JsonObject { ["button"] = 2, ["text"] = secondLabel });
        }
        return new JsonObject { ["open"] = current, ["npc"] = current ? index : -1,
            ["npc_type"] = current ? Main.npc[index].type : -1, ["text"] = Main.npcChatText,
            ["buttons_ready"] = current && capturedNpc == index && capturedType == Main.npc[index].type,
            ["buttons"] = buttons, ["shop_open"] = Main.npcShop > 0 };
    }

    public static JsonObject Open(int index, int type)
    {
        var npc = Target(index, type);
        if (Main.LocalPlayer.talkNPC == index) return Snapshot();
        bool right = Main.mouseRight, release = Main.npcChatRelease;
        try
        {
            capturedNpc = -1;
            Main.mouseRight = Main.npcChatRelease = true;
            Hover(Main.instance, new Rectangle((int)npc.Center.X, (int)npc.Center.Y, 1, 1));
        }
        finally { Main.mouseRight = right; Main.npcChatRelease = release; }
        var state = Snapshot();
        if (Main.LocalPlayer.talkNPC != index) state["error"] = "npc_not_talkable_or_occluded";
        return state;
    }

    public static async Task<JsonObject> Choose(int index, int type, int button, string label, CancellationToken token)
    {
        var request = await NativeAgent.OnMain(() =>
        {
            Target(index, type);
            if (button is not (1 or 2) || string.IsNullOrWhiteSpace(label)) throw new ArgumentException("需要当前button及expected_text。");
            if (pending != null) throw new InvalidOperationException("dialogue_busy");
            var value = new Request { Npc = index, Type = type, Button = button, Label = label, Token = token };
            pending = value;
            return value;
        }, token);
        try { return await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(3), token); }
        finally
        {
            await NativeAgent.OnMain(() => { if (pending == request) pending = null; return true; }, default);
        }
    }

    static void Buttons(On_Main.orig_DrawNPCChatButtons original, int color, Color chatColor, int lines, string first, string second)
    {
        original(color, chatColor, lines, first, second);
        capturedNpc = Main.LocalPlayer.talkNPC;
        capturedType = capturedNpc >= 0 ? Main.npc[capturedNpc].type : -1;
        firstLabel = first; secondLabel = second;
        if (pending == null || pending.Completion.Task.IsCompleted || pending.Token.IsCancellationRequested) return;
        try
        {
            Target(pending.Npc, pending.Type);
            if (capturedNpc != pending.Npc || (pending.Button == 1 ? first : second) != pending.Label)
                throw new InvalidOperationException("dialogue_option_changed");
            Main.mouseX = Main.screenWidth / 2; Main.mouseY = 110;
            Main.mouseLeft = Main.mouseLeftRelease = true;
            Main.npcChatFocus1 = Main.npcChatFocus4 = false;
            Main.npcChatFocus2 = pending.Button == 1; Main.npcChatFocus3 = pending.Button == 2;
            injected = true;
        }
        catch (Exception error) { pending.Completion.TrySetResult(new JsonObject { ["error"] = error.Message, ["dialogue"] = Snapshot() }); }
    }

    static void Draw(On_Main.orig_GUIChatDrawInner original, Main game)
    {
        var mouse = (Main.mouseX, Main.mouseY, Main.mouseLeft, Main.mouseLeftRelease,
            Main.npcChatFocus1, Main.npcChatFocus2, Main.npcChatFocus3, Main.npcChatFocus4);
        injected = false;
        try { original(game); }
        catch (Exception error) { if (injected) pending?.Completion.TrySetException(error); throw; }
        finally
        {
            if (injected)
            {
                (Main.mouseX, Main.mouseY, Main.mouseLeft, Main.mouseLeftRelease,
                    Main.npcChatFocus1, Main.npcChatFocus2, Main.npcChatFocus3, Main.npcChatFocus4) = mouse;
                pending?.Completion.TrySetResult(new JsonObject { ["ok"] = true, ["outcome"] = "button_dispatched", ["dialogue"] = Snapshot() });
            }
        }
    }

    public static void Close() { if (Main.LocalPlayer.talkNPC >= 0) Main.CloseNPCChatOrSign(); capturedNpc = -1; }
    public override void OnWorldUnload() { pending?.Completion.TrySetCanceled(); pending = null; capturedNpc = -1; }
    public override void Unload()
    {
        if (Main.dedServ) return;
        On_Main.DrawNPCChatButtons -= Buttons; On_Main.GUIChatDrawInner -= Draw;
        OnWorldUnload();
    }
}
