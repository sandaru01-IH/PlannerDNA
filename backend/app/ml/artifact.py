"""Model files: plain JSON, never pickle.

A fitted model is fully described by its weight vector and feature scale, so it is
stored as JSON under <data_dir>/models/. Loading parses numbers only (no code is
executed), checks the SHA-256 recorded in the database, and refuses any path
outside the models folder.
"""

from __future__ import annotations

import hashlib
import json
import math
import os
from pathlib import Path
from typing import Any

from app.ml.pairwise import FEATURE_NAMES, ML_VERSION, MODEL_KIND

ARTIFACT_FORMAT = "plannerdna-pairwise-model"
ARTIFACT_FORMAT_VERSION = 1
MODELS_DIR_NAME = "models"


class ModelArtifactError(Exception):
    pass


def models_dir(data_dir: Path) -> Path:
    return Path(data_dir) / MODELS_DIR_NAME


def write_artifact(data_dir: Path, model_version: str, payload: dict[str, Any]) -> tuple[str, str]:
    """Writes atomically; returns (path relative to data_dir, sha256 of the exact bytes)."""
    folder = models_dir(data_dir)
    folder.mkdir(parents=True, exist_ok=True)
    data = (json.dumps(payload, indent=2, sort_keys=True) + "\n").encode("utf-8")
    target = folder / f"{model_version}.json"
    temp = target.with_suffix(".json.tmp")
    temp.write_bytes(data)
    os.replace(temp, target)
    return f"{MODELS_DIR_NAME}/{target.name}", hashlib.sha256(data).hexdigest()


def remove_artifact(data_dir: Path, relative_path: str) -> None:
    try:
        _resolve(data_dir, relative_path).unlink(missing_ok=True)
    except ModelArtifactError:
        pass


def _resolve(data_dir: Path, relative_path: str) -> Path:
    folder = models_dir(data_dir).resolve()
    path = (Path(data_dir) / relative_path).resolve()
    if path.parent != folder or path.suffix != ".json":
        raise ModelArtifactError(f"Model path {relative_path!r} is outside the local models folder.")
    return path


def read_artifact(data_dir: Path, relative_path: str, expected_sha256: str) -> dict[str, Any]:
    path = _resolve(data_dir, relative_path)
    if not path.is_file():
        raise ModelArtifactError(f"Model file {relative_path} is missing.")
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != expected_sha256:
        raise ModelArtifactError(f"Model file {relative_path} does not match its recorded checksum; it was changed after training.")
    try:
        payload = json.loads(data.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ModelArtifactError(f"Model file {relative_path} is not valid JSON: {exc}") from exc
    _validate(payload, relative_path)
    return payload


def _validate(payload: Any, name: str) -> None:
    if not isinstance(payload, dict):
        raise ModelArtifactError(f"{name}: not a model object.")
    expected = {"format": ARTIFACT_FORMAT, "format_version": ARTIFACT_FORMAT_VERSION, "ml_version": ML_VERSION,
                "model_kind": MODEL_KIND, "feature_names": list(FEATURE_NAMES)}
    for key, value in expected.items():
        if payload.get(key) != value:
            raise ModelArtifactError(f"{name}: {key} is {payload.get(key)!r}, expected {value!r}.")
    for key in ("weights_raw", "feature_scale", "weights_standardised"):
        values = payload.get(key)
        if (not isinstance(values, list) or len(values) != len(FEATURE_NAMES)
                or not all(isinstance(v, (int, float)) and not isinstance(v, bool) and math.isfinite(v) for v in values)):
            raise ModelArtifactError(f"{name}: {key} must be {len(FEATURE_NAMES)} finite numbers.")
