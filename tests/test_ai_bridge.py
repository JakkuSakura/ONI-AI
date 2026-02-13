import json
import os
from pathlib import Path

from oni_ai_bridge.ai_bridge import build_prompt, call_codex_exec, normalize_action, strip_fence


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
    assert "screenshot.png is available." in build_prompt(True)
    assert "screenshot.png is not available." in build_prompt(False)


def test_call_codex_exec_uses_stubbed_command(tmp_path: Path) -> None:
    request_dir = tmp_path / "request"
    snapshot_dir = request_dir / "snapshot"
    snapshot_dir.mkdir(parents=True)

    stub = tmp_path / "fake-codex.sh"
    stub.write_text(
        "#!/usr/bin/env bash\n"
        "echo '{\"actions\":[{\"id\":\"t1\",\"type\":\"set_speed\",\"params\":{\"speed\":1}}]}'\n",
        encoding="utf-8",
    )
    stub.chmod(0o755)

    os.environ["ONI_AI_CODEX_CMD"] = str(stub)
    try:
        parsed = json.loads(call_codex_exec({"request_dir": str(request_dir)}))
    finally:
        os.environ.pop("ONI_AI_CODEX_CMD", None)

    assert parsed["actions"][0]["params"]["speed"] == 1
    assert (request_dir / "logs" / "codex_stdout.txt").exists()
