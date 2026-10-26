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
    _, _, journey = engine.run(5, 2, seed=3, mode="lump")
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


def test_cli_writes_files(tmp_path, capsys):
    assert main(["--runs", "1", "--seasons", "1", "--out", str(tmp_path)]) == 0
    assert (tmp_path / "game_outcomes.csv").exists()
    assert "OK:" in capsys.readouterr().out
