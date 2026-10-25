"""Request models for the blinded evaluation endpoints (mirrors PlannerDNA.Storage.EvaluationDtos)."""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import Field, StringConstraints

from app.schemas.records import OptionalSafeId, SafeId, Strict, UtcTimestamp

MAX_EVALUATION_SCENARIOS = 20
MAX_PAIRS_PER_SCENARIO = 10
MAX_REPEATS = 20


class EvaluationScenarioIn(Strict):
    scenario_id: SafeId
    seed: int


class EvaluationPlanIn(Strict):
    schema_version: Literal[1]
    plan_id: SafeId
    # The model the client expects to freeze; it must be the backend's active model.
    model_version: SafeId
    evaluation_scenarios: list[EvaluationScenarioIn] = Field(min_length=2, max_length=MAX_EVALUATION_SCENARIOS)
    pairs_per_scenario: int = Field(ge=1, le=MAX_PAIRS_PER_SCENARIO)
    repeat_count: int = Field(ge=0, le=MAX_REPEATS)
    pair_rule: Annotated[str, StringConstraints(min_length=1, max_length=64)]
    note: Annotated[str, StringConstraints(max_length=1000)] = ""
    app_version: str
    created_utc: UtcTimestamp


class EvaluationTrialIn(Strict):
    schema_version: Literal[1]
    trial_id: SafeId
    plan_id: SafeId
    trial_index: int = Field(ge=0)
    scenario_id: SafeId
    alternative_a_id: SafeId
    alternative_b_id: SafeId
    repeat_of_trial_id: OptionalSafeId = ""
    candidate_seed: int
    pair_rule: Annotated[str, StringConstraints(min_length=1, max_length=64)]


class ClosePlanIn(Strict):
    reason: Annotated[str, StringConstraints(max_length=500)] = ""
