import json
import os
import shutil
from pathlib import Path

import pytest

from oni_ai.ai_bridge import call_codex_exec


REAL_CODEX_ENV_FLAG = "ONI_AI_RUN_REAL_CODEX"
KEEP_ARTIFACTS_ENV_FLAG = "ONI_AI_KEEP_REAL_CODEX_ARTIFACTS"


@pytest.mark.real_codex
def test_call_codex_exec_with_request_idle_fixture() -> None:
    if os.getenv(REAL_CODEX_ENV_FLAG) != "1":
        pytest.skip(f"Set {REAL_CODEX_ENV_FLAG}=1 to run this integration test")

    fixture_root = Path("examples") / "request_idle"
    request_root = Path.cwd() / ".tmp_real_codex_requests"
    request_dir = request_root / "request_idle"

    if request_dir.exists():
        shutil.rmtree(request_dir, ignore_errors=True)

    shutil.copytree(fixture_root, request_dir)
    logs_dir = request_dir / "logs"
    logs_dir.mkdir(parents=True, exist_ok=True)

    env_backup = {
        "ONI_AI_PROMPT": os.environ.get("ONI_AI_PROMPT"),
        "ONI_AI_CODEX_TIMEOUT_SECONDS": os.environ.get("ONI_AI_CODEX_TIMEOUT_SECONDS"),
        "ONI_AI_CODEX_CMD": os.environ.get("ONI_AI_CODEX_CMD"),
    }
    os.environ["ONI_AI_CODEX_CMD"] = "codex"
    os.environ["ONI_AI_CODEX_TIMEOUT_SECONDS"] = "120"
    os.environ["ONI_AI_PROMPT"] = (
        "You are validating ONI bridge behavior from fixture data. "
        "Read state.json and use it as the single source of truth. "
        "Return ONLY valid JSON with top-level key actions. "
        "Return 3-8 meaningful survival actions, not a trivial single set_speed action, "
        "unless emergency and explained in notes."
    )

    try:
        normalized_response = call_codex_exec({"request_dir": str(request_dir)})
    finally:
        for key, value in env_backup.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value

    try:
        parsed = json.loads(normalized_response)
        assert isinstance(parsed.get("actions"), list)
        assert len(parsed["actions"]) >= 1
        if len(parsed["actions"]) == 1:
            only = parsed["actions"][0]
            assert str(only.get("type", "")).lower() != "set_speed"
        assert all(isinstance(action, dict) and isinstance(action.get("type"), str) for action in parsed["actions"])

        extracted_path = logs_dir / "extracted_response.json"
        extracted_path.write_text(normalized_response, encoding="utf-8")

        stdout_path = logs_dir / "codex_stdout.txt"
        stderr_path = logs_dir / "codex_stderr.txt"
        exit_code_path = logs_dir / "codex_exit_code.txt"

        assert stdout_path.exists()
        assert stderr_path.exists()
        assert exit_code_path.exists()
    finally:
        if os.getenv(KEEP_ARTIFACTS_ENV_FLAG) == "1":
            print(f"real_codex_artifacts={request_dir}")
            return

        shutil.rmtree(request_dir, ignore_errors=True)
        shutil.rmtree(request_root, ignore_errors=True)
