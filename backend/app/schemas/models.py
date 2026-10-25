"""Request models for the preference-model endpoints (mirrors PlannerDNA.Storage.LearningDtos)."""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import Field, StringConstraints

from app.schemas.records import CityStateIn, FeatureVectorIn, OptionalSafeId, SafeId, Strict

MAX_CANDIDATES = 50


class PredictPairIn(Strict):
    scenario_id: SafeId
    alternative_a_id: SafeId
    alternative_b_id: SafeId
    comparison_id: OptionalSafeId = ""
    model_version: OptionalSafeId = ""


class CandidateIn(Strict):
    candidate_id: SafeId
    description: Annotated[str, StringConstraints(max_length=500)] = ""
    state_hash: str
    feasible: bool
    changed_cell_ids: list[str]
    snapshot: CityStateIn
    features: FeatureVectorIn


class RankCandidatesIn(Strict):
    schema_version: Literal[1]
    scenario_id: SafeId
    generator_version: Annotated[str, StringConstraints(min_length=1, max_length=64)]
    generator_seed: int
    top_k: int = Field(default=5, ge=1, le=20)
    model_version: OptionalSafeId = ""
    candidates: list[CandidateIn] = Field(min_length=2, max_length=MAX_CANDIDATES)
