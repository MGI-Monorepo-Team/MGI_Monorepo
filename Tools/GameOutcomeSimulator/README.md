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
and progression configuration; it does not yet write game outcomes or alter
Unity save data.
