"""PlannerDNA local backend entry point: ``uvicorn app.main:app --host 127.0.0.1 --port 8765``.

The data directory comes from PLANNERDNA_DATA_DIR, or defaults to backend/data.
Binds to loopback only (enforced by the start command, never 0.0.0.0).
"""

from app.factory import create_app

app = create_app()
