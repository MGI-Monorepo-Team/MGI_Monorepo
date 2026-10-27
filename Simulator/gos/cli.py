
import argparse
import json
import sys
from pathlib import Path

from gos import SCHEMA_VERSION, engine
from gos.export import ValidationError, export_batch


def main(argv=None):
    ap = argparse.ArgumentParser(prog="gos", description="Game Outcome Simulator")
    ap.add_argument("--runs", type=int, default=1, help="number of simulated players")
    ap.add_argument("--seasons", type=int, default=15, help="seasons per player")
    ap.add_argument("--seed", type=int, default=42, help="same seed gives the same output")
    ap.add_argument("--mode", choices=["results", "lump"], default="results",
                    help="results = coins from game results, lump = 1,200,000 at start")
    ap.add_argument("--model", choices=engine.MODELS, default=engine.MODEL_VERSION,
                    help="0.3 = current coach model, 0.2 = legacy coach.json bonus for comparison")
    ap.add_argument("--out", default="out", help="output folder")
    ap.add_argument("--no-json", action="store_true", help="skip the JSON file (it gets large for big runs)")
    args = ap.parse_args(argv)

    games, seasons, journey = engine.run(args.runs, args.seasons, args.seed, args.mode, args.model)
    try:
        files = export_batch(args.out, games, seasons, journey,
                             weeks_per_season=engine.CONFIG["weeks_per_season"],
                             points_per_win=engine.CONFIG["points_per_win"],
                             write_json=not args.no_json)
    except ValidationError as err:
        print(f"Validation failed, nothing written ({len(err.errors)} problems):", file=sys.stderr)
        for e in err.errors[:20]:
            print(f"  - {e}", file=sys.stderr)
        return 1
    # Run settings for the report, kept out of the CSVs so the v0.2 contract is unchanged
    info = {"schema_version": SCHEMA_VERSION, "model_version": args.model, "seed": args.seed,
            "mode": args.mode, "runs": args.runs, "seasons": args.seasons}
    info_path = Path(args.out) / "run_info.json"
    info_path.write_text(json.dumps(info, indent=2) + "\n")
    files.append(info_path)
    print(f"OK:{len(games)} game rows, {len(seasons)} season rows, {len(journey)} journey rows")
    for f in files:
        print(f"  wrote {f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
