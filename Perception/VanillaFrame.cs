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
    internal static Rectangle WorkerBounds { get; private set; }
    public sealed class SnapshotBoundaryException(int x, int y) : InvalidOperationException($"snapshot_boundary:{x},{y}")
    {
        public Point Tile { get; } = new(x, y);
    }

    sealed class MiningState
    {
        static readonly FieldInfo order = typeof(HitTile).GetField("order", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo buffer = typeof(HitTile).GetField("bufferLocation", BindingFlags.Instance | BindingFlags.NonPublic);
        readonly int[] values;

        public MiningState(HitTile source)
        {
            int count = source.data.Length;
            values = new int[2 + count * 6];
            values[0] = count; values[1] = (int)buffer.GetValue(source);
            ((int[])order.GetValue(source)).CopyTo(values, 2);
            for (int i = 0; i < count; i++)
            {
                var hit = source.data[i]; int offset = 2 + count + i * 5;
                values[offset] = hit.X; values[offset + 1] = hit.Y; values[offset + 2] = hit.damage;
                values[offset + 3] = hit.type; values[offset + 4] = hit.timeToLive;
            }
        }

        public MiningState(string packed) => values = UnpackInts(packed);
        public string Pack() => PackInts(values);

        public void Restore(HitTile target)
        {
            int count = target.data.Length;
            if (values.Length != 2 + count * 6 || values[0] != count)
                throw new InvalidOperationException("mining_state_size_mismatch");
            buffer.SetValue(target, values[1]);
            order.SetValue(target, values.AsSpan(2, count).ToArray());
            for (int i = 0; i < count; i++)
            {
                var hit = target.data[i]; int offset = 2 + count + i * 5;
                hit.X = values[offset]; hit.Y = values[offset + 1]; hit.damage = values[offset + 2];
                hit.type = values[offset + 3]; hit.timeToLive = values[offset + 4];
            }
        }
    }
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
        if (type.IsEnum) return JsonSerializer.SerializeToNode(value, type);
        if (type.IsPrimitive || type == typeof(string)) return value switch
        {
            bool scalar => JsonValue.Create(scalar), byte scalar => JsonValue.Create(scalar),
            sbyte scalar => JsonValue.Create(scalar), short scalar => JsonValue.Create(scalar),
            ushort scalar => JsonValue.Create(scalar), int scalar => JsonValue.Create(scalar),
            uint scalar => JsonValue.Create(scalar), long scalar => JsonValue.Create(scalar),
            ulong scalar => JsonValue.Create(scalar), float scalar => JsonValue.Create(scalar),
            double scalar => JsonValue.Create(scalar), char scalar => JsonValue.Create(scalar),
            string scalar => JsonValue.Create(scalar), _ => JsonSerializer.SerializeToNode(value, type)
        };
        if (type.IsArray)
        {
            var source = (Array)value;
            Type element = type.GetElementType();
            if (source.Length >= 16 && element.IsPrimitive && element != typeof(IntPtr) && element != typeof(UIntPtr))
            {
                var bytes = new byte[System.Buffer.ByteLength(source)];
                System.Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);
                return new JsonObject { ["array_bytes"] = Convert.ToBase64String(bytes), ["length"] = source.Length };
            }
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
        if (type.IsEnum) return node.Deserialize(type);
        if (type.IsPrimitive || type == typeof(string)) return Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => node.GetValue<bool>(), TypeCode.Byte => node.GetValue<byte>(),
            TypeCode.SByte => node.GetValue<sbyte>(), TypeCode.Int16 => node.GetValue<short>(),
            TypeCode.UInt16 => node.GetValue<ushort>(), TypeCode.Int32 => node.GetValue<int>(),
            TypeCode.UInt32 => node.GetValue<uint>(), TypeCode.Int64 => node.GetValue<long>(),
            TypeCode.UInt64 => node.GetValue<ulong>(), TypeCode.Single => node.GetValue<float>(),
            TypeCode.Double => node.GetValue<double>(), TypeCode.String => node.GetValue<string>(),
            _ => node.Deserialize(type)
        };
        if (type.IsArray)
        {
            if (node is JsonObject packed)
            {
                var array = Array.CreateInstance(type.GetElementType(), packed["length"].GetValue<int>());
                var bytes = Convert.FromBase64String(packed["array_bytes"].GetValue<string>());
                if (bytes.Length != System.Buffer.ByteLength(array)) throw new InvalidOperationException("array_buffer_size_mismatch");
                System.Buffer.BlockCopy(bytes, 0, array, 0, bytes.Length);
                return array;
            }
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

    public static JsonObject Capture(Player player, Rectangle area, bool navigation = false)
    {
        if (player.mount.Active || player.grapCount > 0 || player.grappling[0] >= 0)
            throw new NotSupportedException("active_mount_or_grapple");
        if ((player.itemAnimation > 0 || player.itemTime > 0)
            && !(navigation && (player.HeldItem.pick > 0 || player.HeldItem.createTile >= 0)))
            throw new NotSupportedException("active_item_use");
        if (area.Width < 8 || area.Height < 8 || area.Width > 256 || area.Height > 256
            || area.X < 0 || area.Y < 0 || area.Right > Main.maxTilesX || area.Bottom > Main.maxTilesY)
            throw new ArgumentOutOfRangeException(nameof(area));
        var bounds = new Rectangle(area.X * 16, area.Y * 16, area.Width * 16, area.Height * 16);
        if (!bounds.Contains(player.Hitbox)) throw new ArgumentException("player_outside_snapshot");
        if (!navigation && (Main.npc.Any(npc => npc.active && bounds.Intersects(npc.Hitbox))
            || Main.projectile.Any(projectile => projectile.active && bounds.Intersects(projectile.Hitbox))
            || Main.item.Any(item => item.active && bounds.Intersects(item.Hitbox))))
            throw new NotSupportedException("dynamic_entities_in_snapshot");
        var tiles = new int[area.Width * area.Height * 10];
        for (int y = area.Top; y < area.Bottom; y++)
        for (int x = area.Left; x < area.Right; x++)
        {
            var tile = Main.tile[x, y];
            if (!navigation && (tile.RedWire || tile.BlueWire || tile.GreenWire || tile.YellowWire))
                throw new NotSupportedException("wiring_in_snapshot");
            int offset = ((y - area.Top) * area.Width + x - area.Left) * 10;
            tiles[offset] = tile.HasTile ? 1 : 0; tiles[offset + 1] = tile.TileType; tiles[offset + 2] = tile.WallType;
            tiles[offset + 3] = tile.TileFrameX; tiles[offset + 4] = tile.TileFrameY; tiles[offset + 5] = (int)tile.Slope;
            tiles[offset + 6] = tile.IsHalfBlock ? 1 : 0; tiles[offset + 7] = tile.IsActuated ? 1 : 0;
            tiles[offset + 8] = tile.LiquidType; tiles[offset + 9] = tile.LiquidAmount;
        }
        return new JsonObject { ["navigation"] = navigation, ["engine"] = EngineId, ["world_width"] = Main.maxTilesX, ["world_height"] = Main.maxTilesY,
            ["game_mode"] = Main.GameMode,
            ["player"] = CaptureFields(player),
            ["hit_tile"] = new MiningState(player.hitTile).Pack(),
            ["hit_replace"] = new MiningState(player.hitReplace).Pack(),
            ["inventory"] = Items(player.inventory), ["armor"] = Items(player.armor),
            ["dye"] = Items(player.dye), ["misc_equips"] = Items(player.miscEquips),
            ["main"] = CaptureStatics(typeof(Main)), ["player_statics"] = CaptureStatics(typeof(Player)),
            ["collision"] = CaptureStatics(typeof(Collision)), ["random"] = CaptureFields(Main.rand),
            ["scene"] = CaptureFields(Main.SceneMetrics),
            ["world_random"] = CaptureFields(WorldGen.genRand), ["area"] = new JsonArray(area.X, area.Y, area.Width, area.Height),
            ["tiles"] = PackInts(tiles),
            ["tile_encoding"] = "i32-native",
            ["omitted_player_references"] = new JsonArray(Fields(typeof(Player)).Where(field => !Supported(field.FieldType))
                .Select(field => JsonValue.Create(field.Name)).ToArray()),
            ["scope"] = navigation ? "vanilla navigation physics; enemies and wiring effects are not predicted"
                : "vanilla movement only; no active mounts, grapples, actors, item use or world wiring" };
    }

    public static bool ContextMatches(JsonObject state, Player player)
        => ContextMatches(state, player, out _);

    internal static int[] ReadTiles(JsonObject state)
    {
        if (state["tiles"] is JsonArray rows)
            return rows.SelectMany(row => row.AsArray().Select(value => value.GetValue<int>())).ToArray();
        var values = UnpackInts(state["tiles"].GetValue<string>());
        int expected = state["area"][2].GetValue<int>() * state["area"][3].GetValue<int>() * 10;
        if (values.Length != expected) throw new InvalidOperationException("tile_buffer_size_mismatch");
        return values;
    }

    internal static string PackInts(int[] values)
        => Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));

    internal static int[] UnpackInts(string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length % sizeof(int) != 0) throw new InvalidOperationException("integer_buffer_size_mismatch");
        return System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes.AsSpan()).ToArray();
    }

    static bool HarmlessDecoration(bool present, int type) => !present || type < TileID.Count
        && Main.tileCut[type] && !Main.tileSolid[type] && !Main.tileSolidTop[type] && !Main.tileRope[type]
        && type != TileID.Cobweb && TileID.Sets.TouchDamageImmediate[type] == 0
        && !TileID.Sets.TouchDamageHot[type] && !TileID.Sets.TouchDamageBleeding[type];

    public static bool ContextMatches(JsonObject state, Player player, out string difference, JsonArray trajectory = null,
        int[] cachedTiles = null, int firstFrame = 0, int frameCount = int.MaxValue)
    {
        difference = "active player state";
        bool navigation = state["navigation"]?.GetValue<bool>() == true;
        if (player.mount.Active || player.grapCount > 0 || player.grappling[0] >= 0
            || !navigation && (player.itemAnimation > 0 || player.itemTime > 0))
            return false;
        difference = "equipment or inventory";
        if ((!navigation && !JsonNode.DeepEquals(state["inventory"], Items(player.inventory)))
            || !JsonNode.DeepEquals(state["armor"], Items(player.armor))
            || !JsonNode.DeepEquals(state["misc_equips"], Items(player.miscEquips))) return false;
        int x0 = state["area"][0].GetValue<int>(), y0 = state["area"][1].GetValue<int>(), width = state["area"][2].GetValue<int>();
        var tiles = cachedTiles ?? ReadTiles(state);
        var bounds = new Rectangle(x0 * 16, y0 * 16, width * 16, state["area"][3].GetValue<int>() * 16);
        difference = "dynamic entity";
        if (!navigation && (Main.npc.Any(npc => npc.active && bounds.Intersects(npc.Hitbox))
            || Main.projectile.Any(projectile => projectile.active && bounds.Intersects(projectile.Hitbox))
            || Main.item.Any(item => item.active && bounds.Intersects(item.Hitbox)))) return false;
        System.Collections.Generic.HashSet<(int, int)> corridor = null;
        if (navigation && trajectory != null)
        {
            corridor = new();
            foreach (var frame in trajectory.Skip(firstFrame).Take(frameCount))
            {
                int fx = (int)Math.Floor(frame["x"].GetValue<float>() / 16), fy = (int)Math.Floor(frame["y"].GetValue<float>() / 16);
                for (int x = fx - 2; x <= fx + 3; x++)
                for (int y = fy - 2; y <= fy + 5; y++) corridor.Add((x, y));
            }
        }
        var indices = corridor == null ? Enumerable.Range(0, tiles.Length / 10)
            : corridor.Where(cell => cell.Item1 >= x0 && cell.Item1 < x0 + width
                && cell.Item2 >= y0 && cell.Item2 < y0 + tiles.Length / 10 / width)
                .Select(cell => (cell.Item2 - y0) * width + cell.Item1 - x0);
        foreach (int i in indices)
        {
            var tile = Main.tile[x0 + i % width, y0 + i / width]; var data = tiles.AsSpan(i * 10, 10);
            if (!navigation && (tile.RedWire || tile.BlueWire || tile.GreenWire || tile.YellowWire))
            { difference = $"wire ({x0 + i % width},{y0 + i / width})"; return false; }
            bool flowingWater = navigation && (data[9] == 0 || data[8] == LiquidID.Water)
                && (tile.LiquidAmount == 0 || tile.LiquidType == LiquidID.Water);
            if (!flowingWater && (tile.LiquidType != data[8] || tile.LiquidAmount != data[9]))
            { difference = $"liquid ({x0 + i % width},{y0 + i / width}): {data[8]}/{data[9]} -> {tile.LiquidType}/{tile.LiquidAmount}"; return false; }
            if (navigation && HarmlessDecoration(data[0] != 0, data[1])
                && HarmlessDecoration(tile.HasTile, tile.TileType)) continue;
            if ((tile.HasTile ? 1 : 0) != data[0] || tile.TileType != data[1]
                || (!navigation || Main.tileFrameImportant[tile.TileType])
                    && (tile.WallType != data[2] || tile.TileFrameX != data[3] || tile.TileFrameY != data[4])
                || (int)tile.Slope != data[5]
                || (tile.IsHalfBlock ? 1 : 0) != data[6] || (tile.IsActuated ? 1 : 0) != data[7])
            {
                difference = $"tile ({x0 + i % width},{y0 + i / width}): [{string.Join(",", data.ToArray())}] -> "
                    + $"[{(tile.HasTile ? 1 : 0)},{tile.TileType},{tile.WallType},{tile.TileFrameX},{tile.TileFrameY},{(int)tile.Slope},{(tile.IsHalfBlock ? 1 : 0)},{(tile.IsActuated ? 1 : 0)},{tile.LiquidType},{tile.LiquidAmount}]";
                return false;
            }
        }
        difference = null;
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
        if (state["hit_tile"] is JsonValue hits) new MiningState(hits.GetValue<string>()).Restore(player.hitTile);
        if (state["hit_replace"] is JsonValue replaces) new MiningState(replaces.GetValue<string>()).Restore(player.hitReplace);
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
        var tiles = ReadTiles(state);
        WorkerBounds = new Rectangle(x0, y0, width, tiles.Length / 10 / width);
        for (int i = 0; i < tiles.Length / 10; i++)
        {
            var tile = Main.tile[x0 + i % width, y0 + i / width]; var data = tiles.AsSpan(i * 10, 10);
            tile.ClearEverything(); tile.HasTile = data[0] != 0;
            tile.TileType = (ushort)data[1]; tile.WallType = (ushort)data[2];
            tile.TileFrameX = (short)data[3]; tile.TileFrameY = (short)data[4];
            tile.Slope = (SlopeType)data[5]; tile.IsHalfBlock = data[6] != 0;
            tile.IsActuated = data[7] != 0; tile.LiquidType = data[8]; tile.LiquidAmount = (byte)data[9];
        }
        return player;
    }

    public sealed class WorkerState
    {
        readonly (FieldInfo field, object value)[] playerFields, main, statics, collision, scene, random, worldRandom;
        readonly Item[] inventory, armor, dye, misc;
        readonly MiningState hitTile, hitReplace;
        readonly int[] tiles;
        readonly TileTypeData[][] tileTypes;
        readonly WallTypeData[][] wallTypes;
        readonly TileWallWireStateData[][] tileStates;
        readonly LiquidData[][] liquids;
        readonly int x0, y0, width, gameMode;
        public int CapturedTileChanges { get; private set; }
        public int RestoredTileWrites { get; private set; }
        public double FieldsMs { get; private set; }
        public double ItemsMs { get; private set; }
        public double TilesMs { get; private set; }

        public WorkerState(JsonObject state)
        {
            var source = RestoreInWorker(state);
            hitTile = new MiningState(source.hitTile); hitReplace = new MiningState(source.hitReplace);
            playerFields = DecodeFields(typeof(Player), state["player"].AsObject());
            main = DecodeFields(typeof(Main), state["main"].AsObject(), true);
            statics = DecodeFields(typeof(Player), state["player_statics"].AsObject(), true);
            collision = DecodeFields(typeof(Collision), state["collision"].AsObject(), true);
            scene = DecodeFields(Main.SceneMetrics.GetType(), state["scene"].AsObject());
            random = DecodeFields(Main.rand.GetType(), state["random"].AsObject());
            worldRandom = DecodeFields(WorldGen.genRand.GetType(), state["world_random"].AsObject());
            inventory = source.inventory.Select(item => item.Clone()).ToArray();
            armor = source.armor.Select(item => item.Clone()).ToArray();
            dye = source.dye.Select(item => item.Clone()).ToArray();
            misc = source.miscEquips.Select(item => item.Clone()).ToArray();
            x0 = state["area"][0].GetValue<int>(); y0 = state["area"][1].GetValue<int>(); width = state["area"][2].GetValue<int>();
            gameMode = state["game_mode"].GetValue<int>();
            tiles = ReadTiles(state);
            tileTypes = CaptureColumns<TileTypeData>(null, null);
            wallTypes = CaptureColumns<WallTypeData>(null, null);
            tileStates = CaptureColumns<TileWallWireStateData>(null, null);
            liquids = CaptureColumns<LiquidData>(null, null);
        }

        static (FieldInfo, object)[] DecodeFields(Type type, JsonObject state, bool isStatic = false)
            => (isStatic ? type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(field => StaticField(type, field)) : Fields(type).Where(field => Supported(field.FieldType)))
                .Where(field => state.ContainsKey(isStatic ? field.Name : field.DeclaringType.FullName + ":" + field.Name))
                .Select(field => (field, Decode(state[isStatic ? field.Name : field.DeclaringType.FullName + ":" + field.Name], field.FieldType))).ToArray();

        WorkerState(WorkerState template, Player source)
        {
            RequireWorker();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            hitTile = new MiningState(source.hitTile); hitReplace = new MiningState(source.hitReplace);
            playerFields = CaptureValues(source, template.playerFields);
            main = CaptureValues(null, template.main); statics = CaptureValues(null, template.statics);
            collision = CaptureValues(null, template.collision); scene = CaptureValues(Main.SceneMetrics, template.scene);
            random = CaptureValues(Main.rand, template.random); worldRandom = CaptureValues(WorldGen.genRand, template.worldRandom);
            FieldsMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            inventory = source.inventory.Select(item => item.Clone()).ToArray();
            armor = source.armor.Select(item => item.Clone()).ToArray();
            dye = source.dye.Select(item => item.Clone()).ToArray();
            misc = source.miscEquips.Select(item => item.Clone()).ToArray();
            ItemsMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            x0 = template.x0; y0 = template.y0; width = template.width; gameMode = Main.GameMode;
            tiles = template.tiles;
            var changedColumns = new bool[width];
            tileTypes = CaptureColumns(template.tileTypes, changedColumns);
            wallTypes = CaptureColumns(template.wallTypes, changedColumns);
            tileStates = CaptureColumns(template.tileStates, changedColumns);
            liquids = CaptureColumns(template.liquids, changedColumns);
            for (int x = 0; x < width; x++)
            {
                if (!changedColumns[x]) continue;
                for (int y = 0; y < tiles.Length / 10 / width; y++)
                {
                    var tile = Main.tile[x0 + x, y0 + y]; int offset = (y * width + x) * 10;
                    if (TileMatches(tile, tiles, offset)) continue;
                    if (CapturedTileChanges++ == 0) tiles = (int[])tiles.Clone();
                    tiles[offset] = tile.HasTile ? 1 : 0; tiles[offset + 1] = tile.TileType; tiles[offset + 2] = tile.WallType;
                    tiles[offset + 3] = tile.TileFrameX; tiles[offset + 4] = tile.TileFrameY; tiles[offset + 5] = (int)tile.Slope;
                    tiles[offset + 6] = tile.IsHalfBlock ? 1 : 0; tiles[offset + 7] = tile.IsActuated ? 1 : 0;
                    tiles[offset + 8] = tile.LiquidType; tiles[offset + 9] = tile.LiquidAmount;
                }
            }
            TilesMs = timer.Elapsed.TotalMilliseconds;
        }

        T[][] CaptureColumns<T>(T[][] previous, bool[] changed) where T : unmanaged, ITileData
        {
            var data = Main.tile.GetData<T>();
            int height = tiles.Length / 10 / width;
            var result = previous ?? new T[width][];
            for (int x = 0; x < width; x++)
            {
                var current = data.AsSpan((x0 + x) * Main.tile.Height + y0, height);
                if (previous != null && System.Runtime.InteropServices.MemoryMarshal.AsBytes(current)
                    .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(previous[x].AsSpan()))) continue;
                if (ReferenceEquals(result, previous)) result = (T[][])previous.Clone();
                result[x] = current.ToArray();
                if (changed != null) changed[x] = true;
            }
            return result;
        }

        static (FieldInfo field, object value)[] CaptureValues(object source, (FieldInfo field, object value)[] template)
            => template.Select(entry => (entry.field, CopyValue(entry.field.GetValue(source)))).ToArray();

        void RestoreColumns<T>(T[][] columns, T[][] known) where T : unmanaged, ITileData
        {
            var data = Main.tile.GetData<T>();
            for (int x = 0; x < width; x++)
            {
                if (known != null && ReferenceEquals(columns[x], known[x])) continue;
                var current = data.AsSpan((x0 + x) * Main.tile.Height + y0, columns[x].Length);
                if (System.Runtime.InteropServices.MemoryMarshal.AsBytes(current)
                    .SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(columns[x].AsSpan()))) continue;
                columns[x].AsSpan().CopyTo(current);
                RestoredTileWrites += columns[x].Length;
            }
        }

        public WorkerState CaptureCurrent(Player player) => new(this, player);

        static bool TileMatches(Tile tile, int[] values, int offset)
            => tile.HasTile == (values[offset] != 0) && tile.TileType == values[offset + 1]
                && tile.WallType == values[offset + 2] && tile.TileFrameX == values[offset + 3]
                && tile.TileFrameY == values[offset + 4] && (int)tile.Slope == values[offset + 5]
                && tile.IsHalfBlock == (values[offset + 6] != 0) && tile.IsActuated == (values[offset + 7] != 0)
                && tile.LiquidType == values[offset + 8] && tile.LiquidAmount == values[offset + 9];

        static object CopyValue(object value)
        {
            if (value is not Array array) return value;
            var copy = (Array)array.Clone();
            if (array.GetType().GetElementType().IsArray)
                for (int i = 0; i < copy.Length; i++) copy.SetValue(CopyValue(copy.GetValue(i)), i);
            return copy;
        }

        static void Apply(object target, (FieldInfo field, object value)[] values)
        {
            foreach (var (field, value) in values) field.SetValue(target, CopyValue(value));
        }

        public Player Restore(WorkerState knownWorld = null)
        {
            RequireWorker();
            WorkerBounds = new Rectangle(x0, y0, width, tiles.Length / 10 / width);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Apply(null, main); Main.GameMode = gameMode; Apply(null, statics); Apply(null, collision);
            Apply(Main.SceneMetrics, scene);
            var player = new Player(); Apply(player, playerFields);
            hitTile.Restore(player.hitTile); hitReplace.Restore(player.hitReplace);
            FieldsMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            player.inventory = inventory.Select(item => item.Clone()).ToArray();
            player.armor = armor.Select(item => item.Clone()).ToArray();
            player.dye = dye.Select(item => item.Clone()).ToArray();
            player.miscEquips = misc.Select(item => item.Clone()).ToArray();
            ItemsMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            Main.player[player.whoAmI] = player;
            foreach (var npc in Main.npc) npc.active = false;
            foreach (var projectile in Main.projectile) projectile.active = false;
            foreach (var item in Main.item) item.active = false;
            Apply(Main.rand, random); Apply(WorldGen.genRand, worldRandom);
            RestoredTileWrites = 0;
            bool known = knownWorld != null && knownWorld.x0 == x0 && knownWorld.y0 == y0
                && knownWorld.width == width && knownWorld.tiles.Length == tiles.Length;
            RestoreColumns(tileTypes, known ? knownWorld.tileTypes : null);
            RestoreColumns(wallTypes, known ? knownWorld.wallTypes : null);
            RestoreColumns(tileStates, known ? knownWorld.tileStates : null);
            RestoreColumns(liquids, known ? knownWorld.liquids : null);
            TilesMs = timer.Elapsed.TotalMilliseconds;
            return player;
        }
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
