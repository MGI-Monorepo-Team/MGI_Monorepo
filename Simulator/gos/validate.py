from collections import Counter, defaultdict 

from gos.schema import check_row

TIERS = (("rookie", 0), ("pro", 50), ("all_star", 100), ("legend", 150))  # progression.json
MAX_FACILITY_LEVEL = 5
MAX_ROW_ERRORS = 20  


def tier_for(xp: float) -> str:
    tier = TIERS[0][0]
    for name, floor in TIERS:
        if xp >= floor:
            tier = name
    return tier


def _rows(rows, table, errors):
    """Row-level checks. Returns True when every row is valid."""
    bad = 0
    for i, row in enumerate(rows):
        for problem in check_row(row, table):
            bad += 1
            if bad <= MAX_ROW_ERRORS:
                errors.append(f"{table} row {i}: {problem}")
    if bad > MAX_ROW_ERRORS:
        errors.append(f"{table}: {bad - MAX_ROW_ERRORS} more row problems not shown")
    return bad == 0


def check_games(games, weeks_per_season=10, points_per_win=2):
    errors = []
    if not games:
        return ["game_outcomes is empty"]
    if not _rows(games, "game_outcomes", errors):
        return errors  

    # 1. league wins equal league losses
    results = Counter(g["result"] for g in games)
    if results["W"] != results["L"]:
        errors.append(f"league wins ({results['W']}) != league losses ({results['L']})")

    # 2. event_id is unique (rewards are paid once per event)
    dupes = [e for e, n in Counter(g["event_id"] for g in games).items() if n > 1]
    if dupes:
        errors.append(f"duplicate event_id: {dupes[:3]}")

    # 3. each match has exactly 2 rows that mirror each other
    by_match = defaultdict(list)
    for g in games:
        by_match[g["match_id"]].append(g)
    for mid, rows in by_match.items():
        if len(rows) != 2:
            errors.append(f"match {mid} has {len(rows)} rows, expected 2")
            continue
        a, b = rows
        if {a["result"], b["result"]} != {"W", "L"}:
            errors.append(f"match {mid} needs one W and one L")
        if {a["home_away"], b["home_away"]} != {"home", "away"}:
            errors.append(f"match {mid} needs one home and one away row")
        if (a["team_id"], a["opponent_id"]) != (b["opponent_id"], b["team_id"]):
            errors.append(f"match {mid} team and opponent ids do not swap")
        if (a["points_for"], a["points_against"]) != (b["points_against"], b["points_for"]):
            errors.append(f"match {mid} scores do not mirror")
        if a["season_id"] != b["season_id"] or a["week"] != b["week"]:
            errors.append(f"match {mid} rows disagree on season or week")
        pa, pb = a.get("win_probability"), b.get("win_probability")
        if pa is not None and pb is not None and abs(pa + pb - 1) > 0.001:
            errors.append(f"match {mid} win probabilities sum to {pa + pb:.4f}, expected 1")

    # 4. score agrees with result, margin is points_for - points_against
    for g in games:
        if (g["result"] == "W") != (g["points_for"] > g["points_against"]):
            errors.append(f"{g['event_id']}: result {g['result']} disagrees with score {g['points_for']}-{g['points_against']}")
        if g["margin"] != g["points_for"] - g["points_against"]:
            errors.append(f"{g['event_id']}: margin {g['margin']} != points_for - points_against")

    # 5. every team plays once a week, every week, and running totals add up
    by_team = defaultdict(list)
    for g in games:
        by_team[(g["season_id"], g["team_id"])].append(g)
    for (season, team), rows in by_team.items():
        rows.sort(key=lambda r: r["week"])
        weeks = [r["week"] for r in rows]
        if weeks != list(range(1, weeks_per_season + 1)):
            errors.append(f"{season} {team} played weeks {weeks}, expected 1..{weeks_per_season}")
        w = l = streak = 0
        for r in rows:
            if r["result"] == "W":
                w, streak = w + 1, max(streak, 0) + 1
            else:
                l, streak = l + 1, min(streak, 0) - 1
            if (r["wins_to_date"], r["losses_to_date"], r["current_streak"]) != (w, l, streak):
                errors.append(f"{r['event_id']}: running record should be {w}-{l} streak {streak}, "
                              f"got {r['wins_to_date']}-{r['losses_to_date']} streak {r['current_streak']}")
                break
            if r["standing_points_to_date"] != w * points_per_win:
                errors.append(f"{r['event_id']}: standing points should be {w * points_per_win}")
                break

    # 6. one player team per season, playing every week
    players = {g["season_id"]: set() for g in games}   
    for g in games:
        if g["is_player_team"]:
            players[g["season_id"]].add(g["team_id"])
    for season, ids in sorted(players.items()):
        if len(ids) != 1:
            errors.append(f"{season} has {len(ids)} player teams, expected 1")
    return errors


