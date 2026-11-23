using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;

namespace TerraBlind;

public sealed class AgentPanel : ModSystem
{
    sealed record Bot(int Id, string Name, int Owner);
    sealed record Message(int Bot, bool Sent, string Text);
    static readonly List<Bot> bots = new();
    static readonly List<Message> messages = new();
    static readonly Dictionary<int, string> statuses = new();
    static int messageVersion;
    static bool unread;
    static ulong refreshAt;
    static string notice = "";

    UserInterface ui;
    PanelState state;

    static bool Multiplayer => Main.netMode == NetmodeID.MultiplayerClient;
    static string T(string en, string zh) => Language.ActiveCulture.Name == "zh-Hans" ? zh : en;
    static bool Visible => !Main.gameMenu && !Main.mapFullscreen && !Main.ingameOptionsWindow
        && (Multiplayer ? !Config.I.AiClient : Config.I.AgentEnabled);

    public override void Load()
    {
        if (Main.dedServ) return;
        ui = new UserInterface();
        state = new PanelState();
        ui.SetState(state);
    }

    public override void OnWorldUnload()
    {
        state?.Close();
        bots.Clear(); messages.Clear(); statuses.Clear(); notice = ""; refreshAt = 0; unread = false; messageVersion++;
    }

    public override void UpdateUI(GameTime time)
    {
        if (!Visible) { state?.Unfocus(); return; }
        if (Multiplayer && state.Open && refreshAt != 0 && Main.GameUpdateCount >= refreshAt)
        {
            refreshAt = 0;
            Refresh();
        }
        ui?.Update(time);
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        int index = layers.FindIndex(layer => layer.Name == "Vanilla: Mouse Text");
        if (index < 0) return;
        layers.Insert(index, new LegacyGameInterfaceLayer("TerraBlind: AI panel", () =>
        {
            if (Visible) ui?.Draw(Main.spriteBatch, Main._drawInterfaceGameTime);
            return true;
        }, InterfaceScaleType.UI));
    }

    internal static void Record(int bot, bool sent, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        messages.Add(new Message(bot, sent, text));
        if (messages.Count > 80) messages.RemoveAt(0);
        if (!sent) unread = true;
        messageVersion++;
    }

    internal static bool Receive(string text, string request)
    {
        if (request == "list" && ReadRoster(text))
        {
            notice = bots.Count == 0 ? T("No AI clients online.", "没有 AI 在线。") : "";
            return true;
        }
        if (text == "已解除绑定并停止 AI。")
        {
            notice = T("Unbound.", "已解绑。");
            refreshAt = Main.GameUpdateCount + 1;
            return true;
        }
        if (!text.StartsWith("AI[", StringComparison.Ordinal)) return false;
        int end = text.IndexOf("] ", StringComparison.Ordinal);
        if (end < 3 || !int.TryParse(text.AsSpan(3, end - 3), out int id)) return false;
        string content = text[(end + 2)..];
        if (content.StartsWith("流程=", StringComparison.Ordinal))
        {
            statuses[id] = content;
            if (request == "bind") { notice = T("Refreshing owner…", "正在刷新绑定……"); refreshAt = Main.GameUpdateCount + 1; }
            return request != "status";
        }
        if (content == "指令已交给内置 agent。")
        {
            notice = T("Instruction accepted.", "已收到指令。");
            return true;
        }
        Record(id, false, content);
        return true;
    }

    internal static bool MatchesReply(string text, string request, int bot)
    {
        if (!text.StartsWith("AI[", StringComparison.Ordinal)) return true;
        string prefix = $"AI[{bot}] ";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string message = text[prefix.Length..];
        if (request is "bind" or "status") return message.StartsWith("流程=", StringComparison.Ordinal);
        if (request == "task") return message == "指令已交给内置 agent。";
        return request != "list" && !message.StartsWith("流程=", StringComparison.Ordinal);
    }

    internal static void RequestTimeout() => notice = T("Server did not reply.", "服务器未回复。");
    internal static void Notice(string text) => notice = text;

