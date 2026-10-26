"""Simulation engine (schema v0.2, model v0.3).

Simulates Monster Gridiron Deep Management seasons from our game data and
returns rows in the contract format (see gos/schema.py). Validation and
export live in gos/validate.py and gos/export.py, not here.

Coaches are read live from Coaches/coaches_schema_data.json through
gos/config_loader.py. Teams, facilities and XP values below are still copied
from MGI_Monorepo main and move to the loader next.

Model v0.3 changes (Oct 26)
  - Coach strength bonus = contract v1, round(avg of 4 role stats x 5) as whole
    percentage points (CoachData.cs). Was the salary-linked coach.json bonus.
  - Coach XP rating = avg of 4 role stats (CoachesService.CalculateStaticOverallRating).
  - XP order as in ProgressionService.ApplyXpBonuses. Facility multiplier first,
    then coach bonus, each max(1, xp), no floor.
  - Facilities no longer affect match strength (Prof. Easley, Oct 22). They still
    affect XP and are still bought with coins.
  - Starter coaches and fixed starter roster from Jashwant's proposal (Oct 19).
Rehab center effects are simplified to costs only.
"""
import math
import random
from collections import Counter

from gos import SCHEMA_VERSION
from gos.config_loader import load_repository_config

MODEL_VERSION = "0.3"

# ---------------------------------------------------------------- our data
# cards_catalog.json: every team has the same pool. Per tier 1-5 there are
# RB 1, OL 3, R 2, S 2, DL 3, DB 2 (13 cards), plus 1 QB at tier 1. 66 total.
TEAMS = [("Hellion", "Fire"), ("Droid", "Radiation"), ("Elemental", "Fire"), ("Creature", "Poison"),
         ("Zombie", "Radiation"), ("Insectoid", "Acid"), ("Ogres", "Ice"), ("Ghost", "Corruption"),
         ("Alien", "Electrical"), ("SewerBeast", "Acid"), ("Vampire", "Corruption"), ("Demonoid", "Fire"),
         ("Undead", "Corruption"), ("Golem", "Electrical"), ("DarkElves", "Poison"), ("Monster", "Ice")]
# team.json roster slots
OFFENCE_SLOTS = ["qb", "rb", "ol1", "ol2", "ol3", "r1", "r2"]
DEFENCE_SLOTS = ["dl1", "dl2", "dl3", "db1", "db2", "s1", "s2"]
SLOT_POS = {"qb": "QB", "rb": "RB", "ol1": "OL", "ol2": "OL", "ol3": "OL", "r1": "R", "r2": "R",
            "dl1": "DL", "dl2": "DL", "dl3": "DL", "db1": "DB", "db2": "DB", "s1": "S", "s2": "S"}
# The 4 stats each coach role uses (CoachData.cs, CoachesService.cs)
ROLE_STATS = {
    "O": ("passing_efficiency", "rush", "red_zone_conversion", "play_variation"),
    "D": ("run_defence", "pressure_control", "coverage_discipline", "turnover"),
    "ST": ("field_goal_accuracy", "kickoff_instance", "return_speed", "return_coverage"),
}
COACH_TYPES = ("O", "D", "ST")


def coach_from_record(rec):
    """One catalog coach as the engine uses it. Python round() matches Mathf.RoundToInt (half to even)."""
    ctype = "ST" if rec["coach_type"] in ("ST", "S") else rec["coach_type"]
    rating = sum(rec[k] for k in ROLE_STATS[ctype]) / 4
    return {"id": rec["coach_id"], "name": rec["coach_name"], "type": ctype,
            "rating": rating, "bonus": round(rating * 5)}


def coach_pool():
    return [coach_from_record(r) for r in load_repository_config().coaches]


def coach_by_name(name, pool=None):
    matches = [c for c in (pool or coach_pool()) if c["name"] == name]
    if len(matches) != 1:
        raise ValueError(f"expected one coach named {name!r} in the catalog, found {len(matches)}")
    return matches[0]


