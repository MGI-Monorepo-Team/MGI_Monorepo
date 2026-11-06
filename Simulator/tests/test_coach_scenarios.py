"""Coach scenarios (S1 to S4, S6) from tests/scenarios/coach_scenarios.json.

The JSON holds fixed inputs and expected numbers worked out from the written
formulas. The C# port is meant to read the same file, so the two versions
cannot drift apart. Every collection in the file is an explicit list with named
fields, so no reader depends on JSON key order.
"""
import json
from pathlib import Path

import pytest

from gos import engine

SCENARIO_FILE = Path(__file__).parent / "scenarios" / "coach_scenarios.json"
DOC = json.loads(SCENARIO_FILE.read_text(encoding="utf-8"))
TOL = DOC["tolerances"]
CONST = DOC["constants"]
SCENARIOS = DOC["scenarios"]
IDS = [s["scenario_id"] for s in SCENARIOS]
ROLES = ("O", "D", "ST")


def _coach(role, spec):
    """Build a coach the way the engine does, from named role stats (coach_name None = empty slot)."""
    if spec["coach_name"] is None:
        return None
    rec = {"coach_id": spec["coach_name"], "coach_name": spec["coach_name"], "coach_type": role}
    rec.update({row["stat"]: row["value"] for row in spec["stats"]})
    return engine.coach_from_record(rec)


def _team(s):
    inp = s["input"]
    return {"roster": {row["slot"]: row["tier"] for row in inp["roster"]},
            "coaches": {row["role"]: _coach(row["role"], row) for row in inp["coaches"]},
            "facilities": {row["facility"]: row["level"] for row in inp["facilities"]}}


def _coach_bonus(team):
    return engine.coach_win_bonus({r: c for r, c in team["coaches"].items() if c})


def _by_id(sid):
    return next(x for x in SCENARIOS if x["scenario_id"] == sid)


def _unit_expected(s):
    return {row["unit"]: row["rating"] for row in s["expected"]["unit_ratings"]}


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_schema_shape(s):
    """The structure a C# reader relies on: ids, versions, 14 slots, 3 roles, named stats in engine order."""
    assert s["schema_version"] == DOC["schema_version"]
    assert s["scenario_id"] and set(s) >= {"input", "expected"}
    assert len(set(IDS)) == len(IDS)
    inp = s["input"]
    slots = [row["slot"] for row in inp["roster"]]
    assert slots == engine.OFFENCE_SLOTS + engine.DEFENCE_SLOTS
    assert all(isinstance(row["tier"], int) for row in inp["roster"])
    assert [row["role"] for row in inp["coaches"]] == list(ROLES)
    for row in inp["coaches"]:
        if row["coach_name"] is None:
            assert row["stats"] == []
        else:
            assert [x["stat"] for x in row["stats"]] == list(engine.ROLE_STATS[row["role"]])
    assert [row["role"] for row in s["expected"]["coach_state"]] == list(ROLES)


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_unit_ratings(s):
    team, exp = _team(s), _unit_expected(s)
    assert engine.unit(team["roster"], engine.OFFENCE_SLOTS) == pytest.approx(exp["O"], abs=1e-3)
    assert engine.unit(team["roster"], engine.DEFENCE_SLOTS) == pytest.approx(exp["D"], abs=1e-3)
    assert engine.unit(team["roster"], engine.OFFENCE_SLOTS + engine.DEFENCE_SLOTS) == pytest.approx(exp["ST"], abs=1e-3)


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_coach_state(s):
    """Per role: coach name, overall rating, bonus %, XP bonus on a win. Then Team Rating."""
    team = _team(s)
    total = 0
    for row in s["expected"]["coach_state"]:
        role, coach = row["role"], team["coaches"][row["role"]]
        assert row["coach_name"] == (coach["name"] if coach else None)
        assert row["overall_rating"] == pytest.approx(coach["rating"] if coach else 0, abs=1e-4)
        assert row["bonus_pct"] == (coach["bonus"] if coach else 0)
        xp = CONST["coach_xp_base"] + CONST["coach_xp_rating_multiplier"] * coach["rating"] \
            if (coach and role in ("O", "D")) else 0.0
        assert row["win_xp_bonus"] == pytest.approx(xp, abs=1e-4)
        total += row["bonus_pct"]
    # The engine has no Team Rating. Check the file's value against the engine's bonuses.
    assert s["expected"]["team_rating"] == (CONST["team_rating_baseline"] + total) / CONST["team_rating_normalization"]


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_strength(s):
    assert engine.strength(_team(s)) == pytest.approx(s["expected"]["strength"], abs=TOL["strength"])


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_win_probability(s):
    strength = engine.strength(_team(s))
    for row in s["expected"]["win_probability"]:
        opp = row["opponent_strength"]
        assert engine.p_home(strength, opp) == pytest.approx(row["home"], abs=TOL["probability"])
        assert 1 - engine.p_home(opp, strength) == pytest.approx(row["away"], abs=TOL["probability"])


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_match_xp(s):
    team, xp = _team(s), s["expected"]["xp"]
    fm = engine.match_xp_multiplier(team["facilities"])
    cb = _coach_bonus(team)
    assert fm == pytest.approx(xp["facility_multiplier"], abs=1e-4)
    assert cb == pytest.approx(xp["coach_win_bonus"], abs=1e-4)
    base_win = engine.XP["played"] + engine.XP["win"]
    base_loss = engine.XP["played"] + engine.XP["loss"]
    assert (base_win, base_loss) == (xp["base_win"], xp["base_loss"])
    assert engine.apply_xp_bonuses(base_win, fm, cb) == pytest.approx(xp["win"], abs=TOL["xp"])
    assert engine.apply_xp_bonuses(base_loss, fm, 0.0) == pytest.approx(xp["loss"], abs=TOL["xp"])


