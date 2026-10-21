using System;
using MonoMod.Cil;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind;

public class BackgroundAutomation : ModSystem
{
    public static bool Installed { get; private set; }
    static bool wasBackground;

    public override void Load()
    {
        if (Main.dedServ) return;
        IL_Player.Update += Patch;
    }

    static void Patch(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After,
            instruction => instruction.MatchCall<Player>("ResetControls"),
            instruction => instruction.MatchLdsfld<Main>("hasFocus")))
            throw new InvalidOperationException("TerraBlind 未找到后台自动操作的输入入口。");
        cursor.EmitLdarg0();
        cursor.EmitDelegate<Func<bool, Player, bool>>(Apply);
        Installed = true;
        DiagLog.Write("[background] control hook installed");
    }

    static bool Apply(bool focused, Player player)
    {
        bool background = !focused && !Main.gameMenu && Main.netMode == NetmodeID.MultiplayerClient
            && Config.I.AiClient && AgentNetwork.CanAutomate && player == Main.LocalPlayer;
        if (background != wasBackground)
        {
            wasBackground = background;
            DiagLog.Write($"[background] automation={background} focused={focused}");
        }
        if (background) player.GetModPlayer<StateSnapshotPlayer>().SetControls();
        return focused;
    }

    public override void Unload()
    {
        if (Installed) IL_Player.Update -= Patch;
        Installed = false; wasBackground = false;
    }
}