# facilities_config.json
FACILITIES = {
    "weight_room":  [(12000, {"PlayerStrengthBoost": 0.04}), (30000, {"PlayerStrengthBoost": 0.08}),
                     (65000, {"PlayerStrengthBoost": 0.12}), (120000, {"PlayerStrengthBoost": 0.16}),
                     (250000, {"PlayerStrengthBoost": 0.20})],
    "film_room":    [(8000, {"PlayerIntelligenceBoost": 0.03}),
                     (18000, {"PlayerIntelligenceBoost": 0.06, "GamePlanEffectiveness": 0.02}),
                     (40000, {"PlayerIntelligenceBoost": 0.09, "GamePlanEffectiveness": 0.05}),
                     (80000, {"PlayerIntelligenceBoost": 0.12, "GamePlanEffectiveness": 0.08}),
                     (150000, {"PlayerIntelligenceBoost": 0.15, "GamePlanEffectiveness": 0.10})],
    "rehab_center": [(10000, {"PlayerHealthBoost": 0.0}), (25000, {"PlayerHealthBoost": 0.02}),
                     (50000, {"PlayerHealthBoost": 0.04}), (100000, {"PlayerHealthBoost": 0.06}),
                     (200000, {"PlayerHealthBoost": 0.08})],
}
TIER_GATES = {"rookie": 2, "pro": 3, "all_star": 4, "legend": 5}          # facilities_config.json
TIERS = [("rookie", 0), ("pro", 50), ("all_star", 100), ("legend", 150)]   # progression.json
XP = {"played": 5, "win": 10, "loss": 2, "season": {1: 50, 2: 30, 3: 15}}  # progression.json

CONFIG = {
    "weeks_per_season": 10,      # LocalSeasonBackend.total_weeks
    "points_per_win": 2,         # team.stats.points = wins * 2
    "w_offence": 0.45, "w_defence": 0.40, "w_special_teams": 0.15,
    "tier_scale": 20,            # card tier 1-5 -> rating 20-100
    "k": 0.06,                   # logistic steepness per strength point
    "home_advantage": 2.0,
    "loser_score_mean": 20.0, "loser_score_sd": 6.0,
    "margin_base": 3.0, "margin_per_strength": 0.25, "margin_sd": 8.0,
    # Facilities do not affect match strength (Prof. Easley, Oct 22)
    "facility_strength_effects": [],
    # PLACEHOLDER coin rewards (Economy + LoopLab own the real values)
    "coins_per_game": 5000, "coins_per_win": 10000,
    "coins_placement": {1: 60000, 2: 40000, 3: 20000},
    "player_team": "Hellion",
    # Starter setup from Jashwant (Oct 19), pending CCAS confirmation of the starter cards
    "player_start_roster_tier": 2,     # tier-1 QB (the only QB tier) and tier 2 everywhere else
    "player_start_coaches": {"O": "Aldo Donelli", "D": "Ed Khayat", "ST": "Aaron Kromer"},
    "ai_tiers": [1, 2, 3, 4, 5],       # AI teams draw from their full pool
}


# ---------------------------------------------------------------- model
def build_team(rng, name, element, tiers, pool):
    roster = {s: rng.choice(tiers) for s in OFFENCE_SLOTS + DEFENCE_SLOTS}
    roster["qb"] = 1  # only tier-1 QB exists per team in cards_catalog.json
    coaches = {t: rng.choice([c for c in pool if c["type"] == t]) for t in COACH_TYPES}
    return {"team_id": name.lower(), "team_name": name, "element": element, "roster": roster,
            "coaches": coaches, "facilities": {f: 0 for f in FACILITIES}}


def unit(roster, slots):
    return sum(roster[s] for s in slots) / len(slots) * CONFIG["tier_scale"]


def facility_effects(levels):
    eff = Counter()
    for f, lvl in levels.items():
        if lvl:
            for k, v in FACILITIES[f][lvl - 1][1].items():
                eff[k] += v
    return eff


def strength(team):
    c, r, co = CONFIG, team["roster"], team["coaches"]
    bonus = {t: (co.get(t) or {}).get("bonus", 0) for t in COACH_TYPES}  # empty slot = 0
    off = unit(r, OFFENCE_SLOTS) * (1 + bonus["O"] / 100)
    de = unit(r, DEFENCE_SLOTS) * (1 + bonus["D"] / 100)
    st = unit(r, OFFENCE_SLOTS + DEFENCE_SLOTS) * (1 + bonus["ST"] / 100)
    base = c["w_offence"] * off + c["w_defence"] * de + c["w_special_teams"] * st
    eff = facility_effects(team["facilities"])
    return base * (1 + sum(eff[k] for k in c["facility_strength_effects"]))


def p_home(sh, sa):
    return 1 / (1 + math.exp(-CONFIG["k"] * (sh - sa + CONFIG["home_advantage"])))


