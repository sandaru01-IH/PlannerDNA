"""Application factory. Each app owns one data directory, so tests can use a
temporary directory and simulate a restart by creating a second app on it."""

from __future__ import annotations

from pathlib import Path

from fastapi import FastAPI

from app.api.v1 import router
from app.config import DATABASE_FILE_NAME, resolve_data_dir
from app.errors import install_error_handlers
from app.storage.db import MIGRATIONS, Database
from app.version import BACKEND_PHASE


def create_app(data_dir: Path | str | None = None) -> FastAPI:
    resolved = resolve_data_dir(data_dir)
    db = Database(resolved / DATABASE_FILE_NAME)
    db.migrate()

    app = FastAPI(
        title="PlannerDNA Local Backend",
        version="0.3.0",
        description=(
            f"Local-only research backend for PlannerDNA (phase {BACKEND_PHASE}). Stores scenarios, sessions, "
            "timeline events, A/B alternatives and preference choices in a local SQLite file, and fits a personal "
            "pairwise logistic preference model (scikit-learn) only when explicitly asked to train. "
            "Nothing leaves this computer."
        ),
    )
    app.state.data_dir = resolved
    app.state.db = db
    app.state.expected_schema_version = MIGRATIONS[-1][0]
    install_error_handlers(app)
    app.include_router(router)
    return app
