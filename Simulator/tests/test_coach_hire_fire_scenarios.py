"""Scenario 5: coach hire/fire from tests/scenarios/coach_hire_fire_scenarios.json.

The JSON gives a starting wallet and coaches, then a list of steps. After every step it states the
expected result, wallet, the three coach slots, Team Rating and the events published. The C# test
for CoachesService is meant to read the same file.

The simulator engine has no hire/fire logic, so this file checks two things in Python:
  1. the catalog facts in the JSON (salary, contract length, bonus, hire cost) match the real catalog
     and the engine's bonus formula;
  2. a small reference model, ported from CoachesService.TryHireCoach / FireCoach / GetTeamRating,
     reproduces every expected step in the file.
The real proof for the Unity code is the C# run against the same JSON.
"""
import json
from pathlib import Path

import pytest

from gos import engine
from gos.config_loader import load_repository_config

SCENARIO_FILE = Path(__file__).parent / "scenarios" / "coach_hire_fire_scenarios.json"
DOC = json.loads(SCENARIO_FILE.read_text(encoding="utf-8"))
CONST = DOC["constants"]
SCENARIOS = DOC["scenarios"]
IDS = [s["scenario_id"] for s in SCENARIOS]
ROLES = ["O", "D", "ST"]
EVENT_TYPE = {"O": "O", "D": "D", "ST": "S"}
COACHES = {c["coach_name"]: c for c in DOC["coaches"]}
BY_ID = {c["coach_id"]: c for c in DOC["coaches"]}


def _catalog():
    return {r["coach_id"]: r for r in load_repository_config().coaches}


# ---- reference model: a port of CoachesService, written from the C# source ----

class Team:
    def __init__(self, coins, rows):
        self.coins = coins
        self.slot = {r["role"]: ({"coach": BY_ID[r["coach_id"]], "games": r["games_served"]}
                                 if r["coach_id"] else None) for r in rows}

    def rating(self):
        return CONST["team_rating_baseline"] + sum(
            s["coach"]["bonus_pct"] for s in self.slot.values() if s) / CONST["team_rating_normalization"]

    def rows(self):
        return [{"role": r, "coach_id": self.slot[r]["coach"]["coach_id"] if self.slot[r] else None,
                 "coach_name": self.slot[r]["coach"]["coach_name"] if self.slot[r] else None,
                 "games_served": self.slot[r]["games"] if self.slot[r] else None} for r in ROLES]

    def result(self, ok, wallet=None, events=None):
        return {"success": ok, "coins": self.coins, "wallet_change": wallet,
                "coaches": self.rows(), "team_rating": self.rating(), "events": events or []}

    def hire(self, coach_id, team_id):
        coach = BY_ID.get(coach_id)
        if coach is None:                                    # not in catalog
            return self.result(False)
        current = self.slot[coach["role"]]
        if current and current["coach"]["coach_id"] == coach_id:   # idempotent re-hire
            return self.result(True)
        cost = round(coach["salary"] * 1_000_000 / 52)       # Mathf.RoundToInt: half to even
        if self.coins < cost:
            return self.result(False)
        self.coins -= cost
        self.slot[coach["role"]] = {"coach": coach, "games": 0}   # replaces the slot, no refund
        return self.result(True, {"source": CONST["hire_source"], "coins_delta": -cost}, [
            {"event_type": "hire_coach", "payload": {
                "team_id": team_id, "coach_id": coach_id, "coach_type": EVENT_TYPE[coach["role"]],
                "cost_paid_coins": cost}}])

    def fire(self, role, team_id):
        current = self.slot[role]
        if not current:
            return self.result(False)
        coach = current["coach"]
        length = max(coach["contract_length"], 1)
        games = min(max(current["games"], 0), length)
        hire_cost = round(coach["salary"] * 1_000_000 / 52)
        amount = round(hire_cost * CONST["refund_rate"] * (1 - games / length))
        self.slot[role] = None
        wallet = None
        if amount > 0:
            self.coins += amount
            wallet = {"source": CONST["fire_refund_source"], "coins_delta": amount}
        return self.result(True, wallet, [
            {"event_type": "fire_coach", "payload": {
                "team_id": team_id, "coach_id": coach["coach_id"], "coach_type": EVENT_TYPE[role],
                "refund_amount_coins": amount}}])

    def set_games(self, role, games):
        self.slot[role]["games"] = games
        return self.result(True)


