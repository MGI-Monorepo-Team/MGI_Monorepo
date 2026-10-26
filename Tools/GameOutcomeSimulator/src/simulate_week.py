"""Command-line entry point for one reproducible simulator week."""

from __future__ import annotations

import argparse
from pathlib import Path

try:  # Supports both `python -m ...` and direct script execution.
    from .config_loader import StreamingAssetsConfigLoader
    from .outcome_engine import WeeklyOutcomeSimulator, load_weekly_input, write_outcome_file
except ImportError:  # pragma: no cover - exercised by normal direct CLI usage.
    from config_loader import StreamingAssetsConfigLoader
    from outcome_engine import WeeklyOutcomeSimulator, load_weekly_input, write_outcome_file


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate one MGI game-outcome week.")
    parser.add_argument("--repo-root", required=True, type=Path, help="Path to the MGI_Monorepo root.")
    parser.add_argument("--input", required=True, type=Path, help="Weekly roster and coach snapshot JSON.")
    parser.add_argument("--output", type=Path, help="Output game.json; defaults to Unity StreamingAssets.")
    arguments = parser.parse_args()

    output = arguments.output or arguments.repo_root / "Assets/StreamingAssets/Coaches/Database/game.json"
    configuration = StreamingAssetsConfigLoader.from_repository_root(arguments.repo_root).load()
    simulator = WeeklyOutcomeSimulator(configuration)
    result = simulator.simulate_week(load_weekly_input(arguments.input))
    write_outcome_file(output, result)
    print(f"Wrote {len(result['outcomes'])} outcomes for season {result['season_id']}, week {result['week']} to {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
