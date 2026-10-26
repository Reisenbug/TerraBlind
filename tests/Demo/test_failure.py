"""Exercise the real runner failure branch without launching a client."""

import tempfile
from pathlib import Path
from unittest.mock import Mock, patch

import run


with tempfile.TemporaryDirectory(prefix="terrablind-demo-failure-", dir="/tmp") as directory:
    root = Path(directory)
    (root / "Mods").mkdir()
    process = Mock(pid=12345)
    process.poll.return_value = None

    def launch(*args, **kwargs):
        kwargs["stdout"].write("VNAV_DEMO_FAILED unsupported_wire_fixture\n")
        kwargs["stdout"].flush()
        return process

    with patch.object(run.sys, "argv", ["run.py", "--root", directory]), \
            patch.object(run.shutil, "copy2"), \
            patch.object(Path, "exists", lambda path: path.name not in {"config.json", "input profiles.json"}), \
            patch.object(run.subprocess, "Popen", side_effect=launch):
        try:
            run.main()
        except RuntimeError as error:
            assert "Demo failed" in str(error)
        else:
            raise AssertionError("Runner must still report failure")
    process.terminate.assert_not_called()
    process.kill.assert_not_called()
    print("DEMO_FAILURE_PRESERVES_CLIENT_PASS")
