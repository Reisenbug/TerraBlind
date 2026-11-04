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
        IL_Main.DoUpdate += PatchBackgroundPause;
    }

    static bool LocalNavigation => Main.netMode == NetmodeID.SinglePlayer
        && (RecedingNav.Active || VanillaNavigationDemo.Active || ItemUseCoordinator.IsActive);

    static void PatchBackgroundPause(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After, instruction => instruction.MatchStsfld<Main>("hasFocus"))
            || !cursor.TryGotoNext(MoveType.After, instruction => instruction.MatchLdsfld<Main>("hasFocus")))
            throw new InvalidOperationException("TerraBlind 未找到单人后台寻路的暂停入口。");
        cursor.EmitDelegate<Func<bool, bool>>(focused => focused || LocalNavigation);
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
        bool background = !focused && !Main.gameMenu && AgentNetwork.CanAutomate && player == Main.LocalPlayer
            && (LocalNavigation || Main.netMode == NetmodeID.MultiplayerClient && Config.I.AiClient);
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
        IL_Main.DoUpdate -= PatchBackgroundPause;
        Installed = false; wasBackground = false;
    }
}
