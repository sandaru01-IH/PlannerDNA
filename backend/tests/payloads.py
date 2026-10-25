"""Builders for valid request payloads, shaped exactly like the Unity DTO JSON.

Metrics here come from the server's own port; cross-language agreement with
Unity is covered separately by test_unity_fixtures.py.
"""

from __future__ import annotations

import copy
import uuid
from typing import Any

from app.domain.city import city_from_dict, format_cell_id
from app.domain.metrics import (
    COST_RULES_VERSION,
    FEATURE_VERSION,
    METRIC_VERSION,
    ScenarioRules,
    evaluate,
    evaluate_constraints,
    extract_features,
)
from app.services.records import CONSTRAINT_ORDER

GENERATOR_VERSION = "citygen-test"
NOW = "2026-10-25T10:00:00.0000000Z"

RULES = {
    "budget_limit": 100,
    "min_housing_ratio": 0.9,
    "max_floors": 6,
    "access_threshold_metres": 150,
    "units_per_floor": 4,
    "new_building_floors": 3,
}

# R road, h residential, c commercial, p park, v vacant, C civic (fixed)
GRID = [
    "RRRRRR",
    "RhhvhR",
    "RhpchR",
    "RRRRRR",
    "RhhvCR",
    "RRRRRR",
]

_USES = {"R": "road", "h": "residential", "c": "commercial", "p": "park", "v": "vacant", "C": "civic"}
_FLOORS = {"h": 3, "c": 2, "C": 2}


def new_id(prefix: str) -> str:
    return f"{prefix}-{uuid.uuid4().hex}"


def city(scenario_id: str = "test-scenario", seed: int = 1, grid: list[str] | None = None) -> dict[str, Any]:
    grid = grid or GRID
    cells = []
    for r, line in enumerate(grid):
        for c, ch in enumerate(line):
            use = _USES[ch]
            cells.append({
                "cell_id": format_cell_id(r, c),
                "row": r,
                "col": c,
                "land_use": use,
                "floors": _FLOORS.get(ch, 0),
                "units_per_floor": 4 if use == "residential" else 0,
                "max_floors": 0 if use in ("road", "park") else 6,
                "editable": use not in ("road", "civic"),
                "zoning": "protected" if use == "park" else "mixed",
            })
    data = {
        "schema_version": 1,
        "scenario_id": scenario_id,
        "seed": seed,
        "generator_version": GENERATOR_VERSION,
        "metric_version": METRIC_VERSION,
        "width": len(grid[0]),
        "height": len(grid),
        "cell_metres": 50,
        "content_hash": "",
        "cells": cells,
    }
    return rehash(data)


def rehash(data: dict[str, Any]) -> dict[str, Any]:
    data = copy.deepcopy(data)
    data["content_hash"] = ""
    data["content_hash"] = city_from_dict(data).content_hash()
    return data


def edit(data: dict[str, Any], cell_id: str, land_use: str, floors: int | None = None) -> dict[str, Any]:
    data = copy.deepcopy(data)
    for cell in data["cells"]:
        if cell["cell_id"] == cell_id:
            cell["land_use"] = land_use
            cell["floors"] = floors if floors is not None else (3 if land_use in ("residential", "commercial", "civic") else 0)
            break
    else:
        raise KeyError(cell_id)
    return rehash(data)


def _metrics_dict(state: dict[str, Any], baseline: dict[str, Any]):
    rules = ScenarioRules(**RULES)
    b, s = city_from_dict(baseline), city_from_dict(state)
    bm, sm = evaluate(b, b, rules), evaluate(s, b, rules)
    return s, b, rules, bm, sm


def scenario(baseline: dict[str, Any], created_utc: str = NOW) -> dict[str, Any]:
    _, b, _, bm, _ = _metrics_dict(baseline, baseline)
    return {
        "schema_version": 1,
        "scenario_id": baseline["scenario_id"],
        "seed": baseline["seed"],
        "generator_version": baseline["generator_version"],
        "metric_version": METRIC_VERSION,
        "cost_rules_version": COST_RULES_VERSION,
        "mission_id": "green_space_dilemma",
        "baseline_hash": b.content_hash(),
        "created_utc": created_utc,
        "constraints": dict(RULES),
        "baseline": copy.deepcopy(baseline),
        "baseline_metrics": bm.to_dict(),
    }


