# TerraBlind

A tModLoader mod that exposes Terraria game state and accepts input commands over a local HTTP server for external AI agents.

## Overview

TerraBlind runs an HTTP server on `http://127.0.0.1:17878`. It serves full game state every tick and accepts control commands that are applied at 60fps on the game thread.

Designed as the perception + execution layer for [Terraria Agent](https://github.com/Reisenbug/Tairaria).

## API Endpoints

### `GET /state`

Returns complete game state as JSON.

| Field | Description |
|-------|-------------|
| `player` | Position, velocity, HP, mana, direction, on_ground, in_liquid, selected_slot |
| `inventory_slots` | Full inventory (hotbar + bags) with item stats |
| `movement` | Jump speed, gravity, wing time, extra jumps, etc. |
| `buffs` | Active buffs with id, name, time_left |
| `enemies` | Hostile NPCs within range: pos, vel, HP, boss flag, screen coords (`sx`, `sy`) |
| `town_npcs` | Friendly NPCs: pos, name, homeless |
| `tiles` | 120×80 RLE tile window centered on player |
| `objects` | World objects (chests, trees, workbenches, etc.) |
| `dropped_items` | Ground items within tile window |
| `walk_to_edge_done` | True when `/walk_to_edge` has finished |

**Tile encoding:** each row is `[type, sflags, count]` RLE runs.

`sflags` bitmask: 1=active, 2=solid, 4=water, 8=lava, 16=honey, 32=shimmer

**Inventory slot fields:** `id`, `name`, `stack`, `damage`, `pick`, `axe`, `hammer`, `create_tile`, `consumable`, `is_weapon`, `is_ammo`

**Enemy screen coords:** `sx`/`sy` are pixel coordinates relative to the top-left of the game window, computed as `(npc.Center - Main.screenPosition) * Main.GameZoomTarget`.

---

### `POST /control`

Set per-tick player inputs. Applied every game frame until a new command arrives or the timeout expires (~200ms).

```json
{
  "left": true, "right": true, "up": true, "down": true,
  "jump": true,
  "use_item": true,
  "selected_slot": 3,
  "mx": -2.5, "my": 1.0
}
```

`mx`/`my` are tile offsets from player center. Setting them moves `Main.mouseX/Y` to that world position.

---

### `POST /fight`

Start or stop mod-side autonomous combat at 60fps.

```json
{"active": true, "max_dist": 20.0}
{"active": false}
```

While active, every frame:
1. Finds nearest enemy within `max_dist` tiles
2. Selects first hotbar weapon (`item.ammo == AmmoID.None && item.damage > 0`)
3. Sets `Main.mouseX/Y` to enemy center
4. Sets `controlUseItem = true`

Timeout resets each frame a target is found. Stops 6s after last target is lost.

---

### `POST /replay`

Replay a recorded skill frame-by-frame at 60fps.

```json
[
  {"right": true, "jump": true, "slot": 2, "mx": 1.2, "my": -0.5, "repeat": 8},
  {"right": true, "slot": 2, "mx": 1.5, "my": -0.3}
]
```

Supported fields per frame: `left`, `right`, `up`, `down`, `jump`, `use_item`, `grapple`, `use_alt`, `use_tile`, `mount`, `slot`, `sc` (smart cursor 0/1), `mx`, `my`, `repeat`.

---

### `POST /walk_to_edge`

Walk the player in a direction until overhead completely clears, then walk `extra_tiles` more.

```json
{"direction": "left", "extra_tiles": 1.0}
```

Poll `/state` for `walk_to_edge_done: true` to detect completion.

### `GET /walk_to_edge_stop`

Cancel an in-progress walk.

---

### `POST /place`

Place a tile at a position relative to the player's feet, holding left-click for `duration_frames` frames.

```json
{"dx": -2, "dy": -3, "slot": 5, "duration_frames": 10}
```

`dx`/`dy` are tile offsets. Anchors cursor to that world tile.

### `GET /place_stop`

Cancel an in-progress place action.

---

### `POST /interact`

Right-click a tile at absolute tile coordinates.

```json
{"tile_x": 100, "tile_y": 200}
```

---

### `GET /loot_all`

Calls `ChestUI.LootAll()` if a chest is open.

### `GET /quick_heal`

Uses the best available healing potion.

### `GET /swap?src=N&dst=M`

Swaps two inventory slots (0–57): 0-9 hotbar, 10-49 inventory, 50-53 coins, 54-57 ammo.

### `GET /health`

Returns `{"ok": true}`. Connectivity check.

---

## Files

| File | Purpose |
|------|---------|
| `TerraBlind.cs` | Mod entry point |
| `HttpServerSystem.cs` | HTTP server, request routing, control input parsing |
| `StateSnapshotPlayer.cs` | Per-tick control application + snapshot collection |
| `Snapshot.cs` | Data model definitions |
| `StateSerializer.cs` | Manual JSON serialization |
| `ReplaySystem.cs` | Frame-by-frame skill replay at 60fps |
| `RecordSystem.cs` | Frame recording for skill capture |
| `FightCoordinator.cs` | Autonomous combat (aim + attack) at 60fps |
| `WalkCoordinator.cs` | Walk-to-edge with overhead tile detection |
| `PlaceCoordinator.cs` | Tile placement at relative coordinates |

## Installation

1. Place this folder in `tModLoader/ModSources/TerraBlind/`
2. Build in tModLoader: `Mod Sources` → `Build + Reload`
3. The HTTP server starts automatically when a world is loaded

## Requirements

- Terraria 1.4.4+
- tModLoader 2024+

## License

MIT
