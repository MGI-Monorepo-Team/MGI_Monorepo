from gos.validate import check_games, check_journey, check_seasons, tier_for, validate_batch

W = 3  # weeks in the test batch


def test_valid_batch_passes(batch, journey):
    games, seasons = batch
    assert validate_batch(games, seasons, journey, weeks_per_season=W) == []


def test_empty_batch_fails():
    assert check_games([]) == ["game_outcomes is empty"]


def test_row_errors_stop_the_batch_checks(batch):
    games, _ = batch
    games[0]["result"] = "T"
    errors = check_games(games, W)
    assert errors == ["game_outcomes row 0: 'result' must be one of ('W', 'L'), got 'T'"]


def test_wins_must_equal_losses(batch):
    """The bug in today's SimulateWeek: teams flip coins on their own."""
    games, _ = batch
    loser = next(g for g in games if g["result"] == "L")
    loser.update(result="W", points_for=99, margin=99 - loser["points_against"])
    assert any("league wins" in e for e in check_games(games, W))


def test_duplicate_event_id(batch):
    games, _ = batch
    games[1]["event_id"] = games[0]["event_id"]
    assert any("duplicate event_id" in e for e in check_games(games, W))


def test_match_with_missing_row(batch):
    games, _ = batch
    games.pop()
    assert any("rows, expected 2" in e for e in check_games(games, W))


def test_scores_must_mirror(batch):
    games, _ = batch
    games[0]["points_against"] += 1
    games[0]["margin"] -= 1
    assert any("scores do not mirror" in e for e in check_games(games, W))


def test_result_must_agree_with_score(batch):
    games, _ = batch
    g = games[0]
    g["points_for"], g["points_against"] = g["points_against"], g["points_for"]
    g["margin"] = -g["margin"]
    assert any("disagrees with score" in e for e in check_games(games, W))


def test_win_probabilities_must_sum_to_one(batch):
    games, _ = batch
    games[0]["win_probability"] = 0.9
    assert any("win probabilities sum" in e for e in check_games(games, W))


def test_missing_week_is_caught(batch):
    games, _ = batch
    assert any("expected 1..4" in e for e in check_games(games, weeks_per_season=4))


def test_running_record_must_add_up(batch):
    games, _ = batch
    last = [g for g in games if g["team_id"] == "t1"][-1]
    last["wins_to_date"] += 1
    assert any("running record" in e for e in check_games(games, W))


def test_exactly_one_player_team(batch):
    games, _ = batch
    for g in games:
        if g["team_id"] == "t2":
            g["is_player_team"] = True
    assert any("2 player teams" in e for e in check_games(games, W))


def test_season_summary_must_match_games(batch):
    games, seasons = batch
    seasons[0]["points_for"] += 5
    seasons[0]["point_diff"] += 5
    assert any("does not match its games" in e for e in check_seasons(seasons, games))


def test_placements_must_be_unique(batch):
    games, seasons = batch
    seasons[1]["placement"] = 1
    assert any("are not 1..4" in e for e in check_seasons(seasons, games))


def test_placement_order_must_follow_record(batch):
    games, seasons = batch
    seasons[0]["placement"], seasons[-1]["placement"] = seasons[-1]["placement"], seasons[0]["placement"]
    assert any("with a worse record" in e for e in check_seasons(seasons, games))


def test_tier_thresholds_follow_progression_json():
    assert [tier_for(x) for x in (0, 49.9, 50, 100, 150, 999)] == ["rookie", "rookie", "pro", "all_star", "legend", "legend"]


def test_journey_facility_cannot_go_down(journey):
    journey[2]["film_room"] = 0
    assert any("film_room went down" in e for e in check_journey(journey))


def test_journey_tier_must_match_xp(journey):
    journey[2]["tier"] = "pro"
    assert any("does not match" in e for e in check_journey(journey))
