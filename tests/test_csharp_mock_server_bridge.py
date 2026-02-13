import json
import os
import shutil
import socket
import subprocess
import time
from pathlib import Path
from urllib import request

import pytest

from oni_ai.ai_bridge import call_codex_exec


def _find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def _http_json(url: str, method: str = "GET", body: dict | None = None) -> tuple[int, dict]:
    data = None
    headers = {}
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"

    req = request.Request(url, method=method, data=data, headers=headers)
    with request.urlopen(req, timeout=5) as response:
        status = response.getcode()
        payload = json.loads(response.read().decode("utf-8"))
        return status, payload


def _assert_meaningful_action(action: dict) -> None:
    assert isinstance(action, dict)
    action_type = str(action.get("type", "")).strip()
    assert action_type in {
        "priority",
        "build",
        "dig",
        "research",
        "set_duplicant_priority",
        "set_duplicant_skills",
        "cancel",
        "deconstruct",
        "arrangement",
    }

    params = action.get("params")
    assert isinstance(params, dict)
    assert len(params) > 0


def _start_csharp_mock_server(port: int) -> subprocess.Popen:
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        pytest.skip("dotnet is required for this integration test")

    project = Path("tests") / "csharp" / "OniApiMockServer" / "OniApiMockServer.csproj"
    return subprocess.Popen(
        [dotnet, "run", "--project", str(project), "--", "--port", str(port)],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        cwd=Path.cwd(),
    )


def _wait_server_ready(base_url: str) -> None:
    for _ in range(60):
        try:
            status, _ = _http_json(f"{base_url}/health")
            if status == 200:
                return
        except Exception:
            time.sleep(0.2)

    raise AssertionError("C# mock ONI API server did not start")


@pytest.mark.integration
def test_bridge_with_csharp_mock_server(tmp_path: Path) -> None:
    curl = shutil.which("curl")
    if curl is None:
        pytest.skip("curl is required for this integration test")

    port = _find_free_port()
    mock_base = f"http://127.0.0.1:{port}"
    process = _start_csharp_mock_server(port)

    try:
        _wait_server_ready(mock_base)

        post_status, post_payload = _http_json(
            f"{mock_base}/actions",
            method="POST",
            body={
                "actions": [
                    {
                        "id": "seed-action",
                        "type": "priority",
                        "params": {"target_id": "act_001", "priority": 8},
                    }
                ]
            },
        )
        assert post_status == 200
        assert post_payload["accepted"] == 1

        get_status, get_payload = _http_json(f"{mock_base}/actions")
        assert get_status == 200
        assert isinstance(get_payload.get("actions"), list)
        assert len(get_payload["actions"]) >= 1
        _assert_meaningful_action(get_payload["actions"][0])

        request_dir = tmp_path / "request"
        request_dir.mkdir(parents=True)
        (request_dir / "logs").mkdir(parents=True)
        (request_dir / "screenshot.png").write_bytes(b"png")

        fake_codex = tmp_path / "fake-codex.sh"
        fake_codex.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "args=\"$*\"\n"
            "api_base_url=$(printf '%s' \"$args\" | sed -nE 's/.*api_base_url=(http[^; ]+).*/\\1/p')\n"
            "state_url=\"$api_base_url/state\"\n"
            "if [ -z \"$api_base_url\" ]; then\n"
            "  echo '{\"actions\":[]}'\n"
            "  exit 0\n"
            "fi\n"
            f"state_payload=$({curl} -sS \"$state_url\")\n"
            "if printf '%s' \"$state_payload\" | grep -q '\"pending_action_count\"'; then\n"
            "  echo '{\"analysis\":\"Mock state fetched successfully\",\"suggestions\":[\"Promote life-support errands first\"],\"actions\":[{\"id\":\"raise-priority\",\"type\":\"priority\",\"params\":{\"target_id\":\"act_001\",\"priority\":9}}],\"notes\":\"Generated from mock C# /state endpoint.\"}'\n"
            "else\n"
            "  echo '{\"actions\":[]}'\n"
            "fi\n",
            encoding="utf-8",
        )
        fake_codex.chmod(0o755)

        payload = {
            "request_id": "mock_bridge_001",
            "request_dir": str(request_dir),
            "api_base_url": mock_base,
            "screenshot_path": "screenshot.png",
            "requested_at_utc": "2026-02-13T00:00:00Z",
            "context": {
                "cycle": 42,
                "time_since_cycle_start": 312.6,
                "time_in_cycles": 42.52,
                "paused": True,
                "current_speed": 1,
                "previous_speed": 2,
                "real_time_since_startup_seconds": 5123.4,
                "unscaled_time_seconds": 5098.1,
            },
            "duplicants": [],
            "pending_actions": [],
            "priorities": [],
        }

        env_backup = {
            "ONI_AI_CODEX_CMD": os.environ.get("ONI_AI_CODEX_CMD"),
            "ONI_AI_CODEX_TIMEOUT_SECONDS": os.environ.get("ONI_AI_CODEX_TIMEOUT_SECONDS"),
        }
        os.environ["ONI_AI_CODEX_CMD"] = str(fake_codex)
        os.environ["ONI_AI_CODEX_TIMEOUT_SECONDS"] = "15"

        try:
            normalized = call_codex_exec(payload, request_tag="mock_test")
        finally:
            for key, value in env_backup.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        parsed = json.loads(normalized)
        assert isinstance(parsed.get("actions"), list)
        assert len(parsed["actions"]) == 1
        _assert_meaningful_action(parsed["actions"][0])

        stats_status, stats_payload = _http_json(f"{mock_base}/stats")
        assert stats_status == 200
        assert stats_payload["counters"]["state"] >= 1
        assert stats_payload["counters"]["actions_post"] >= 1
        assert stats_payload["counters"]["priorities_get"] >= 0

    finally:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


