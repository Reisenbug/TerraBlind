using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind;

public class AgentContactEscape : ModSystem
{
    long attemptedDamageTick = -1;
    int direction, frames;
    float startX;
    ItemUseRequest clearing;
    bool clearingStarted;
    int clearingType, clearedTiles;

    static bool SafeAfterRemoval(Player player, int x, int y)
    {
        if (y * 16 < player.Bottom.Y - 1 || !Main.tileSolid[Main.tile[x, y].TileType]) return true;
        for (int floor = y + 1; floor < Main.maxTilesY - 1; floor++)
        {
            var tile = Main.tile[x, floor];
            if (tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Lava) return false;
            if (!tile.HasUnactuatedTile || !Main.tileSolid[tile.TileType] && !Main.tileSolidTop[tile.TileType]) continue;
            return FallSafety.LandingDamage(player, new Vector2(player.position.X, floor * 16 - player.height), player.fallStart) == 0;
        }
        return false;
    }

    static ItemUseRequest ClearContact(Player player)
    {
        if (!AgentTools.ControlAllows(ControlBehavior.Mining, ControlChannels.ItemUse)) return null;
        var hit = Collision.HurtTiles(player.position, player.width, player.height, player);
        if (hit.type < 0 || TileID.Sets.TouchDamageImmediate[hit.type] <= 0
            || AgentTools.Control.CheckAt(ControlBehavior.Mining, hit.x, hit.y) != null || !SafeAfterRemoval(player, hit.x, hit.y)) return null;
        int slot = -1, best = DigTable.Unmineable;
        for (int i = 0; i < 50; i++)
        {
            var item = player.inventory[i];
            if (item.IsAir || item.pick <= 0 || !Reach.CanMine(player, hit.x, hit.y, item)) continue;
            int cost = DigTable.CostFrames(hit.x, hit.y, player, item);
            if (cost < best) { slot = i; best = cost; }
        }
        return slot < 0 ? null : new ItemUseRequest { TargetWx = hit.x, TargetWy = hit.y, Slot = slot, Strict = true };
    }

    static bool Harmful(Vector2 position, Player player, bool ignoreCurrentContact = false, bool includeLava = true)
    {
        int left = (int)Math.Floor((position.X - 1) / 16), right = (int)Math.Floor((position.X + player.width) / 16);
        int top = (int)Math.Floor((position.Y - 1) / 16), bottom = (int)Math.Floor((position.Y + player.height) / 16);
        for (int x = left; x <= right; x++)
        for (int y = top; y <= bottom; y++)
        {
            if (!WorldGen.InWorld(x, y)) return true;
            var tile = Main.tile[x, y];
            bool previous = ignoreCurrentContact && x >= Math.Floor((player.position.X - 1) / 16)
                && x <= Math.Floor((player.position.X + player.width) / 16)
                && y >= Math.Floor((player.position.Y - 1) / 16) && y <= Math.Floor((player.position.Y + player.height) / 16);
            if (!previous && tile.HasUnactuatedTile && TileID.Sets.TouchDamageImmediate[tile.TileType] > 0
                || includeLava && tile.LiquidAmount > 0 && tile.LiquidType == LiquidID.Lava) return true;
        }
        return false;
    }

    static bool SafeSide(Player player, int direction)
    {
        for (int distance = 8; distance <= 80; distance += 8)
        {
            var position = player.position + new Vector2(direction * distance, 0);
            if (Collision.SolidCollision(position, player.width, player.height) || Harmful(position, player, ignoreCurrentContact: true)) return false;
            if (distance < 48) continue;
            bool floor = false;
            for (int drop = 0; drop <= 48; drop += 2)
            {
                var landing = position + new Vector2(0, drop);
                if (Harmful(landing, player)) return false;
                if (Collision.SolidCollision(landing, player.width, player.height)) { floor = true; break; }
            }
            if (!floor) return false;
        }
        return true;
    }

    void Move() => ActExecutor.Start(new List<ActStep> { new() { Left = direction < 0, Right = direction > 0,
        UntilKind = "frames", UntilN = 60 } }, 90);

