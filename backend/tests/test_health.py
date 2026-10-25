from datetime import datetime


def test_health_returns_ok_json(client):
    response = client.get("/api/v1/health")
    assert response.status_code == 200
    assert response.headers["content-type"].startswith("application/json")
    body = response.json()
    assert body["status"] == "ok"
    assert body["service"] == "plannerdna-backend"
    assert body["api_version"] == "v1"
    assert body["schema_version"] == 1
    assert body["backend_phase"] == 4


def test_health_reports_storage_ready_and_no_model_honestly(client):
    body = client.get("/api/v1/health").json()
    assert body["model_status"] == "no_model"
    assert body["active_model_version"] == ""
    assert body["storage_status"] == "ready"
    assert body["db_schema_version"] == 3
    assert body["evaluation_status"] == "none" and body["open_evaluation_plan_id"] == ""


def test_health_time_is_iso_utc(client):
    body = client.get("/api/v1/health").json()
    parsed = datetime.fromisoformat(body["server_time_utc"])
    assert parsed.utcoffset() is not None
    assert parsed.utcoffset().total_seconds() == 0


def test_openapi_docs_available(client):
    assert client.get("/docs").status_code == 200
    paths = client.get("/openapi.json").json()["paths"]
    for path in ("/api/v1/health", "/api/v1/scenarios", "/api/v1/sessions", "/api/v1/events",
                 "/api/v1/alternatives", "/api/v1/preferences", "/api/v1/preferences/stats",
                 "/api/v1/sessions/{session_id}/events", "/api/v1/export", "/api/v1/export/comparisons.csv",
                 "/api/v1/data", "/api/v1/models/readiness", "/api/v1/models/train", "/api/v1/models/current",
                 "/api/v1/models/history", "/api/v1/models/predict-pair", "/api/v1/models/rank-candidates",
                 "/api/v1/evaluation", "/api/v1/evaluation/plans", "/api/v1/evaluation/plans/{plan_id}",
                 "/api/v1/evaluation/trials", "/api/v1/evaluation/trials/{trial_id}/reveal",
                 "/api/v1/evaluation/plans/{plan_id}/close", "/api/v1/evaluation/plans/{plan_id}/report",
                 "/api/v1/evaluation/plans/{plan_id}/trials.csv", "/api/v1/evaluation/plans/{plan_id}/metrics.csv"):
        assert path in paths, path


def test_unknown_route_is_structured_404(client):
    response = client.get("/api/v1/does-not-exist")
    assert response.status_code == 404
    assert response.json()["error"]["code"] == "not_found"