def check_seasons(seasons, games=(), points_per_win=2):
    errors = []
    if not seasons:
        return errors
    if not _rows(seasons, "season_summary", errors):
        return errors
    totals = defaultdict(lambda: {"w": 0, "l": 0, "pf": 0, "pa": 0})
    for g in games:
        t = totals[(g["season_id"], g["team_id"])]
        t["w" if g["result"] == "W" else "l"] += 1
        t["pf"] += g["points_for"]
        t["pa"] += g["points_against"]

    by_season = defaultdict(list)
    for s in seasons:
        by_season[s["season_id"]].append(s)
        key = f"{s['season_id']} {s['team_id']}"
        if s["games_played"] != s["wins"] + s["losses"]:
            errors.append(f"{key}: games_played != wins + losses")
        if s["standing_points"] != s["wins"] * points_per_win:
            errors.append(f"{key}: standing_points != wins x {points_per_win}")
        if s["point_diff"] != s["points_for"] - s["points_against"]:
            errors.append(f"{key}: point_diff != points_for - points_against")
        if games:
            t = totals.get((s["season_id"], s["team_id"]))
            if t is None:
                errors.append(f"{key}: no games found for this team")
            elif (s["wins"], s["losses"], s["points_for"], s["points_against"]) != (t["w"], t["l"], t["pf"], t["pa"]):
                errors.append(f"{key}: summary does not match its games")

    for season, rows in by_season.items():
        places = sorted(r["placement"] for r in rows)
        if places != list(range(1, len(rows) + 1)):
            errors.append(f"{season}: placements {places} are not 1..{len(rows)}")
            continue
        ordered = sorted(rows, key=lambda r: r["placement"])
        for hi, lo in zip(ordered, ordered[1:]):  
            if (hi["standing_points"], hi["point_diff"], hi["points_for"]) < (lo["standing_points"], lo["point_diff"], lo["points_for"]):
                errors.append(f"{season}: {hi['team_id']} placed above {lo['team_id']} with a worse record")
                break
    return errors


def check_journey(journey):
    errors = []
    if not journey:
        return errors
    if not _rows(journey, "player_journey", errors):
        return errors
    by_run = defaultdict(list)
    for r in journey:
        by_run[(r.get("mode"), r["run"])].append(r)
    for (mode, run), rows in by_run.items():
        rows.sort(key=lambda r: (r["season"], r["week"]))
        prev = None
        for r in rows:
            at = f"run {run} S{r['season']} W{r['week']}"
            for f in ("weight_room", "film_room", "rehab_center"):
                if r[f] > MAX_FACILITY_LEVEL:
                    errors.append(f"{at}: {f} level {r[f]} is above {MAX_FACILITY_LEVEL}")
            if r["tier"] != tier_for(r["total_xp"]):
                errors.append(f"{at}: tier {r['tier']} does not match {r['total_xp']} XP")
            if prev:
                if r["total_xp"] < prev["total_xp"]:
                    errors.append(f"{at}: total_xp went down")
                for f in ("weight_room", "film_room", "rehab_center"):
                    if r[f] < prev[f]:
                        errors.append(f"{at}: {f} went down")
            prev = r
    return errors


def validate_batch(games, seasons=(), journey=(), weeks_per_season=10, points_per_win=2):
    """Run every check. Returns a list of problems, empty when the batch is valid."""
    return (check_games(games, weeks_per_season, points_per_win)
            + check_seasons(seasons, games, points_per_win)
            + check_journey(journey))
