import {readFileSync} from 'node:fs';
import {resolve} from 'node:path';
import {pathToFileURL} from 'node:url';
import vm from 'node:vm';

function helpers() {
  let callCount = 0;
  const read = (name, args = {}) => {
    callCount++;
    return tool(name, args);
  };
  const check = (condition, reason, evidence = {}) => {
    if (!condition) throw new Error(JSON.stringify({reason, evidence}));
  };
  const call = (name, args = {}) => {
    const result = read(name, args);
    check(result && (!('error' in result) || !result.error) && (!('ok' in result) || result.ok !== false), name, result);
    return result;
  };
  const blocked = (reason, evidence = {}) => ({ok: false, outcome: 'blocked', reason, evidence});
  const done = (facts = {}) => ({ok: true, outcome: 'verified', ...facts});
  const inventory = () => call('inventory_review').inventory;
  const stock = type => inventory().filter(i => i.type === type).reduce((n, i) => n + i.stack, 0);
  const itemAt = slot => call('slot_state', {section: 'inventory', slot}).item;
  const move = (x, y, mode = 'stand') => {
    const result = call('nav_to', {x, y, mode});
    check(result.done === true, 'navigation_incomplete', result);
    return result;
  };
  const npc = type => call('npc_find', {type}).npcs;
  const tile = ({x, y}) => call('opportunity_assess', {x, y}).target;
  const use = (slot, {x, y}, extra = {}) => call('use_item', {x, y, slot, ...extra});
  const place = ({x, y}, item) => call('place_at', {x, y, item: String(item), local: true});
  const click = (section, slot) => {
    const args = {section, slot};
    const state = call('slot_state', args);
    const result = call('slot_click', {...args, button: 'left', expected_item: state.item, expected_cursor: state.cursor});
    check(result.outcome === 'changed', 'slot_unchanged', result);
    return result;
  };
  const open = ({x, y}) => {
    const result = call('interact', {x, y});
    check(result.outcome === 'opened', 'container_not_opened', result);
    return result.chest;
  };
  const transfer = (expected_container, direction, item_type, count) =>
    call('container_transfer', {expected_container, direction, item_type, count});
  const craft = (item_type, amount = 1) => call('craft_into_inventory', {item_type, amount});
  const gather = (targets, source_area) => {
    const result = call('gather', source_area ? {targets, source_area} : {targets});
    check(targets.every(t => stock(t.item_type) >= t.count), 'gather_inventory_unmet', result.outcome);
    return result;
  };
  const editCells = (cells, item) => {
    check(Array.isArray(cells) && cells.length > 0, 'missing_cells');
    for (const cell of cells) if (!tile(cell).has_tile) place(cell, item);
    return cells.map(cell => ({...cell, actual: tile(cell)}));
  };
  const act = (keys, frames, at) => {
    const step = {keys, until: {frames}};
    if (at) step.at = at;
    return call('act', {steps: [step], timeout_frames: frames + 60});
  };
  const railEntry = (input, featherfall = false) => {
    const {track, jump = true} = input;
    const placed = place(track, input.track_item);
    check(placed.cells[0].verified === true, 'track_not_placed', placed);
    if (featherfall) {
      const potion = use(input.potion_slot, track);
      check(potion.verified === true, 'featherfall_not_consumed', potion);
    }
    const fields = featherfall ? ['player', 'buffs'] : ['player'];
    const before = call('get_state', {fields});
    if (featherfall) check(before.buffs.some(buff => buff.id === 8 && buff.time_left > 0), 'featherfall_missing');
    let boarding;
    if (featherfall && jump) {
      const p = before.player;
      const at = [track.x - Math.floor((p.pos.x + p.width / 2 - 0.001) / 16),
        track.y - Math.floor((p.pos.y + p.height - 2) / 16)];
      boarding = call('act', {steps: [
        {keys: ['up', 'jump'], until: {frames: 1}},
        {keys: ['up', 'use_tile'], at, until: {frames: 30}},
      ], timeout_frames: 90});
    } else {
      if (jump) act(['jump'], 1);
      boarding = read('interact', track);
    }
    const after = call('get_state', {fields});
    return blocked('entry_speed_gain_not_verified', {before: before.player.vel, after: after.player.vel,
      boarding: boarding.outcome, featherfall: featherfall && after.buffs.some(buff => buff.id === 8 && buff.time_left > 0)});
  };
  const assign = (room, resident) => {
    check(resident, 'resident_missing', room);
    check(call('housing', {action: 'check', ...room}).suitable === true, 'housing_invalid', room);
    const result = call('housing', {action: 'assign', ...room, npc: resident.idx, npc_type: resident.type});
    check(result.outcome === 'applied' && result.homeless === false, 'housing_assignment_unconfirmed', result);
    return result;
  };
  return {read, check, call, blocked, done, inventory, stock, itemAt, move, npc, tile, use, place,
    click, open, transfer, craft, gather, editCells, act, railEntry, assign, calls: () => callCount};
}

