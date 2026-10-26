# MGI Game Outcome Simulator

This standalone tool reads the same JSON configuration files used by Unity.
It intentionally lives outside `Assets/`, so Unity does not import Python files
or simulator-only test fixtures.

## Run the loader checks

From the repository root:

```bash
python3 -m unittest discover -s Tools/GameOutcomeSimulator/tests -v
```

The simulator is read-only at this stage. It loads card, team, coach, facility,
and progression configuration; it does not alter Unity save data.

## Generate one week

Provide a weekly snapshot with the roster card IDs and active coach IDs for
every team. The input is intentionally weekly: later management decisions can
change the next week's snapshot instead of locking an entire season in advance.

```bash
python3 Tools/GameOutcomeSimulator/src/simulate_week.py \
  --repo-root . \
  --input Tools/GameOutcomeSimulator/examples/week_input.example.json
```

The default output is Unity's `Assets/StreamingAssets/Coaches/Database/game.json`.
It includes the seed, model version, a configuration hash, the strength inputs,
and a complete set of results. Facilities are recorded as excluded and do not
affect the outcome model.

`game.json` contains game outcomes only. Economy reward values remain owned by
the Economy contract rather than being invented by the simulator.
