using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerraBlind;

public static class VanillaNavBridge
{
    public static bool Enabled;
    public static bool Terminal { get; private set; }
    static Dictionary<(int, int), int> field;
    static Vector2? failedAt;
    static int bestH = int.MaxValue;
    static uint progressAt;

    public static void Reset()
    {
        if (VanillaNavigationDemo.Instance.NavigationOwned) VanillaNavigationDemo.Instance.Stop();
        failedAt = null; bestH = int.MaxValue; field = null; Terminal = false;
    }

    public static bool Tick(Player player, int gx, int gy)
    {
        if (!Enabled) return false;
        var demo = VanillaNavigationDemo.Instance;
        if (StateSpacePlanner.ExecRunning || MineCoordinator.IsActive || ItemUseCoordinator.IsActive)
        {
            if (demo.NavigationOwned) demo.Stop("地形操作接管");
            return false;
        }
        if (failedAt.HasValue && Vector2.Distance(player.position, failedAt.Value) < 24) return false;
        if (player.itemTime > 0 || player.itemAnimation > 0 || player.controlUseItem) return true;
        field = MazeWand.PeekFieldOrNull(gx, gy);
        if (field == null) return false;
        int cx = (int)(player.Center.X / 16), cy = (int)(player.Bottom.Y / 16) - 1;
        if (field.TryGetValue((cx, cy), out int h) && h < bestH)
        { bestH = h; progressAt = Main.GameUpdateCount; }
        if (demo.NavigationOwned && !demo.PlannerReady) progressAt = Main.GameUpdateCount;
        if (demo.NavigationOwned && demo.PlannerReady && Main.GameUpdateCount - progressAt > 240)
        {
            DiagLog.Write($"[engine-nav] no H progress at ({cx},{cy}); yielding to terrain actions");
            failedAt = player.position; demo.Stop("局部移动无进展，交给挖掘/搭建"); return false;
        }
        Vector2? target = null;
        var destination = new Vector2(gx * 16 + 8 - player.width / 2, (gy + 1) * 16 - player.height);
        Terminal = Math.Abs(cx - gx) + Math.Abs(cy - gy) <= 12
            && !Collision.SolidCollision(destination, player.width, player.height);
        var cell = (x: cx, y: cy);
        for (int i = 0; i < 24 && cell != (gx, gy); i++)
        {
            var direction = MazeWand.FieldDir(field, cell.x, cell.y);
            if (direction == (0, 0)) break;
            cell = (cell.x + direction.dx, cell.y + direction.dy);
            var position = new Vector2(cell.x * 16 + 8 - player.width / 2, (cell.y + 1) * 16 - player.height);
            if (Vector2.Distance(player.position, position) < 40) continue;
            if (!Collision.SolidCollision(position, player.width, player.height)) target = position;
        }
        if (Terminal) target = destination;
        if (!target.HasValue)
        {
            if (demo.NavigationOwned) demo.Stop("前方需要改变地形");
            return false;
        }
        if (demo.NavigationOwned) demo.UpdateNavigationGoal(target.Value);
        else
        {
            try
            {
                demo.StartNavigation(target.Value); progressAt = Main.GameUpdateCount;
                DiagLog.Write($"[engine-nav] movement from ({cx},{cy}) toward {target.Value}");
            }
            catch (NotSupportedException error)
            { DiagLog.Write("[engine-nav] unsupported: " + error.Message); failedAt = player.position; return false; }
        }
        return true;
    }

    public static string CaptureGuidance(JsonObject snapshot)
    {
        if (field == null) return null;
        var result = new List<int>();
        int x0 = snapshot["area"][0].GetValue<int>(), y0 = snapshot["area"][1].GetValue<int>();
        int width = snapshot["area"][2].GetValue<int>(), height = snapshot["area"][3].GetValue<int>();
        for (int x = x0; x < x0 + width; x++)
        for (int y = y0; y < y0 + height; y++)
            if (field.TryGetValue((x, y), out int cost)) { result.Add(x); result.Add(y); result.Add(cost); }
        return VanillaFrame.PackInts(result.ToArray());
    }

    public static void TerrainAction() { failedAt = null; bestH = int.MaxValue; }
}
