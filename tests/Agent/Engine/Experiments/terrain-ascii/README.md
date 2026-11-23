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
- `TB_TERRAIN_ASCII=vision-calibrate` verifies the physical actions and captures the input images. `vision-live` additionally tests all three input formats with both models.
- `TB_TERRAIN_ASCII=vision-deep` compares the three formats on two four-block enclosure layouts. It also measures Jev on the tile maps from the four simpler layouts.
- `TB_TERRAIN_ASCII=recovery-calibrate` runs direct navigation and a reference waypoint on six layouts. `recovery-live` then asks the Agent for a waypoint from ASCII and from the game frame and runs both choices.
- `TB_TERRAIN_ASCII=pit-inputs` compares existing navigation with engine-tested input sequences in the pit. `pit-inputs-live` adds the Agent's first input choice with ASCII and with a game frame.

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

## Image comparison / 画面对比

The two cover and two detour layouts are each presented as ASCII, a labeled `terrain_map` image, and a cropped native game frame. The window and candidate markers describe the same tiles in each representation. The game crop uses arena lighting and labeled cells. Native projectile and movement checks establish which option works before the model calls.

Each enclosure layout has two plans of four blocks, all within reach. The outside columns change across layouts, reversing which plan seals the room. The fixture places every block through `WorldGen.PlaceTile` and checks whether a four-connected path from the player to the exterior remains. The model picks one of the two plans using a single observation.

`terrain-vision/report.json` records the scene inputs and decisions. The same directory contains the exact map and game PNGs. Agent decision time starts when the terrain tool returns. Jev time covers the complete request. Image requests use TypeSafe's experimental [image interface](https://docs.typesafe.ai/images).

## Waypoint recovery / 卡点续走

The six fixtures are three layouts and their mirrors. They represent approaching a high crystal shelf from below, backtracking out of a pit, and getting around a wall to a clear shot. The shooting fixture checks the native line-of-sight query at arrival. The layouts reconstruct the geometry of reported problems.

Each arm starts with the same terrain, position and vanilla movement. Direct navigation starts at G. The two Agent arms request one intermediate standing coordinate, navigate there, and continue to G. The only difference in their model inputs is the terrain representation. Terrain editing is disabled. The reference waypoint is recorded for calibration and withheld from the model.

`terrain-recovery/report.json` records completion, the first visible displacement, time to move one tile from start, longest idle frames, the chosen coordinates and movement traces. Report the model session startup and the measured time from observation to choice separately. The total for an Agent arm adds cold-session time to physical execution time.

| Trial | Arrivals | Median observation-to-choice | Median cold session-to-choice |
| --- | ---: | ---: | ---: |
| Direct navigation | 1/6 | — | — |
| Agent with ASCII | 1/6 | 3.02 s | 9.58 s |
| Agent with game frame | 1/6 | 2.53 s | 8.55 s |
| Reference route | 2/6 | — | — |

All three comparison arms reached the same right-side firing position. On that layout, direct navigation took 4.11 s. The Agent routes took 2.71 s and 3.57 s to execute, and 12.93 s and 11.87 s including the cold session. Only the reference waypoint reached the mirrored firing position. In the right-side pit, the ASCII choice reached its first rung in 0.90 s, then the final leg hit the limit. Each layout has one run per arm.

## Native input sequences / 原版按键序列

The original pit fixture left 16 px between the raised step and the overhang. The 20 px wide player stopped against that corner in both directions. This trial trims the overhang by one tile for 32 px of clearance and requires a successful native reference playback before asking the model.

The worker tries eight walk and jump prefixes from the same snapshot. Only distinct, damage-free landings at least one tile from the start become options. The code-only selector minimizes straight-line distance from the landing to G. Each selector uses the same engine search to continue through four fixed standing bands on the staircase and rim. The geometry reference starts that search with no prefix. Its bands are specified in this fixture. The Agent sees candidate inputs and measured endpoints, plus either ASCII or the native frame, before choosing a single input sequence.

`pit-inputs/report.json` includes every candidate, the options selected, segment search times and the real client playback. The playback checks for damage, a supported landing on the rim, and arrival at G. It also measures position error against the worker's prediction.

| Arm | Arrivals | Playback frames on right / left |
| --- | ---: | ---: |
| Existing navigation | 1/2 | 54 (stopped) / 244 |
| Nearest-to-G landing | 2/2 | 279 / 279 |
| GPT-6.2 Sol with ASCII | 2/2 | 219 / 219 |
| GPT-6.2 Sol with game frame | 2/2 | 219 / 219 |
| Geometry reference | 2/2 | 204 / 204 |

Both model inputs picked the same run and jump on each layout. The code-only selector spent its first move heading toward the wall. The model saved 60 playback frames on both layouts. It took 1.36–1.69 s from observation to choice, and 7.25–10.16 s from cold session launch to choice. Even using the shorter decision time, adding search time and playback yielded 5.73–6.00 s for the model arms and 5.62–5.65 s for the nearest-to-G arm. Candidate generation was shared. Every completed native playback was damage-free and matched the worker's positions with zero measured pixel error.
