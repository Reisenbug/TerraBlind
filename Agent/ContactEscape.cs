using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace TerraBlind;

public class AgentContactEscape : ModSystem
{
    bool attempted;
    int direction, frames;
    float startX;

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
            || !AgentPermissions.Dodge || PvpDuel.Active || player.dead || player.gravDir != 1)
        {
            if (AgentTools.ContactEscapeActive) { ActExecutor.Stop(); DiagLog.Write("[agent_escape] cancelled"); }
            AgentTools.ContactEscapeActive = false; attempted = false; return;
        }
        bool touching = Harmful(player.position, player, includeLava: false);
        if (!AgentTools.ContactEscapeActive)
        {
            if (!touching) { attempted = false; return; }
            if (attempted) return;
            attempted = true;
            direction = SafeSide(player, -1) ? -1 : SafeSide(player, 1) ? 1 : 0;
            if (direction == 0)
            { DiagLog.Write("[agent_escape] no_checked_side"); return; }
            AgentTools.StopInputs();
            AgentTools.ContactEscapeActive = true;
            frames = 0; startX = player.position.X;
            Move();
            DiagLog.Write($"[agent_escape] start direction={direction} x={startX} hp={player.statLife}");
            return;
        }
        frames++;
        bool grounded = Collision.SolidCollision(player.position + new Vector2(0, 1), player.width, player.height);
        if (!touching && grounded || direction * (player.position.X - startX) >= 48 || frames >= 90)
        {
            ActExecutor.Stop();
            AgentTools.ContactEscapeActive = false;
            DiagLog.Write($"[agent_escape] end frames={frames} clear={!touching} grounded={grounded} x={player.position.X} hp={player.statLife}");
        }
        else if (!ActExecutor.IsActive) Move();
    }

    public override void OnWorldUnload()
    { AgentTools.ContactEscapeActive = false; attempted = false; }
}
