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

Same seed and config always give the same output.
