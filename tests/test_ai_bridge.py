import json
import os
from pathlib import Path

from oni_ai.ai_bridge import build_prompt, call_codex_exec, normalize_action, strip_fence


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
        "state_endpoint": "http://127.0.0.1:8766/state",
        "actions_endpoint": "http://127.0.0.1:8766/actions",
        "health_endpoint": "http://127.0.0.1:8766/health",
    }

    prompt_with_image = build_prompt(payload, True)
    prompt_without_image = build_prompt(payload, False)

    assert "screenshot.png is available." in prompt_with_image
    assert "screenshot.png is not available." in prompt_without_image
    assert "bridge-response.schema.json" in prompt_with_image
    assert "state=http://127.0.0.1:8766/state" in prompt_with_image
    assert "set_duplicant_priority" in prompt_with_image
    assert "Do not run broad exploratory shell scans" in prompt_with_image


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
    assert "--output-schema" in args_file.read_text(encoding="utf-8")
    args_text = args_file.read_text(encoding="utf-8")
    assert "--output-schema" in args_text
    assert "-o" in args_text
    assert (request_dir / "logs" / "codex_stdout.txt").exists()
    assert (request_dir / "bridge-request.schema.json").exists()
    assert (request_dir / "bridge-response.schema.json").exists()
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
    try:
        json.loads(call_codex_exec({"request_dir": str(request_dir)}))
    finally:
        os.environ.pop("ONI_AI_CODEX_CMD", None)
        os.environ.pop("ONI_AI_ARGS_FILE", None)
        os.environ.pop("ONI_AI_CODEX_SKIP_GIT_REPO_CHECK", None)

    assert "--skip-git-repo-check" not in args_file.read_text(encoding="utf-8")


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
