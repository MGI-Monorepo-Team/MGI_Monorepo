"""Read the management-game configuration used as simulator input.

The simulator deliberately reads the repository's StreamingAssets files instead
of maintaining copied values. This keeps simulation inputs aligned with Unity.
"""

from __future__ import annotations

from dataclasses import dataclass
import json
from pathlib import Path
from typing import Any


class ConfigurationLoadError(ValueError):
    """Raised when a required simulator input is missing or malformed."""


@dataclass(frozen=True)
class SimulatorConfiguration:
    """Raw, source-owned configuration consumed by the outcome simulator."""

    cards_catalog: dict[str, Any]
    teams: list[dict[str, Any]]
    coaches: list[dict[str, Any]]
    facilities: dict[str, Any]
    progression: dict[str, Any]


class StreamingAssetsConfigLoader:
    """Loads the five configuration inputs required by simulator task A1."""

    _RELATIVE_PATHS = {
        "cards_catalog": Path("CCAS/cards_catalog.json"),
        "teams": Path("Coaches/Database/team.json"),
        "coaches": Path("Coaches/Database/coach.json"),
        "facilities": Path("Facilities/facilities_config.json"),
        "progression": Path("Progression/progression.json"),
    }

    def __init__(self, streaming_assets_path: str | Path) -> None:
        self._streaming_assets_path = Path(streaming_assets_path)

    @classmethod
    def from_repository_root(cls, repository_root: str | Path) -> "StreamingAssetsConfigLoader":
        return cls(Path(repository_root) / "Assets" / "StreamingAssets")

    def load(self) -> SimulatorConfiguration:
        loaded = {
            name: self._read_json(name, relative_path)
            for name, relative_path in self._RELATIVE_PATHS.items()
        }

        self._require_object("cards_catalog", loaded["cards_catalog"])
        self._require_list("teams", loaded["teams"])
        self._require_list("coaches", loaded["coaches"])
        self._require_object("facilities", loaded["facilities"])
        self._require_object("progression", loaded["progression"])

        return SimulatorConfiguration(
            cards_catalog=loaded["cards_catalog"],
            teams=loaded["teams"],
            coaches=loaded["coaches"],
            facilities=loaded["facilities"],
            progression=loaded["progression"],
        )

    def _read_json(self, name: str, relative_path: Path) -> Any:
        path = self._streaming_assets_path / relative_path
        if not path.is_file():
            raise ConfigurationLoadError(f"Missing {name} configuration: {path}")

        try:
            with path.open(encoding="utf-8") as source:
                return json.load(source)
        except json.JSONDecodeError as error:
            raise ConfigurationLoadError(f"Invalid JSON in {name} configuration: {path}") from error

    @staticmethod
    def _require_object(name: str, value: Any) -> None:
        if not isinstance(value, dict):
            raise ConfigurationLoadError(f"Expected {name} configuration to be a JSON object")

    @staticmethod
    def _require_list(name: str, value: Any) -> None:
        if not isinstance(value, list):
            raise ConfigurationLoadError(f"Expected {name} configuration to be a JSON array")
