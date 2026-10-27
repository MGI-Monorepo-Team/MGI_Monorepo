# MGI Game Outcome Simulator (Kanboard #2300)

Standalone Python tool that simulates Deep Management seasons and outputs game
results in the agreed contract (schema v0.2). It reads the same StreamingAssets
JSON as Unity and lives outside `Assets/`, so Unity does not import it.

| Module | What it does | Owner |
| --- | --- | --- |
| `gos/schema.py` | The data contract, 3 tables | Daniel |
| `gos/validate.py` | Batch checks every output must pass | Daniel |
| `gos/export.py` | CSV for LoopLab, JSON for DM systems | Daniel |
| `gos/config_loader.py` | Reads StreamingAssets inputs | Adnan (A1), merged |
| `gos/engine.py` | Strength, win chance, seasons, player journey | shared |
| `gos/cli.py`, `gos/report.py` | Runner and visual test report | Daniel |

## Run

From this folder:

```bash
pip install pytest matplotlib
python3 -m pytest -v
python3 -m gos --runs 100 --seasons 15 --no-json --out out_results
python3 -m gos --runs 100 --seasons 15 --mode lump --no-json --out out_lump
python3 -m gos.report out_results --compare out_lump
```

To see what the coach bonus fix changed, run the old v0.2 coach model next to v0.3
(`--model 0.2` uses the legacy `coach.json` salary-linked bonus, everything else is v0.3):

```bash
python3 -m gos --runs 100 --seasons 15 --no-json --out out_v03
python3 -m gos --runs 100 --seasons 15 --no-json --model 0.2 --out out_v02
python3 -m gos.report out_v03 --compare out_v02
```

Same seed and config always give the same output. Each output folder gets a
`run_info.json` with the seed, model version and schema version, and the report
prints them at the top.

## How it connects to the game

The simulator is plain Python. It reads the game's data files but does not run
or call any Unity code.

| System | What the simulator uses | Status |
| --- | --- | --- |
| Coaches | `Coaches/coaches_schema_data.json` (499 coaches) for coach ratings and strength bonus, `CoachData.cs` and `CoachesService.cs` rules | Read live through `gos/config_loader.py` |
| CCAS | `CCAS/cards_catalog.json` card tiers 1 to 5 for team rosters | Loader reads it, engine still uses copied values |
| Progression | `Progression/progression.json` XP rewards and tiers, XP order from `ProgressionService.ApplyXpBonuses` | Loader reads it, engine still uses copied values |
| Facilities | `Facilities/facilities_config.json` costs, XP effects and tier gates | Loader reads it, engine still uses copied values |

All files are under `Assets/StreamingAssets/`. "Copied values" are constants at
the top of `gos/engine.py` that match `main` today. Moving them onto the loader
is the next step, so a change in the game's JSON shows up in the simulator
automatically.

**Back into Unity (not connected yet).** Progression's `LocalSeasonBackend` can
already read simulator results week by week through `GameOutcomeReader`
(`StreamingAssets/Coaches/Database/game.json`) and pays coin rewards once per
`event_id`. If the file is empty it simulates the week itself, which is what
happens today. Two things are still open:

- The simulator does not write that file. Unity expects `{"schema_version": "1.0", "outcomes": [...]}`
  with one record per match (home, away, winner, scores, coin reward). The simulator
  exports schema v0.2, with two mirrored rows per match plus season and journey tables.
- Team IDs must match Unity's. Unity uses the UUIDs from `Coaches/Database/team.json`,
  while the simulator uses lowercase team names such as `hellion`.

Adnan owns the Unity side of this (per-week integration).