def test_xp_order_is_facility_then_coach_without_floor():
    """S6b: the engine must not use the v0.2 spec order (coach first, floored, then facility)."""
    s = _by_id("S6b")
    team, exp = _team(s), s["expected"]
    got = engine.apply_xp_bonuses(15, engine.match_xp_multiplier(team["facilities"]), _coach_bonus(team))
    assert got == pytest.approx(exp["xp"]["win"], abs=TOL["xp"])
    assert got != pytest.approx(exp["spec_v02_order_win_xp"], abs=TOL["xp"])


def test_facilities_do_not_change_strength():
    """S6a has film room level 3, S2 has none. Same roster and coaches, same strength."""
    assert engine.strength(_team(_by_id("S6a"))) == pytest.approx(engine.strength(_team(_by_id("S2"))), abs=1e-9)


def test_isolation_scenarios_add_only_their_own_unit():
    """S4: a 30 bonus on one unit adds exactly weight x unit x 0.30 to the no-coach strength."""
    base = engine.strength(_team(_by_id("S2")))
    r = _team(_by_id("S2"))["roster"]
    units = {"O": engine.unit(r, engine.OFFENCE_SLOTS), "D": engine.unit(r, engine.DEFENCE_SLOTS),
             "ST": engine.unit(r, engine.OFFENCE_SLOTS + engine.DEFENCE_SLOTS)}
    weights = {"O": 0.45, "D": 0.40, "ST": 0.15}
    for sid, role in (("S4a", "O"), ("S4b", "D"), ("S4c", "ST")):
        gain = engine.strength(_team(_by_id(sid))) - base
        assert gain == pytest.approx(weights[role] * units[role] * 0.30, abs=1e-6)


def test_starter_scenario_matches_engine_defaults():
    """S1 must describe the same starter setup the engine uses for the player team."""
    s1 = _by_id("S1")
    cfg = engine.CONFIG
    assert {row["role"]: row["coach_name"] for row in s1["input"]["coaches"]} == cfg["player_start_coaches"]
    for slot, tier in _team(s1)["roster"].items():
        assert tier == (1 if slot == "qb" else cfg["player_start_roster_tier"])
    pool = {c["name"]: c for c in engine.coach_pool()}
    for row in s1["input"]["coaches"]:
        assert _coach(row["role"], row)["bonus"] == pool[row["coach_name"]]["bonus"]


def test_max_scenario_uses_catalog_maximums():
    """S3 names the highest-bonus coach per role. Their catalog bonuses must match the scenario stats."""
    s3 = _by_id("S3")
    pool = {c["name"]: c for c in engine.coach_pool()}
    best = {}
    for c in pool.values():
        best[c["type"]] = max(best.get(c["type"], 0), c["bonus"])
    for row in s3["input"]["coaches"]:
        assert pool[row["coach_name"]]["bonus"] == best[row["role"]]
        assert _coach(row["role"], row)["bonus"] == best[row["role"]]
