"""Visual test report for a simulator output folder.

  python3 -m gos.report out                         one run folder
  python3 -m gos.report out_results --compare out_lump   overlay two folders
  python3 -m gos.report out --show                   also open the window

Reads the CSVs that `python3 -m gos` writes, re-runs every validation check,
and saves report.png into the folder. Needs matplotlib (pip install matplotlib).

Panels
  1. Validation        every contract check, pass or fail, plus batch size
  2. Calibration       predicted win chance vs actual win rate (tests the match model)
  3. Strength vs wins  each team-season, player team highlighted
  4. Facilities        levels owned by the player over time (pacing)
  5. XP and tiers      player XP over time against the tier thresholds
  6. Coins             player wallet and coins earned over time
  7. Margins           winning margin distribution
  8. Placement         where the player team finishes
"""
import argparse
import statistics as st
import sys
from collections import Counter, defaultdict
from pathlib import Path

from gos.export import read_csv
from gos.validate import TIERS, validate_batch

BLUE, GREY, ORANGE, INK = "#2F6FD6", "#9A9A9A", "#E07B39", "#222222"


def load(folder):
    folder = Path(folder)
    files = {t: folder / f"{t}.csv" for t in ("game_outcomes", "season_summary", "player_journey")}
    if not files["game_outcomes"].exists():
        sys.exit(f"{folder}: no game_outcomes.csv, run `python3 -m gos --out {folder}` first")
    data = {t: (read_csv(p, t) if p.exists() else []) for t, p in files.items()}
    games = data["game_outcomes"]
    data["weeks"] = max(g["week"] for g in games)
    data["label"] = games[0].get("mode") or folder.name
    data["errors"] = validate_batch(games, data["season_summary"], data["player_journey"], weeks_per_season=data["weeks"])
    return data


def summary_lines(d):
    games, seasons, journey = d["game_outcomes"], d["season_summary"], d["player_journey"]
    runs = len({r["run"] for r in journey}) or "?"
    n_seasons = len({g["season_id"] for g in games})
    lines = [
        f"mode: {d['label']}",
        f"{len(games):,} game rows, {len(seasons):,} season rows, {len(journey):,} journey rows",
        f"{runs} player runs, {n_seasons} seasons, {d['weeks']} weeks per season",
        "",
    ]
    if d["errors"]:
        lines.append(f"FAIL: {len(d['errors'])} problem(s)")
        lines += [f"  - {e[:70]}" for e in d["errors"][:6]]
    else:
        lines.append("PASS: all contract checks")
        lines += ["  rows match schema v0.2", "  wins = losses, 2 mirrored rows per match",
                  "  unique event IDs, scores agree with results", "  running records and standings add up",
                  "  journey only moves forward, tiers match XP"]
    return lines


def journey_series(journey, key):
    """Mean and 10th to 90th percentile of a journey field at each week step, across runs."""
    by_run = defaultdict(list)
    for r in sorted(journey, key=lambda r: (r["run"], r["season"], r["week"])):
        by_run[r["run"]].append(r[key])
    steps = min(len(v) for v in by_run.values())
    mean, lo, hi = [], [], []
    for i in range(steps):
        vals = sorted(v[i] for v in by_run.values())
        mean.append(st.mean(vals))
        lo.append(vals[int(0.1 * (len(vals) - 1))])
        hi.append(vals[int(0.9 * (len(vals) - 1))])
    return list(range(1, steps + 1)), mean, lo, hi


