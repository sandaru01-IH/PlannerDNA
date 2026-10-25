"""City state as the Unity client defines it, plus the identical content hash.

This mirrors PlannerDNA.Core.CityState / CellData / StableHash (schema 1). The
enum orders below are the C# enum values used inside the hash and must never
be reordered.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any

LAND_USES = ("road", "residential", "commercial", "park", "vacant", "civic")
ZONINGS = ("mixed", "residential_only", "protected")
BUILDING_USES = frozenset({"residential", "commercial", "civic"})

CELL_ID_PATTERN = re.compile(r"^r(\d{2,})_c(\d{2,})$")

_FNV_OFFSET = 14695981039346656037
_FNV_PRIME = 1099511628211
_MASK64 = (1 << 64) - 1


class CityFormatError(ValueError):
    """The submitted city JSON is structurally invalid."""


class StableHash:
    """FNV-1a 64-bit, byte-for-byte identical to PlannerDNA.Core.StableHash."""

    def __init__(self) -> None:
        self.value = _FNV_OFFSET

    def add_int(self, value: int) -> "StableHash":
        v = value & 0xFFFFFFFF  # C# casts the int to uint, then hashes 4 little-endian bytes
        for i in range(4):
            self.value ^= (v >> (8 * i)) & 0xFF
            self.value = (self.value * _FNV_PRIME) & _MASK64
        return self

    def add_str(self, value: str | None) -> "StableHash":
        data = (value or "").encode("utf-8")
        self.add_int(len(data))
        for b in data:
            self.value ^= b
            self.value = (self.value * _FNV_PRIME) & _MASK64
        return self

    def hex(self) -> str:
        return f"{self.value:016x}"


def format_cell_id(row: int, col: int) -> str:
    return f"r{row:02d}_c{col:02d}"


@dataclass(frozen=True)
class Cell:
    row: int
    col: int
    use: str
    floors: int
    units_per_floor: int
    max_floors: int
    editable: bool
    zoning: str

    @property
    def id(self) -> str:
        return format_cell_id(self.row, self.col)

    @property
    def housing_units(self) -> int:
        return self.floors * self.units_per_floor if self.use == "residential" else 0

    @property
    def is_building(self) -> bool:
        return self.use in BUILDING_USES

    def fixed_properties(self) -> tuple[Any, ...]:
        """Properties no edit may change; edits set use, floors and units_per_floor (CellData.With)."""
        return (self.row, self.col, self.max_floors, self.editable, self.zoning)


@dataclass(frozen=True)
class City:
    scenario_id: str
    seed: int
    generator_version: str
    metric_version: str
    schema_version: int
    width: int
    height: int
    cell_metres: int
    cells: tuple[Cell, ...]

    @property
    def cell_count(self) -> int:
        return len(self.cells)

    def neighbours(self, index: int) -> list[int]:
        """N, E, S, W order, matching CityState.GetNeighbours."""
        row, col = divmod(index, self.width)
        result = []
        if row > 0:
            result.append(index - self.width)
        if col < self.width - 1:
            result.append(index + 1)
        if row < self.height - 1:
            result.append(index + self.width)
        if col > 0:
            result.append(index - 1)
        return result

    def same_scenario_as(self, other: "City") -> bool:
        return (
            self.scenario_id == other.scenario_id
            and self.seed == other.seed
            and self.generator_version == other.generator_version
            and self.metric_version == other.metric_version
            and self.schema_version == other.schema_version
            and self.width == other.width
            and self.height == other.height
            and self.cell_metres == other.cell_metres
        )

    def content_hash(self) -> str:
        h = StableHash()
        h.add_str(self.scenario_id).add_int(self.seed).add_str(self.generator_version).add_str(self.metric_version)
        h.add_int(self.schema_version).add_int(self.width).add_int(self.height).add_int(self.cell_metres)
        for c in self.cells:
            h.add_int(LAND_USES.index(c.use)).add_int(c.floors).add_int(c.units_per_floor).add_int(c.max_floors)
            h.add_int(1 if c.editable else 0).add_int(ZONINGS.index(c.zoning))
        return h.hex()

    def changed_cell_ids(self, baseline: "City") -> list[str]:
        return [c.id for c, b in zip(self.cells, baseline.cells) if c != b]


def city_from_dict(data: dict[str, Any]) -> City:
    """Parses a CityStateDto dict and checks its structure (not its design rules)."""
    try:
        width, height = int(data["width"]), int(data["height"])
        raw_cells = data["cells"]
    except (KeyError, TypeError, ValueError) as exc:
        raise CityFormatError(f"City is missing required fields: {exc}") from exc
    if width <= 0 or height <= 0:
        raise CityFormatError("City width and height must be positive.")
    if len(raw_cells) != width * height:
        raise CityFormatError(f"City has {len(raw_cells)} cells; expected {width * height} ({width}x{height}).")

    cells = []
    for index, rc in enumerate(raw_cells):
        expected_row, expected_col = divmod(index, width)
        row, col = int(rc["row"]), int(rc["col"])
        if (row, col) != (expected_row, expected_col):
            raise CityFormatError(f"Cell {index} is at r{row}/c{col}; cells must be in row-major order.")
        if rc.get("cell_id") not in (None, "", format_cell_id(row, col)):
            raise CityFormatError(f"Cell id {rc.get('cell_id')!r} does not match its position {format_cell_id(row, col)}.")
        use, zoning = rc.get("land_use"), rc.get("zoning")
        if use not in LAND_USES:
            raise CityFormatError(f"Unknown land_use {use!r} at {format_cell_id(row, col)}.")
        if zoning not in ZONINGS:
            raise CityFormatError(f"Unknown zoning {zoning!r} at {format_cell_id(row, col)}.")
        floors = int(rc["floors"])
        if use in BUILDING_USES and floors < 1:
            raise CityFormatError(f"Building at {format_cell_id(row, col)} has {floors} floors (minimum 1).")
        if use not in BUILDING_USES and floors != 0:
            raise CityFormatError(f"Non-building at {format_cell_id(row, col)} has {floors} floors (must be 0).")
        cells.append(
            Cell(
                row=row,
                col=col,
                use=use,
                floors=floors,
                units_per_floor=int(rc["units_per_floor"]),
                max_floors=int(rc["max_floors"]),
                editable=bool(rc["editable"]),
                zoning=zoning,
            )
        )

    city = City(
        scenario_id=str(data["scenario_id"]),
        seed=int(data["seed"]),
        generator_version=str(data["generator_version"]),
        metric_version=str(data["metric_version"]),
        schema_version=int(data["schema_version"]),
        width=width,
        height=height,
        cell_metres=int(data["cell_metres"]),
        cells=tuple(cells),
    )
    claimed = data.get("content_hash") or ""
    if claimed and claimed != city.content_hash():
        raise CityFormatError("City content_hash does not match its cells; the snapshot is corrupted or was edited.")
    return city
