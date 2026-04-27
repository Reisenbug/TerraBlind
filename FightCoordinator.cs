using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
    public static class FightCoordinator
    {
        private static volatile bool _active;
        private static volatile int _framesLeft;
        private static float _maxDistTiles = 20f;
        private const int TimeoutFrames = 360;

        public static bool IsActive => _active && _framesLeft > 0;

        public static void Start(float maxDistTiles)
        {
            _maxDistTiles = maxDistTiles;
            _framesLeft = TimeoutFrames;
            _active = true;
        }

        public static void Stop()
        {
            _active = false;
            _framesLeft = 0;
        }

        public static void Tick(Player p)
        {
            if (!_active || _framesLeft <= 0)
            {
                _active = false;
                return;
            }
            _framesLeft--;

            NPC target = FindNearest(p);
            if (target == null) return;

            int weaponSlot = FindWeaponSlot(p);
            if (weaponSlot < 0) return;

            float ncx = target.position.X + target.width / 2f;
            float ncy = target.position.Y + target.height / 2f;
            Main.mouseX = (int)(ncx - Main.screenPosition.X);
            Main.mouseY = (int)(ncy - Main.screenPosition.Y);

            p.selectedItem = weaponSlot;
            p.controlUseItem = true;
        }

        private static NPC FindNearest(Player p)
        {
            float pcx = p.position.X + p.width / 2f;
            float pcy = p.position.Y + p.height / 2f;
            float maxDist = _maxDistTiles * 16f;
            NPC best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < Main.maxNPCs; i++)
            {
                NPC npc = Main.npc[i];
                if (npc == null || !npc.active) continue;
                if (npc.townNPC || npc.friendly) continue;
                if (npc.lifeMax <= 5 && npc.damage == 0) continue;
                float ncx = npc.position.X + npc.width / 2f;
                float ncy = npc.position.Y + npc.height / 2f;
                float dx = ncx - pcx;
                float dy = ncy - pcy;
                float dist = dx * dx + dy * dy;
                if (dist < bestDist && System.MathF.Sqrt(dist) <= maxDist)
                {
                    bestDist = dist;
                    best = npc;
                }
            }
            return best;
        }

        private static int FindWeaponSlot(Player p)
        {
            for (int i = 0; i < 10; i++)
            {
                var item = p.inventory[i];
                if (item != null && !item.IsAir && item.ammo == AmmoID.None && item.damage > 0)
                    return i;
            }
            return -1;
        }
    }
}
