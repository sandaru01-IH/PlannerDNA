import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

sys.path.insert(0, str(Path(__file__).resolve().parent))

from app.factory import create_app  # noqa: E402
import payloads  # noqa: E402


@pytest.fixture
def data_dir(tmp_path: Path) -> Path:
    return tmp_path / "data"


@pytest.fixture
def client(data_dir: Path) -> TestClient:
    return TestClient(create_app(data_dir))


@pytest.fixture
def seeded(client: TestClient):
    """A registered scenario with one started session."""
    baseline = payloads.city()
    assert client.post("/api/v1/scenarios", json=payloads.scenario(baseline)).status_code == 201
    sess = payloads.session(baseline)
    assert client.post("/api/v1/sessions", json=sess).status_code == 201
    return baseline, sess