def session(baseline: dict[str, Any], session_id: str | None = None) -> dict[str, Any]:
    return {
        "schema_version": 1,
        "session_id": session_id or new_id("session"),
        "scenario_id": baseline["scenario_id"],
        "baseline_hash": baseline["content_hash"],
        "started_utc": NOW,
        "app_version": "0.2.0-test",
    }


def alternative(state: dict[str, Any], baseline: dict[str, Any], session_id: str, slot: str = "A") -> dict[str, Any]:
    s, b, rules, bm, sm = _metrics_dict(state, baseline)
    constraints = evaluate_constraints(s, sm, bm, rules)
    feats = extract_features(sm, bm, rules)
    return {
        "schema_version": 1,
        "alternative_id": new_id("alt"),
        "slot": slot,
        "session_id": session_id,
        "scenario_id": state["scenario_id"],
        "seed": state["seed"],
        "generator_version": state["generator_version"],
        "metric_version": METRIC_VERSION,
        "cost_rules_version": COST_RULES_VERSION,
        "baseline_hash": b.content_hash(),
        "state_hash": s.content_hash(),
        "created_utc": NOW,
        "feasible": all(constraints.values()),
        "failed_constraints": [cid for cid in CONSTRAINT_ORDER if not constraints[cid]],
        "changed_cell_ids": s.changed_cell_ids(b),
        "snapshot": copy.deepcopy(state),
        "metrics": sm.to_dict(),
        "constraints": [{"id": cid, "label": cid, "passed": constraints[cid], "detail": ""} for cid in CONSTRAINT_ORDER],
        "features": feats,
    }


def preference(alt_a: dict[str, Any], alt_b: dict[str, Any], choice: str = "A", mode: str = "train") -> dict[str, Any]:
    eligible = mode == "train" and choice in ("A", "B") and alt_a["feasible"] and alt_b["feasible"]
    return {
        "schema_version": 1,
        "app_version": "0.2.0-test",
        "comparison_id": new_id("cmp"),
        "session_id": alt_a["session_id"],
        "scenario_id": alt_a["scenario_id"],
        "alternative_a_id": alt_a["alternative_id"],
        "alternative_b_id": alt_b["alternative_id"],
        "choice": choice,
        "reason": "test",
        "mode": mode,
        "shown_left_alternative_id": alt_a["alternative_id"],
        "model_version_locked": "",
        "a_feasible": alt_a["feasible"],
        "b_feasible": alt_b["feasible"],
        "training_eligible": eligible,
        "eligibility_note": "",
        "feature_version": FEATURE_VERSION,
        "created_utc": NOW,
        "sync_status": "local_only",
    }


def summary(feasible: bool = True) -> dict[str, Any]:
    return {"housing_units": 0, "access_defined": False, "access_pct": 0.0, "park_share_pct": 0.0,
            "spent_credits": 0, "feasible": feasible}


def blank_cell() -> dict[str, Any]:
    return {"cell_id": "", "row": 0, "col": 0, "land_use": "", "floors": 0, "units_per_floor": 0,
            "max_floors": 0, "editable": False, "zoning": ""}


def event(session_payload: dict[str, Any], sequence: int, baseline: dict[str, Any] | None = None,
          cell_id: str = "r01_c03", action: str = "set_park", event_type: str = "command") -> dict[str, Any]:
    has_change = event_type in ("command", "undo", "redo")
    before = blank_cell()
    after = blank_cell()
    if has_change and baseline is not None:
        before = copy.deepcopy(next(c for c in baseline["cells"] if c["cell_id"] == cell_id))
        after = dict(before, land_use="park", floors=0)
    return {
        "event_id": uuid.uuid4().hex,
        "session_id": session_payload["session_id"],
        "scenario_id": session_payload["scenario_id"],
        "sequence": sequence,
        "event_type": event_type,
        "action": action,
        "cell_id": cell_id if has_change else "",
        "has_cell_change": has_change,
        "before": before,
        "after": after,
        "metrics_before": summary(),
        "metrics_after": summary(),
        "input_source": "mouse",
        "utc_time": NOW,
        "reversal_of_event_id": "",
        "original_event_id": "",
        "note": "",
    }
