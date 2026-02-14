import json
import os
import socket
import threading
import time
from pathlib import Path
from urllib import request

import oni_ai.ai_bridge as ai_bridge
from oni_ai.ai_bridge import build_prompt, call_codex_exec, normalize_action, strip_fence


def _find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        sock.bind(("127.0.0.1", 0))
        return int(sock.getsockname()[1])


def _http_json(url: str, method: str = "GET", body: dict | None = None) -> tuple[int, dict]:
    payload_bytes = None
    headers = {}
    if body is not None:
        payload_bytes = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"

    req = request.Request(url, method=method, data=payload_bytes, headers=headers)
    with request.urlopen(req, timeout=5) as response:
        return int(response.getcode()), json.loads(response.read().decode("utf-8"))


def test_strip_fence_json_block() -> None:
    raw = """```json
    {\"action\": \"set_speed\", \"speed\": 2}
    ```"""
    assert strip_fence(raw) == '{"action": "set_speed", "speed": 2}'


def test_normalize_action_accepts_structured_actions() -> None:
    raw = '{"actions":[{"id":"x","type":"set_speed","params":{"speed":3}}]}'
    parsed = json.loads(normalize_action(raw))
    assert parsed["actions"][0]["params"]["speed"] == 3


def test_normalize_action_legacy_set_speed() -> None:
    parsed = json.loads(normalize_action("SET_SPEED:2"))
    assert parsed == {
        "actions": [
            {"id": "set_speed_1", "type": "set_speed", "params": {"speed": 2}}
        ]
    }


def test_normalize_action_invalid_input_defaults_to_noop() -> None:
    parsed = json.loads(normalize_action("nonsense"))
    assert parsed == {"actions": []}


def test_build_prompt_mentions_screenshot_flag() -> None:
    payload = {
        "api_base_url": "http://127.0.0.1:8766",
    }

    prompt_with_image = build_prompt(payload, True)
    prompt_without_image = build_prompt(payload, False)

    assert "screenshot.png is available." in prompt_with_image
    assert "screenshot.png is not available." in prompt_without_image
    assert "api_base_url=http://127.0.0.1:8766" in prompt_with_image
    assert "set_duplicant_priority" in prompt_with_image
    assert "Do not run broad exploratory shell scans" in prompt_with_image
    assert "POST /actions and POST /priorities" in prompt_with_image
    assert "GET /state and GET /priorities" in prompt_with_image
    assert "wiki.gg/Oxygen_Not_Included" in prompt_with_image
    assert "oxygennotincluded.wiki.gg" in prompt_with_image

def test_call_codex_exec_uses_stubbed_command(tmp_path: Path) -> None:
    request_dir = tmp_path / "request"
    snapshot_dir = request_dir / "snapshot"
    snapshot_dir.mkdir(parents=True)
    args_file = request_dir / "logs" / "argv.txt"

    stub = tmp_path / "fake-codex.sh"
    stub.write_text(
        "#!/usr/bin/env bash\n"
        "mkdir -p \"$(dirname \"$ONI_AI_ARGS_FILE\")\"\n"
        "printf '%s\\n' \"$*\" > \"$ONI_AI_ARGS_FILE\"\n"
        "echo '{\"actions\":[{\"id\":\"t1\",\"type\":\"set_speed\",\"params\":{\"speed\":1}}]}'\n",
        encoding="utf-8",
    )
    stub.chmod(0o755)

    os.environ["ONI_AI_CODEX_CMD"] = str(stub)
    os.environ["ONI_AI_ARGS_FILE"] = str(args_file)
    try:
        parsed = json.loads(call_codex_exec({"request_dir": str(request_dir)}))
    finally:
        os.environ.pop("ONI_AI_CODEX_CMD", None)
        os.environ.pop("ONI_AI_ARGS_FILE", None)

    assert parsed["actions"][0]["params"]["speed"] == 1
    assert "--skip-git-repo-check" in args_file.read_text(encoding="utf-8")
    assert "-s" in args_file.read_text(encoding="utf-8")
    assert "danger-full-access" in args_file.read_text(encoding="utf-8")
    args_text = args_file.read_text(encoding="utf-8")
    assert "-o" in args_text
    assert (request_dir / "logs" / "codex_stdout.txt").exists()
    assert (request_dir / "openapi.yaml").exists()
    assert (request_dir / "state.example.json").exists()
    assert (request_dir / "response.example.json").exists()


def test_call_codex_exec_can_disable_skip_git_repo_check(tmp_path: Path) -> None:
    request_dir = tmp_path / "request"
    snapshot_dir = request_dir / "snapshot"
    snapshot_dir.mkdir(parents=True)
    args_file = request_dir / "logs" / "argv.txt"

    stub = tmp_path / "fake-codex.sh"
    stub.write_text(
        "#!/usr/bin/env bash\n"
        "mkdir -p \"$(dirname \"$ONI_AI_ARGS_FILE\")\"\n"
        "printf '%s\\n' \"$*\" > \"$ONI_AI_ARGS_FILE\"\n"
        "echo '{\"actions\":[]}'\n",
        encoding="utf-8",
    )
    stub.chmod(0o755)

    os.environ["ONI_AI_CODEX_CMD"] = str(stub)
    os.environ["ONI_AI_ARGS_FILE"] = str(args_file)
    os.environ["ONI_AI_CODEX_SKIP_GIT_REPO_CHECK"] = "0"
    os.environ["ONI_AI_CODEX_SANDBOX"] = "workspace-write"
    try:
        json.loads(call_codex_exec({"request_dir": str(request_dir)}))
    finally:
        os.environ.pop("ONI_AI_CODEX_CMD", None)
        os.environ.pop("ONI_AI_ARGS_FILE", None)
        os.environ.pop("ONI_AI_CODEX_SKIP_GIT_REPO_CHECK", None)
        os.environ.pop("ONI_AI_CODEX_SANDBOX", None)

    args_text = args_file.read_text(encoding="utf-8")
    assert "--skip-git-repo-check" not in args_text
    assert "workspace-write" in args_text


