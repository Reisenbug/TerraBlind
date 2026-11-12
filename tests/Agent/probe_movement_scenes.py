import concurrent.futures
import json
from pathlib import Path
import sys
import time
import urllib.request

root = Path(sys.argv[1])
scenes = {}
for path in (root / 'TerraBlindLogs').glob('*/**/*.log'):
    if 'latest' in path.parts:
        continue
    for line in path.read_text(errors='replace').splitlines():
        if '[script_decision_request] ' not in line:
            continue
        payload = json.loads(line.split('[script_decision_request] ', 1)[1])
        state = payload['state']
        if 'follow_route' not in payload['questions'].get('response', {}).get('criteria', {}):
            continue
        if 'x' not in state['combat']['player']:
            continue
        targets = state['combat']['targets']
        if targets and min(n['distance_tiles'] for n in targets) < 3:
            scenes[state['combat']['tick']] = payload

chosen = [scenes[t] for t in sorted(scenes)[::max(1, len(scenes) // 4)]][:4]
key = json.loads((Path.home() / 'Library/Application Support/Terraria/tModLoader/ModConfigs/TerraBlind_Config.json').read_text())['TypeSafeKey']

def probe(payload):
    state = payload['state']
    player = state['combat']['player']
    for target in state['combat']['targets']:
        target.pop('weapons', None)
        target['dx'] = target['x'] - player['x']
        target['dy'] = target['y'] - player['y']
    original = payload['questions']['response']
    revised = json.loads(json.dumps(original))
    revised['instructions'] = 'Which movement best avoids enemy contact and projectile damage over the next half second, considering the local terrain? Attacks can continue during every movement. The journey resumes after the response.'
    revised['criteria']['follow_route'] = 'Keep the currently observed movement keys in combat.player.controls while navigation continues.'
    request = {'model': 'jev-latest', 'state': state, 'questions': {'travel_question': original, 'evasion_question': revised}}
    started = time.monotonic()
    with urllib.request.urlopen(urllib.request.Request('https://api.typesafe.ai/v1/systemone',
            data=json.dumps(request).encode(), headers={'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json'}), timeout=15) as response:
        result = json.load(response)
    return {'tick': state['combat']['tick'], 'elapsed_ms': round((time.monotonic() - started) * 1000), 'request': request, 'result': result}

with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    for result in pool.map(probe, chosen):
        print(json.dumps(result, ensure_ascii=False), flush=True)
