import pytest


@pytest.fixture
def game_row():
    """One valid game_outcomes row (Hellion at home, won 27-20)."""
    return {
        "schema_version": "0.2", "source": "simulator", "mode": "results", "seed": 42,
        "season_id": "R001-S01", "week": 1, "match_id": "R001-S01-W01-G01",
        "event_id": "R001-S01-W01-G01:hellion", "league": "MGI", "game_type": "regular",
        "team_id": "hellion", "team_name": "Hellion", "element": "Fire", "is_player_team": True,
        "opponent_id": "ghost", "home_away": "home", "result": "W",
        "points_for": 27, "points_against": 20, "margin": 7,
        "wins_to_date": 1, "losses_to_date": 0, "current_streak": 1, "standing_points_to_date": 2,
        "offence_coach": "Joe Bach", "defence_coach": "Joe Collier", "special_teams_coach": "John Whelchel",
        "coach_bonus_applied_percent": 1.25,
        "team_strength": 41.6, "opponent_strength": 58.2, "win_probability": 0.21,
    }


@pytest.fixture
def journey_row():
    """One valid player_journey row from mid-season (no season-end fields)."""
    return {
        "mode": "results", "run": 1, "season": 1, "week": 3, "season_id": "R001-S01",
        "result": "W", "xp_gained": 15.0, "total_xp": 34.0, "tier": "rookie",
        "coins_earned": 15000, "wallet": 3000, "weight_room": 1, "film_room": 1, "rehab_center": 0,
        "upgrades_bought": "film_room:1", "team_strength": 42.3,
    }


def make_batch(n_teams=4, weeks=3, seed=1):
    """A small, fully consistent batch built by hand (no engine needed).

    Round robin, home team always the lower index, winner picked by a seeded
    coin so results vary. Returns (games, seasons).
    """
    import random
    rng = random.Random(seed)
    teams = [f"t{i}" for i in range(n_teams)]
    idx, rounds = list(range(n_teams)), []
    for _ in range(n_teams - 1):
        rounds.append([(idx[i], idx[-1 - i]) for i in range(n_teams // 2)])
        idx = [idx[0], idx[-1]] + idx[1:-1]
    rec = {t: {"w": 0, "l": 0, "pf": 0, "pa": 0, "streak": 0} for t in teams}
    games = []
    for week, pairs in enumerate(rounds[:weeks], start=1):
        for g, (h, a) in enumerate(pairs, start=1):
            home_wins = rng.random() < 0.5
            win, lose = 20 + rng.randint(1, 14), 10 + rng.randint(0, 9)
            mid = f"S01-W{week:02d}-G{g:02d}"
            for side, me, opp in (("home", h, a), ("away", a, h)):
                won = (side == "home") == home_wins
                pf, pa = (win, lose) if won else (lose, win)
                r = rec[teams[me]]
                r["w" if won else "l"] += 1
                r["pf"] += pf
                r["pa"] += pa
                r["streak"] = max(r["streak"], 0) + 1 if won else min(r["streak"], 0) - 1
                games.append({
                    "schema_version": "0.2", "source": "simulator", "mode": "results", "seed": seed,
                    "season_id": "S01", "week": week, "match_id": mid, "event_id": f"{mid}:{teams[me]}",
                    "league": "MGI", "game_type": "regular", "team_id": teams[me], "team_name": teams[me].upper(),
                    "element": "Fire", "is_player_team": me == 0, "opponent_id": teams[opp], "home_away": side,
                    "result": "W" if won else "L", "points_for": pf, "points_against": pa, "margin": pf - pa,
                    "wins_to_date": r["w"], "losses_to_date": r["l"], "current_streak": r["streak"],
                    "standing_points_to_date": r["w"] * 2,
                    "offence_coach": "Joe Bach", "defence_coach": "Joe Collier", "special_teams_coach": "John Whelchel",
                    "coach_bonus_applied_percent": 1.25, "team_strength": 50.0, "opponent_strength": 50.0,
                    "win_probability": 0.6 if side == "home" else 0.4,
                })
    order = sorted(teams, key=lambda t: (-rec[t]["w"], -(rec[t]["pf"] - rec[t]["pa"]), -rec[t]["pf"], t))
    seasons = [{
        "schema_version": "0.2", "source": "simulator", "mode": "results", "season_id": "S01",
        "team_id": t, "team_name": t.upper(), "is_player_team": t == "t0",
        "games_played": rec[t]["w"] + rec[t]["l"], "wins": rec[t]["w"], "losses": rec[t]["l"],
        "standing_points": rec[t]["w"] * 2, "points_for": rec[t]["pf"], "points_against": rec[t]["pa"],
        "point_diff": rec[t]["pf"] - rec[t]["pa"], "placement": place, "team_strength": 50.0,
    } for place, t in enumerate(order, start=1)]
    return games, seasons


@pytest.fixture
def batch():
    return make_batch()


@pytest.fixture
def journey():
    """Three weeks of a player journey that only moves forward."""
    rows = []
    xp, wallet, film = 0.0, 0, 0
    for week, won in ((1, True), (2, False), (3, True)):
        gained = 15.0 if won else 7.0
        xp += gained
        coins = 15000 if won else 5000
        wallet += coins
        if wallet >= 8000 and film == 0:
            wallet, film = wallet - 8000, 1
        rows.append({
            "mode": "results", "run": 1, "season": 1, "week": week, "season_id": "R001-S01",
            "result": "W" if won else "L", "xp_gained": gained, "total_xp": xp, "tier": "rookie",
            "coins_earned": coins, "wallet": wallet, "weight_room": 0, "film_room": film, "rehab_center": 0,
            "upgrades_bought": "", "team_strength": 41.0,
        })
    return rows