    public override void PostUpdateEverything()
    {
        if (Main.dedServ || Main.gameMenu) return;
        var player = Main.LocalPlayer;
        if (!Config.I.AgentEnabled || !AgentNetwork.CanAutomate || !AgentTools.Watches.Armed
            || !AgentPermissions.Dodge || !AgentTools.ControlAllows(ControlBehavior.Survival, ControlChannels.All)
            || PvpDuel.Active || player.dead || player.gravDir != 1)
        {
            if (AgentTools.ContactEscapeActive)
            {
                ActExecutor.Stop();
                if (clearing != null) ItemUseCoordinator.Stop();
                clearing = null;
                AgentTools.LastContactEscape["status"] = "cancelled";
                AgentTools.LastContactEscape["end_tick"] = (long)Main.GameUpdateCount;
                DiagLog.Write("[agent_escape] cancelled");
            }
            AgentTools.ContactEscapeActive = false; attemptedDamageTick = -1; return;
        }
        bool touching = Harmful(player.position, player, includeLava: false);
        if (!AgentTools.ContactEscapeActive)
        {
            if (!touching) return;
            var damage = AgentTools.RecentContactDamage((long)Main.GameUpdateCount);
            if (damage == null || damage["tick"].GetValue<long>() == attemptedDamageTick) return;
            attemptedDamageTick = damage["tick"].GetValue<long>();
            direction = SafeSide(player, -1) ? -1 : SafeSide(player, 1) ? 1 : 0;
            clearing = direction == 0 ? ClearContact(player) : null;
            if (direction == 0 && clearing == null)
            { DiagLog.Write("[agent_escape] no_checked_side"); return; }
            AgentTools.StopInputs();
            AgentTools.ContactEscapeActive = true;
            frames = clearedTiles = 0; clearingStarted = false; startX = player.position.X;
            AgentTools.LastContactEscape = new JsonObject { ["tick"] = (long)Main.GameUpdateCount, ["status"] = clearing == null ? "moving" : "clearing_contact",
                ["reason"] = "contact_damage", ["direction"] = direction < 0 ? "left" : direction > 0 ? "right" : "none",
                ["start_pos"] = new JsonObject { ["x"] = player.position.X, ["y"] = player.position.Y }, ["hp_start"] = player.statLife,
                ["damage_event"] = damage.DeepClone(), ["hazard_tiles"] = new JsonArray(TerrainInspection.Read(2)["near_body_tiles"].AsArray()
                    .Where(tile => tile["touch_damage"].GetValue<int>() > 0).Select(tile => tile.DeepClone()).ToArray()) };
            if (clearing == null) Move();
            DiagLog.Write($"[agent_escape] start direction={direction} x={startX} hp={player.statLife}");
            return;
        }
        frames++;
        if (clearing != null && frames < 90)
        {
            if (!clearingStarted)
            {
                if (player.itemAnimation != 0 || player.itemTime != 0) return;
                clearingType = Main.tile[clearing.TargetWx, clearing.TargetWy].TileType;
                ItemUseCoordinator.Start(clearing); clearingStarted = true;
                DiagLog.Write($"[agent_escape] clear_contact x={clearing.TargetWx} y={clearing.TargetWy} type={clearingType} slot={clearing.Slot}");
                return;
            }
            var tile = Main.tile[clearing.TargetWx, clearing.TargetWy];
            if (tile.HasTile && tile.TileType == clearingType && ItemUseCoordinator.IsActive) return;
            bool removed = !tile.HasTile || tile.TileType != clearingType;
            ItemUseCoordinator.Stop(); clearing = null;
            if (removed) clearedTiles++;
            AgentTools.LastContactEscape["cleared_tiles"] = clearedTiles;
            DiagLog.Write($"[agent_escape] clear_result removed={removed} count={clearedTiles} hp={player.statLife}");
        }
        if (direction == 0 && frames < 90)
        {
            direction = SafeSide(player, -1) ? -1 : SafeSide(player, 1) ? 1 : 0;
            if (direction != 0)
            {
                startX = player.position.X; Move();
                AgentTools.LastContactEscape["status"] = "moving";
                AgentTools.LastContactEscape["direction"] = direction < 0 ? "left" : "right";
            }
            else if ((clearing = ClearContact(player)) != null)
            { clearingStarted = false; return; }
        }
        bool grounded = Collision.SolidCollision(player.position + new Vector2(0, 1), player.width, player.height);
        if (!touching && grounded || direction * (player.position.X - startX) >= 48 || frames >= 90)
        {
            ActExecutor.Stop();
            if (clearing != null) ItemUseCoordinator.Stop();
            clearing = null;
            AgentTools.ContactEscapeActive = false;
            AgentTools.LastContactEscape["status"] = "controls_released";
            AgentTools.LastContactEscape["end_tick"] = (long)Main.GameUpdateCount;
            AgentTools.LastContactEscape["frames"] = frames;
            AgentTools.LastContactEscape["contact_clear"] = !touching;
            AgentTools.LastContactEscape["grounded"] = grounded;
            AgentTools.LastContactEscape["end_pos"] = new JsonObject { ["x"] = player.position.X, ["y"] = player.position.Y };
            AgentTools.LastContactEscape["hp_end"] = player.statLife;
            DiagLog.Write($"[agent_escape] end frames={frames} clear={!touching} grounded={grounded} x={player.position.X} hp={player.statLife}");
        }
        else if (direction != 0 && !ActExecutor.IsActive) Move();
    }

    public override void OnWorldUnload()
    { AgentTools.ContactEscapeActive = false; AgentTools.LastContactEscape = null; attemptedDamageTick = -1; }
}
