using System;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class PlayerCombat
{
    public static int Target { get; private set; } = -1;
    public static string Outcome { get; private set; } = "idle";
    public static bool Active => Target >= 0;
    static int _frames;
    static float _range;

    public static string TargetReason(Player self, int target)
    {
        if (Main.netMode != NetmodeID.MultiplayerClient) return "multiplayer_required";
        if (target < 0 || target >= Main.maxPlayers) return "invalid_player";
        if (target == self.whoAmI) return "self_target";
        var other = Main.player[target];
        if (other == null || !other.active) return "target_offline";
        if (other.dead) return "target_dead";
        if (!other.hostile) return "target_pvp_disabled";
        if (self.team != 0 && self.team == other.team) return "same_team";
        return "";
    }

    public static bool Start(int target, int seconds, float range, out string reason)
    {
        Stop();
        var self = Main.LocalPlayer;
        reason = TargetReason(self, target);
        if (reason == "" && (!self.active || self.dead)) reason = "self_dead";
        if (reason == "" && !AgentPermissions.Attack) reason = "attack_forbidden";
        if (reason == "" && WeaponSlot(self) < 0) reason = "no_weapon_in_hotbar";
        if (reason == "" && DistanceSquared(self, Main.player[target]) > range * range * 256f) reason = "out_of_range";
        if (reason != "") { Outcome = reason; return false; }
        if (!self.hostile)
        {
            self.hostile = true;
            NetMessage.SendData(MessageID.TogglePVP, number: self.whoAmI);
        }
        Target = target;
        _frames = Math.Clamp(seconds, 1, 120) * 60;
        _range = range;
        Outcome = "running";
        return true;
    }

    public static void Stop(string outcome = "stopped")
    {
        if (Active) Main.LocalPlayer.controlUseItem = false;
        Target = -1;
        _frames = 0;
        Outcome = outcome;
    }

    public static void Tick(Player self)
    {
        if (!Active) return;
        string reason = TargetReason(self, Target);
        if (reason == "" && (!self.active || self.dead)) reason = "self_dead";
        if (reason == "" && !self.hostile) reason = "self_pvp_disabled";
        if (reason == "" && !AgentPermissions.Attack) reason = "attack_forbidden";
        if (reason != "") { Stop(reason); return; }
        if (_frames-- <= 0) { Stop("duration_complete"); return; }
        var other = Main.player[Target];
        if (DistanceSquared(self, other) > _range * _range * 256f) { Stop("out_of_range"); return; }
        int slot = WeaponSlot(self);
        if (slot < 0) { Stop("no_weapon_in_hotbar"); return; }
        Cursor.AimPx(other.position.X + other.width / 2f, other.position.Y + other.height / 2f);
        self.direction = other.position.X < self.position.X ? -1 : 1;
        self.selectedItem = slot;
        self.controlUseItem = true;
    }

    static float DistanceSquared(Player a, Player b)
    {
        float x = a.position.X + a.width / 2f - b.position.X - b.width / 2f;
        float y = a.position.Y + a.height / 2f - b.position.Y - b.height / 2f;
        return x * x + y * y;
    }

    static int WeaponSlot(Player player)
    {
        bool Weapon(int slot)
        {
            var item = player.inventory[slot];
            return item != null && !item.IsAir && item.damage > 0 && item.useStyle > 0
                && item.ammo == 0 && item.pick == 0 && item.axe == 0 && item.hammer == 0;
        }
        if (player.selectedItem >= 0 && player.selectedItem < 10 && Weapon(player.selectedItem)) return player.selectedItem;
        for (int slot = 0; slot < 10; slot++) if (Weapon(slot)) return slot;
        return -1;
    }
}
