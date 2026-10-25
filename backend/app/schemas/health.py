from typing import Literal

from pydantic import BaseModel


class HealthResponse(BaseModel):
    status: Literal["ok"]
    service: str
    api_version: str
    schema_version: int
    backend_phase: int
    storage_status: Literal["not_implemented", "ready", "unavailable"]
    db_schema_version: int
    model_status: Literal["no_model", "trained"]
    active_model_version: str = ""
    evaluation_status: Literal["none", "open"] = "none"
    open_evaluation_plan_id: str = ""
    server_time_utc: str