    static bool ReadRoster(string text)
    {
        var found = new List<Bot>();
        const string ownerMarker = " 所有者=";
        if (text != "没有 AI 在线。")
        {
            int start = 0;
            while (start < text.Length)
            {
                int colon = text.IndexOf(':', start);
                if (colon < start || !int.TryParse(text.AsSpan(start, colon - start), out int id)
                    || id < 0 || id >= Main.maxPlayers) return false;
                string name = Main.player[id].name;
                string identity = name + ownerMarker;
                if (!text.AsSpan(colon + 1).StartsWith(identity, StringComparison.Ordinal)) return false;
                int ownerStart = colon + 1 + identity.Length;
                int end = text.IndexOf("; ", ownerStart, StringComparison.Ordinal);
                if (!int.TryParse(text.AsSpan(ownerStart, (end < 0 ? text.Length : end) - ownerStart), out int owner)) return false;
                found.Add(new Bot(id, name, owner));
                if (end < 0) break;
                start = end + 2;
            }
            if (found.Count == 0) return false;
        }
        bots.Clear(); bots.AddRange(found.OrderBy(bot => bot.Id));
        return true;
    }

    static void Refresh()
    {
        if (AgentNetwork.Request(-1, "list", fromPanel: true)) notice = T("Refreshing…", "正在刷新……");
    }

    sealed class PanelState : UIState
    {
        UIPanel window;
        UIText headline, status;
        UITextPanel<string> binding, toggle;
        UIList chat;
        DraftField draft;
        int selected = -1, shownBot = int.MinValue, shownVersion = -1;
        Bot Current => bots.FirstOrDefault(bot => bot.Id == selected && Main.player[bot.Id].active && Main.player[bot.Id].name == bot.Name);
        public bool Open { get; private set; }

        static void Place(UIElement element, float x, float y, float width, float height)
        {
            element.Left.Set(x, 0); element.Top.Set(y, 0); element.Width.Set(width, 0); element.Height.Set(height, 0);
        }

        static UITextPanel<string> Button(UIElement parent, string label, float x, float y, float width, Action press)
        {
            var button = new UITextPanel<string>(label, .8f);
            Place(button, x, y, width, 27);
            button.OnLeftClick += (_, _) => press();
            parent.Append(button);
            return button;
        }

        public override void OnInitialize()
        {
            toggle = Button(this, "AI", -12, -12, 42, Toggle);
            toggle.HAlign = toggle.VAlign = 1;
            window = new UIPanel();
            Place(window, -12, -46, 362, 326);
            window.HAlign = window.VAlign = 1;
            headline = new UIText("", .86f);
            Place(headline, 0, 0, 330, 20); window.Append(headline);
            Button(window, "‹", 0, 23, 28, () => Cycle(-1));
            Button(window, "›", 32, 23, 28, () => Cycle(1));
            binding = Button(window, T("Bind", "绑定"), 68, 23, 76, Bind);
            Button(window, T("Refresh", "刷新"), 152, 23, 80, () => { if (Multiplayer) Refresh(); });
            status = new UIText("", .72f);
            Place(status, 0, 56, 330, 18); window.Append(status);
            chat = new UIList { ListPadding = 7, ManualSortMethod = _ => { } };
            Place(chat, 0, 78, 316, 183);
            var scrollbar = new UIScrollbar();
            Place(scrollbar, 322, 78, 15, 183);
            chat.SetScrollbar(scrollbar);
            window.Append(chat); window.Append(scrollbar);
            draft = new DraftField(Send);
            Place(draft, 0, 270, 254, 31); window.Append(draft);
            Button(window, T("Send", "发送"), 261, 271, 75, Send);
        }

        void Toggle()
        {
            if (Open) { Close(); return; }
            Open = true; Append(window); Recalculate();
            shownVersion = -1;
            if (Multiplayer) Refresh();
        }

        public void Unfocus() => draft?.Unfocus();

        public void Close()
        {
            if (!Open) return;
            Unfocus(); window.Remove(); Open = false; selected = -1;
        }

        void Cycle(int direction)
        {
            if (!Multiplayer || bots.Count == 0) return;
            int index = bots.FindIndex(bot => bot.Id == selected);
            selected = bots[(index + direction + bots.Count) % bots.Count].Id;
        }

