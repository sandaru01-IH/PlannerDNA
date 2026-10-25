"""The backend must not contain outbound network code (data remains local)."""

import re
from pathlib import Path

APP_DIR = Path(__file__).resolve().parent.parent / "app"
FORBIDDEN = re.compile(r"^\s*(import|from)\s+(requests|httpx|urllib\.request|urllib3|aiohttp|socket|smtplib|ftplib)\b", re.M)


def test_app_has_no_outbound_network_clients():
    offenders = [str(p) for p in APP_DIR.rglob("*.py") if FORBIDDEN.search(p.read_text(encoding="utf-8"))]
    assert offenders == []


def test_start_commands_bind_loopback_only():
    root = APP_DIR.parent
    texts = [p.read_text(encoding="utf-8") for p in root.glob("*.ps1")] + [(root / "README.md").read_text(encoding="utf-8")]
    commands = [line for text in texts for line in text.splitlines() if "uvicorn app.main:app" in line]
    assert commands, "no documented uvicorn start command found"
    for line in commands:
        assert "--host 127.0.0.1" in line, line