@pytest.mark.integration
def test_bridge_allows_codex_live_post_actions(tmp_path: Path) -> None:
    curl = shutil.which("curl")
    if curl is None:
        pytest.skip("curl is required for this integration test")

    port = _find_free_port()
    mock_base = f"http://127.0.0.1:{port}"
    process = _start_csharp_mock_server(port)

    try:
        _wait_server_ready(mock_base)

        request_dir = tmp_path / "request_live_post"
        request_dir.mkdir(parents=True)
        (request_dir / "logs").mkdir(parents=True)
        (request_dir / "screenshot.png").write_bytes(b"png")

        fake_codex = tmp_path / "fake-codex-live-post.sh"
        fake_codex.write_text(
            "#!/usr/bin/env bash\n"
            "set -euo pipefail\n"
            "args=\"$*\"\n"
            "api_base_url=$(printf '%s' \"$args\" | sed -nE 's/.*api_base_url=(http[^; ]+).*/\\1/p')\n"
            "state_url=\"$api_base_url/state\"\n"
            "actions_url=\"$api_base_url/actions\"\n"
            "if [ -n \"$api_base_url\" ]; then\n"
            f"  state_payload=$({curl} -sS \"$state_url\")\n"
            "  is_paused=$(printf '%s' \"$state_payload\" | grep -c '\"paused\":true' || true)\n"
            "  if [ \"$is_paused\" -gt 0 ] && [ -n \"$actions_url\" ]; then\n"
            f"    {curl} -sS -X POST \"$actions_url\" -H 'Content-Type: application/json' --data-binary '{{\"actions\":[{{\"id\":\"live-posted\",\"type\":\"priority\",\"params\":{{\"target_id\":\"act_001\",\"priority\":9}}}}]}}' >/dev/null\n"
            "  fi\n"
            "fi\n"
            "echo '{\"analysis\":\"Posted live action while paused\",\"suggestions\":[\"Live-posted urgent priority update\"],\"actions\":[{\"id\":\"mirror-live-post\",\"type\":\"priority\",\"params\":{\"target_id\":\"act_001\",\"priority\":9}}],\"notes\":\"Codex performed on-the-fly POST /actions during paused planning.\"}'\n",
            encoding="utf-8",
        )
        fake_codex.chmod(0o755)

        payload = {
            "request_id": "mock_bridge_live_post_001",
            "request_dir": str(request_dir),
            "api_base_url": mock_base,
            "screenshot_path": "screenshot.png",
            "requested_at_utc": "2026-02-13T00:00:00Z",
            "context": {
                "cycle": 42,
                "time_since_cycle_start": 312.6,
                "time_in_cycles": 42.52,
                "paused": True,
                "current_speed": 1,
                "previous_speed": 2,
                "real_time_since_startup_seconds": 5123.4,
                "unscaled_time_seconds": 5098.1,
            },
            "duplicants": [],
            "pending_actions": [],
            "priorities": [],
        }

        env_backup = {
            "ONI_AI_CODEX_CMD": os.environ.get("ONI_AI_CODEX_CMD"),
            "ONI_AI_CODEX_TIMEOUT_SECONDS": os.environ.get("ONI_AI_CODEX_TIMEOUT_SECONDS"),
        }
        os.environ["ONI_AI_CODEX_CMD"] = str(fake_codex)
        os.environ["ONI_AI_CODEX_TIMEOUT_SECONDS"] = "15"

        try:
            normalized = call_codex_exec(payload, request_tag="live_post_test")
        finally:
            for key, value in env_backup.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        parsed = json.loads(normalized)
        assert isinstance(parsed.get("actions"), list)
        assert len(parsed["actions"]) == 1
        _assert_meaningful_action(parsed["actions"][0])

        stats_status, stats_payload = _http_json(f"{mock_base}/stats")
        assert stats_status == 200
        assert stats_payload["counters"]["state"] >= 1
        assert stats_payload["counters"]["actions_post"] >= 1

        actions_status, actions_payload = _http_json(f"{mock_base}/actions")
        assert actions_status == 200
        queued_actions = actions_payload.get("actions", [])
        posted_actions = [action for action in queued_actions if action.get("id") == "live-posted"]
        assert len(posted_actions) >= 1
        _assert_meaningful_action(posted_actions[0])

        priorities_post_status, priorities_post_payload = _http_json(
            f"{mock_base}/priorities",
            method="POST",
            body={
                "priorities": [
                    {
                        "duplicant_id": "1001",
                        "duplicant_name": "Ada",
                        "values": {"dig": 8, "build": 6},
                    }
                ]
            },
        )
        assert priorities_post_status == 200
        assert priorities_post_payload.get("accepted") == 1

        priorities_get_status, priorities_get_payload = _http_json(f"{mock_base}/priorities")
        assert priorities_get_status == 200
        assert isinstance(priorities_get_payload.get("priorities"), list)
        assert isinstance(priorities_get_payload.get("updates"), list)
        assert len(priorities_get_payload["updates"]) >= 1

    finally:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
