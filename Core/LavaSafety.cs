using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class LavaSafety
{
    public static bool IsLava(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
        var tile = Main.tile[x, y];
        return tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Lava;
    }

    public static bool CanMine(int x, int y)
    {
        if (x <= 0 || y <= 0 || x >= Main.maxTilesX - 1 || y >= Main.maxTilesY - 1) return false;
        var tile = Main.tile[x, y];
        if (tile.HasTile && tile.TileType == TileID.Hellstone) return false;
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
            if (IsLava(x + dx, y + dy)) return false;
        return true;
    }
}
