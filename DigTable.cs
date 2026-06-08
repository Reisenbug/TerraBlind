using System.Collections.Generic;
using System.Text;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
    public class DigTableSystem : ModSystem
    {
        public static volatile bool Pending;
        public override void PostUpdateEverything() { if (Pending) { Pending = false; DigTable.Dump(); } }
    }

    public static class DigTable
    {
        static (string cat, int div, int minPick) Category(ushort type)
        {
            switch (type)
            {
                case TileID.Meteorite: return ("meteorite", 1, 0);
                case TileID.Demonite: case TileID.Crimtane: return ("evil_ore", 1, 0);
                case TileID.Cobalt: case TileID.Palladium: return ("cobalt", 1, 100);
                case TileID.Mythril: case TileID.Orichalcum: return ("mythril", 2, 110);
                case TileID.Adamantite: case TileID.Titanium: return ("adamantite", 3, 150);
                case TileID.Chlorophyte: return ("chlorophyte", 5, 200);
                case TileID.LihzahrdBrick: return ("lihzahrd", 4, 210);
                case TileID.BlueDungeonBrick: case TileID.GreenDungeonBrick: case TileID.PinkDungeonBrick:
                case TileID.Crimstone: case TileID.Ebonstone: case TileID.Pearlstone:
                    return ("dungeon/evilstone", 2, 0);
                default: return ("normal", 1, 0);
            }
        }

        public const int Unmineable = 100000;
        public static int CostFrames(ushort type)
        {
            var p = Main.LocalPlayer;
            if (p == null) return Unmineable;
            int slot = -1;
            for (int i = 0; i < 10; i++) { var it = p.inventory[i]; if (it != null && !it.IsAir && it.pick > 0) { slot = i; break; } }
            if (slot < 0) return Unmineable;
            var pick = p.inventory[slot];
            var (_, div, minPick) = Category(type);
            if (pick.pick < minPick) return Unmineable;
            int perSwing = System.Math.Max(1, pick.pick / div);
            int swings = (100 + perSwing - 1) / perSwing;
            return (int)(swings * pick.useTime * p.pickSpeed);
        }

        public static void Dump()
        {
            var p = Main.LocalPlayer;
            if (p == null || !p.active) { DiagLog.Write("[digtable] no player"); return; }

            int slot = -1;
            for (int i = 0; i < 10; i++) { var it = p.inventory[i]; if (it != null && !it.IsAir && it.pick > 0) { slot = i; break; } }
            if (slot < 0) { DiagLog.Write("[digtable] no pickaxe in hotbar"); return; }
            var pick = p.inventory[slot];
            int pickPower = pick.pick;
            int useTime = pick.useTime;
            float pickSpeed = p.pickSpeed;
            DiagLog.Write($"[digtable] pickaxe='{pick.Name}' pickPower={pickPower} useTime={useTime} pickSpeed={pickSpeed:0.##}");

            int pcx = (int)(p.Center.X / 16f), pcy = (int)(p.Center.Y / 16f);
            var seen = new Dictionary<ushort, int>();
            for (int y = pcy - 12; y <= pcy + 12; y++)
                for (int x = pcx - 14; x <= pcx + 14; x++)
                {
                    if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
                    var t = Main.tile[x, y];
                    if (t.HasTile && Main.tileSolid[t.TileType]) seen[t.TileType] = seen.TryGetValue(t.TileType, out int c) ? c + 1 : 1;
                }

            var sb = new StringBuilder("[digtable] surrounding blocks (type:name cat canMine perSwing swings frames count):\n");
            foreach (var kv in seen)
            {
                ushort type = kv.Key;
                var (cat, div, minPick) = Category(type);
                bool canMine = pickPower >= minPick;
                int perSwing = canMine ? System.Math.Max(1, pickPower / div) : 0;
                int swings = canMine ? (100 + perSwing - 1) / perSwing : -1;
                int frames = canMine ? (int)(swings * useTime * pickSpeed) : -1;
                sb.Append($"  type={type} {cat} mine={canMine} per={perSwing} swings={swings} frames={frames} cnt={kv.Value}\n");
            }
            DiagLog.Write(sb.ToString());
        }
    }
}
