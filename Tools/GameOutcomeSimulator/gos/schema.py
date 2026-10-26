"""The data contract (schema v0.2).

Every batch, simulator or live, must match these tables. Keep this file and
the design spec in sync. Any field change bumps SCHEMA_VERSION.

Three tables
  game_outcomes   one row per team per match (2 rows per match)
  season_summary  one row per team per season
  player_journey  one row per week for the player team
"""
from dataclasses import dataclass

from gos import SCHEMA_VERSION

SOURCES = ("simulator", "live")


@dataclass(frozen=True)
class Field:
    name: str
    type: type                 # str, int, float or bool
    required: bool = True      # must be present and non-null
    sim_only: bool = False     # null allowed when source == "live"
    allowed: tuple = ()        # enum values, empty means any value
    min: float | None = None   # lowest allowed number


F = Field

GAME_OUTCOMES = (
    F("schema_version", str),
    F("source", str, allowed=SOURCES),
    F("mode", str, sim_only=True, allowed=("results", "lump")),
    F("seed", int, sim_only=True),
    F("season_id", str),
    F("week", int, min=1),
    F("match_id", str),
    F("event_id", str),
    F("league", str),
    F("game_type", str, allowed=("regular", "playoff")),
    F("team_id", str),
    F("team_name", str),
    F("element", str),
    F("is_player_team", bool),
    F("opponent_id", str),
    F("home_away", str, allowed=("home", "away")),
    F("result", str, allowed=("W", "L")),
    F("points_for", int, min=0),
    F("points_against", int, min=0),
    F("margin", int),
    F("wins_to_date", int, min=0),
    F("losses_to_date", int, min=0),
    F("current_streak", int),
    F("standing_points_to_date", int, min=0),
    F("offence_coach", str),
    F("defence_coach", str),
    F("special_teams_coach", str),
    F("coach_bonus_applied_percent", float, min=0),
    F("team_strength", float, sim_only=True, min=0),
    F("opponent_strength", float, sim_only=True, min=0),
    F("win_probability", float, sim_only=True, min=0),
)

SEASON_SUMMARY = (
    F("schema_version", str),
    F("source", str, allowed=SOURCES),
    F("mode", str, sim_only=True, allowed=("results", "lump")),
    F("season_id", str),
    F("team_id", str),
    F("team_name", str),
    F("is_player_team", bool),
    F("games_played", int, min=0),
    F("wins", int, min=0),
    F("losses", int, min=0),
    F("standing_points", int, min=0),
    F("points_for", int, min=0),
    F("points_against", int, min=0),
    F("point_diff", int),
    F("placement", int, min=1),
    F("team_strength", float, sim_only=True, min=0),
)

PLAYER_JOURNEY = (
    F("mode", str, sim_only=True, allowed=("results", "lump")),
    F("run", int, min=1),
    F("season", int, min=1),
    F("week", int, min=1),
    F("season_id", str),
    F("result", str, allowed=("W", "L")),
    F("xp_gained", float, min=0),
    F("total_xp", float, min=0),
    F("tier", str, allowed=("rookie", "pro", "all_star", "legend")),
    F("coins_earned", int, min=0),
    F("wallet", int, min=0),
    F("weight_room", int, min=0),
    F("film_room", int, min=0),
    F("rehab_center", int, min=0),
    F("upgrades_bought", str, required=False),
    F("team_strength", float, sim_only=True, min=0),
    # only on the last week of a season
    F("season_bonus_xp", float, required=False, min=0),
    F("season_bonus_coins", int, required=False, min=0),
    F("placement", int, required=False, min=1),
)

TABLES = {
    "game_outcomes": GAME_OUTCOMES,
    "season_summary": SEASON_SUMMARY,
    "player_journey": PLAYER_JOURNEY,
}


def columns(table: str) -> list[str]:
    """Column names in contract order."""
    return [f.name for f in TABLES[table]]


def _type_ok(value, expected: type) -> bool:
    if expected is bool:
        return isinstance(value, bool)
    if expected is int:
        return isinstance(value, int) and not isinstance(value, bool)
    if expected is float:  # ints are fine where a float is expected
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    return isinstance(value, expected)


def check_row(row: dict, table: str) -> list[str]:
    """Return a list of problems with one row. Empty list means it is valid."""
    errors = []
    fields = TABLES[table]
    known = {f.name for f in fields}
    live = row.get("source") == "live"

    for extra in sorted(set(row) - known):
        errors.append(f"unknown field '{extra}'")

    for f in fields:
        value = row.get(f.name)
        if value is None or value == "":
            if f.required and not (f.sim_only and live):
                errors.append(f"'{f.name}' is missing")
            continue
        if not _type_ok(value, f.type):
            errors.append(f"'{f.name}' should be {f.type.__name__}, got {type(value).__name__} {value!r}")
            continue
        if f.allowed and value not in f.allowed:
            errors.append(f"'{f.name}' must be one of {f.allowed}, got {value!r}")
        if f.min is not None and value < f.min:
            errors.append(f"'{f.name}' must be >= {f.min}, got {value!r}")

    if "schema_version" in known and row.get("schema_version") not in (None, SCHEMA_VERSION):
        errors.append(f"schema_version {row.get('schema_version')!r} does not match {SCHEMA_VERSION!r}")
    return errors
