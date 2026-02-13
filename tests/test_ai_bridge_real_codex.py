import json
import os
import shutil
import uuid
from datetime import datetime, timezone
from pathlib import Path

import pytest

from oni_ai.ai_bridge import call_codex_exec


REAL_CODEX_ENV_FLAG = "ONI_AI_RUN_REAL_CODEX"
KEEP_ARTIFACTS_ENV_FLAG = "ONI_AI_KEEP_REAL_CODEX_ARTIFACTS"


@pytest.mark.real_codex
def test_call_codex_exec_with_realistic_payload_and_real_codex() -> None:
    if os.getenv(REAL_CODEX_ENV_FLAG) != "1":
        pytest.skip(f"Set {REAL_CODEX_ENV_FLAG}=1 to run this integration test")

    request_id = f"{datetime.now(tz=timezone.utc).strftime('%Y%m%d_%H%M%S_%f')}_{uuid.uuid4().hex[:4]}"
    trusted_root = Path.cwd() / ".tmp_real_codex_requests"
    request_dir = trusted_root / request_id
    snapshot_dir = request_dir / "snapshot"
    logs_dir = request_dir / "logs"
    snapshot_dir.mkdir(parents=True)
    logs_dir.mkdir(parents=True)

    screenshot_path = snapshot_dir / "screenshot.png"
    screenshot_path.write_bytes(
        bytes.fromhex(
            "89504E470D0A1A0A"
            "0000000D49484452000000010000000108060000001F15C489"
            "0000000D49444154789C6360000002000154010D0A2DB40000000049454E44AE426082"
        )
    )

    context = {
        "cycle": 127,
        "time_since_cycle_start": 432.5,
        "time_in_cycles": 127.18,
        "paused": True,
        "current_speed": 1,
        "previous_speed": 2,
        "real_time_since_startup_seconds": 15023.3,
        "unscaled_time_seconds": 14987.8,
    }

    runtime_config = {
        "unity_version": "2021.3.39f1",
        "platform": "OSXPlayer",
        "target_frame_rate": -1,
        "product_name": "Oxygen Not Included",
        "version": "U55-678078",
        "scene_count": 2,
    }

    assemblies = [
        {"name": "Assembly-CSharp", "version": "0.0.0.0"},
        {"name": "UnityEngine.CoreModule", "version": "0.0.0.0"},
        {"name": "Newtonsoft.Json", "version": "13.0.0.0"},
    ]

    scenes = [
        {
            "index": 0,
            "name": "Main",
            "is_loaded": True,
            "root_count": 2,
            "roots": [
                {
                    "name": "Game",
                    "active": True,
                    "child_count": 5,
                    "components": ["Game", "KMonoBehaviour"],
                },
                {
                    "name": "OverlayCanvas",
                    "active": True,
                    "child_count": 22,
                    "components": ["Canvas", "GraphicRaycaster"],
                },
            ],
        },
        {
            "index": 1,
            "name": "ClusterManager",
            "is_loaded": True,
            "root_count": 1,
            "roots": [
                {
                    "name": "WorldContainer",
                    "active": True,
                    "child_count": 3,
                    "components": ["Transform", "ClusterManager"],
                }
            ],
        },
    ]

    singletons = [
        {
            "type": "SpeedControlScreen",
            "summary": {
                "object_type": "SpeedControlScreen",
                "values": {"IsPaused": True, "CurrentSpeed": 1},
            },
        },
        {
            "type": "GameClock",
            "summary": {
                "object_type": "GameClock",
                "values": {"Cycle": 127, "TimeInCycle": 432.5},
            },
        },
    ]

    request_envelope = {
        "request_id": request_id,
        "request_dir": str(request_dir),
        "api_base_url": "http://127.0.0.1:8766",
        "state_endpoint": "http://127.0.0.1:8766/state",
        "actions_endpoint": "http://127.0.0.1:8766/actions",
        "health_endpoint": "http://127.0.0.1:8766/health",
        "screenshot_path": str(screenshot_path),
        "requested_at_utc": datetime.now(tz=timezone.utc).isoformat(),
        "context": context,
        "duplicants": [],
        "pending_actions": [
            {
                "duplicant_id": "1001",
                "duplicant_name": "Ada",
                "current_action": "Idle",
                "queue": [],
            }
        ],
        "priorities": [
            {
                "duplicant_id": "1001",
                "duplicant_name": "Ada",
                "values": {"dig": 5, "build": 5},
            }
        ],
        "runtime_config": runtime_config,
        "assemblies": assemblies,
        "scenes": scenes,
        "singletons": singletons,
    }

    env_backup = {
        "ONI_AI_PROMPT": os.environ.get("ONI_AI_PROMPT"),
        "ONI_AI_CODEX_TIMEOUT_SECONDS": os.environ.get("ONI_AI_CODEX_TIMEOUT_SECONDS"),
        "ONI_AI_CODEX_CMD": os.environ.get("ONI_AI_CODEX_CMD"),
    }
    os.environ["ONI_AI_CODEX_CMD"] = "codex"
    os.environ["ONI_AI_CODEX_TIMEOUT_SECONDS"] = "120"
    os.environ["ONI_AI_PROMPT"] = (
        "You are testing an ONI bridge. Use request payload as source of truth. "
        "Return ONLY valid JSON with top-level key actions. "
        "Choose exactly one action: set_speed with params.speed between 1 and 3."
    )

    try:
        normalized_response = call_codex_exec(request_envelope)
    finally:
        for key, value in env_backup.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value

    try:
        normalized_parsed = json.loads(normalized_response)
        assert isinstance(normalized_parsed.get("actions"), list)

        stdout_path = logs_dir / "codex_stdout.txt"
        stderr_path = logs_dir / "codex_stderr.txt"
        exit_code_path = logs_dir / "codex_exit_code.txt"

        assert stdout_path.exists()
        assert stderr_path.exists()
        assert exit_code_path.exists()

        raw_stdout = stdout_path.read_text(encoding="utf-8").strip()
        assert raw_stdout

        extracted_path = logs_dir / "extracted_response.json"
        extracted_path.write_text(normalized_response, encoding="utf-8")

        extracted = json.loads(extracted_path.read_text(encoding="utf-8"))
        assert "actions" in extracted

        if os.getenv(KEEP_ARTIFACTS_ENV_FLAG) == "1":
            print(f"real_codex_artifacts={request_dir}")
    finally:
        if os.getenv(KEEP_ARTIFACTS_ENV_FLAG) == "1":
            return

        shutil.rmtree(request_dir, ignore_errors=True)
        shutil.rmtree(trusted_root, ignore_errors=True)