        void Bind()
        {
            var bot = Current;
            if (!Multiplayer || bot == null || bot.Owner >= 0 && bot.Owner != Main.myPlayer) return;
            if (AgentNetwork.Request(bot.Id, bot.Owner < 0 ? "bind" : "unbind", fromPanel: true))
                notice = T("Waiting for server…", "等待服务器……");
        }

        void Send()
        {
            string instruction = draft.Text.Trim();
            if (instruction.Length == 0) return;
            if (Multiplayer)
            {
                var bot = Current;
                if (bot == null || bot.Owner != Main.myPlayer) { notice = T("Bind a bot first.", "请先绑定 AI。"); return; }
                if (!AgentNetwork.Request(bot.Id, "task " + instruction, fromPanel: true)) return;
            }
            else AgentChat.SubmitLocal(instruction);
            notice = "";
            draft.SetText("");
        }

        public override void Update(GameTime time)
        {
            var mouse = new Vector2(Main.mouseX, Main.mouseY);
            if (Open && window.ContainsPoint(mouse) || toggle.ContainsPoint(mouse)) Main.LocalPlayer.mouseInterface = true;
            base.Update(time);
            if (!Open) { toggle.SetText(unread ? "AI •" : "AI"); return; }
            unread = false; toggle.SetText("AI");
            if (Multiplayer && Current == null) selected = bots.FirstOrDefault()?.Id ?? -1;
            var bot = Multiplayer ? Current : null;
            headline.SetText(!Multiplayer ? "TerraBlind" : bot == null ? T("Online AI", "在线 AI")
                : bot.Name + "  #" + bot.Id + "  ·  " + (bot.Owner < 0 ? T("Unbound", "未绑定")
                    : bot.Owner == Main.myPlayer ? T("Bound to you", "已绑定给你") : T("Bound", "已绑定")));
            binding.SetText(bot?.Owner == Main.myPlayer ? T("Unbind", "解绑") : T("Bind", "绑定"));
            string current = !Multiplayer ? NativeAgent.Status : bot != null ? statuses.GetValueOrDefault(bot.Id, "") : "";
            string line = notice.Length != 0 ? notice : current;
            status.SetText(line.Length > 54 ? line[..54] + "…" : line);
            int conversation = Multiplayer ? bot?.Id ?? -2 : -1;
            if (shownVersion == messageVersion && shownBot == conversation) return;
            shownVersion = messageVersion; shownBot = conversation;
            chat.Clear();
            foreach (var entry in messages.Where(message => message.Bot == conversation))
            {
                var bubble = new UIText((entry.Sent ? T("You  ", "你  ") : "AI  ") + entry.Text, .78f)
                { IsWrapped = true, TextColor = entry.Sent ? new Color(142, 211, 255) : new Color(228, 239, 228), IgnoresMouseInteraction = true };
                bubble.Width.Set(0, 1);
                chat.Add(bubble);
            }
            chat.Recalculate();
            chat.ViewPosition = chat.GetTotalHeight();
        }
    }

    sealed class DraftField(Action send) : UITextPanel<string>("", .83f)
    {
        bool focused;
        string draft = "";
        public new string Text => draft;
        public override void LeftClick(UIMouseEvent evt)
        {
            base.LeftClick(evt);
            Main.clrInput();
            focused = true;
        }
        public void Unfocus() => focused = false;
        public new void SetText(string value) => draft = value;

        public override void Update(GameTime time)
        {
            if (Main.mouseLeft && !ContainsPoint(new Vector2(Main.mouseX, Main.mouseY))) Unfocus();
            if (focused && !Main.drawingPlayerChat && Main.hasFocus)
            {
                PlayerInput.WritingText = true;
                Main.CurrentInputTextTakerOverride = this;
                Main.instance.HandleIME();
                draft = Main.GetInputText(draft);
                if (draft.Length > 3900) draft = draft[..3900];
                if (Main.inputTextEscape) { Main.inputTextEscape = false; Unfocus(); }
                if (Main.inputTextEnter) { Main.inputTextEnter = false; send(); }
            }
            base.SetText(draft.Length == 0 ? T("Message…", "发消息……") : draft.Length > 28 ? "…" + draft[^28..] : draft);
            base.Update(time);
        }
    }
}
