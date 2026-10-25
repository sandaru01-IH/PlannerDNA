"""Version 1 HTTP API. Every write is idempotent by the client's record id."""

from __future__ import annotations

import csv
import io
import json
import shutil
import sqlite3
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse, Response

from app.errors import ApiError
from app.schemas.health import HealthResponse
from app.schemas.records import (
    AlternativeIn,
    DeleteDataIn,
    EventIn,
    PreferenceIn,
    ScenarioIn,
    SessionIn,
)
from app.schemas.evaluation import ClosePlanIn, EvaluationPlanIn, EvaluationTrialIn
from app.schemas.models import PredictPairIn, RankCandidatesIn
from app.services import evaluation, evaluation_rules, models, records
from app.ml.artifact import models_dir
from app.storage.db import DATA_TABLES_DELETE_ORDER, Database
from app.version import API_VERSION, BACKEND_PHASE, SCHEMA_VERSION, SERVICE_NAME

DELETE_CONFIRMATION_PHRASE = "DELETE ALL PLANNERDNA DATA"

router = APIRouter(prefix="/api/v1")


def _db(request: Request) -> Database:
    return request.app.state.db


def _write(request: Request, record_id: str, action: Callable[[sqlite3.Connection], str]) -> JSONResponse:
    with _db(request).transaction() as conn:
        status = action(conn)
    code = 201 if status == "created" else 200
    detail = "stored" if status == "created" else "already stored; nothing changed"
    return JSONResponse(status_code=code, content={"status": status, "id": record_id, "detail": detail})


@router.get("/health", response_model=HealthResponse, tags=["system"])
def health(request: Request) -> HealthResponse:
    active = ""
    open_plan_id = ""
    try:
        with _db(request).reader() as conn:
            version = Database.schema_version(conn)
            storage = "ready" if version == request.app.state.expected_schema_version else "unavailable"
            if storage == "ready":
                active = models.active_model_version(conn)
                plan = evaluation_rules.open_plan(conn)
                open_plan_id = plan["id"] if plan is not None else ""
    except sqlite3.Error:
        version, storage = 0, "unavailable"
    return HealthResponse(
        status="ok",
        service=SERVICE_NAME,
        api_version=API_VERSION,
        schema_version=SCHEMA_VERSION,
        backend_phase=BACKEND_PHASE,
        storage_status=storage,
        db_schema_version=version,
        model_status="trained" if active else "no_model",
        active_model_version=active,
        evaluation_status="open" if open_plan_id else "none",
        open_evaluation_plan_id=open_plan_id,
        server_time_utc=datetime.now(timezone.utc).isoformat(),
    )


@router.post("/scenarios", tags=["records"], status_code=201)
def post_scenario(body: ScenarioIn, request: Request) -> JSONResponse:
    return _write(request, body.scenario_id, lambda c: records.register_scenario(c, body))


@router.post("/sessions", tags=["records"], status_code=201)
def post_session(body: SessionIn, request: Request) -> JSONResponse:
    return _write(request, body.session_id, lambda c: records.start_session(c, body))


@router.post("/events", tags=["records"], status_code=201)
def post_event(body: EventIn, request: Request) -> JSONResponse:
    return _write(request, body.event_id, lambda c: records.record_event(c, body))


@router.post("/alternatives", tags=["records"], status_code=201)
def post_alternative(body: AlternativeIn, request: Request) -> JSONResponse:
    return _write(request, body.alternative_id, lambda c: records.save_alternative(c, body))


@router.post("/preferences", tags=["records"], status_code=201)
def post_preference(body: PreferenceIn, request: Request) -> JSONResponse:
    return _write(request, body.comparison_id, lambda c: records.save_preference(c, body))


@router.get("/preferences/stats", tags=["reads"])
def get_preference_stats(request: Request) -> dict[str, Any]:
    with _db(request).reader() as conn:
        return records.preference_stats(conn)


