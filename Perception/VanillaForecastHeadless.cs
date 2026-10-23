using System;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.GameContent.Achievements;
using Terraria.ModLoader;

namespace TerraBlind;

public sealed class VanillaForecastHeadless : ModSystem
{
    bool installed;
    public override void Load()
    {
        if (!Main.dedServ || !Terraria.Program.LaunchParameters.ContainsKey("-vanillaforecastport")) return;
        VanillaFrame.RequireWorker();
        Main.GameViewMatrix = new Terraria.Graphics.SpriteViewMatrix(null);
        IL_Player.UpdateBiomes += SkipVisualTail;
        On_Player.ResetControls += SetControls;
        On_WorldGen.saveToonWhilePlaying += SkipSave;
        On_AchievementsHelper.HandleRunning += SkipRunning;
        On_AchievementsHelper.HandleSpecialEvent += SkipAchievement;
        installed = true;
    }

    static void SkipSave(On_WorldGen.orig_saveToonWhilePlaying original) { }
    static void SetControls(On_Player.orig_ResetControls original, Player player)
    {
        original(player);
        VanillaPrediction.ApplyKeys(player);
    }
    static void SkipRunning(On_AchievementsHelper.orig_HandleRunning original, float pixels) { }
    static void SkipAchievement(On_AchievementsHelper.orig_HandleSpecialEvent original, Player player, int id) { }

    static void SkipVisualTail(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(instruction => instruction.MatchLdsfld<Main>("shimmerAlpha")))
            throw new InvalidOperationException("vanilla_biome_visual_boundary_missing");
        cursor.EmitLdarg0();
        cursor.EmitDelegate<Action<Player>>(player => player.ZonePurity = player.InZonePurity());
        cursor.Emit(OpCodes.Ret);
    }

    public override void Unload()
    {
        if (installed) IL_Player.UpdateBiomes -= SkipVisualTail;
        if (installed) On_Player.ResetControls -= SetControls;
        if (installed) On_WorldGen.saveToonWhilePlaying -= SkipSave;
        if (installed) On_AchievementsHelper.HandleRunning -= SkipRunning;
        if (installed) On_AchievementsHelper.HandleSpecialEvent -= SkipAchievement;
        installed = false;
    }
}
