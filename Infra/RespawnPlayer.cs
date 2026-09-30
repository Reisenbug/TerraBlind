using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind
{
	public class RespawnPlayer : ModPlayer
	{
		public override void UpdateDead()
		{
			if (Player.whoAmI == Main.myPlayer && !Main.drawingPlayerChat
				&& Main.keyState.IsKeyDown(Keys.Space) && Main.oldKeyState.IsKeyUp(Keys.Space))
				Player.respawnTimer = 0;
		}

		public override void Kill(double damage, int hitDirection, bool pvp, PlayerDeathReason damageSource)
		{
			for (int i = 0; i < Main.maxNPCs; i++)
			{
				var npc = Main.npc[i];
				if (npc.active && (npc.boss || npc.type == NPCID.EaterofWorldsHead
					|| npc.type == NPCID.EaterofWorldsBody || npc.type == NPCID.EaterofWorldsTail)) return;
			}
			Player.respawnTimer = 5 * 60;
		}
	}
}
