import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("dev_client", Path(__file__).resolve().parents[2] / "scripts/dev_client.py")
client = importlib.util.module_from_spec(spec)
spec.loader.exec_module(client)


class ClientIsolationTests(unittest.TestCase):
    def test_roles_and_source_directories_are_isolated(self):
        with patch.object(client, "REPO", Path("/tmp/a/TerraBlind")):
            nav = client.layout("navigation")
            agent = client.layout("agent")
        with patch.object(client, "REPO", Path("/tmp/b/TerraBlind")):
            other = client.layout("navigation")
        self.assertTrue(set(nav).isdisjoint(agent))
        self.assertTrue(set(nav).isdisjoint(other))

    def test_prepare_clones_without_overwriting(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source"
            source.mkdir()
            (source / "config.json").write_text('{"volume":1}')
            world = source / "World.wld"
            world.write_bytes(b"original world")
            world.with_suffix(".twld").write_bytes(b"mod world")
            save = root / "client"
            with patch.object(client, "USER_SAVES", source):
                client.prepare(save, world)
                (save / "config.json").write_text('{"volume":0}')
                (save / "Worlds/World.wld").write_bytes(b"played world")
                client.prepare(save, world)
            self.assertEqual(json.loads((save / "config.json").read_text())["volume"], 0)
            self.assertEqual((save / "Worlds/World.wld").read_bytes(), b"played world")
            self.assertEqual(world.read_bytes(), b"original world")
            self.assertEqual((save / "Worlds/World.twld").read_bytes(), b"mod world")
            self.assertEqual(json.loads((save / "Mods/enabled.json").read_text()), ["TerraBlind"])


if __name__ == "__main__":
    unittest.main()