@router.get("/models/readiness", tags=["models"])
def get_model_readiness(request: Request) -> dict[str, Any]:
    with _db(request).reader() as conn:
        return models.readiness(conn)


@router.post("/models/train", tags=["models"], status_code=201)
def post_train(request: Request) -> JSONResponse:
    """Fits a new model from all training-eligible comparisons, or returns the existing one if the data is unchanged."""
    status, summary = models.train(_db(request), request.app.state.data_dir)
    detail = "trained a new model" if status == "created" else "no new comparisons since the last model; nothing changed"
    return JSONResponse(status_code=201 if status == "created" else 200,
                        content={"status": status, "detail": detail, "model": summary})


@router.get("/models/current", tags=["models"])
def get_current_model(request: Request) -> dict[str, Any]:
    with _db(request).reader() as conn:
        return models.current(conn)


@router.get("/models/history", tags=["models"])
def get_model_history(request: Request) -> dict[str, Any]:
    with _db(request).reader() as conn:
        return models.history(conn)


@router.post("/models/predict-pair", tags=["models"])
def post_predict_pair(body: PredictPairIn, request: Request) -> dict[str, Any]:
    return models.predict_pair(_db(request), request.app.state.data_dir, body)


@router.post("/models/rank-candidates", tags=["models"])
def post_rank_candidates(body: RankCandidatesIn, request: Request) -> dict[str, Any]:
    return models.rank_candidates(_db(request), request.app.state.data_dir, body)


@router.get("/evaluation", tags=["evaluation"])
def get_evaluation_overview(request: Request) -> dict[str, Any]:
    """The open blinded evaluation (if any) with its next step, and every earlier plan."""
    with _db(request).reader() as conn:
        return evaluation.overview(conn)


@router.post("/evaluation/plans", tags=["evaluation"], status_code=201)
def post_evaluation_plan(body: EvaluationPlanIn, request: Request) -> JSONResponse:
    """Pre-registers unseen scenarios and freezes the active model. Training is paused until the plan is closed."""
    status, plan = evaluation.create_plan(_db(request), request.app.state.data_dir, body)
    return JSONResponse(status_code=201 if status == "created" else 200, content={"status": status, "plan": plan})


@router.get("/evaluation/plans/{plan_id}", tags=["evaluation"])
def get_evaluation_plan(plan_id: str, request: Request) -> dict[str, Any]:
    with _db(request).reader() as conn:
        return evaluation.plan_status(conn, evaluation._plan(conn, plan_id))


@router.post("/evaluation/trials", tags=["evaluation"], status_code=201)
def post_evaluation_trial(body: EvaluationTrialIn, request: Request) -> JSONResponse:
    """Registers the next pre-registered pair and seals the frozen model's prediction. The probability is not returned."""
    status, trial = evaluation.register_trial(_db(request), request.app.state.data_dir, body)
    return JSONResponse(status_code=201 if status == "created" else 200, content={"status": status, "trial": trial})


@router.post("/evaluation/trials/{trial_id}/reveal", tags=["evaluation"])
def post_evaluation_reveal(trial_id: str, request: Request) -> dict[str, Any]:
    """Reveals a sealed prediction, only after the choice for that pair is stored."""
    return evaluation.reveal(_db(request), request.app.state.data_dir, trial_id)


@router.post("/evaluation/plans/{plan_id}/close", tags=["evaluation"])
def post_evaluation_close(plan_id: str, body: ClosePlanIn, request: Request) -> dict[str, Any]:
    status, report = evaluation.close_plan(_db(request), plan_id, body)
    return {"status": status, "report_sha256": evaluation.report_sha256(report), "report": report}


@router.get("/evaluation/plans/{plan_id}/report", tags=["evaluation"])
def get_evaluation_report(plan_id: str, request: Request) -> dict[str, Any]:
    with _db(request).reader() as conn:
        return evaluation.get_report(conn, plan_id)


