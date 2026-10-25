"""Request models mirroring the Unity DTOs (PlannerDNA.Storage.Dtos, schema 1).

Field names are snake_case and identical on both sides. Unity's JsonUtility
cannot emit null for strings, so an empty string means "none" in optional ids.
Unknown keys are rejected so contract drift is caught immediately.
"""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, StringConstraints

SCHEMA_VERSION = 1

SafeId = Annotated[str, StringConstraints(pattern=r"^[A-Za-z0-9_-]{1,128}$")]
OptionalSafeId = Annotated[str, StringConstraints(pattern=r"^[A-Za-z0-9_-]{0,128}$")]
UtcTimestamp = Annotated[
    str, StringConstraints(pattern=r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,9})?(Z|[+-]\d{2}:\d{2})$")
]
CellId = Annotated[str, StringConstraints(pattern=r"^(r\d{2,}_c\d{2,})?$")]


class Strict(BaseModel):
    model_config = ConfigDict(extra="forbid")


class CellIn(Strict):
    cell_id: str = ""
    row: int = Field(ge=0)
    col: int = Field(ge=0)
    land_use: str
    floors: int = Field(ge=0)
    units_per_floor: int = Field(ge=0)
    max_floors: int = Field(ge=0)
    editable: bool
    zoning: str


class CityStateIn(Strict):
    schema_version: Literal[1]
    scenario_id: SafeId
    seed: int
    generator_version: str
    metric_version: str
    width: int = Field(gt=0, le=200)
    height: int = Field(gt=0, le=200)
    cell_metres: int = Field(gt=0)
    content_hash: str = ""
    cells: list[CellIn]


class MetricsIn(Strict):
    metric_version: str
    cost_rules_version: str
    housing_units: int
    residential_cells: int
    park_cells: int
    road_cells: int
    non_road_cells: int
    park_share_pct: float
    access_defined: bool
    access_pct: float
    accessible_housing_units: int
    accessible_residential_cells: int
    unreachable_residential_cells: int
    spent_credits: int
    cost_defined: bool
    changed_cells: int
    lost_original_housing_units: int
    net_housing_change: int


class MetricsSummaryIn(Strict):
    housing_units: int
    access_defined: bool
    access_pct: float
    park_share_pct: float
    spent_credits: int
    feasible: bool


class ConstraintIn(Strict):
    id: str
    label: str
    passed: bool
    detail: str


class FeatureVectorIn(Strict):
    feature_version: str
    delta_access_fraction: float
    delta_housing_fraction: float
    delta_park_share_fraction: float
    negative_budget_fraction: float
    negative_lost_housing_fraction: float


class ScenarioConstraintsIn(Strict):
    budget_limit: int = Field(gt=0)
    min_housing_ratio: float = Field(gt=0, le=1)
    max_floors: int = Field(gt=0)
    access_threshold_metres: int = Field(gt=0)
    units_per_floor: int = Field(gt=0)
    new_building_floors: int = Field(gt=0)


class ScenarioIn(Strict):
    schema_version: Literal[1]
    scenario_id: SafeId
    seed: int
    generator_version: str
    metric_version: str
    cost_rules_version: str
    mission_id: str
    baseline_hash: Annotated[str, StringConstraints(pattern=r"^[0-9a-f]{16}$")]
    created_utc: UtcTimestamp
    constraints: ScenarioConstraintsIn
    baseline: CityStateIn
    baseline_metrics: MetricsIn


class SessionIn(Strict):
    schema_version: Literal[1]
    session_id: SafeId
    scenario_id: SafeId
    baseline_hash: str
    started_utc: UtcTimestamp
    app_version: str


class EventIn(Strict):
    event_id: SafeId
    session_id: SafeId
    scenario_id: SafeId
    sequence: int = Field(ge=1)
    event_type: Literal["command", "undo", "redo", "checkpoint", "decision"]
    action: str
    cell_id: CellId = ""
    has_cell_change: bool
    before: CellIn | None = None
    after: CellIn | None = None
    metrics_before: MetricsSummaryIn | None = None
    metrics_after: MetricsSummaryIn | None = None
    input_source: Literal["mouse", "keyboard", "gesture_optional", "assistant_generated", "system"]
    utc_time: UtcTimestamp
    reversal_of_event_id: OptionalSafeId = ""
    original_event_id: OptionalSafeId = ""
    note: str = ""


class AlternativeIn(Strict):
    schema_version: Literal[1]
    alternative_id: SafeId
    slot: Literal["A", "B"]
    session_id: SafeId
    scenario_id: SafeId
    seed: int
    generator_version: str
    metric_version: str
    cost_rules_version: str
    baseline_hash: str
    state_hash: str
    created_utc: UtcTimestamp
    feasible: bool
    failed_constraints: list[str]
    changed_cell_ids: list[str]
    snapshot: CityStateIn
    metrics: MetricsIn
    constraints: list[ConstraintIn]
    features: FeatureVectorIn


class PreferenceIn(Strict):
    schema_version: Literal[1]
    app_version: str
    comparison_id: SafeId
    session_id: SafeId
    scenario_id: SafeId
    alternative_a_id: SafeId
    alternative_b_id: SafeId
    choice: Literal["A", "B", "tie", "skip"]
    reason: Annotated[str, StringConstraints(max_length=4000)] = ""
    mode: Literal["train", "evaluation"]
    shown_left_alternative_id: SafeId
    model_version_locked: OptionalSafeId = ""
    # Phase 4: links an evaluation-mode choice to its pre-registered blinded trial.
    evaluation_trial_id: OptionalSafeId = ""
    a_feasible: bool
    b_feasible: bool
    training_eligible: bool
    eligibility_note: str = ""
    feature_version: str
    created_utc: UtcTimestamp
    sync_status: str = "local_only"


class DeleteDataIn(Strict):
    confirm: str
    backup: bool = True


class WriteResult(BaseModel):
    status: Literal["created", "duplicate"]
    id: str
    detail: str = ""
