import collections
import json
import math
import re
from pathlib import Path
import statistics
import sys

for argument in sys.argv[1:]:
    root = Path(argument)
    report = json.loads((root / 'decision-comparison.json').read_text())
    samples = report['samples']
    weapons = set(report.get('policy_config', {}).get('weapon_order', []))
    weapon_enemy_samples = [s for s in samples if s['enemies'] and s['held_type'] in weapons]
    weapon_uses = [s for s in weapon_enemy_samples if s['attack'] or s['item_animation'] > 0]
    damage = {s['last_damage']['tick']: s['last_damage'] for s in samples if s.get('last_damage')}
    anchor = samples[0]
    stationary_start = anchor['elapsed_ms']
    stationary = []
    for sample in samples[1:]:
        moved = math.hypot(sample['x'] - anchor['x'], sample['y'] - anchor['y']) > 8
        if moved or sample['dead']:
            duration = sample['elapsed_ms'] - stationary_start
            if duration >= 2000:
                stationary.append(duration)
            anchor, stationary_start = sample, sample['elapsed_ms']
    if samples[-1]['elapsed_ms'] - stationary_start >= 2000:
        stationary.append(samples[-1]['elapsed_ms'] - stationary_start)
    unique_lines = set()
    markers = ('script_decision_request', 'script_decision_result', 'script_decision_channel', 'script_log',
        'combat_forecast', 'combat_forecast_unavailable', 'combat_candidate_apply', 'combat_candidate_abort')
    pattern = re.compile(r'\[(' + '|'.join(markers) + r')\] ')
    decoder = json.JSONDecoder()
    for path in (root / 'TerraBlindLogs').glob('*/**/*.log'):
        if 'latest' not in path.parts:
            content = path.read_text(errors='replace')
            for match in pattern.finditer(content):
                try:
                    payload, _ = decoder.raw_decode(content, match.end())
                except json.JSONDecodeError:
                    continue
                prefix = content[content.rfind('\n', 0, match.start()) + 1:match.start()]
                unique_lines.add(prefix + '[' + match.group(1) + '] ' + json.dumps(payload, sort_keys=True))
    answers = []
    valid_choices = set()
    requests = collections.Counter()
    channels = collections.defaultdict(list)
    applied = collections.defaultdict(list)
    forecasts = []
    unavailable = collections.Counter()
    candidate_events = collections.Counter()
    for line in unique_lines:
        if '[combat_forecast_unavailable] ' in line:
            unavailable[json.loads(line.split('[combat_forecast_unavailable] ', 1)[1])['reason']] += 1
        if '[combat_forecast] ' in line:
            payload = json.loads(line.split('[combat_forecast] ', 1)[1])
            if payload.get('status') == 'ready':
                forecasts.append(payload)
        for marker in ('apply', 'abort'):
            if '[combat_candidate_' + marker + '] ' in line:
                candidate_events[marker] += 1
        if '[script_decision_request] ' in line:
            payload = json.loads(line.split('[script_decision_request] ', 1)[1])
            criteria = payload['questions'].get('response', {}).get('criteria', {})
            dimension = 'combat' if any('_weapon_' in k for k in criteria) else 'weapon' if any(k.startswith('weapon_') for k in criteria) else 'movement' if 'follow_route' in criteria else 'opportunity' if 'take' in criteria else 'probe'
            requests[dimension] += 1
            if dimension != 'probe':
                valid_choices.update(criteria)
        if '[script_decision_result] ' in line:
            payload = json.loads(line.split('[script_decision_result] ', 1)[1])
            if 'response' in payload.get('answers', {}):
                answers.append(payload)
        if '[script_decision_channel] ' in line:
            payload = json.loads(line.split('[script_decision_channel] ', 1)[1])
            channels[payload['channel']].append(payload)
        if '[script_log] ' in line:
            try:
                payload = json.loads(line.split('[script_log] ', 1)[1])
            except json.JSONDecodeError:
                continue
            if isinstance(payload, dict) and 'decision' in payload:
                applied[payload['decision']].append(payload)
    answers = [a for a in answers if a['answers']['response'].get('choice') in valid_choices]
    choices = collections.Counter(a['answers']['response']['choice'] for a in answers)
    latencies = [a['elapsed_ms'] for a in answers]
    workflow = report['after'].get('script_workflows', {}).get('decision-comparison', {})
    inventory = report['after']['inventory_slots']
    observed_resources = collections.defaultdict(list)
    queries = []
    for line in (root / 'client.log').read_text(errors='replace').splitlines():
        if line.startswith('BEHAVIOR_TOOL action_query '):
            entry = json.loads(line.split('BEHAVIOR_TOOL action_query ', 1)[1])
            if entry['handle'] == report['action']['handle']:
                queries.append(entry)
    for sample, entry in zip(samples, queries):
        observation = entry.get('observation')
        if not observation:
            continue
        for resource in (29, 331, 209):
            count = sum(i['stack'] for i in observation['inventory_slots'] if i['type'] == resource)
            previous = observed_resources[resource][-1]['count'] if observed_resources[resource] else 0
            if count > previous:
                observed_resources[resource].append({'count': count, 'seconds': round(sample['elapsed_ms'] / 1000, 2)})
    print(json.dumps({
        'root': root.name, 'variant': report['variant'], 'seed': report['seed'],
        'unarmed': report['unarmed'], 'seconds': round(report['elapsed_ms'] / 1000, 2),
        'completed_legs': sum(s['status'] == 'completed' for s in workflow.get('steps', [])),
        'total_legs': len(report['waypoints']), 'status': report['action']['status'],
        'route_start_index': report.get('route_start_index', -1),
        'weapon_order': report.get('policy_config', {}).get('weapon_order'),
        'agent_authored': report.get('agent_authored', False), 'author_applied_ms': report.get('author_applied_ms'),
        'dead': samples[-1]['dead'], 'hp': samples[-1]['hp'],
        'damage_events': len(damage),
        'observed_damage_including_overkill': sum(e['damage'] for e in damage.values()),
        'sampled_hp_lost': sum(max(0, a['hp'] - max(0, b['hp'])) for a, b in zip(samples, samples[1:])),
        'end_tiles': [round(samples[-1]['x'] / 16, 1), round(samples[-1]['y'] / 16, 1)],
        'stationary_including_mining_seconds': round(sum(stationary) / 1000, 2),
        'stationary_over_2s_count': len(stationary),
        'longest_stationary_seconds': round(max(stationary, default=0) / 1000, 2),
        'enemy_samples': sum(bool(s['enemies']) for s in samples),
        'weapon_enemy_samples': len(weapon_enemy_samples),
        'weapon_use_enemy_samples': len(weapon_uses),
        'moving_weapon_use_samples': sum(math.hypot(s['vx'], s['vy']) > 0.1 for s in weapon_uses),
        'requests': dict(requests), 'choices': dict(choices),
        'forecasts': {'completed': len(forecasts), 'candidate_events': dict(candidate_events),
            'unavailable': dict(unavailable),
            'median_compute_ms': round(statistics.median(f['compute_ms'] for f in forecasts), 1) if forecasts else None,
            'median_capture_ms': round(statistics.median(f['capture_ms'] for f in forecasts), 1) if forecasts else None,
            'queued_prefix_frames': dict(collections.Counter(f['known_route_frames'] for f in forecasts)),
            'safe_landing_candidates': sum(m['safe_landing'] for f in forecasts for m in f['movements'])},
        'channel_results': {key: {'count': len(values),
            'errors': collections.Counter(v['result']['error'] for v in values if v['result'].get('error')),
            'median_age_ms': round(statistics.median(v['request_age_ms'] for v in values), 1)} for key, values in channels.items()},
        'adoptions': {key: {'count': len(values),
            'actions': dict(collections.Counter(v.get('action', v.get('error')) for v in values)),
            'median_age_ms': round(statistics.median(v['request_age_ms'] for v in values if 'request_age_ms' in v), 1)
                if any('request_age_ms' in v for v in values) else None} for key, values in applied.items()},
        'model_median_ms': statistics.median(latencies) if latencies else None,
        'model_max_ms': max(latencies, default=None),
        'resources': {str(t): sum(i['stack'] for i in inventory if i['type'] == t) for t in [29, 331, 209]},
        'resource_inventory_observations': dict(observed_resources),
    }, ensure_ascii=False))
