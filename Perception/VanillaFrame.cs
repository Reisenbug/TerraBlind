using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerraBlind;

public static class VanillaFrame
{
    static readonly ConcurrentDictionary<Type, FieldInfo[]> fields = new();
    static readonly ConcurrentDictionary<Type, bool> supported = new();
    static readonly string[] mainFields = { "dayTime", "time", "windSpeedCurrent", "windSpeedTarget", "raining", "maxRaining",
        "hardMode", "expertMode", "masterMode", "remixWorld", "drunkWorld", "notTheBeesWorld", "getGoodWorld",
        "dontStarveWorld", "noTrapsWorld", "tenthAnniversaryWorld", "zenithWorld", "worldSurface", "rockLayer",
        "moonPhase", "invasionType", "<GameUpdateCount>k__BackingField", "_gameUpdateCount" };
    public static string EngineId => typeof(Player).Module.ModuleVersionId.ToString();

    static FieldInfo[] Fields(Type type) => fields.GetOrAdd(type, t =>
    {
        var result = new System.Collections.Generic.List<FieldInfo>();
        for (; t != null && t != typeof(object); t = t.BaseType)
            result.AddRange(t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        return result.OrderBy(field => field.DeclaringType.FullName + ":" + field.Name).ToArray();
    });

    static bool Supported(Type type) => supported.GetOrAdd(type, t => t.IsPrimitive || t.IsEnum || t == typeof(string)
        || t.IsArray && t.GetArrayRank() == 1 && Supported(t.GetElementType())
        || t.IsValueType && Fields(t).All(field => Supported(field.FieldType)));

    static JsonNode Encode(object value, Type type)
    {
        if (value == null) return null;
        if (Nullable.GetUnderlyingType(type) is Type inner) return Encode(value, inner);
        if (type.IsPrimitive || type.IsEnum || type == typeof(string)) return JsonSerializer.SerializeToNode(value, type);
        if (type.IsArray)
        {
            var array = new JsonArray();
            foreach (object item in (Array)value) array.Add(Encode(item, type.GetElementType()));
            return array;
        }
        return CaptureFields(value);
    }

    static object Decode(JsonNode node, Type type)
    {
        if (node == null) return null;
        if (Nullable.GetUnderlyingType(type) is Type inner) return Decode(node, inner);
        if (type.IsPrimitive || type.IsEnum || type == typeof(string)) return node.Deserialize(type);
        if (type.IsArray)
        {
            var result = Array.CreateInstance(type.GetElementType(), node.AsArray().Count);
            for (int i = 0; i < result.Length; i++) result.SetValue(Decode(node[i], type.GetElementType()), i);
            return result;
        }
        object value = Activator.CreateInstance(type);
        RestoreFields(value, node.AsObject());
        return value;
    }

    public static JsonObject CaptureFields(object source)
    {
        var result = new JsonObject();
        foreach (var field in Fields(source.GetType()).Where(field => Supported(field.FieldType)))
            result[field.DeclaringType.FullName + ":" + field.Name] = Encode(field.GetValue(source), field.FieldType);
        return result;
    }

    public static void RestoreFields(object target, JsonObject state)
    {
        foreach (var field in Fields(target.GetType()).Where(field => Supported(field.FieldType)))
        {
            string key = field.DeclaringType.FullName + ":" + field.Name;
            if (state.ContainsKey(key)) field.SetValue(target, Decode(state[key], field.FieldType));
        }
    }

    public static JsonObject CaptureStatics(Type type)
    {
        var result = new JsonObject();
        foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => StaticField(type, field)))
            result[field.Name] = Encode(field.GetValue(null), field.FieldType);
        return result;
    }

    public static void RestoreStatics(Type type, JsonObject state)
    {
        foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => StaticField(type, field)))
            if (state.ContainsKey(field.Name)) field.SetValue(null, Decode(state[field.Name], field.FieldType));
    }

    static bool StaticField(Type type, FieldInfo field) => !field.IsInitOnly && !field.IsLiteral
        && (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType.IsValueType && Supported(field.FieldType))
        && (type != typeof(Main) || mainFields.Contains(field.Name));

    static JsonArray Items(Item[] items)
    {
        var result = new JsonArray();
        foreach (var item in items)
        {
            if (item.ModItem != null || item.prefix >= PrefixID.Count) throw new NotSupportedException("modded_item");
            result.Add(new JsonArray(item.type, (int)item.prefix, item.stack));
        }
        return result;
    }

    static void RestoreItems(Item[] target, JsonArray source)
    {
        if (target.Length != source.Count) throw new InvalidOperationException("item_array_size_mismatch");
        for (int i = 0; i < target.Length; i++)
        {
            var item = new Item();
            item.SetDefaults(source[i][0].GetValue<int>());
            if (source[i][1].GetValue<int>() != 0) item.Prefix(source[i][1].GetValue<int>());
            item.stack = source[i][2].GetValue<int>(); target[i] = item;
        }
    }

    public static JsonObject Capture(Player player, Rectangle area)
    {
        if (player.mount.Active || player.grapCount > 0 || player.grappling[0] >= 0)
            throw new NotSupportedException("active_mount_or_grapple");
        if (player.itemAnimation > 0 || player.itemTime > 0) throw new NotSupportedException("active_item_use");
        if (area.Width < 8 || area.Height < 8 || area.Width > 160 || area.Height > 160
            || area.X < 0 || area.Y < 0 || area.Right > Main.maxTilesX || area.Bottom > Main.maxTilesY)
            throw new ArgumentOutOfRangeException(nameof(area));
        var bounds = new Rectangle(area.X * 16, area.Y * 16, area.Width * 16, area.Height * 16);
        if (!bounds.Contains(player.Hitbox)) throw new ArgumentException("player_outside_snapshot");
        if (Main.npc.Any(npc => npc.active && bounds.Intersects(npc.Hitbox))
            || Main.projectile.Any(projectile => projectile.active && bounds.Intersects(projectile.Hitbox))
            || Main.item.Any(item => item.active && bounds.Intersects(item.Hitbox)))
            throw new NotSupportedException("dynamic_entities_in_snapshot");
        var tiles = new JsonArray();
        for (int y = area.Top; y < area.Bottom; y++)
        for (int x = area.Left; x < area.Right; x++)
        {
            var tile = Main.tile[x, y];
            if (tile.RedWire || tile.BlueWire || tile.GreenWire || tile.YellowWire)
                throw new NotSupportedException("wiring_in_snapshot");
            tiles.Add(new JsonArray(tile.HasTile ? 1 : 0, (int)tile.TileType, (int)tile.WallType,
                (int)tile.TileFrameX, (int)tile.TileFrameY, (int)tile.Slope, tile.IsHalfBlock ? 1 : 0,
                tile.IsActuated ? 1 : 0, (int)tile.LiquidType, (int)tile.LiquidAmount));
        }
        return new JsonObject { ["engine"] = EngineId, ["world_width"] = Main.maxTilesX, ["world_height"] = Main.maxTilesY,
            ["game_mode"] = Main.GameMode,
            ["player"] = CaptureFields(player),
            ["inventory"] = Items(player.inventory), ["armor"] = Items(player.armor),
            ["dye"] = Items(player.dye), ["misc_equips"] = Items(player.miscEquips),
            ["main"] = CaptureStatics(typeof(Main)), ["player_statics"] = CaptureStatics(typeof(Player)),
            ["collision"] = CaptureStatics(typeof(Collision)), ["random"] = CaptureFields(Main.rand),
            ["scene"] = CaptureFields(Main.SceneMetrics),
            ["world_random"] = CaptureFields(WorldGen.genRand), ["area"] = new JsonArray(area.X, area.Y, area.Width, area.Height),
            ["tiles"] = tiles,
            ["omitted_player_references"] = new JsonArray(Fields(typeof(Player)).Where(field => !Supported(field.FieldType))
                .Select(field => JsonValue.Create(field.Name)).ToArray()),
            ["scope"] = "vanilla movement only; no active mounts, grapples, actors, item use or world wiring" };
    }

    public static bool ContextMatches(JsonObject state, Player player)
    {
        if (player.mount.Active || player.grapCount > 0 || player.grappling[0] >= 0 || player.itemAnimation > 0 || player.itemTime > 0)
            return false;
        if (!JsonNode.DeepEquals(state["inventory"], Items(player.inventory))
            || !JsonNode.DeepEquals(state["armor"], Items(player.armor))
            || !JsonNode.DeepEquals(state["misc_equips"], Items(player.miscEquips))) return false;
        int x0 = state["area"][0].GetValue<int>(), y0 = state["area"][1].GetValue<int>(), width = state["area"][2].GetValue<int>();
        var tiles = state["tiles"].AsArray();
        var bounds = new Rectangle(x0 * 16, y0 * 16, width * 16, state["area"][3].GetValue<int>() * 16);
        if (Main.npc.Any(npc => npc.active && bounds.Intersects(npc.Hitbox))
            || Main.projectile.Any(projectile => projectile.active && bounds.Intersects(projectile.Hitbox))
            || Main.item.Any(item => item.active && bounds.Intersects(item.Hitbox))) return false;
        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = Main.tile[x0 + i % width, y0 + i / width]; var data = tiles[i];
            if (tile.RedWire || tile.BlueWire || tile.GreenWire || tile.YellowWire) return false;
            if ((tile.HasTile ? 1 : 0) != data[0].GetValue<int>() || tile.TileType != data[1].GetValue<int>()
                || tile.WallType != data[2].GetValue<int>() || tile.TileFrameX != data[3].GetValue<int>()
                || tile.TileFrameY != data[4].GetValue<int>() || (int)tile.Slope != data[5].GetValue<int>()
                || (tile.IsHalfBlock ? 1 : 0) != data[6].GetValue<int>() || (tile.IsActuated ? 1 : 0) != data[7].GetValue<int>()
                || tile.LiquidType != data[8].GetValue<int>() || tile.LiquidAmount != data[9].GetValue<int>()) return false;
        }
        return true;
    }

    public static Player RestoreInWorker(JsonObject state)
    {
        RequireWorker();
        if (state["engine"]?.GetValue<string>() != EngineId) throw new InvalidOperationException("engine_mismatch");
        if (state["world_width"].GetValue<int>() != Main.maxTilesX || state["world_height"].GetValue<int>() != Main.maxTilesY)
            throw new InvalidOperationException("worker_world_dimensions_mismatch");
        RestoreStatics(typeof(Main), state["main"].AsObject());
        Main.GameMode = state["game_mode"].GetValue<int>();
        RestoreStatics(typeof(Player), state["player_statics"].AsObject());
        RestoreStatics(typeof(Collision), state["collision"].AsObject());
        RestoreFields(Main.SceneMetrics, state["scene"].AsObject());
        var player = new Player();
        RestoreFields(player, state["player"].AsObject());
        RestoreItems(player.inventory, state["inventory"].AsArray());
        RestoreItems(player.armor, state["armor"].AsArray());
        RestoreItems(player.dye, state["dye"].AsArray());
        RestoreItems(player.miscEquips, state["misc_equips"].AsArray());
        Main.player[player.whoAmI] = player;
        foreach (var npc in Main.npc) npc.active = false;
        foreach (var projectile in Main.projectile) projectile.active = false;
        foreach (var item in Main.item) item.active = false;
        RestoreFields(Main.rand, state["random"].AsObject());
        RestoreFields(WorldGen.genRand, state["world_random"].AsObject());
        int x0 = state["area"][0].GetValue<int>(), y0 = state["area"][1].GetValue<int>(), width = state["area"][2].GetValue<int>();
        var tiles = state["tiles"].AsArray();
        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = Main.tile[x0 + i % width, y0 + i / width]; var data = tiles[i];
            tile.ClearEverything(); tile.HasTile = data[0].GetValue<int>() != 0;
            tile.TileType = (ushort)data[1].GetValue<int>(); tile.WallType = (ushort)data[2].GetValue<int>();
            tile.TileFrameX = (short)data[3].GetValue<int>(); tile.TileFrameY = (short)data[4].GetValue<int>();
            tile.Slope = (SlopeType)data[5].GetValue<int>(); tile.IsHalfBlock = data[6].GetValue<int>() != 0;
            tile.IsActuated = data[7].GetValue<int>() != 0; tile.LiquidType = data[8].GetValue<int>(); tile.LiquidAmount = (byte)data[9].GetValue<int>();
        }
        return player;
    }

    public static void RequireWorker()
    {
        if (!Main.dedServ || !Terraria.Program.LaunchParameters.ContainsKey("-vanillaforecastport"))
            throw new InvalidOperationException("prediction_must_run_in_isolated_worker");
        if (!Terraria.Program.LaunchParameters.TryGetValue("-tmlsavedirectory", out string root))
            throw new InvalidOperationException("worker_requires_disposable_save_root");
        root = System.IO.Path.GetFullPath(root);
        string temporary = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        if (!root.StartsWith(temporary, StringComparison.Ordinal) && !root.StartsWith("/tmp/", StringComparison.Ordinal)
            && !root.StartsWith("/private/tmp/", StringComparison.Ordinal))
            throw new InvalidOperationException("worker_save_root_must_be_temporary");
    }
}
