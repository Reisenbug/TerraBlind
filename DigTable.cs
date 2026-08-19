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
        // Per-swing pickaxe damage, transcribed VERBATIM from vanilla Player.GetPickaxeDamage (Player.cs 1.4.5.4
        // L52756-52838). Bare type numbers kept exactly as vanilla (226, 211, 85, ...) — NOT translated to TileID
        // constants and NOT annotated with block names, so this can be diffed line-for-line against the source with
        // zero room for a mislabel. Returns 0 when the current pickaxe is too weak (= unmineable). ONLY字面 deviation
        // from source: vanilla's `tileTarget.type`/`.frameY` become tModLoader's `.TileType`/`.TileFrameY` (the Tile
        // wrapper renames the fields) — the numeric type IDs and all logic are untouched.
        //
        // NOT transcribed: the framed-object tail (vanilla L52839-52900, types 128/269/334 = racks/dressers). It has
        // side effects (mutates x/y, writes hitTile.UpdatePosition and Main.blockMouse) that a pure cost query must
        // not perform, and those are non-solid furniture the navigator never digs (it only mines solid blocks), so
        // the branch is unreachable here. If digging ever targets framed tiles, port that tail WITHOUT its writes.
        static int GetPickaxeDamage(int x, int y, int pickPower, Tile tileTarget)
        {
            int num = 0;
            if (Main.tileNoFail[tileTarget.TileType])
            {
                num = 100;
            }
            num = ((!Main.tileDungeon[tileTarget.TileType] && tileTarget.TileType != 58 && tileTarget.TileType != 25 && tileTarget.TileType != 117 && tileTarget.TileType != 203) ? ((tileTarget.TileType == 85) ? ((!Main.getGoodWorld) ? (num + pickPower) : (num + pickPower / 4)) : ((tileTarget.TileType != 48 && tileTarget.TileType != 232 && (tileTarget.TileType < 0 || !TileID.Sets.Clouds[tileTarget.TileType])) ? ((tileTarget.TileType == 226) ? (num + pickPower / 4) : ((tileTarget.TileType != 107 && tileTarget.TileType != 221) ? ((tileTarget.TileType != 108 && tileTarget.TileType != 222) ? ((tileTarget.TileType == 111 || tileTarget.TileType == 223) ? (num + pickPower / 4) : ((tileTarget.TileType != 211) ? (num + pickPower) : (num + pickPower / 5))) : (num + pickPower / 3)) : (num + pickPower / 2))) : (num + pickPower * 2))) : (num + pickPower / 2));
            if (tileTarget.TileType == 211 && pickPower < 200)
            {
                num = 0;
            }
            // vanilla guards this with `!Main.infectedSeed`, but tModLoader's Terraria assembly doesn't expose that
            // field. infectedSeed is a special drunk-world variant; assume a normal world (false) → keep the guard.
            if ((tileTarget.TileType == 25 || tileTarget.TileType == 203) && pickPower < 65)
            {
                num = 0;
            }
            else if (tileTarget.TileType == 117 && pickPower < 65)
            {
                num = 0;
            }
            else if (tileTarget.TileType == 37 && pickPower < 50)
            {
                num = 0;
            }
            else if ((tileTarget.TileType == 22 || tileTarget.TileType == 204) && (double)y > Main.worldSurface && pickPower < 55)
            {
                num = 0;
            }
            else if (tileTarget.TileType == 56 && pickPower < 55)
            {
                num = 0;
            }
            else if (tileTarget.TileType == 77 && pickPower < 65 && y >= Main.UnderworldLayer)
            {
                num = 0;
            }
            else if (tileTarget.TileType == 58 && pickPower < 65)
            {
                num = 0;
            }
            else if ((tileTarget.TileType == 226 || tileTarget.TileType == 237) && pickPower < 210)
            {
                num = 0;
            }
            else if (tileTarget.TileType == 137 && pickPower < 210 && (!Main.notTheBeesWorld || !Main.noTrapsWorld || Main.tenthAnniversaryWorld))
            {
                int num2 = tileTarget.TileFrameY / 18;
                if ((uint)(num2 - 1) <= 3u)
                {
                    num = 0;
                }
            }
            else if (Main.tileDungeon[tileTarget.TileType] && pickPower < 100 && (double)y > Main.worldSurface)
            {
                if ((double)x < (double)Main.maxTilesX * 0.35 || (double)x > (double)Main.maxTilesX * 0.65)
                {
                    num = 0;
                }
            }
            else if ((tileTarget.TileType == 107 || tileTarget.TileType == 221) && pickPower < 100)
            {
                num = 0;
            }
            else if ((tileTarget.TileType == 108 || tileTarget.TileType == 222) && pickPower < 110)
            {
                num = 0;
            }
            else if ((tileTarget.TileType == 111 || tileTarget.TileType == 223) && pickPower < 150)
            {
                num = 0;
            }
            if (tileTarget.TileType == 147 || tileTarget.TileType == 0 || tileTarget.TileType == 40 || tileTarget.TileType == 53 || tileTarget.TileType == 57 || tileTarget.TileType == 59 || tileTarget.TileType == 123 || tileTarget.TileType == 224 || tileTarget.TileType == 397)
            {
                num += pickPower;
            }
            if (tileTarget.TileType == 404)
            {
                num += 5;
            }
            if (tileTarget.TileType == 165 || Main.tileRope[tileTarget.TileType] || tileTarget.TileType == 199)
            {
                num = 100;
            }
            return num;
        }

        public const int Unmineable = 100000;

        // 挖开的【后果】,不是挖它要多久。蜂巢又软又快,纯按耗时算规划器专挑它走 —— 但捅破了
        // 蜂蜜流出来拖慢移动、放出小蜜蜂、还可能招来蜂王。有别的路就绕开,只有无路可走才付这个价。
        private const int HivePenalty = 3000;
        private static int Trouble(int type)
        {
            if (type == TileID.Hive) return HivePenalty;
            if (type == TileID.HoneyBlock || type == TileID.CrispyHoneyBlock) return HivePenalty / 3;
            return 0;
        }

        // 场和生成器必须共用同一套"能不能挖"的定义:漏掉 CanKillTile 那次,树根块被算成便宜的 160,
        // 线穿过去了但每个生成器都拒绝,人就被钉在树下电死((1335,223))
        public static bool MineableWith(int x, int y, int pickPower)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return false;
            if (pickPower <= 0) return false;
            var tile = Main.tile[x, y];
            if (!tile.HasTile) return true;
            int dmg = GetPickaxeDamage(x, y, pickPower, tile);
            if (Main.getGoodWorld) dmg *= 2;
            return dmg > 0 && Terraria.WorldGen.CanKillTile(x, y);
        }

        // 真实挖掘帧数:swings=ceil(100/伤害),再乘 useTime 和 pickSpeed。伤害 0 或 CanKillTile 假 = 挖不动
        public static int CostFrames(int x, int y)
        {
            if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) return Unmineable;
            var p = Main.LocalPlayer;
            if (p == null) return Unmineable;
            int slot = -1;
            for (int i = 0; i < 10; i++) { var it = p.inventory[i]; if (it != null && !it.IsAir && it.pick > 0) { slot = i; break; } }
            if (slot < 0) return Unmineable;
            var pick = p.inventory[slot];
            var tile = Main.tile[x, y];
            int dmg = GetPickaxeDamage(x, y, pick.pick, tile);
            if (Main.getGoodWorld) dmg *= 2;
            if (dmg <= 0 || !Terraria.WorldGen.CanKillTile(x, y)) return Unmineable;
            int swings = (100 + dmg - 1) / dmg;
            return (int)(swings * pick.useTime * p.pickSpeed) + Trouble(tile.TileType);
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
            // one representative (x,y) per tile type so CostFrames sees real coords (some thresholds are depth/x-gated)
            var seen = new Dictionary<ushort, (int x, int y, int cnt)>();
            for (int y = pcy - 12; y <= pcy + 12; y++)
                for (int x = pcx - 14; x <= pcx + 14; x++)
                {
                    if (x < 0 || y < 0 || x >= Main.maxTilesX || y >= Main.maxTilesY) continue;
                    var t = Main.tile[x, y];
                    if (Predicates.IsSolid(x, y))
                        seen[t.TileType] = seen.TryGetValue(t.TileType, out var e) ? (e.x, e.y, e.cnt + 1) : (x, y, 1);
                }

            var sb = new StringBuilder("[digtable] surrounding blocks (type frames count):\n");
            foreach (var kv in seen)
            {
                int frames = CostFrames(kv.Value.x, kv.Value.y);
                sb.Append($"  type={kv.Key} frames={(frames >= Unmineable ? "UNMINEABLE" : frames.ToString())} cnt={kv.Value.cnt}\n");
            }
            DiagLog.Write(sb.ToString());
        }
    }
}
