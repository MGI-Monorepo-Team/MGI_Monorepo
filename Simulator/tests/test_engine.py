"""End to end: the real engine's output must pass every contract check."""
import pytest

from gos import engine
from gos.cli import main
from gos.validate import validate_batch

WEEKS = engine.CONFIG["weeks_per_season"]


@pytest.fixture(scope="module")
def results_run():
    return engine.run(runs=3, seasons=4, seed=7, mode="results")


def test_engine_output_passes_validation(results_run):
    games, seasons, journey = results_run
    assert validate_batch(games, seasons, journey, weeks_per_season=WEEKS) == []


def test_lump_mode_passes_validation():
    games, seasons, journey = engine.run(runs=2, seasons=3, seed=7, mode="lump")
    assert validate_batch(games, seasons, journey, weeks_per_season=WEEKS) == []


def test_row_counts(results_run):
    games, seasons, journey = results_run
    teams = len(engine.TEAMS)
    assert len(games) == 3 * 4 * teams * WEEKS      # runs x seasons x teams x weeks
    assert len(seasons) == 3 * 4 * teams
    assert len(journey) == 3 * 4 * WEEKS


def test_same_seed_gives_identical_output():
    assert engine.run(1, 2, seed=11) == engine.run(1, 2, seed=11)


def test_different_seed_gives_different_output():
    assert engine.run(1, 2, seed=11)[0] != engine.run(1, 2, seed=12)[0]


def test_lump_mode_pays_no_result_coins():
    _, _, journey = engine.run(1, 2, seed=3, mode="lump")
    assert all(r["coins_earned"] == 0 for r in journey)


def test_lump_sum_maxes_every_facility_by_season_two():
    """The pacing problem from the spec: 1,200,000 coins buys everything early."""
    _, _, journey = engine.run(5, 2, seed=3, mode="lump", model="0.3")
    end_s2 = [r for r in journey if r["season"] == 2 and r["week"] == WEEKS]
    assert all(r["weight_room"] + r["film_room"] + r["rehab_center"] == 15 for r in end_s2)


def _team(tier=3, coaches=None):
    return {"roster": {s: tier for s in engine.OFFENCE_SLOTS + engine.DEFENCE_SLOTS},
            "coaches": coaches or {}, "facilities": {f: 0 for f in engine.FACILITIES}}


def test_facilities_do_not_change_match_strength():
    """Prof. Easley, Oct 22: leave facilities out of match strength for now."""
    team = _team()
    before = engine.strength(team)
    team["facilities"] = {f: 5 for f in engine.FACILITIES}
    assert engine.strength(team) == pytest.approx(before)


def test_coach_bonus_follows_contract_v1():
    """round(avg of the 4 role stats x 5), as in CoachData.cs."""
    rec = {"coach_id": "c1", "coach_name": "Test", "coach_type": "O",
           "passing_efficiency": 4, "rush": 6, "red_zone_conversion": 5, "play_variation": 5}
    coach = engine.coach_from_record(rec)
    assert (coach["rating"], coach["bonus"]) == (5.0, 25)   # Jashwant's write-up example


def test_coach_catalog_bonus_range():
    pool = engine.coach_pool()
    assert len(pool) == 499
    for ctype, hi in (("O", 30), ("D", 30), ("ST", 26)):
        bonuses = [c["bonus"] for c in pool if c["type"] == ctype]
        assert (min(bonuses), max(bonuses)) == (5, hi)


def test_empty_coach_slot_counts_as_zero():
    assert engine.strength(_team(coaches={})) == pytest.approx(engine.strength(_team()))


def test_coach_bonus_scales_its_unit():
    o30 = {"name": "x", "type": "O", "rating": 6.0, "bonus": 30}
    base = engine.unit(_team()["roster"], engine.OFFENCE_SLOTS)
    gained = engine.strength(_team(coaches={"O": o30})) - engine.strength(_team())
    assert gained == pytest.approx(engine.CONFIG["w_offence"] * base * 0.30)


def test_xp_order_facility_first_then_coach_no_floor():
    """ProgressionService.ApplyXpBonuses: facility multiplier, then coach bonus, max(1, xp), no floor."""
    assert engine.apply_xp_bonuses(15, 1.0) == 15
    assert engine.apply_xp_bonuses(15, 1.2) == pytest.approx(18.0)
    assert engine.apply_xp_bonuses(15, 1.2, 0.0808) == pytest.approx(15 * 1.2 * 1.0808)
    assert engine.apply_xp_bonuses(0.5, 1.1) == 1.0          # minimum of 1


def test_coach_xp_bonus_rules():
    o = {"type": "O", "rating": 1.0}
    d = {"type": "D", "rating": 1.0}
    assert engine.coach_win_bonus({}) == 0
    assert engine.coach_win_bonus({"O": o}) == pytest.approx(0.0304)
    assert engine.coach_win_bonus({"O": o, "D": d}) == pytest.approx(0.0808)   # with O+D synergy


def test_player_starts_with_the_agreed_setup():
    games, _, _ = engine.run(1, 1, seed=1)
    row = next(g for g in games if g["is_player_team"])
    assert (row["offence_coach"], row["defence_coach"], row["special_teams_coach"]) == (
        "Aldo Donelli", "Ed Khayat", "Aaron Kromer")
    assert row["coach_bonus_applied_percent"] == 15.0