// Each function runs through the game's existing run_script host. A blocked result is a capability probe,
// not a passing test. Fixture inputs identify world facts or materials; they never stand in for verification.
export const cases = {
  '01-no-dirt-jungle': (i, h) => h.blocked('navigation_has_no_player_contact_type_constraint',
    {jungle: h.read('find_biome', {name: 'jungle'})}),

  '02-house-guide': (i, h) => {
    const guide = h.npc(22)[0];
    h.check(guide, 'guide_missing');
    const site = i.site ?? h.call('house_site', {rooms: 1, dir: 1}).build_args;
    h.check(site, 'house_site_missing');
    const built = h.call('build_house', site);
    h.check(built.outcome === 'done', 'house_build_incomplete', built.outcome);
    const room = i.room ?? {x: built.x0 + 2 * built.dir, y: built.floor_row - 2};
    return h.done({assignment: h.assign(room, guide)});
  },

  '03-hellstone-demolition': (i, h) => {
    const recipes = i.output_types.map(name => h.read('recipe', {name}));
    return h.blocked('missing_blast_footprint_lava_immunity_clock_and_verified_escape', {recipes});
  },

  '04-exact-building': (i, h) => {
    const snapshot = h.read('block_region', i.source);
    return h.blocked('full_blueprint_has_no_stance_planner_or_wall_furniture_shape_coverage', {snapshot});
  },

  '05-remember-house-variant': (i, h) => {
    const snapshot = h.read('block_region', i.source);
    return h.blocked('no_complete_house_blueprint_format_or_replay', {snapshot});
  },

  '06-light-dark-souls': (i, h) => {
    const receipts = [];
    for (const zone of [i.light, i.night]) {
      const before = h.stock(zone.item_type);
      h.move(zone.stand.x, zone.stand.y);
      const target = {item_type: zone.item_type, count: before + 9, npc_types: zone.npc_types};
      h.gather([target], zone.source_area);
      receipts.push({item_type: zone.item_type, gained: h.stock(zone.item_type) - before});
    }
    return h.done({souls: receipts});
  },

  '07-altars-hardmode-ores': (i, h) => {
    const world = h.call('get_state', {fields: ['world']}).world;
    if (!world.hardmode) return h.blocked('hardmode_required');
    h.check(i.altars.length === 9 && new Set(i.altars.map(a => a.x + ',' + a.y)).size === 9, 'need_nine_distinct_altar_anchors');
    for (const altar of i.altars) {
      h.move(altar.stand.x, altar.stand.y);
      h.check(h.tile(altar).tile_type === 26, 'altar_changed', altar);
      h.use(i.pwnhammer_slot, altar, {strict: true, duration_ticks: 120});
      h.check(h.tile(altar).tile_type !== 26, 'altar_not_smashed', altar);
    }
    for (const stage of i.stages) {
      for (const target of stage.ores) h.gather([target], stage.source_area);
      for (const output of stage.craft) {
        h.craft(output.item_type, output.count);
        h.check(h.stock(output.item_type) >= output.count, 'crafted_output_missing', output);
      }
      if (stage.station) {
        h.place(stage.station, stage.station.item_type);
        h.check(h.tile(stage.station).tile_type === stage.station.tile_type, 'station_not_placed', stage.station);
      }
    }
    h.check(i.armor_types.length === 3 && i.armor_types.every(type => h.stock(type) > 0), 'hardmode_armor_incomplete');
    return h.done({altars_smashed: 9, armor_types: i.armor_types});
  },

  '08-single-damage-class': (i, h) => h.blocked('no_task_wide_damage_class_allowlist_for_all_weapon_writers',
    {requested_class: i.damage_class, equipped: h.read('equipment_list')}),

  '09-block-enclosure': (i, h) => {
    const {left, right, top, bottom} = i.bounds;
    const perimeter = new Set(i.cells.map(c => c.x + ',' + c.y));
    for (let x = left; x <= right; x++) {
      h.check(perimeter.has(x + ',' + top) && perimeter.has(x + ',' + bottom), 'unsealed_horizontal_edge');
    }
    for (let y = top; y <= bottom; y++) {
      h.check(perimeter.has(left + ',' + y) && perimeter.has(right + ',' + y), 'unsealed_vertical_edge');
    }
    const cells = h.editCells(i.cells, i.item_type);
    h.check(cells.every(c => c.actual.has_tile && c.actual.tile_type === i.tile_type), 'enclosure_cells_mismatch', cells);
    const p = h.call('inspect_terrain', {radius: 4}).player_bounds;
    h.check(p.left > i.bounds.left && p.right < i.bounds.right && p.top > i.bounds.top && p.bottom < i.bounds.bottom,
      'player_outside_enclosure', p);
    return h.done({cells, player: p});
  },

  '10-one-way-wall': (i, h) => h.blocked('no_bidirectional_collision_verification_for_one_way_wall',
    {site: h.read('inspect_terrain', {radius: 6}), requested_direction: i.direction}),

  '11-old-ones-army': (i, h) => h.blocked('missing_crystal_wave_and_dual_front_defense_observation',
    {nearby: h.read('npc_find')}),

  '12-blood-moon-nautilus': (i, h) => {
    const status = h.call('fishing_status');
    if (!status.blood_moon) return h.blocked('blood_moon_required', status);
    const attempts = Math.min(i.attempts ?? 12, 20);
    for (let attempt = 0; attempt < attempts; attempt++) {
      h.call('fish', {action: 'cast', slot: i.rod, ...i.water});
      h.call('fish', {action: 'wait_reel', slot: i.rod, wait_seconds: i.wait_seconds ?? 45});
      if (!h.npc(i.nautilus).length) continue;
      const result = h.call('fight', {npc_types: [i.nautilus], seconds: i.fight_seconds ?? 45});
      return h.blocked('verify_fished_nautilus_kill_with_host_npc_kill_counter', {fight: result.outcome});
    }
    return h.blocked('target_not_hooked', {attempts});
  },

  '13-reforge-defense': (i, h) => {
    let spent = 0;
    const checked = [];
    const start = h.call('slot_state', {section: 'reforge', slot: 0});
    h.check(start.item.type === 0 && start.cursor.type === 0, 'requires_empty_reforge_slot_and_cursor');
    const targets = [...(i.slots ?? []).map(slot => ({section: 'inventory', slot})),
      ...(i.equipment_slots ?? []).map(slot => ({section: 'equipment', slot}))];
    h.check(targets.length > 0, 'missing_reforge_targets');
    for (const {section, slot} of targets) {
      if (h.calls() > 85) return h.blocked('reforge_batch_needs_continuation', {section, slot, spent, prefixes: checked});
      const at = () => h.call('slot_state', {section, slot}).item;
      const initial = at();
      h.check(initial.type > 0, 'missing_accessory', {section, slot});
      if ([64, 65].includes(initial.prefix)) { checked.push(initial.prefix); continue; }
      h.click(section, slot);
      h.click('reforge', 0);
      let matched = false;
      try {
        for (let roll = 0; roll < (i.max_rolls_per_item ?? 20); roll++) {
          if (h.calls() > 88) break;
          const quote = h.call('reforge', {action: 'quote'});
          if (!quote.affordable || quote.cost_copper > i.budget_copper - spent) break;
          const next = h.call('reforge', {action: 'roll', expected_item: quote.item, max_cost: i.budget_copper - spent});
          spent += next.spent_copper;
          if (next.accessory_defense >= 3) { matched = true; break; }
        }
      } finally {
        h.click('reforge', 0);
        h.click(section, slot);
      }
      if (!matched) return h.blocked(h.calls() > 88 ? 'reforge_batch_needs_continuation' : 'reforge_budget_or_roll_limit',
        {section, slot, spent, prefixes: checked});
      const actual = at();
      h.check([64, 65].includes(actual.prefix), 'reforge_prefix_unverified', actual);
      checked.push(actual.prefix);
    }
    return h.done({spent, prefixes: checked});
  },

  '14-queen-bee-spare-hornets': (i, h) => h.blocked('no_protected_npc_constraint_on_queen_bee_damage_paths',
    {hornets: h.read('npc_find', {type: i.hornet}), boss: h.read('boss_status')}),

  '15-starfury-protect-larva': (i, h) => h.blocked('no_landing_footprint_veto_for_starfury_near_larva',
    {larvae: h.read('find_tiles', {name: 'Larva', n: 12, max_dist: 200})}),

  '16-max-life-no-plate': (i, h) => {
    const {x, y} = i.entry;
    const entry = skill('travel_disarm_plates', 0, {x, y, allow_removal: i.allow_removal === true});
    return h.blocked('crystal_gather_has_no_end_to_end_plate_trigger_audit',
      {entry_done: entry.done === true, removed: entry.removed, hp: h.read('get_state', {fields: ['permanent_max_health']})});
  },

  '17-trapped-chest': (i, h) => h.blocked('missing_complete_wire_trace_and_native_trapped_chest_dispatch',
    {target: h.read('opportunity_assess', i.chest), nearby: h.read('inspect_terrain', {radius: 12}).trap_tiles}),

  '18-minecart-mushroom': (i, h) => {
    const mount = h.call('interact', i.board_at);
    h.check(['minecart_mounted', 'already_mounted'].includes(mount.outcome), 'minecart_not_mounted', mount);
    const readPlayer = () => h.call('get_state', {fields: ['player']}).player;
    const stationX = (i.station.x + 0.5) * 16;
    for (let n = 0; n < Math.min(i.max_steps ?? 35, 40); n++) {
      const p = readPlayer(), dx = stationX - p.pos.x;
      const vx = p.vel.x;
      if (Math.abs(dx) <= 24 && Math.abs(vx) < 0.35 && Math.abs(p.pos.y / 16 - i.station.y) < 3) {
        if (Math.abs(vx) >= 0.01) {
          h.act([], 16);
          continue;
        }
        const dismount = h.call('game_input', {action: 'quick_mount', enabled: false});
        h.check(dismount.mounted === false, 'minecart_dismount_not_observed', dismount);
        h.gather(i.resources, i.source_area);
        return h.done({stop: p.pos, resources: i.resources});
      }
      const speed = Math.abs(vx);
      const stoppingDistance = speed * speed / (2 * 0.03);
      const brake = speed >= 0.35 && (Math.sign(vx) !== Math.sign(dx)
        || Math.abs(dx) <= 12 + stoppingDistance + speed * 12);
      const direction = brake ? -Math.sign(vx) : Math.sign(dx);
      const frames = brake ? Math.min(24, Math.floor(speed / 0.03)) : Math.abs(dx) > 96 ? 16 : 6;
      h.act(direction < 0 ? ['left'] : ['right'], frames);
    }
    return h.blocked('station_stop_not_observed', {player: readPlayer()});
  },

  '19-rail-boost': (i, h) => h.railEntry(i),

  '20-featherfall-up-rail-boost': (i, h) => h.railEntry(i, true),

  '21-grapple-only': (i, h) => h.blocked('no_grapple_only_route_with_confirmed_attach_release',
    {site: h.read('inspect_terrain', {radius: 8}), destination: i.destination}),

  '22-grapple-one-wide': (i, h) => h.blocked('missing_grapple_latch_and_one_tile_body_transition_observation',
    {target: h.read('opportunity_assess', i.anchor)}),

  '23-grapple-one-wide-dig': (i, h) => h.blocked('missing_grapple_latch_and_single_column_excavation_constraint',
    {target: h.read('opportunity_assess', i.anchor), column: i.anchor.x}),

  '24-honey-bucket-reaction': (i, h) => h.blocked('missing_buff_and_bucket_state_in_same_damage_reaction',
    {bucket: h.itemAt(i.slot), liquids: h.read('opportunity_assess', i.honey).liquids}),

  '25-gravity-sky-chests': (i, h) => h.blocked('missing_gravity_navigation_and_sky_chest_coverage',
    {sky_candidates: h.read('find_tiles', {name: 'Containers', n: 20, max_dist: 3000})}),

  '26-ab-storage': (i, h) => {
    const receipts = [];
    for (const storage of i.storage) {
      if (storage.stand) h.move(storage.stand.x, storage.stand.y);
      const id = h.open(storage.chest);
      for (const item of storage.items) {
        const before = h.stock(item.type);
        const moved = h.transfer(id, 'deposit', item.type, item.count);
        h.check(moved.transferred === item.count && h.stock(item.type) === before - item.count, 'deposit_mismatch', moved);
        receipts.push({id, type: item.type, count: moved.transferred});
      }
    }
    return h.done({receipts});
  },

  '27-safe-demolition-hellevator': (i, h) => h.blocked('missing_blast_prediction_and_protected_building_footprints',
    {head: h.read('inspect_terrain', {radius: 10}), protected_regions: i.protected_regions}),

  '28-empty-chest-remove-blocks': (i, h) => {
    const id = h.open(i.chest);
    const loot = h.call('loot_all', {expected_container: id});
    h.check(loot.remaining_stacks === 0, 'chest_has_items', loot);
    h.call('game_input', {action: 'inventory', enabled: false});
    for (const point of [i.chest, ...i.below]) {
      h.use(i.pick_slot, point, {duration_ticks: 120, strict: true});
      h.check(!h.tile(point).has_tile, 'tile_not_removed', point);
    }
    return h.done({transferred: loot.transferred_items, removed: 1 + i.below.length});
  },

  '29-floating-placement': (i, h) => h.blocked('no_verified_temporary_ice_or_wiring_support_window',
    {target: h.read('opportunity_assess', i.target), tool: h.itemAt(i.slot)}),

  '30-bouncy-track': (i, h) => {
    h.check(h.inventory().some(item => item.slot === i.hammer_slot && item.hammer > 0), 'hammer_missing');
    for (let swing = 0; swing <= (i.max_swings ?? 6); swing++) {
      const track = h.tile(i.track);
      h.check(track.tile_type === 314, 'track_changed', track);
      if (track.front_bouncy || track.back_bouncy) return h.done({track, swings: swing});
      if (swing < (i.max_swings ?? 6)) h.use(i.hammer_slot, i.track, {strict: true, duration_ticks: 45});
    }
    return h.blocked('bouncy_track_state_not_observed', {track: h.tile(i.track)});
  },

  '31-find-shimmer-safely': (i, h) => {
    const p = h.call('get_state', {fields: ['player']}).player;
    h.check(!p.in_liquid && p.on_ground, 'requires_dry_stance');
    const origin = i.search ?? {x: Math.floor((p.pos.x + p.width / 2) / 16), y: Math.floor((p.pos.y + p.height) / 16)};
    const offsets = [0, -8, 8, -16, 16, -24, 24];
    let sampled = 0;
    for (const dy of offsets) for (const dx of offsets) {
      const at = {x: origin.x + dx, y: origin.y + dy};
      if (at.x < 1 || at.y < 1) continue;
      const probe = h.read('opportunity_assess', at);
      if (!probe.ok) continue;
      sampled++;
      const liquid = probe.liquids.find(t => t.liquid_type === 3 && t.amount > 0);
      if (liquid) {
        const current = h.call('get_state', {fields: ['player']}).player;
        h.check(!current.in_liquid && !current.dead, 'dry_stance_lost', current.pos);
        return h.done({liquid, sampled, player: current.pos});
      }
    }
    return h.blocked('shimmer_not_observed_in_search_region', {origin, sampled});
  },

  '32-grapple-no-knockback': (i, h) => {
    const action = h.act(['hook'], 2, i.anchor_offset);
    return h.blocked('grapple_input_is_observable_but_latch_and_knockback_are_not', {action: action.outcome});
  },

  '33-shimmer-transform-and-pickup': (i, h) => {
    const expected = h.itemAt(i.slot);
    h.check(expected.type === i.input_type && expected.stack > 0, 'wrong_shimmer_input', expected);
    h.check(i.outputs.length > 0, 'missing_shimmer_outputs');
    const p = h.call('get_state', {fields: ['player']}).player;
    h.check(!p.in_liquid && p.on_ground, 'requires_dry_stance');
    const centerX = p.pos.x + p.width / 2;
    const tileX = Math.floor(centerX / 16), feetY = Math.floor((p.pos.y + p.height) / 16);
    const delta = i.target.x - tileX;
    h.check(Math.abs(delta) >= 1 && Math.abs(delta) <= 6 && i.target.y >= feetY && i.target.y <= feetY + 3,
      'requires_nearby_pool_below_stance', i.target);
    const pool = h.call('opportunity_assess', i.target).liquids;
    h.check(pool.some(t => t.liquid_type === 3 && t.amount >= 128 && t.x === i.target.x && t.y === i.target.y),
      'shimmer_target_unverified', i.target);
    const initial = h.call('get_state', {fields: ['inventory_counts']}).inventory_counts;
    const baseline = i.outputs.map(({type, count}) => ({type, count, stock: String(type) in initial ? initial[type] : 0}));
    const direction = delta < 0 ? 'left' : 'right';
    if (p.direction !== direction) h.act([direction], 1);
    const drop = h.call('inventory_drop', {slot: i.slot, expected});
    h.check(drop.outcome === 'dropped' && Math.sign(drop.spawn_pixels.vx) === Math.sign(delta), 'throw_unverified', drop);
    for (let n = 0; n < 20; n++) {
      const {player: current, inventory_counts: counts} = h.call('get_state', {fields: ['player', 'inventory_counts']});
      if (current.in_liquid || current.dead) return h.blocked('dry_stance_lost', {position: current.pos});
      const gained = baseline.map(({type, stock}) => ({type, count: (String(type) in counts ? counts[type] : 0) - stock}));
      if (gained.every((item, index) => item.count >= baseline[index].count))
        return h.done({gained, world_item: drop.world_item});
      h.act([], 20);
    }
    return h.blocked('shimmer_output_not_collected_from_shore', {world_item: drop.world_item,
      drops: h.call('nearby_drops', {radius: 10}).drops});
  },

  '34-use-pylon': (i, h) => {
    const observed = h.call('pylon_list');
    h.check(observed.near_pylon, 'no_source_pylon_in_reach');
    h.check(observed.pylons.some(p => p.x === i.x && p.y === i.y), 'destination_pylon_missing');
    const result = h.call('pylon_use', {x: i.x, y: i.y});
    h.check(result.outcome === 'arrived', 'pylon_arrival_unconfirmed', result);
    return h.done({arrival: {x: result.x, y: result.y}});
  },

  '35-repair-pylon-residency': (i, h) => {
    const destination = () => h.call('pylon_list').pylons.find(p => p.x === i.x && p.y === i.y);
    const original = destination();
    h.check(original && original.vanilla_residency_ok === false, 'requires_missing_pylon_residents', original);
    for (const housing of i.houses) h.assign(housing.room, h.npc(housing.npc_type)[0]);
    h.move(i.wait_at.x, i.wait_at.y);
    for (let n = 0; n < Math.min(i.samples ?? 10, 20); n++) {
      const pylon = destination();
      if (pylon?.vanilla_residency_ok === true) {
        const arrival = h.call('pylon_use', {x: i.x, y: i.y});
        return arrival.outcome === 'arrived' ? h.done({arrival, residency: {before: false, after: true}})
          : h.blocked('pylon_arrival_unconfirmed', arrival);
      }
      h.act([], 30);
    }
    return h.blocked('pylon_residency_still_unconfirmed', {pylon: destination()});
  },

  '36-wait-npc-return': (i, h) => {
    const world = h.call('get_state', {fields: ['world']}).world;
    if (world.day && !world.raining) return h.blocked('requires_night_or_rain');
    const resident = h.npc(i.npc_type)[0];
    h.check(resident, 'resident_missing');
    const assigned = h.assign(i.room, resident);
    const nearHome = n => n && Math.abs(n.cell[0] - assigned.home_x) <= 4 && Math.abs(n.cell[1] - assigned.home_y) <= 4;
    if (nearHome(resident)) return h.blocked('resident_already_at_home', {actual: resident.cell});
    h.move(i.wait_at.x, i.wait_at.y);
    for (let n = 0; n < (i.samples ?? 10); n++) {
      const actual = h.npc(i.npc_type).find(npc => npc.idx === resident.idx);
      if (nearHome(actual)) return h.done({home: {x: assigned.home_x, y: assigned.home_y}, actual: actual.cell,
        before: resident.cell});
      h.act([], 30);
    }
    return h.blocked('npc_not_observed_at_home', {npc: resident.idx});
  },

  '37-wall-of-flesh-dynamite': (i, h) => {
    const before = h.call('get_state', {fields: ['world']}).world.hardmode;
    if (before) return h.blocked('requires_pre_hardmode_world');
    const result = h.call('hell_run');
    const hardmode = h.call('get_state', {fields: ['world']}).world.hardmode;
    h.check(hardmode && result.wof_outcome === 'done', 'wall_of_flesh_result_unverified', {hardmode, result});
    return h.done({hardmode, outcome: result.wof_outcome});
  },

  '38-wall-of-flesh-no-bridge': (i, h) => h.blocked('missing_potion_aware_no_construction_wall_of_flesh_controller',
    {boss: h.read('boss_status'), consumables: i.potion_slots.map(h.itemAt)}),

  '39-vertical-only-queen-bee': (i, h) => h.blocked('missing_frame_level_vertical_only_boss_policy_with_kill_verification',
    {shaft: h.read('inspect_terrain', {radius: 8}), boss: h.read('boss_status')}),

  '40-mod-godseeker-dash': (i, h) => {
    const keys = h.call('mod_input_list', {mod: i.mod});
    if (!keys.keys.includes(i.key)) return h.blocked('binding_not_found_or_is_a_mod_config', {keys: keys.keys});
    const result = h.call('mod_input', {key: i.key, frames: 1});
    return h.blocked('dash_effect_needs_mod_specific_verification', {dispatch: result.outcome});
  }
};

