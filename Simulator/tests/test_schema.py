from gos.schema import check_row, columns


def test_column_counts_match_the_spec():
    assert len(columns("game_outcomes")) == 31
    assert len(columns("season_summary")) == 16
    assert len(columns("player_journey")) == 19


def test_valid_row_passes(game_row):
    assert check_row(game_row, "game_outcomes") == []


def test_missing_required_field(game_row):
    del game_row["result"]
    assert check_row(game_row, "game_outcomes") == ["'result' is missing"]


def test_wrong_type_is_caught(game_row):
    game_row["week"] = "1"           # string from a bad CSV read
    game_row["is_player_team"] = 1   # int is not a bool
    errors = check_row(game_row, "game_outcomes")
    assert any("'week' should be int" in e for e in errors)
    assert any("'is_player_team' should be bool" in e for e in errors)


def test_bool_is_not_accepted_as_a_number(game_row):
    game_row["points_for"] = True
    assert any("'points_for' should be int" in e for e in check_row(game_row, "game_outcomes"))


def test_enum_values_are_enforced(game_row):
    game_row["result"] = "T"         # no ties in v0.2
    assert check_row(game_row, "game_outcomes") == ["'result' must be one of ('W', 'L'), got 'T'"]


def test_minimums_are_enforced(game_row):
    game_row["points_against"] = -3
    assert check_row(game_row, "game_outcomes") == ["'points_against' must be >= 0, got -3"]


def test_live_rows_may_skip_simulator_only_fields(game_row):
    game_row["source"] = "live"
    for f in ("mode", "seed", "team_strength", "opponent_strength", "win_probability"):
        game_row[f] = None
    assert check_row(game_row, "game_outcomes") == []


def test_simulator_rows_must_have_simulator_fields(game_row):
    game_row["win_probability"] = None
    assert check_row(game_row, "game_outcomes") == ["'win_probability' is missing"]


def test_unknown_field_is_flagged(game_row):
    game_row["weather"] = "rain"
    assert check_row(game_row, "game_outcomes") == ["unknown field 'weather'"]


def test_wrong_schema_version_is_flagged(game_row):
    game_row["schema_version"] = "0.1"
    assert check_row(game_row, "game_outcomes") == ["schema_version '0.1' does not match '0.2'"]


def test_journey_season_end_fields_are_optional(journey_row):
    assert check_row(journey_row, "player_journey") == []
    journey_row.update(season_bonus_xp=50.0, season_bonus_coins=60000, placement=1)
    assert check_row(journey_row, "player_journey") == []
