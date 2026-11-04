"""Verify prewarm, task lifetime reuse, and cleanup in the actual client."""

import json
import sys
from pathlib import Path

root = Path(sys.argv[1])
trace = (root / "TerraBlindLogs/latest/jump_trace.log").read_text()
client = (root / "demo-client.log").read_text()
starts = [line for line in trace.splitlines() if "[engine-worker-start]" in line]
ready = [line.split("[engine-worker-ready] ", 1)[1]
         for line in trace.splitlines() if "[engine-worker-ready] " in line]
assert len(starts) == 1 and "prewarm=True" in starts[0], starts
assert len(ready) == 1, ready
assert "VNAV_WORKER_COMPLETE tasks=2 starts=1 pending_stop=preserved unload=closed" in client
assert "[engine-worker-prewarm-failed]" not in trace
timing = json.loads(ready[0])
assert timing["connected_ms"] >= timing["ready_ms"] >= timing["files_ready_ms"]
print(json.dumps(timing, indent=2))
print("WORKER_PREWARM_REUSE_PASS tasks=2 starts=1")
