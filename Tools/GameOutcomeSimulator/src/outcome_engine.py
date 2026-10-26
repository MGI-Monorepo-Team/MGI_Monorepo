"""Deterministic, standalone weekly game-outcome simulation.

Unity consumes the resulting game.json. This module never imports Unity and
does not apply Economy rewards or Facility bonuses.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass
import hashlib
import json
import math
from pathlib import Path
import random
from typing import Any, Iterable
import uuid

try:  # Supports both `python -m ...` and direct script execution.
    from .config_loader import SimulatorConfiguration
except ImportError:  # pragma: no cover - exercised by the CLI's direct invocation.
    from config_loader import SimulatorConfiguration


MODEL_VERSION = "1.0.0"
SCHEMA_VERSION = "1.0"
HOME_ADVANTAGE = 2.0
LOGISTIC_SCALE = 0.06
TIER_POINTS = 10.0
OFFENSE_POSITIONS = {"QB", "RB", "OL", "R"}
DEFENSE_POSITIONS = {"DL", "DB", "S"}


class SimulationInputError(ValueError):
    """Raised when a weekly simulator input cannot produce a valid league week."""


@dataclass(frozen=True)
class TeamWeekInput:
    team_id: str
    card_uids: tuple[str, ...]
    coach_ids: tuple[str, ...]

    @classmethod
    def from_json(cls, data: dict[str, Any]) -> "TeamWeekInput":
        team_id = data.get("team_id")
        if not isinstance(team_id, str) or not team_id.strip():
            raise SimulationInputError("Every team input requires a non-empty team_id.")
        card_uids = data.get("card_uids", [])
        coach_ids = data.get("coach_ids", [])
        if not isinstance(card_uids, list) or not all(isinstance(card, str) for card in card_uids):
            raise SimulationInputError(f"Team '{team_id}' card_uids must be a list of strings.")
        if not isinstance(coach_ids, list) or not all(isinstance(coach, str) for coach in coach_ids):
            raise SimulationInputError(f"Team '{team_id}' coach_ids must be a list of strings.")
        if not card_uids:
            raise SimulationInputError(f"Team '{team_id}' requires at least one roster card.")
        return cls(team_id=team_id, card_uids=tuple(card_uids), coach_ids=tuple(coach_ids))


@dataclass(frozen=True)
class WeeklySimulationInput:
    season_id: str
    week: int
    seed: int
    teams: tuple[TeamWeekInput, ...]

    @classmethod
    def from_json(cls, data: dict[str, Any]) -> "WeeklySimulationInput":
        season_id = data.get("season_id")
        week = data.get("week")
        seed = data.get("seed")
        teams = data.get("teams")
        if not isinstance(season_id, str) or not season_id.strip():
            raise SimulationInputError("season_id is required.")
        if not isinstance(week, int) or week < 1:
            raise SimulationInputError("week must be a positive integer.")
        if not isinstance(seed, int):
            raise SimulationInputError("seed must be an integer.")
        if not isinstance(teams, list) or len(teams) < 2 or len(teams) % 2 != 0:
            raise SimulationInputError("teams must contain an even number of at least two teams.")
        parsed_teams = tuple(TeamWeekInput.from_json(team) for team in teams)
        ids = [team.team_id for team in parsed_teams]
        if len(set(ids)) != len(ids):
            raise SimulationInputError("Every team_id must be unique within a simulated week.")
        return cls(season_id=season_id, week=week, seed=seed, teams=parsed_teams)


@dataclass(frozen=True)
class TeamStrength:
    team_id: str
    offense: float
    defense: float
    special_teams: float
    total: float


class WeeklyOutcomeSimulator:
    """Produces a reproducible, complete week from roster-tier and coach inputs."""

    def __init__(self, configuration: SimulatorConfiguration) -> None:
        self._cards = {card.get("uid"): card for card in configuration.cards_catalog.get("cards", []) if card.get("uid")}
        self._coaches = {coach.get("coach_id"): coach for coach in configuration.coaches if coach.get("coach_id")}
        self._config_hash = self._hash_configuration(configuration)

    def simulate_week(self, weekly_input: WeeklySimulationInput) -> dict[str, Any]:
        strengths = {team.team_id: self._calculate_strength(team) for team in weekly_input.teams}
        ordered_teams = sorted(weekly_input.teams, key=lambda team: team.team_id)
        schedule_rng = random.Random(weekly_input.seed)
        schedule_rng.shuffle(ordered_teams)

        outcomes = []
        for match_index in range(0, len(ordered_teams), 2):
            home = ordered_teams[match_index]
            away = ordered_teams[match_index + 1]
            home_strength = strengths[home.team_id]
            away_strength = strengths[away.team_id]
            probability = self.home_win_probability(home_strength.total, away_strength.total)
            roll = schedule_rng.random()
            winner = home.team_id if roll < probability else away.team_id
            event_id = str(uuid.uuid5(
                uuid.NAMESPACE_URL,
                f"mgi:{MODEL_VERSION}:{weekly_input.season_id}:{weekly_input.week}:{home.team_id}:{away.team_id}",
            ))
            outcomes.append({
                "event_id": event_id,
                "season_id": weekly_input.season_id,
                "week": weekly_input.week,
                "home_team_id": home.team_id,
                "away_team_id": away.team_id,
                "winner_team_id": winner,
                "home_win_probability": round(probability, 6),
            })

        return {
            "schema_version": SCHEMA_VERSION,
            "model_version": MODEL_VERSION,
            "season_id": weekly_input.season_id,
            "week": weekly_input.week,
            "seed": weekly_input.seed,
            "input_snapshot": {
                "config_hash": self._config_hash,
                "facility_effects_included": False,
                "teams": [asdict(strengths[team.team_id]) for team in sorted(weekly_input.teams, key=lambda team: team.team_id)],
            },
            "outcomes": outcomes,
        }

    @staticmethod
    def home_win_probability(home_strength: float, away_strength: float) -> float:
        exponent = -LOGISTIC_SCALE * (home_strength - away_strength + HOME_ADVANTAGE)
        return 1.0 / (1.0 + math.exp(exponent))

    def _calculate_strength(self, team: TeamWeekInput) -> TeamStrength:
        cards = []
        for card_id in team.card_uids:
            card = self._cards.get(card_id)
            if card is None:
                raise SimulationInputError(f"Team '{team.team_id}' references unknown card '{card_id}'.")
            cards.append(card)

        offense_tiers = [self._tier(card, team.team_id) for card in cards if card.get("position5") in OFFENSE_POSITIONS]
        defense_tiers = [self._tier(card, team.team_id) for card in cards if card.get("position5") in DEFENSE_POSITIONS]
        all_tiers = [self._tier(card, team.team_id) for card in cards]
        base = self._average(all_tiers) * TIER_POINTS
        offense = self._average(offense_tiers, fallback=base / TIER_POINTS) * TIER_POINTS
        defense = self._average(defense_tiers, fallback=base / TIER_POINTS) * TIER_POINTS
        special_teams = base

        for coach_id in team.coach_ids:
            coach = self._coaches.get(coach_id)
            if coach is None:
                raise SimulationInputError(f"Team '{team.team_id}' references unknown coach '{coach_id}'.")
            bonus = float(coach.get("overall_rating", 0.0))
            coach_type = str(coach.get("coach_type", "")).upper()
            if coach_type == "O":
                offense += bonus
            elif coach_type == "D":
                defense += bonus
            elif coach_type in {"S", "ST"}:
                special_teams += bonus

        total = 0.45 * offense + 0.40 * defense + 0.15 * special_teams
        return TeamStrength(team.team_id, round(offense, 4), round(defense, 4), round(special_teams, 4), round(total, 4))

    @staticmethod
    def _tier(card: dict[str, Any], team_id: str) -> float:
        tier = card.get("cardTier")
        if not isinstance(tier, (int, float)) or tier <= 0:
            raise SimulationInputError(f"Team '{team_id}' has a card with an invalid cardTier.")
        return float(tier)

    @staticmethod
    def _average(values: Iterable[float], fallback: float | None = None) -> float:
        values = list(values)
        if values:
            return sum(values) / len(values)
        if fallback is not None:
            return fallback
        raise SimulationInputError("A team roster must contain at least one card.")

    @staticmethod
    def _hash_configuration(configuration: SimulatorConfiguration) -> str:
        payload = json.dumps(asdict(configuration), sort_keys=True, separators=(",", ":"))
        return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def load_weekly_input(path: str | Path) -> WeeklySimulationInput:
    with Path(path).open(encoding="utf-8") as source:
        return WeeklySimulationInput.from_json(json.load(source))


def write_outcome_file(path: str | Path, outcome_file: dict[str, Any]) -> None:
    destination = Path(path)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(outcome_file, indent=2, sort_keys=True) + "\n", encoding="utf-8")