def test_call_codex_exec_prefers_output_last_message_file(tmp_path: Path) -> None:
    request_dir = tmp_path / "request"
    request_dir.mkdir(parents=True)

    stub = tmp_path / "fake-codex.sh"
    stub.write_text(
        "#!/usr/bin/env bash\n"
        "out=\"\"\n"
        "while [[ $# -gt 0 ]]; do\n"
        "  case \"$1\" in\n"
        "    -o|--output-last-message) out=\"$2\"; shift 2 ;;\n"
        "    *) shift ;;\n"
        "  esac\n"
        "done\n"
        "mkdir -p \"$(dirname \"$out\")\"\n"
        "printf '%s' '{\"actions\":[{\"id\":\"from_last\",\"type\":\"set_speed\",\"params\":{\"speed\":3}}]}' > \"$out\"\n"
        "echo 'stdout noise that is not json'\n",
        encoding="utf-8",
    )
    stub.chmod(0o755)

    os.environ["ONI_AI_CODEX_CMD"] = str(stub)
    try:
        parsed = json.loads(call_codex_exec({"request_dir": str(request_dir)}))
    finally:
        os.environ.pop("ONI_AI_CODEX_CMD", None)

    assert parsed["actions"][0]["id"] == "from_last"
    assert parsed["actions"][0]["params"]["speed"] == 3


def test_call_codex_exec_timeout_disabled_allows_long_running_stub(tmp_path: Path) -> None:
    request_dir = tmp_path / "request"
    request_dir.mkdir(parents=True)

    stub = tmp_path / "fake-codex.sh"
    stub.write_text(
        "#!/usr/bin/env bash\n"
        "sleep 1\n"
        "echo '{\"actions\":[{\"id\":\"slow_ok\",\"type\":\"set_speed\",\"params\":{\"speed\":2}}]}'\n",
        encoding="utf-8",
    )
    stub.chmod(0o755)

    os.environ["ONI_AI_CODEX_CMD"] = str(stub)
    os.environ["ONI_AI_CODEX_TIMEOUT_SECONDS"] = "0"
    try:
        parsed = json.loads(call_codex_exec({"request_dir": str(request_dir)}))
    finally:
        os.environ.pop("ONI_AI_CODEX_CMD", None)
        os.environ.pop("ONI_AI_CODEX_TIMEOUT_SECONDS", None)

    assert parsed["actions"][0]["id"] == "slow_ok"


def test_analyze_endpoint_is_async_with_status_polling(monkeypatch, tmp_path: Path) -> None:
    ai_bridge.reset_runtime_state_for_tests()

    def fake_call_codex_exec(payload: dict, request_tag: str = "-") -> str:
        return '{"actions":[{"id":"job-1","type":"set_speed","params":{"speed":1}}]}'

    monkeypatch.setattr(ai_bridge, "call_codex_exec", fake_call_codex_exec)

    port = _find_free_port()
    server = ai_bridge.HTTPServer(("127.0.0.1", port), ai_bridge.OniAiHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    try:
        request_dir = tmp_path / "request"
        request_dir.mkdir(parents=True)
        payload = {
            "request_id": "async_test_001",
            "request_dir": str(request_dir),
            "api_base_url": "http://127.0.0.1:8766",
        }

        status_code, submit = _http_json(f"http://127.0.0.1:{port}/analyze", method="POST", body=payload)
        assert status_code == 202
        assert submit["status"] == "queued"
        assert submit["progress"] == 0
        assert isinstance(submit.get("job_id"), str)
        assert submit["status_url"].startswith("/analyze/")

        poll_url = f"http://127.0.0.1:{port}{submit['status_url']}"
        final = None
        for _ in range(40):
            _, current = _http_json(poll_url)
            if current["status"] in {"completed", "failed"}:
                final = current
                break
            time.sleep(0.05)

        assert final is not None
        assert final["status"] == "completed"
        assert final["progress"] == 100
        parsed_response = json.loads(final["response"])
        assert parsed_response["actions"][0]["id"] == "job-1"
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


def test_analyze_endpoint_accepts_trailing_slash(monkeypatch, tmp_path: Path) -> None:
    ai_bridge.reset_runtime_state_for_tests()

    def fake_call_codex_exec(payload: dict, request_tag: str = "-") -> str:
        return '{"actions":[]}'

    monkeypatch.setattr(ai_bridge, "call_codex_exec", fake_call_codex_exec)

    port = _find_free_port()
    server = ai_bridge.HTTPServer(("127.0.0.1", port), ai_bridge.OniAiHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    try:
        request_dir = tmp_path / "request_trailing"
        request_dir.mkdir(parents=True)
        payload = {
            "request_id": "async_test_slash_001",
            "request_dir": str(request_dir),
        }

        status_code, submit = _http_json(f"http://127.0.0.1:{port}/analyze/", method="POST", body=payload)
        assert status_code == 202
        assert submit["status"] == "queued"
        assert isinstance(submit.get("job_id"), str)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)
