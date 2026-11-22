# ASCII 地形实验 / ASCII terrain trial

比较同一份决策输入加上 ASCII 地图前后的首次选路和放置结果。每对场景只移动开口，人物、候选格、目标和四个方向最近的实体方块距离都相同。

Compare the first action chosen with and without an ASCII map. Each pair moves an opening while keeping player state, candidate cells, the objective, and cardinal distances to the nearest solid tiles identical.

| Case | Engine check |
| --- | --- |
| `cover-upper`, `cover-lower` | Place one reachable block. Two native bullets must both stop before the player. |
| `descent-left`, `descent-right` | Walk toward the selected side. The player must land on the lower floor. |

## 复现 / Reproduce

Build `tests/Agent/Engine/AgentChecks.csproj` into an isolated tModLoader save containing `TerraBlind.tmod` and a copied `Worlds/Checks.wld`. Use the [arena launch preset](../arena.config.json). Launch the Engine test mod with `-agentenginecheck -agentplanningcheck`.

- `TB_TERRAIN_ASCII=calibrate` runs both physical candidates on each map without model calls.
- `TB_TERRAIN_ASCII=live` compares the coarse input with and without ASCII for GPT-6.2 Sol and Jev. It uses the existing Codex login and TypeSafe configuration.
- `TB_TERRAIN_ASCII=jev-rows` compares the raw ASCII with ASCII prefixed by row numbers, using Jev only.
- `TB_TERRAIN_ASCII=detour-calibrate` verifies both reachable entrances and executes direct and assisted navigation in separate copies of each detour layout. `detour-live` also runs the paired model choices.

`terrain-ascii.json` contains the exact inputs, measured game outcomes, model selections, and time to the first choice. A model selection is scored against its own scene's physical trial. Agent latency includes opening its isolated session. Jev latency measures the existing `jev_judge` call. Treat this as a small paired trial.

## First trials / 首次结果

| Model | Coarse | Raw ASCII | Numbered ASCII |
| --- | ---: | ---: | ---: |
| GPT-6.2 Sol | 2/4 | 4/4 | — |
| Jev 1.14.0 | 3/4 | 3/4 | 3/4 |

Jev chose both entrances correctly with ASCII and chose the lower cover gap in both cover scenes. A second run reproduced the raw result and row numbers preserved the same choices. The coarse input is identical for both members of a pair. Differences within such a pair can occur through response variability.

## Detours / 回头绕路

Both entrances lead to a lower landing. Only one landing connects to the goal chamber. The paired layouts swap the open passage while keeping the markers fixed. The calibration walks both routes with the original movement physics. The direct arm starts RecedingNav at the goal. The assisted arm walks into the selected entrance and then starts RecedingNav at the same goal. Terrain edits are disabled in every arm.

`terrain-detour.json` records the actual first entrance, goal arrival, time to first movement, idle frames, and trajectory samples. In live mode it also records the model's first choice and the sum of decision time and its physical route replay time. Each route replay runs in a fresh copy of its layout in the isolated world.

| Arm | Left passage | Right passage |
| --- | ---: | ---: |
| Direct navigation | 3.85 s, reached | 3.67 s, reached |
| Agent, coarse | Wrong entrance | 9.19 s, reached |
| Agent, ASCII | 8.35 s, reached | 8.40 s, reached |
| Jev, coarse | Wrong entrance | 2.33 s, reached |
| Jev, ASCII | 3.80 s, reached | 2.58 s, reached |

The ASCII arms selected the connected entrance in both layouts. Direct navigation also reached the goal in both layouts and moved within 67 and 137 ms of starting. Model-arm times sum the reported choice latency and the selected route's separate native replay.
