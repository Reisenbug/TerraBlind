# TerraBlind

A tModLoader mod that exposes Terraria game state over a local HTTP server for external AI agents.

## Overview

TerraBlind runs an HTTP server on `http://127.0.0.1:17878` that serializes the full game state every tick. This allows external programs to read game data without screen capture or memory hacking.

Designed as the perception layer for [Terraria Agent](https://github.com/Reisenbug/Tairaria).

## API Endpoints

### `GET /state`

Returns the complete game state as JSON.

**Response fields:**

| Field | Description |
|-------|-------------|
| `player` | Position, velocity, HP, mana, direction, on_ground, in_liquid |
| `equipment` | Selected slot, held item, hotbar (10), inventory (40), coins (4), ammo (4), smart_cursor, chest_open |
| `camera` | Screen position, size, zoom |
| `movement` | jump_speed, gravity, max_run_speed, acc_run_speed, wing_time_max, extra_jumps, no_fall_dmg, lava_immune, lava_time |
| `buffs` | Active buffs with id, name, time_left |
| `enemies` | Hostile NPCs within 60x36 tile range: position, velocity, HP, boss flag |
| `town_npcs` | Friendly NPCs: position, name, homeless |
| `tiles` | 120x80 RLE tile window centered on player |
| `objects` | World objects (chests, trees, workbenches, etc.) within tile window |
| `dropped_items` | Ground items within tile window |

**Tile encoding:**

Each row is an array of `[type, sflags, count]` runs (RLE).

`sflags` bitmask:
- Bit 0 (1): tile active
- Bit 1 (2): tile solid
- Bit 2 (4): water
- Bit 3 (8): lava
- Bit 4 (16): honey
- Bit 5 (32): shimmer

**Object classification:**

Objects are detected via `TileFrame(0,0)` anchoring and classified by tile type:
- `chest` — BasicChest set + Containers2
- `tree` — IsATreeTrunk set
- `torch` — Torch set
- `workbench`, `anvil`, `furnace`, `pot`, `sign`, `bed`, `alchemy`, `cooking_pot`, `sawmill`, `tinkerer`, `altar`, `loom`, `solidifier`

**Inventory slot fields:**

Each item slot includes: `id`, `name`, `stack`, `damage`, `pick`, `axe`, `hammer`, `create_tile`, `consumable`.

### `GET /swap?src=N&dst=M`

Swaps two inventory slots (indices 0-57). Executed on the main thread next tick.

- 0-9: hotbar
- 10-49: inventory
- 50-53: coins
- 54-57: ammo

### `GET /loot_all`

Calls `ChestUI.LootAll()` if a chest is currently open. Executed on the main thread next tick.

### `GET /health`

Returns `{"ok": true}`. Use for connectivity checks.

## Installation

1. Place this folder in `tModLoader/ModSources/TerraBlind/`
2. Build in tModLoader: `Mod Sources` -> `Build + Reload`
3. The HTTP server starts automatically when a world is loaded

## Requirements

- Terraria 1.4.4+
- tModLoader 2024+

## Files

| File | Purpose |
|------|---------|
| `TerraBlind.cs` | Mod entry point |
| `HttpServerSystem.cs` | HTTP server, request routing, main-thread action queue |
| `StateSnapshotPlayer.cs` | Per-tick data collection (player, tiles, objects, enemies, etc.) |
| `Snapshot.cs` | Data model definitions |
| `StateSerializer.cs` | Manual JSON serialization |

## License

MIT
