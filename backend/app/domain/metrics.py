"""Server-side recomputation of metrics-1.0.0, cost-rules-1.0.0 and features-v1.

A line-by-line port of PlannerDNA.Metrics (MetricEngine, AccessibilityCalculator,
CostRules, ConstraintEvaluator, FeatureExtractor). The backend uses it to verify
every stored snapshot instead of trusting client-reported numbers. Cross-language
agreement is pinned by tests/fixtures/unity_metrics_fixtures.json, which is
exported from Unity.
"""

from __future__ import annotations

from collections import deque
from dataclasses import dataclass, field
from typing import Any

from app.domain.city import City, Cell

METRIC_VERSION = "metrics-1.0.0"
COST_RULES_VERSION = "cost-rules-1.0.0"
FEATURE_VERSION = "features-v1"
UNREACHABLE = -1


@dataclass(frozen=True)
class ScenarioRules:
    budget_limit: int
    min_housing_ratio: float
    max_floors: int
    access_threshold_metres: int
    units_per_floor: int
    new_building_floors: int

    def minimum_housing_units(self, baseline_units: int) -> int:
        ratio_per_mille = round(self.min_housing_ratio * 1000.0)
        return (ratio_per_mille * baseline_units + 999) // 1000


@dataclass(frozen=True)
class CostRules:
    vacant_to_park: int = 20
    new_building_base: int = 25
    per_new_floor: int = 5
    built_to_park: int = 35
    demolition: int = 15
    park_clearance: int = 10
    use_conversion: int = 20
    per_added_floor: int = 5
    per_removed_floor: int = 3

    def evaluate(self, b: Cell, c: Cell) -> tuple[bool, int, str]:
        """(defined, credits, rule) for one parcel, baseline -> current."""
        editable = {"residential", "commercial", "park", "vacant"}
        built = {"residential", "commercial"}
        if b.use == c.use and b.floors == c.floors:
            return True, 0, "unchanged"
        if b.use not in editable or c.use not in editable:
            return False, 0, f"{b.use} -> {c.use} is not an editable transition"
        if b.use == c.use:
            if b.use not in built:
                return False, 0, f"floor change on {b.use}"
            delta = c.floors - b.floors
            return (True, delta * self.per_added_floor, "floors added") if delta > 0 else (
                True, -delta * self.per_removed_floor, "floors removed")
        if b.use == "vacant" and c.use == "park":
            return True, self.vacant_to_park, "vacant -> park"
        if b.use in ("vacant", "park") and c.use in built:
            return True, self.new_building_base + self.per_new_floor * c.floors, "new building"
        if b.use in built and c.use == "park":
            return True, self.built_to_park, "building -> park"
        if b.use in built and c.use == "vacant":
            return True, self.demolition, "demolish"
        if b.use == "park" and c.use == "vacant":
            return True, self.park_clearance, "park -> vacant"
        if b.use in built and c.use in built:
            delta = c.floors - b.floors
            floor_part = delta * self.per_added_floor if delta > 0 else -delta * self.per_removed_floor
            return True, self.use_conversion + floor_part, "use conversion"
        return False, 0, f"{b.use} -> {c.use} has no cost rule"


DEFAULT_COST_RULES = CostRules()


@dataclass
class Metrics:
    housing_units: int = 0
    residential_cells: int = 0
    park_cells: int = 0
    road_cells: int = 0
    non_road_cells: int = 0
    park_share_pct: float = 0.0
    access_defined: bool = False
    access_pct: float = 0.0
    accessible_housing_units: int = 0
    accessible_residential_cells: int = 0
    unreachable_residential_cells: int = 0
    spent_credits: int = 0
    cost_defined: bool = True
    changed_cells: int = 0
    lost_original_housing_units: int = 0
    net_housing_change: int = 0
    undefined_transitions: list[str] = field(default_factory=list)

    COMPARED_FIELDS = (
        "housing_units", "residential_cells", "park_cells", "road_cells", "non_road_cells", "park_share_pct",
        "access_defined", "access_pct", "accessible_housing_units", "accessible_residential_cells",
        "unreachable_residential_cells", "spent_credits", "cost_defined", "changed_cells",
        "lost_original_housing_units", "net_housing_change",
    )

    def to_dict(self) -> dict[str, Any]:
        d = {name: getattr(self, name) for name in self.COMPARED_FIELDS}
        d["metric_version"] = METRIC_VERSION
        d["cost_rules_version"] = COST_RULES_VERSION
        return d


