"""Cross-system scenario: duplicate-card XP stays at 1.0x when Facilities and Coaches are upgraded.

Reads tests/scenarios/cross_system_xp_scenarios.json (the C# tests are meant to read the same file).
The simulator engine does not model CCAS duplicate cards, so this file checks in Python that:
  1. the base XP per rarity in the JSON matches the live CCAS config (phase2_config.json);
  2. a small reference model of the XP rules reproduces every row. For a duplicate_card source,
     Facilities returns a 1.0x multiplier and Coaches returns no bonus (FacilitiesService and
     CoachesService both special-case the prefix). For match_win the engine's own functions are used;
  3. upgrades are really active in the same scenario (the match_win control row grows) while duplicate
     XP does not move.
The real proof for the Unity code is the C# run against the same JSON.
"""
import json
from pathlib import Path

import pytest

from gos import engine
from gos.config_loader import REPO_ROOT

SCENARIO_FILE = Path(__file__).parent / "scenarios" / "cross_system_xp_scenarios.json"
DOC = json.loads(SCENARIO_FILE.read_text(encoding="utf-8"))
SCENARIOS = DOC["scenarios"]
IDS = [s["scenario_id"] for s in SCENARIOS]
TOL = DOC["constants"]["tolerance_xp"]
ROLES = ["O", "D", "ST"]
FACILITIES = ["weight_room", "film_room", "rehab_center"]
CCAS_CONFIG = REPO_ROOT / "Assets" / "StreamingAssets" / "CCAS" / "phase2_config.json"


def _coach(role, spec):
    if spec["coach_name"] is None:
        return None
    rec = {"coach_id": spec["coach_name"], "coach_name": spec["coach_name"], "coach_type": role}
    rec.update({row["stat"]: row["value"] for row in spec["stats"]})
    return engine.coach_from_record(rec)


def _setup(s):
    coaches = {row["role"]: _coach(row["role"], row) for row in s["input"]["coaches"]}
    facilities = {row["facility"]: row["level"] for row in s["input"]["facilities"]}
    return {r: c for r, c in coaches.items() if c}, facilities


def _reference_xp(source, base_xp, coaches, facilities):
    """What ProgressionService.ApplyXpBonuses does for one source. Returns (facility multiplier, coach bonus, final XP)."""
    if source.startswith("duplicate_card"):
        return 1.0, 0.0, float(base_xp)
    fm = engine.match_xp_multiplier(facilities)
    cb = engine.coach_win_bonus(coaches)
    return fm, cb, engine.apply_xp_bonuses(base_xp, fm, cb)


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_schema_shape(s):
    assert s["schema_version"] == DOC["schema_version"]
    assert len(set(IDS)) == len(IDS)
    assert {"input", "expected"} <= set(s)
    assert [r["role"] for r in s["input"]["coaches"]] == ROLES
    assert [r["facility"] for r in s["input"]["facilities"]] == FACILITIES
    sources = [r["source"] for r in s["expected"]["xp_by_source"]]
    assert sources == ["duplicate_card_common", "duplicate_card_uncommon", "duplicate_card_rare",
                       "duplicate_card_epic", "duplicate_card_legendary", "match_win"]


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_duplicate_base_xp_matches_ccas_config(s):
    cfg = json.loads(CCAS_CONFIG.read_text(encoding="utf-8"))["duplicate_xp"]
    for row in s["expected"]["xp_by_source"]:
        if row["source"].startswith("duplicate_card_"):
            rarity = row["source"].removeprefix("duplicate_card_")
            assert row["base_xp"] == cfg[f"{rarity}_duplicate_xp"]


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_rows_match_reference_model(s):
    coaches, facilities = _setup(s)
    for row in s["expected"]["xp_by_source"]:
        fm, cb, final = _reference_xp(row["source"], row["base_xp"], coaches, facilities)
        assert fm == pytest.approx(row["facility_multiplier"], abs=1e-4), row["source"]
        assert cb == pytest.approx(row["coach_bonus"], abs=1e-4), row["source"]
        assert final == pytest.approx(row["final_xp"], abs=TOL), row["source"]


@pytest.mark.parametrize("s", SCENARIOS, ids=IDS)
def test_duplicate_xp_is_exactly_the_ccas_amount(s):
    for row in s["expected"]["xp_by_source"]:
        if row["source"].startswith("duplicate_card_"):
            assert row["facility_multiplier"] == 1.0 and row["coach_bonus"] == 0.0
            assert row["final_xp"] == row["base_xp"]


def test_duplicate_xp_is_identical_in_every_scenario():
    """Baseline, facilities only, coaches only, both: the duplicate rows are the same."""
    def dup_rows(s):
        return [(r["source"], r["final_xp"]) for r in s["expected"]["xp_by_source"] if r["source"].startswith("duplicate_card_")]
    first = dup_rows(SCENARIOS[0])
    assert all(dup_rows(s) == first for s in SCENARIOS)


def test_upgrades_are_active_in_the_same_scenarios():
    """The match_win control grows with upgrades, so a 1.0x duplicate result is not just 'nothing was upgraded'."""
    win = {s["scenario_id"]: next(r for r in s["expected"]["xp_by_source"] if r["source"] == "match_win") for s in SCENARIOS}
    assert win["X1a"]["final_xp"] == win["X1a"]["base_xp"]
    for sid in ("X1b", "X1c", "X1d"):
        assert win[sid]["final_xp"] > win[sid]["base_xp"]
    assert win["X1b"]["coach_bonus"] == 0.0 and win["X1b"]["facility_multiplier"] > 1.0
    assert win["X1c"]["facility_multiplier"] == 1.0 and win["X1c"]["coach_bonus"] > 0.0
    assert win["X1d"]["facility_multiplier"] > 1.0 and win["X1d"]["coach_bonus"] > 0.0