def test_legacy_coach_bonus_is_salary_linked():
    """v0.2 model: coach.json bonus = salary / 4, 31 coaches."""
    pool = engine.legacy_coach_pool()
    assert len(pool) == 31
    assert {c["type"] for c in pool} == set(engine.COACH_TYPES)
    for rec in engine.json.loads(engine.LEGACY_COACHES_PATH.read_text()):
        assert rec["bonus"] == pytest.approx(rec["salary"] / 4)


def test_v02_model_passes_validation_with_legacy_starters():
    games, seasons, journey = engine.run(runs=2, seasons=2, seed=7, model="0.2")
    assert validate_batch(games, seasons, journey, weeks_per_season=WEEKS) == []
    row = next(g for g in games if g["is_player_team"])
    assert (row["offence_coach"], row["defence_coach"], row["special_teams_coach"]) == (
        "Joe Bach", "Joe Collier", "John Whelchel")
    assert row["coach_bonus_applied_percent"] == 1.25   # 0.5 + 0.5 + 0.25, as in the v0.2 fixtures


def test_default_model_is_v04():
    assert engine.run(1, 1, seed=5) == engine.run(1, 1, seed=5, model="0.4")


def test_unknown_model_is_rejected():
    with pytest.raises(ValueError, match="unknown model"):
        engine.run(1, 1, seed=5, model="0.1")


def test_cli_writes_files(tmp_path, capsys):
    assert main(["--runs", "1", "--seasons", "1", "--out", str(tmp_path)]) == 0
    assert (tmp_path / "game_outcomes.csv").exists()
    assert "OK:" in capsys.readouterr().out


# ---------------------------------------------------------------- PR #94 XP rules
def test_xp_amounts_come_from_progression_json():
    assert (engine.XP["played"], engine.XP["win"], engine.XP["loss"], engine.XP["training"]) == (5, 10, 2, 2)
    assert engine.XP["season"] == {1: 50, 2: 30, 3: 15}


def test_training_xp_uses_the_weight_room():
    # level 2: strength 0.08 + conditioning 0.04 + fatigue resistance 0.03 (facilities_config.json)
    assert engine.training_xp_multiplier({"weight_room": 2, "film_room": 5, "rehab_center": 0}) == pytest.approx(1.15)


def test_tiers_and_facilities_come_from_config():
    assert engine.TIERS == (("rookie", 0), ("pro", 50), ("all_star", 100), ("legend", 150))
    assert engine.TIER_GATES == {"rookie": 2, "pro": 3, "all_star": 4, "legend": 5}
    assert [cost for cost, _ in engine.FACILITIES["weight_room"]] == [12000, 30000, 65000, 120000, 250000]


def test_tied_teams_share_the_better_placement():
    """SeasonStandings.PlacementOf: 1, 2, 2, 4."""
    rec = {0: {"w": 8}, 1: {"w": 6}, 2: {"w": 6}, 3: {"w": 2}}
    assert [engine.shared_placement(rec, i) for i in rec] == [1, 2, 2, 4]


# ---------------------------------------------------------------- v0.4 team growth
def test_hire_cost_matches_coaches_service():
    assert engine.hire_cost({"salary": 1}) == 19231
    assert engine.hire_cost({"salary": 15}) == 288462


def test_starter_collection_gives_the_v03_starter_roster():
    owned = engine.starter_collection("Hellion", 2)
    roster = engine.lineup(owned)
    assert sum(owned.values()) == 14
    assert roster["qb"] == 1 and all(roster[s] == 2 for s in roster if s != "qb")


def test_lineup_takes_the_best_owned_card_per_position():
    owned = engine.starter_collection("Hellion", 2)
    better = next(u for u, c in engine.CARDS.items() if c["position5"] == "OL" and c["cardTier"] == 5)
    owned[better] += 1
    roster = engine.lineup(owned)
    assert sorted(roster[s] for s in ("ol1", "ol2", "ol3")) == [2, 2, 5]


def test_v04_passes_validation_and_the_player_team_grows():
    games, seasons, journey = engine.run(runs=2, seasons=5, seed=7, model="0.4")
    assert validate_batch(games, seasons, journey, weeks_per_season=WEEKS) == []
    first = next(r["team_strength"] for r in journey if r["run"] == 1)
    last = [r["team_strength"] for r in journey if r["run"] == 1][-1]
    assert last > first
    assert any("pack:" in r["upgrades_bought"] for r in journey)


def test_csv_total_xp_never_rounds_up_into_the_next_tier():
    assert engine.floor_xp(149.996) == 149.99
    assert engine.floor_xp(0.29) == 0.29 and engine.floor_xp(1.15) == 1.15


def test_v04_still_buys_facilities():
    _, _, journey = engine.run(runs=3, seasons=5, seed=7, model="0.4")
    end = [r for r in journey if r["season"] == 5 and r["week"] == WEEKS]
    assert all(r["weight_room"] + r["film_room"] + r["rehab_center"] > 0 for r in end)


def test_replacing_a_hired_coach_refunds_half_its_cost():
    assert engine.fire_refund({"paid": 19231}) == 9616   # Mathf.RoundToInt(9615.5), round half to even
    assert engine.fire_refund({"name": "starter"}) == 0  # starters were never paid
    assert engine.tier_of(149.996) == "all_star"


def test_a_duplicate_card_fills_only_one_slot():
    owned = engine.Counter({next(u for u, c in engine.CARDS.items() if c["position5"] == "OL" and c["cardTier"] == 5): 3})
    roster = engine.lineup(owned)
    assert sorted(roster[s] for s in ("ol1", "ol2", "ol3")) == [0, 0, 5]