def _csv_response(header: list[str], rows: list[list[Any]], filename: str) -> Response:
    buffer = io.StringIO()
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow(header)
    writer.writerows(rows)
    return Response(content=buffer.getvalue(), media_type="text/csv; charset=utf-8",
                    headers={"Content-Disposition": f'attachment; filename="{filename}"'})


@router.get("/evaluation/plans/{plan_id}/trials.csv", tags=["evaluation"])
def get_evaluation_trials_csv(plan_id: str, request: Request) -> Response:
    with _db(request).reader() as conn:
        report = evaluation.get_report(conn, plan_id)["report"]
    header, rows = evaluation.trials_csv_rows(report)
    return _csv_response(header, rows, f"plannerdna-evaluation-{plan_id}-trials.csv")


@router.get("/evaluation/plans/{plan_id}/metrics.csv", tags=["evaluation"])
def get_evaluation_metrics_csv(plan_id: str, request: Request) -> Response:
    with _db(request).reader() as conn:
        report = evaluation.get_report(conn, plan_id)["report"]
    header, rows = evaluation.metrics_csv_rows(report)
    return _csv_response(header, rows, f"plannerdna-evaluation-{plan_id}-metrics.csv")


@router.get("/sessions/{session_id}/events", tags=["reads"])
def get_session_timeline(session_id: str, request: Request) -> dict[str, Any]:
    with _db(request).reader() as conn:
        events = records.session_timeline(conn, session_id)
    return {"session_id": session_id, "event_count": len(events), "events": events}


@router.get("/export", tags=["export"])
def get_export(request: Request) -> JSONResponse:
    with _db(request).reader() as conn:
        bundle = records.export_all(conn, Database.schema_version(conn))
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    return JSONResponse(content=bundle, headers={"Content-Disposition": f'attachment; filename="plannerdna-export-{stamp}.json"'})


@router.get("/export/comparisons.csv", tags=["export"])
def get_comparisons_csv(request: Request) -> Response:
    with _db(request).reader() as conn:
        header, rows = records.comparisons_csv_rows(conn)
    buffer = io.StringIO()
    writer = csv.writer(buffer, lineterminator="\n")
    writer.writerow(header)
    writer.writerows(rows)
    return Response(content=buffer.getvalue(), media_type="text/csv; charset=utf-8",
                    headers={"Content-Disposition": 'attachment; filename="plannerdna-comparisons.csv"'})


@router.delete("/data", tags=["export"])
def delete_all_data(body: DeleteDataIn, request: Request) -> dict[str, Any]:
    if body.confirm != DELETE_CONFIRMATION_PHRASE:
        raise ApiError(400, "confirmation_required",
                       f"Deletion is permanent. Send confirm exactly as \"{DELETE_CONFIRMATION_PHRASE}\".")
    db = _db(request)
    data_dir: Path = request.app.state.data_dir
    backup_path = None
    if body.backup:
        with db.reader() as conn:
            bundle = records.export_all(conn, Database.schema_version(conn))
        backups = data_dir / "backups"
        backups.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
        target = backups / f"plannerdna-backup-{stamp}.json"
        temp = target.with_suffix(".json.tmp")
        temp.write_text(json.dumps(bundle, indent=2), encoding="utf-8")
        temp.replace(target)
        backup_path = str(target)

    deleted: dict[str, int] = {}
    with db.transaction() as conn:
        for table in DATA_TABLES_DELETE_ORDER:
            deleted[table] = conn.execute(f"DELETE FROM {table}").rowcount
    with db.reader() as conn:
        conn.execute("VACUUM")

    model_folder = models_dir(data_dir)
    models_removed = model_folder.exists()
    if models_removed:
        shutil.rmtree(model_folder)
    return {
        "status": "deleted",
        "deleted_rows": deleted,
        "models_removed": models_removed,
        "backup_path": backup_path,
        "detail": "All PlannerDNA research data and fitted model files were removed from this computer's backend.",
    }
