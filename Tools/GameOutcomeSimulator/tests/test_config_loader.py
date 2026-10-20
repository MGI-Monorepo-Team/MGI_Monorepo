import json
from pathlib import Path
import sys
import tempfile
import unittest


SIMULATOR_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SIMULATOR_ROOT / "src"))

from config_loader import ConfigurationLoadError, StreamingAssetsConfigLoader  # noqa: E402


class StreamingAssetsConfigLoaderTests(unittest.TestCase):
    def test_loads_the_repository_configuration(self) -> None:
        repository_root = SIMULATOR_ROOT.parents[1]

        configuration = StreamingAssetsConfigLoader.from_repository_root(repository_root).load()

        self.assertGreater(len(configuration.cards_catalog["cards"]), 0)
        self.assertGreater(len(configuration.teams), 0)
        self.assertGreater(len(configuration.coaches), 0)
        self.assertIn("facility_definitions", configuration.facilities)
        self.assertIn("xp_reward_rules", configuration.progression)

    def test_changed_progression_config_is_reflected_in_loaded_output(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            streaming_assets = Path(temporary_directory)
            self._write_required_configuration(streaming_assets, win_xp=10)
            loader = StreamingAssetsConfigLoader(streaming_assets)
            first = loader.load()

            self._write_required_configuration(streaming_assets, win_xp=42)
            second = loader.load()

            self.assertEqual(10, first.progression["xp_reward_rules"]["win"])
            self.assertEqual(42, second.progression["xp_reward_rules"]["win"])

    def test_missing_required_file_has_actionable_error(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            with self.assertRaisesRegex(ConfigurationLoadError, "cards_catalog"):
                StreamingAssetsConfigLoader(temporary_directory).load()

    @staticmethod
    def _write_required_configuration(streaming_assets: Path, win_xp: int) -> None:
        files = {
            "CCAS/cards_catalog.json": {"cards": [{"uid": "card-1"}]},
            "Coaches/Database/team.json": [{"team_id": "team-1"}],
            "Coaches/Database/coach.json": [{"coach_id": "coach-1"}],
            "Facilities/facilities_config.json": {"facility_definitions": []},
            "Progression/progression.json": {"xp_reward_rules": {"win": win_xp}},
        }
        for relative_path, contents in files.items():
            path = streaming_assets / relative_path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(contents), encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