export function buildCase(name, input) {
  if (!Object.hasOwn(cases, name)) throw new Error('Unknown case: ' + name);
  return 'return (' + cases[name].toString() + ')(' + JSON.stringify(input) + ',(' + helpers.toString() + ')());';
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const [command, name, path] = process.argv.slice(2);
  if (command === 'list') console.log(Object.keys(cases).join('\n'));
  else if (command === 'build') {
    const code = buildCase(name, JSON.parse(readFileSync(path, 'utf8')));
    if (code.length > 16000) throw new Error('Game script exceeds 16000 characters');
    console.log(JSON.stringify({code}));
  } else if (command === 'bundle') {
    console.log(JSON.stringify(Object.fromEntries(Object.keys(cases).map(id => [id, buildCase(id, '__TB_FIXTURE__')]))));
  } else if (command === 'audit') {
    const schema = JSON.parse(readFileSync(new URL('../../../Agent/Tools.json', import.meta.url), 'utf8'));
    const names = new Set(schema.map(entry => entry.function.name));
    for (const name of Object.keys(cases)) {
      const code = buildCase(name, {});
      new vm.Script('(function(){' + code + '\n})');
      if (code.length > 16000) throw new Error(name + ' exceeds 16000 characters');
      for (const [, tool] of code.matchAll(/\b(?:call|read|tool)\('([^']+)'/g)) {
        if (!names.has(tool)) throw new Error(name + ' references unknown tool: ' + tool);
      }
    }
    console.log('CASE_AUDIT count=' + Object.keys(cases).length + ' node_parse=true tool_names=true');
  } else throw new Error('Usage: node task_cases.mjs list | audit | build <case> <fixture.json>');
}
