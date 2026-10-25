"""Where the backend keeps its local data.

The data directory comes from (in order): an explicit argument (tests), the
PLANNERDNA_DATA_DIR environment variable, or ``backend/data`` next to the code.
Nothing is ever written outside this directory.
"""

from __future__ import annotations

import os
from pathlib import Path

DATA_DIR_ENV = "PLANNERDNA_DATA_DIR"
DEFAULT_DATA_DIR = Path(__file__).resolve().parent.parent / "data"
DATABASE_FILE_NAME = "plannerdna.db"


def resolve_data_dir(explicit: Path | str | None = None) -> Path:
    if explicit is not None:
        return Path(explicit).resolve()
    env = os.environ.get(DATA_DIR_ENV)
    if env:
        return Path(env).resolve()
    return DEFAULT_DATA_DIR