def _replay(s):
    inp = s["input"]
    team = Team(inp["coins_start"], inp["coaches_start"])
    for step in s["steps"]:
        if step["action"] == "hire":
            yield step, team.hire(step["coach_id"], inp["team_id"])
        elif step["action"] == "fire":
            yield step, team.fire(step["role"], inp["team_id"])
        else:
            yield step, team.set_games(step["role"], step["games_served"])


# ---- tests ----

@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_schema_shape(s):
    """Structure a C# reader relies on: ids, versions, three role rows everywhere, known actions."""
    assert s["schema_version"] == DOC["schema_version"]
    assert len(set(IDS)) == len(IDS)
    assert {"input", "steps"} <= set(s)
    assert [r["role"] for r in s["input"]["coaches_start"]] == ROLES
    assert [st["step"] for st in s["steps"]] == list(range(1, len(s["steps"]) + 1))
    for step in s["steps"]:
        assert step["action"] in ("hire", "fire", "set_games_served")
        exp = step["expected"]
        assert [r["role"] for r in exp["coaches"]] == ROLES
        assert set(exp) == {"success", "coins", "wallet_change", "coaches", "team_rating", "events"}
        if not exp["success"]:
            assert exp["events"] == [] and exp["wallet_change"] is None


def test_catalog_facts_match_the_real_catalog():
    """Every coach in the file: salary, contract length, bonus and hire cost match the catalog and the engine."""
    catalog = _catalog()
    for c in DOC["coaches"]:
        rec = catalog[c["coach_id"]]
        assert rec["coach_name"] == c["coach_name"]
        assert (rec["salary"], rec["contract_length"]) == (c["salary"], c["contract_length"])
        engine_coach = engine.coach_from_record(rec)
        assert engine_coach["type"] == c["role"]
        assert engine_coach["bonus"] == c["bonus_pct"]
        assert engine_coach["rating"] == pytest.approx(c["overall_rating"], abs=1e-4)
        assert round(rec["salary"] * 1_000_000 / 52) == c["hire_cost"]


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_start_state(s):
    team = Team(s["input"]["coins_start"], s["input"]["coaches_start"])
    assert team.rating() == s["input"]["team_rating_start"]


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_steps_match_reference_model(s):
    for step, got in _replay(s):
        assert got == step["expected"], f"{s['scenario_id']} step {step['step']}: {step.get('comment', '')}"


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_wallet_changes_add_up(s):
    """Coins after each step = coins before + wallet_change; failed or no-cost steps leave coins alone."""
    coins = s["input"]["coins_start"]
    for step in s["steps"]:
        exp = step["expected"]
        coins += exp["wallet_change"]["coins_delta"] if exp["wallet_change"] else 0
        assert exp["coins"] == coins


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_team_rating_uses_engine_bonuses(s):
    """Team Rating after every step = 12 + the engine's bonus for each coach in the three slots."""
    catalog = _catalog()
    for step in s["steps"]:
        total = sum(engine.coach_from_record(catalog[r["coach_id"]])["bonus"]
                    for r in step["expected"]["coaches"] if r["coach_id"])
        assert step["expected"]["team_rating"] == CONST["team_rating_baseline"] + total


def test_half_even_rounding_case_is_in_the_file():
    """S5e: Khayat refund is exactly 9,615.5 and must come out as 9,616, not 9,615 or 9,616 by accident of float."""
    khayat = COACHES["Ed Khayat"]
    assert khayat["hire_cost"] * CONST["refund_rate"] == 9615.5
    s = next(x for x in SCENARIOS if x["scenario_id"] == "S5e")
    fire = next(st for st in s["steps"] if st["action"] == "fire" and st["role"] == "D")
    assert fire["expected"]["wallet_change"]["coins_delta"] == 9616


def test_full_contract_refunds_nothing_and_still_publishes_the_event():
    s = next(x for x in SCENARIOS if x["scenario_id"] == "S5b")
    last = s["steps"][-1]["expected"]
    assert last["wallet_change"] is None
    assert last["events"][0]["payload"]["refund_amount_coins"] == 0


def test_replacing_a_coach_gives_no_refund_and_no_fire_event():
    """S5d documents current behavior (needs owner confirmation): replace charges full cost, no refund."""
    s = next(x for x in SCENARIOS if x["scenario_id"] == "S5d")
    replace = s["steps"][1]["expected"]
    assert replace["wallet_change"]["coins_delta"] == -COACHES["Don Shula"]["hire_cost"]
    assert [e["event_type"] for e in replace["events"]] == ["hire_coach"]
    assert s.get("note")
