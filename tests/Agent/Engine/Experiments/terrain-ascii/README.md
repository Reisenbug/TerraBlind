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

`terrain-ascii.json` contains the exact inputs, measured game outcomes, model selections, and time to the first choice. A model selection is scored against its own scene's physical trial. Agent latency includes opening its isolated session. Jev latency measures the existing `jev_judge` call. Treat this as a small paired trial.

## First trials / 首次结果

| Model | Coarse | Raw ASCII | Numbered ASCII |
| --- | ---: | ---: | ---: |
| GPT-6.2 Sol | 2/4 | 4/4 | — |
| Jev 1.14.0 | 3/4 | 3/4 | 3/4 |

Jev chose both entrances correctly with ASCII and chose the lower cover gap in both cover scenes. A second run reproduced the raw result and row numbers preserved the same choices. The coarse input is identical for both members of a pair. Differences within such a pair can occur through response variability.
