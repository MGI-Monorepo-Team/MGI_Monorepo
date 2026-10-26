"""Loader tests. Originally Adnan's (A1), updated for the 499-coach catalog."""
import json
import tempfile
import unittest
from pathlib import Path

from gos.config_loader import REPO_ROOT, ConfigurationLoadError, StreamingAssetsConfigLoader


class StreamingAssetsConfigLoaderTests(unittest.TestCase):
    def test_loads_the_repository_configuration(self) -> None:
        configuration = StreamingAssetsConfigLoader.from_repository_root(REPO_ROOT).load()

        self.assertGreater(len(configuration.cards_catalog["cards"]), 0)
        self.assertGreater(len(configuration.teams), 0)
        self.assertEqual(499, len(configuration.coaches))
        self.assertIn("xp_bonus_rules", configuration.coach_bonus_rules)
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

    def test_coach_catalog_must_have_a_coach_list(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            streaming_assets = Path(temporary_directory)
            self._write_required_configuration(streaming_assets, win_xp=10)
            (streaming_assets / "Coaches/coaches_schema_data.json").write_text('{"coach": {}}', encoding="utf-8")
            with self.assertRaisesRegex(ConfigurationLoadError, "coaches.coach"):
                StreamingAssetsConfigLoader(streaming_assets).load()

    @staticmethod
    def _write_required_configuration(streaming_assets: Path, win_xp: int) -> None:
        files = {
            "CCAS/cards_catalog.json": {"cards": [{"uid": "card-1"}]},
            "Coaches/Database/team.json": [{"team_id": "team-1"}],
            "Coaches/coaches_schema_data.json": {"coach": [{"coach_id": "coach-1"}]},
            "Coaches/coaches_bonus_config.json": {"xp_bonus_rules": []},
            "Facilities/facilities_config.json": {"facility_definitions": []},
            "Progression/progression.json": {"xp_reward_rules": {"win": win_xp}},
        }
        for relative_path, contents in files.items():
            path = streaming_assets / relative_path
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(contents), encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
