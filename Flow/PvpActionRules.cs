using Terraria;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerraBlind;

public class PvpActionRules : GlobalItem
{
    public static bool AllowedAt(Player player, Item item, int x, int y)
    {
        if (player == Main.LocalPlayer && AgentNetwork.CanAutomate)
        {
            var behavior = item.pick > 0 || item.axe > 0 || item.hammer > 0 ? ControlBehavior.Mining
                : item.createTile >= 0 || item.createWall >= 0 ? ControlBehavior.Placement
                : item.damage > 0 ? ControlBehavior.Attack : ControlBehavior.Direct;
            if (!AgentTools.ControlAllows(behavior, ControlChannels.ItemUse, delegated: true)) return false;
            if (AgentTools.Control.CheckAt(behavior, x, y) != null) return false;
        }
        if (!PvpDuel.Active || player != Main.LocalPlayer || !AgentNetwork.CanAutomate) return true;
        var rules = PvpDuel.Directive;
        if (item.pick > 0 || item.axe > 0 || item.hammer > 0) return rules.AllowsTerrain(false, x, y);
        if (item.createTile >= 0 || item.createWall >= 0)
        {
            var data = item.createTile >= 0 ? TileObjectData.GetTileData(item.createTile, item.placeStyle) : null;
            int left = x - (data?.Origin.X ?? 0), top = y - (data?.Origin.Y ?? 0);
            for (int dx = 0; dx < (data?.Width ?? 1); dx++)
            for (int dy = 0; dy < (data?.Height ?? 1); dy++)
                if (!rules.AllowsTerrain(true, left + dx, top + dy)) return false;
            return true;
        }
        return item.damage <= 0 || rules.AllowsWeapon(item.type);
    }

    public override bool CanUseItem(Item item, Player player)
        => AllowedAt(player, item, Player.tileTargetX, Player.tileTargetY);
}

public class PvpMiningRules : ModSystem
{
    public bool Installed { get; private set; }
    public override void Load()
    {
        if (Main.dedServ) return;
        On_Player.PickTile += PickTile; Installed = true;
    }
    public override void Unload()
    {
        if (Installed) On_Player.PickTile -= PickTile;
        Installed = false;
    }
    static void PickTile(On_Player.orig_PickTile original, Player self, int x, int y, int power)
    {
        if (self == Main.LocalPlayer && AgentNetwork.CanAutomate
            && !AgentTools.ControlAllows(ControlBehavior.Mining, ControlChannels.ItemUse, delegated: true)) return;
        if (self == Main.LocalPlayer && AgentNetwork.CanAutomate
            && AgentTools.Control.CheckAt(ControlBehavior.Mining, x, y) != null) return;
        if (PvpDuel.Active && self == Main.LocalPlayer && AgentNetwork.CanAutomate
            && !PvpDuel.Directive.AllowsTerrain(false, x, y)) return;
        original(self, x, y, power);
    }
}
