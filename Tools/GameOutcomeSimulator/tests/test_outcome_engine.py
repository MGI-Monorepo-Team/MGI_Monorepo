import copy
from pathlib import Path
import sys
import unittest


SIMULATOR_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(SIMULATOR_ROOT / "src"))

from config_loader import SimulatorConfiguration, StreamingAssetsConfigLoader  # noqa: E402
from outcome_engine import WeeklyOutcomeSimulator, WeeklySimulationInput  # noqa: E402


class WeeklyOutcomeSimulatorTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        repository_root = SIMULATOR_ROOT.parents[1]
        cls.configuration = StreamingAssetsConfigLoader.from_repository_root(repository_root).load()
        cls.cards = cls.configuration.cards_catalog["cards"]

    def test_same_seed_and_input_produce_identical_output(self) -> None:
        simulator = WeeklyOutcomeSimulator(self.configuration)
        weekly_input = self._weekly_input(seed=17)

        self.assertEqual(simulator.simulate_week(weekly_input), simulator.simulate_week(weekly_input))

    def test_complete_week_contains_each_team_once_and_balances_wins_losses(self) -> None:
        result = WeeklyOutcomeSimulator(self.configuration).simulate_week(self._weekly_input(seed=3))
        outcomes = result["outcomes"]
        teams = {team_id for outcome in outcomes for team_id in (outcome["home_team_id"], outcome["away_team_id"])}
        winners = [outcome["winner_team_id"] for outcome in outcomes]
        losses = [
            outcome["away_team_id"] if outcome["winner_team_id"] == outcome["home_team_id"] else outcome["home_team_id"]
            for outcome in outcomes
        ]

        self.assertEqual(4, len(teams))
        self.assertEqual(2, len(outcomes))
        self.assertEqual(len(winners), len(losses))
        self.assertTrue(all(outcome["winner_team_id"] in {outcome["home_team_id"], outcome["away_team_id"]} for outcome in outcomes))
        self.assertFalse(result["input_snapshot"]["facility_effects_included"])

    def test_stronger_home_team_has_higher_win_rate_over_many_seeds(self) -> None:
        weak = self._cards_for_tier(1)
        strong = self._cards_for_tier(5)
        wins = 0
        trials = 300
        simulator = WeeklyOutcomeSimulator(self.configuration)
        for seed in range(trials):
            data = {
                "season_id": "calibration-season",
                "week": 1,
                "seed": seed,
                "teams": [
                    {"team_id": "strong", "card_uids": strong, "coach_ids": []},
                    {"team_id": "weak", "card_uids": weak, "coach_ids": []},
                ],
            }
            result = simulator.simulate_week(WeeklySimulationInput.from_json(data))
            outcome = result["outcomes"][0]
            if outcome["home_team_id"] == "strong" and outcome["winner_team_id"] == "strong":
                wins += 1
            elif outcome["away_team_id"] == "strong" and outcome["winner_team_id"] == "strong":
                wins += 1

        self.assertGreater(wins / trials, 0.70)

    def test_facility_configuration_does_not_change_outcomes(self) -> None:
        changed_facilities = copy.deepcopy(self.configuration.facilities)
        changed_facilities["test_only_facility_change"] = {"PlayerStrengthBoost": 999}
        changed_configuration = SimulatorConfiguration(
            cards_catalog=self.configuration.cards_catalog,
            teams=self.configuration.teams,
            coaches=self.configuration.coaches,
            facilities=changed_facilities,
            progression=self.configuration.progression,
        )
        weekly_input = self._weekly_input(seed=73)

        baseline = WeeklyOutcomeSimulator(self.configuration).simulate_week(weekly_input)
        changed = WeeklyOutcomeSimulator(changed_configuration).simulate_week(weekly_input)

        self.assertEqual(baseline["outcomes"], changed["outcomes"])
        self.assertEqual(baseline["input_snapshot"]["teams"], changed["input_snapshot"]["teams"])
        self.assertNotEqual(baseline["input_snapshot"]["config_hash"], changed["input_snapshot"]["config_hash"])

    def _weekly_input(self, seed: int) -> WeeklySimulationInput:
        tier_two = self._cards_for_tier(2)
        tier_three = self._cards_for_tier(3)
        tier_four = self._cards_for_tier(4)
        tier_five = self._cards_for_tier(5)
        return WeeklySimulationInput.from_json({
            "season_id": "season-under-test",
            "week": 1,
            "seed": seed,
            "teams": [
                {"team_id": "team-a", "card_uids": tier_two, "coach_ids": []},
                {"team_id": "team-b", "card_uids": tier_three, "coach_ids": []},
                {"team_id": "team-c", "card_uids": tier_four, "coach_ids": []},
                {"team_id": "team-d", "card_uids": tier_five, "coach_ids": []},
            ],
        })

    def _cards_for_tier(self, tier: int) -> list[str]:
        # The current catalog has QB cards only at tier 1. These positions occur
        # at every configured tier and still exercise offense, defense, and ST.
        positions = {"RB", "OL", "R", "DL", "DB", "S"}
        selected = []
        for position in positions:
            card = next(card for card in self.cards if card["cardTier"] == tier and card["position5"] == position)
            selected.append(card["uid"])
        return selected


if __name__ == "__main__":
    unittest.main()