def park_distances_metres(city: City) -> list[int]:
    n = city.cell_count
    road_steps = [UNREACHABLE] * n
    queue: deque[int] = deque()
    for i in range(n):
        if city.cells[i].use != "road":
            continue
        if any(city.cells[nb].use == "park" for nb in city.neighbours(i)):
            road_steps[i] = 0
            queue.append(i)
    while queue:
        current = queue.popleft()
        for nb in city.neighbours(current):
            if road_steps[nb] != UNREACHABLE or city.cells[nb].use != "road":
                continue
            road_steps[nb] = road_steps[current] + 1
            queue.append(nb)

    cell = city.cell_metres
    result = [UNREACHABLE] * n
    for i in range(n):
        if city.cells[i].use == "road":
            continue
        best = None
        for nb in city.neighbours(i):
            if city.cells[nb].use != "road" or road_steps[nb] == UNREACHABLE:
                continue
            metres = cell + road_steps[nb] * cell + cell
            if best is None or metres < best:
                best = metres
        if best is not None:
            result[i] = best
    return result


def evaluate(current: City, baseline: City, rules: ScenarioRules, cost_rules: CostRules = DEFAULT_COST_RULES) -> Metrics:
    if not current.same_scenario_as(baseline):
        raise ValueError("Current state and baseline belong to different scenarios or versions.")
    m = Metrics()
    distances = park_distances_metres(current)
    for i, c in enumerate(current.cells):
        if c.use == "road":
            m.road_cells += 1
        elif c.use == "park":
            m.park_cells += 1
        elif c.use == "residential":
            m.residential_cells += 1
            units = c.housing_units
            m.housing_units += units
            d = distances[i]
            if d != UNREACHABLE and d <= rules.access_threshold_metres:
                m.accessible_housing_units += units
                m.accessible_residential_cells += 1
            elif d == UNREACHABLE:
                m.unreachable_residential_cells += 1

    m.non_road_cells = current.cell_count - m.road_cells
    m.park_share_pct = 100.0 * m.park_cells / m.non_road_cells if m.non_road_cells > 0 else 0.0
    m.access_defined = m.housing_units > 0
    m.access_pct = 100.0 * m.accessible_housing_units / m.housing_units if m.access_defined else 0.0

    baseline_housing = 0
    for b, c in zip(baseline.cells, current.cells):
        baseline_housing += b.housing_units
        if b == c:
            continue
        m.changed_cells += 1
        defined, credits, rule = cost_rules.evaluate(b, c)
        if defined:
            m.spent_credits += credits
        else:
            m.cost_defined = False
            m.undefined_transitions.append(f"{c.id}: {rule}")
        if b.use == "residential":
            m.lost_original_housing_units += max(0, b.housing_units - c.housing_units)
    m.net_housing_change = m.housing_units - baseline_housing
    return m


def evaluate_constraints(current: City, metrics: Metrics, baseline_metrics: Metrics, rules: ScenarioRules) -> dict[str, bool]:
    """Constraint id -> passed, matching ConstraintEvaluator (labels/details are display text, not compared)."""
    min_units = rules.minimum_housing_units(baseline_metrics.housing_units)
    floors_ok = all(
        c.floors <= min(c.max_floors, rules.max_floors) for c in current.cells if c.is_building
    )
    return {
        "budget": metrics.spent_credits <= rules.budget_limit,
        "housing_floor": metrics.housing_units >= min_units,
        "max_floors": floors_ok,
        "cost_rules_defined": metrics.cost_defined,
    }


def extract_features(current: Metrics, baseline: Metrics, rules: ScenarioRules) -> dict[str, Any]:
    baseline_access = baseline.access_pct if baseline.access_defined else 0.0
    current_access = current.access_pct if current.access_defined else 0.0
    housing_den = max(1, baseline.housing_units)
    return {
        "feature_version": FEATURE_VERSION,
        "delta_access_fraction": (current_access - baseline_access) / 100.0,
        "delta_housing_fraction": (current.housing_units - baseline.housing_units) / housing_den,
        "delta_park_share_fraction": (current.park_share_pct - baseline.park_share_pct) / 100.0,
        "negative_budget_fraction": -(current.spent_credits / max(1, rules.budget_limit)),
        "negative_lost_housing_fraction": -(current.lost_original_housing_units / housing_den),
    }


FLOAT_TOLERANCE = 1e-9


def diff_values(expected: dict[str, Any], claimed: dict[str, Any], names: tuple[str, ...] | list[str]) -> list[str]:
    """Human-readable mismatches between recomputed and client-reported values."""
    problems = []
    for name in names:
        if name not in claimed:
            problems.append(f"{name}: missing (server computed {expected[name]!r})")
            continue
        e, c = expected[name], claimed[name]
        if isinstance(e, bool) or isinstance(c, bool):
            ok = bool(e) == bool(c) and isinstance(c, bool)
        elif isinstance(e, float) or isinstance(c, float):
            ok = abs(float(e) - float(c)) <= FLOAT_TOLERANCE
        else:
            ok = e == c
        if not ok:
            problems.append(f"{name}: client sent {c!r}, server computed {e!r}")
    return problems