def build(datasets, out_path, show=False):
    import matplotlib
    if not show:
        matplotlib.use("Agg")
    import matplotlib.pyplot as plt

    colors = [BLUE, ORANGE]
    fig, axes = plt.subplots(4, 2, figsize=(15, 19))
    fig.suptitle("Game Outcome Simulator test report", fontsize=17, fontweight="bold", x=0.02, ha="left", y=0.995)
    ax = axes.flat

    # 1 validation
    a = ax[0]
    a.axis("off")
    y = 1.0
    for d, c in zip(datasets, colors):
        for i, line in enumerate(summary_lines(d)):
            weight = "bold" if i == 0 or line.startswith(("PASS", "FAIL")) else "normal"
            col = ("#2E8B57" if line.startswith("PASS") else "#C0392B") if line.startswith(("PASS", "FAIL")) else (c if i == 0 else INK)
            a.text(0, y, line, fontsize=10, fontweight=weight, color=col, va="top", family="monospace")
            y -= 0.075
        y -= 0.05
    a.set_title("1. Validation", loc="left", fontweight="bold")

    # 2 calibration (first dataset)
    a = ax[1]
    for d, c in zip(datasets, colors):
        buckets = defaultdict(lambda: [0, 0])
        for g in d["game_outcomes"]:
            p = g.get("win_probability")
            if p is None:
                continue
            b = min(int(p * 10), 9)
            buckets[b][0] += g["result"] == "W"
            buckets[b][1] += 1
        xs = [(b + 0.5) / 10 for b in sorted(buckets) if buckets[b][1] >= 30]
        ys = [buckets[b][0] / buckets[b][1] for b in sorted(buckets) if buckets[b][1] >= 30]
        a.plot(xs, ys, "o-", color=c, label=f"{d['label']} actual")
    a.plot([0, 1], [0, 1], "--", color=GREY, label="perfect calibration")
    a.set(xlim=(0, 1), ylim=(0, 1), xlabel="Predicted win chance", ylabel="Actual win rate")
    a.set_title("2. Win chance calibration (dots should sit on the line)", loc="left", fontweight="bold")
    a.legend(frameon=False, fontsize=9)

    # 3 strength vs win rate
    a = ax[2]
    d = datasets[0]
    s = d["season_summary"]
    if s and s[0].get("team_strength") is not None:
        others = [r for r in s if not r["is_player_team"]]
        mine = [r for r in s if r["is_player_team"]]
        a.scatter([r["team_strength"] for r in others], [r["wins"] / r["games_played"] for r in others], s=10, alpha=0.25, color=GREY, label="AI teams")
        a.scatter([r["team_strength"] for r in mine], [r["wins"] / r["games_played"] for r in mine], s=18, alpha=0.6, color=BLUE, label="player team")
        a.legend(frameon=False, fontsize=9)
    a.set(xlabel="Team strength", ylabel="Season win rate", ylim=(-0.02, 1.02))
    a.set_title(f"3. Strength vs wins ({d['label']}, each dot a team-season)", loc="left", fontweight="bold")

    # 4 to 6 journey panels
    def journey_panel(a, key_fn, title, ylabel, hlines=()):
        for d, c in zip(datasets, colors):
            j = d["player_journey"]
            if not j:
                continue
            for r in j:
                r["_v"] = key_fn(r)
            xs, mean, lo, hi = journey_series(j, "_v")
            a.plot(xs, mean, color=c, lw=2, label=f"{d['label']} mean")
            a.fill_between(xs, lo, hi, color=c, alpha=0.15, label=f"{d['label']} 10th to 90th pct")
            weeks = d["weeks"]
            for sx in range(weeks, len(xs) + 1, weeks):
                a.axvline(sx + 0.5, color="#EEEEEE", lw=0.8, zorder=0)
        for yv, lab in hlines:
            a.axhline(yv, ls="--", color=GREY, lw=1)
            a.text(1, yv, f" {lab}", color=GREY, fontsize=8, va="bottom")
        a.set(xlabel="Week (season boundaries shaded)", ylabel=ylabel)
        a.set_title(title, loc="left", fontweight="bold")
        a.legend(frameon=False, fontsize=8)

    journey_panel(ax[3], lambda r: r["weight_room"] + r["film_room"] + r["rehab_center"],
                  "4. Facility levels owned by the player (of 15)", "Levels", [(15, "all maxed")])
    journey_panel(ax[4], lambda r: r["total_xp"], "5. Player XP vs tier thresholds", "Total XP",
                  [(floor, name) for name, floor in TIERS if floor > 0])
    journey_panel(ax[5], lambda r: r["wallet"], "6. Player wallet after upgrades", "Coins")

    # 7 margins
    a = ax[6]
    for d, c in zip(datasets, colors):
        m = [g["margin"] for g in d["game_outcomes"] if g["result"] == "W"]
        a.hist(m, bins=range(1, max(m) + 2), color=c, alpha=0.5, label=f"{d['label']} (median {st.median(m):.0f})")
    a.set(xlabel="Winning margin (points)", ylabel="Games")
    a.set_title("7. Winning margins", loc="left", fontweight="bold")
    a.legend(frameon=False, fontsize=9)

    # 8 placement
    a = ax[7]
    width = 0.8 / len(datasets)
    for i, (d, c) in enumerate(zip(datasets, colors)):
        places = Counter(r["placement"] for r in d["season_summary"] if r["is_player_team"])
        total = sum(places.values()) or 1
        n = max((r["placement"] for r in d["season_summary"]), default=1)
        xs = list(range(1, n + 1))
        a.bar([x + (i - (len(datasets) - 1) / 2) * width for x in xs], [places[x] / total for x in xs], width=width, color=c, label=d["label"])
    a.set(xlabel="Final placement", ylabel="Share of seasons")
    a.set_title("8. Where the player team finishes", loc="left", fontweight="bold")
    a.legend(frameon=False, fontsize=9)

    for a in axes.flat[1:]:
        a.spines[["top", "right"]].set_visible(False)
    fig.tight_layout(rect=(0, 0, 1, 0.985))
    fig.savefig(out_path, dpi=110)
    if show:
        plt.show()
    plt.close(fig)
    return out_path


def main(argv=None):
    ap = argparse.ArgumentParser(prog="gos.report", description="Visual test report for simulator output")
    ap.add_argument("folder", help="output folder from `python3 -m gos --out FOLDER`")
    ap.add_argument("--compare", help="second output folder to overlay, e.g. a lump-mode run")
    ap.add_argument("--show", action="store_true", help="open the report window as well as saving it")
    args = ap.parse_args(argv)

    datasets = [load(args.folder)] + ([load(args.compare)] if args.compare else [])
    for d in datasets:
        print("\n".join(summary_lines(d)), end="\n\n")
    path = build(datasets, Path(args.folder) / "report.png", show=args.show)
    print(f"wrote {path}")
    return 1 if any(d["errors"] for d in datasets) else 0


if __name__ == "__main__":
    sys.exit(main())