def round_robin(n, weeks):
    idx, rounds = list(range(n)), []
    for r in range(n - 1):
        rounds.append([(idx[i], idx[n - 1 - i]) if (r + i) % 2 == 0 else (idx[n - 1 - i], idx[i]) for i in range(n // 2)])
        idx = [idx[0]] + [idx[-1]] + idx[1:-1]
    return rounds[:weeks]


def tier_of(xp):
    t = "rookie"
    for name, lo in TIERS:
        if xp >= lo: t = name
    return t


def match_xp_multiplier(levels):  # FacilitiesService.CalculateMatchXpMultiplier
    eff = facility_effects(levels)
    return 1 + eff["PlayerIntelligenceBoost"] + eff["GamePlanEffectiveness"]


def coach_win_bonus(co):
    """coaches_bonus_config.json, win only. 0.03 + rating x 0.0004 per hired O and D coach,
    +0.02 synergy when both are hired. Special Teams has no XP rule."""
    hired = [t for t in ("O", "D") if co.get(t)]
    b = sum(0.03 + 0.0004 * co[t]["rating"] for t in hired)
    return b + (0.02 if len(hired) == 2 else 0.0)


def apply_xp_bonuses(base_xp, facility_multiplier, coach_bonus=0.0):
    """ProgressionService.ApplyXpBonuses. Facility first, then coach, each max(1, xp), no floor."""
    xp = base_xp if math.isclose(facility_multiplier, 1.0) else max(1.0, base_xp * facility_multiplier)
    if coach_bonus > 0:
        xp = max(1.0, xp * (1 + coach_bonus))
    return xp


def buy_upgrades(state, team):
    """Policy: buy the cheapest next level allowed by the tier gate while affordable."""
    bought = []
    while True:
        gate = TIER_GATES[tier_of(state["xp"])]
        opts = [(FACILITIES[f][lvl][0], f) for f, lvl in team["facilities"].items() if lvl < min(5, gate)]
        opts = [o for o in opts if o[0] <= state["coins"]]
        if not opts: return bought
        cost, f = min(opts)
        state["coins"] -= cost
        team["facilities"][f] += 1
        bought.append(f"{f}:{team['facilities'][f]}")


def run(runs, seasons, seed, mode="results"):
    """mode results = coins from game results. mode lump = 1,200,000 at start (today)."""
    rng = random.Random(seed)
    games, standings, journey = [], [], []
    c = CONFIG
    pool = coach_pool()
    for run_i in range(1, runs + 1):
        teams = [build_team(rng, n, e, c["ai_tiers"], pool) for n, e in TEAMS]
        me = next(t for t in teams if t["team_name"] == c["player_team"])
        me["roster"] = {s: c["player_start_roster_tier"] for s in OFFENCE_SLOTS + DEFENCE_SLOTS}
        me["roster"]["qb"] = 1
        me["coaches"] = {t: coach_by_name(n, pool) for t, n in c["player_start_coaches"].items()}
        state = {"coins": 1200000 if mode == "lump" else 0, "xp": 0.0}
        if mode == "lump": buy_upgrades(state, me)
        for s in range(1, seasons + 1):
            season_id = f"R{run_i:03d}-S{s:02d}"
            rec = {i: {"w": 0, "l": 0, "pf": 0, "pa": 0, "streak": 0} for i in range(len(teams))}
            for week, pairs in enumerate(round_robin(len(teams), c["weeks_per_season"]), 1):
                for g, (h, a) in enumerate(pairs, 1):
                    th, ta = teams[h], teams[a]
                    sh, sa = strength(th), strength(ta)
                    ph = p_home(sh, sa)
                    home_won = rng.random() < ph
                    loser = max(0, round(rng.gauss(c["loser_score_mean"], c["loser_score_sd"])))
                    margin = max(1, round(abs(rng.gauss(c["margin_base"] + c["margin_per_strength"] * abs(sh - sa), c["margin_sd"]))))
                    match_id = f"{season_id}-W{week:02d}-G{g:02d}"
                    for side, i, o, si, so, p in (("home", h, a, sh, sa, ph), ("away", a, h, sa, sh, 1 - ph)):
                        won = (side == "home") == home_won
                        pf, pa = (loser + margin, loser) if won else (loser, loser + margin)
                        r = rec[i]; r["w" if won else "l"] += 1; r["pf"] += pf; r["pa"] += pa
                        r["streak"] = (max(r["streak"], 0) + 1) if won else (min(r["streak"], 0) - 1)
                        t, op = teams[i], teams[o]
                        games.append({
                            "schema_version": SCHEMA_VERSION, "source": "simulator", "mode": mode, "seed": seed,
                            "season_id": season_id, "week": week, "match_id": match_id,
                            "event_id": f"{match_id}:{t['team_id']}", "league": "MGI", "game_type": "regular",
                            "team_id": t["team_id"], "team_name": t["team_name"], "element": t["element"],
                            "is_player_team": t is me, "opponent_id": op["team_id"], "home_away": side,
                            "result": "W" if won else "L", "points_for": pf, "points_against": pa, "margin": pf - pa,
                            "wins_to_date": r["w"], "losses_to_date": r["l"], "current_streak": r["streak"],
                            "standing_points_to_date": r["w"] * c["points_per_win"],
                            "offence_coach": t["coaches"]["O"]["name"], "defence_coach": t["coaches"]["D"]["name"],
                            "special_teams_coach": t["coaches"]["ST"]["name"],
                            "coach_bonus_applied_percent": float(sum(t["coaches"][k]["bonus"] for k in COACH_TYPES)),
                            "team_strength": round(si, 2), "opponent_strength": round(so, 2), "win_probability": round(p, 4),
                        })
                        if t is me:
                            base = XP["played"] + (XP["win"] if won else XP["loss"])
                            xp_gain = apply_xp_bonuses(base, match_xp_multiplier(me["facilities"]),
                                                       coach_win_bonus(me["coaches"]) if won else 0.0)
                            coins = c["coins_per_game"] + (c["coins_per_win"] if won else 0) if mode == "results" else 0
                            state["xp"] += xp_gain; state["coins"] += coins
                            bought = buy_upgrades(state, me)
                            journey.append({"mode": mode, "run": run_i, "season": s, "week": week, "season_id": season_id,
                                            "result": "W" if won else "L", "xp_gained": round(xp_gain, 2),
                                            "total_xp": round(state["xp"], 2), "tier": tier_of(state["xp"]),
                                            "coins_earned": coins, "wallet": state["coins"],
                                            "weight_room": me["facilities"]["weight_room"], "film_room": me["facilities"]["film_room"],
                                            "rehab_center": me["facilities"]["rehab_center"], "upgrades_bought": " ".join(bought),
                                            "team_strength": round(strength(me), 2)})
            order = sorted(rec, key=lambda i: (-rec[i]["w"], -(rec[i]["pf"] - rec[i]["pa"]), -rec[i]["pf"], teams[i]["team_id"]))
            for place, i in enumerate(order, 1):
                r, t = rec[i], teams[i]
                standings.append({"schema_version": SCHEMA_VERSION, "source": "simulator", "mode": mode, "season_id": season_id,
                                  "team_id": t["team_id"], "team_name": t["team_name"], "is_player_team": t is me,
                                  "games_played": r["w"] + r["l"], "wins": r["w"], "losses": r["l"],
                                  "standing_points": r["w"] * c["points_per_win"], "points_for": r["pf"], "points_against": r["pa"],
                                  "point_diff": r["pf"] - r["pa"], "placement": place, "team_strength": round(strength(t), 2)})
                if t is me:
                    season_base = XP["season"].get(place, 0)  # coach bonus does not apply to season XP
                    sxp = apply_xp_bonuses(season_base, match_xp_multiplier(me["facilities"])) if season_base else 0
                    scoins = c["coins_placement"].get(place, 0) if mode == "results" else 0
                    state["xp"] += sxp; state["coins"] += scoins
                    bought = buy_upgrades(state, me)
                    if journey:
                        journey[-1]["season_bonus_xp"] = round(sxp, 2); journey[-1]["season_bonus_coins"] = scoins
                        journey[-1]["placement"] = place; journey[-1]["wallet"] = state["coins"]
                        journey[-1]["total_xp"] = round(state["xp"], 2); journey[-1]["tier"] = tier_of(state["xp"])
                        journey[-1]["upgrades_bought"] = (journey[-1]["upgrades_bought"] + " " + " ".join(bought)).strip()
                        for f in FACILITIES: journey[-1][f] = me["facilities"][f]
                        journey[-1]["team_strength"] = round(strength(me), 2)
    return games, standings, journey
