#!/usr/bin/env python3
import json
import logging
import os
import re
import shlex
import shutil
import subprocess
import threading
import time
import uuid
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path


LOGGER = logging.getLogger("oni_ai")
SERVER_STARTED_AT = time.monotonic()
RUNTIME_STATE_LOCK = threading.Lock()
RUNTIME_STATE: dict[str, object] = {
    "last_request": None,
    "last_response": None,
    "manual_actions": [],
}


def is_truthy_env(var_name: str, default: bool) -> bool:
    raw_value = os.getenv(var_name)
    if raw_value is None:
        return default

    value = raw_value.strip().lower()
    if value in {"1", "true", "yes", "on"}:
        return True
    if value in {"0", "false", "no", "off"}:
        return False
    return default


def configure_logging() -> None:
    level_name = os.getenv("ONI_AI_LOG_LEVEL", "INFO").strip().upper() or "INFO"
    level = getattr(logging, level_name, logging.INFO)
    logging.basicConfig(
        level=level,
        format="%(asctime)s | %(levelname)s | %(name)s | %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
        force=True,
    )


def preview_text(text: str, limit: int = 300) -> str:
    if not isinstance(text, str):
        return ""
    compact = text.strip().replace("\n", "\\n")
    if len(compact) <= limit:
        return compact
    return f"{compact[:limit]}...(truncated {len(compact) - limit} chars)"


def summarize_actions(normalized_response: str) -> str:
    try:
        parsed = json.loads(normalized_response)
    except json.JSONDecodeError:
        return "actions=unknown"

    actions = parsed.get("actions")
    if not isinstance(actions, list):
        return "actions=missing"

    action_types = []
    for action in actions:
        if isinstance(action, dict):
            action_types.append(str(action.get("type") or action.get("action") or "unknown"))

    return f"actions={len(actions)} types={action_types}"


def get_runtime_state_snapshot() -> dict:
    with RUNTIME_STATE_LOCK:
        return {
            "last_request": RUNTIME_STATE.get("last_request"),
            "last_response": RUNTIME_STATE.get("last_response"),
            "manual_actions": list(RUNTIME_STATE.get("manual_actions") or []),
        }


def set_last_analyze_payload(payload: dict, normalized_response: str) -> None:
    with RUNTIME_STATE_LOCK:
        RUNTIME_STATE["last_request"] = payload
        try:
            RUNTIME_STATE["last_response"] = json.loads(normalized_response)
        except json.JSONDecodeError:
            RUNTIME_STATE["last_response"] = {"actions": []}


def set_manual_actions(actions: list[dict]) -> None:
    with RUNTIME_STATE_LOCK:
        RUNTIME_STATE["manual_actions"] = actions


def reset_runtime_state_for_tests() -> None:
    with RUNTIME_STATE_LOCK:
        RUNTIME_STATE["last_request"] = None
        RUNTIME_STATE["last_response"] = None
        RUNTIME_STATE["manual_actions"] = []


def strip_fence(text: str) -> str:
    match = re.search(r"```(?:json)?\s*(.*?)\s*```", text, re.IGNORECASE | re.DOTALL)
    if match:
        return match.group(1).strip()
    return text.strip()


def normalize_action(raw_output: str, request_tag: str = "-") -> str:
    if not isinstance(raw_output, str) or not raw_output.strip():
        LOGGER.warning("request=%s normalize_action got empty output", request_tag)
        return json.dumps({"actions": []}, ensure_ascii=False)

    cleaned = strip_fence(raw_output)

    try:
        parsed = json.loads(cleaned)
        if isinstance(parsed, dict):
            action = str(parsed.get("action", "")).strip().lower()
            if action == "resume" or action == "keep_paused":
                LOGGER.info("request=%s mapped legacy action=%s to noop", request_tag, action)
                return json.dumps({"actions": []}, ensure_ascii=False)
            if action == "set_speed":
                speed = parsed.get("speed", 0)
                if isinstance(speed, int) and 1 <= speed <= 3:
                    LOGGER.info("request=%s mapped legacy set_speed=%s", request_tag, speed)
                    return json.dumps({"actions": [{"id": "set_speed_1", "type": "set_speed", "params": {"speed": speed}}]}, ensure_ascii=False)
            if isinstance(parsed.get("actions"), list):
                normalized_actions = []
                for item in parsed.get("actions", []):
                    if not isinstance(item, dict):
                        continue

                    normalized_item = dict(item)
                    item_type = str(normalized_item.get("type", "")).strip()
                    if not item_type:
                        item_type = str(normalized_item.get("action", "")).strip()

                    if item_type:
                        normalized_item["type"] = item_type

                    normalized_actions.append(normalized_item)

                parsed["actions"] = normalized_actions
                LOGGER.info("request=%s accepted structured actions payload with count=%s", request_tag, len(parsed.get("actions", [])))
                return json.dumps(parsed, ensure_ascii=False)
    except json.JSONDecodeError:
        LOGGER.debug("request=%s output is not direct JSON; trying legacy text mapping", request_tag)
        pass

    upper = cleaned.upper()
    if "KEEP_PAUSED" in upper:
        LOGGER.info("request=%s mapped KEEP_PAUSED to noop", request_tag)
        return json.dumps({"actions": []}, ensure_ascii=False)

    speed_match = re.search(r"SET_SPEED\s*:\s*([1-3])", upper)
    if speed_match:
        LOGGER.info("request=%s mapped SET_SPEED text to speed=%s", request_tag, speed_match.group(1))
        return json.dumps({"actions": [{"id": "set_speed_1", "type": "set_speed", "params": {"speed": int(speed_match.group(1))}}]}, ensure_ascii=False)

    if "RESUME" in upper:
        LOGGER.info("request=%s mapped RESUME to noop", request_tag)
        return json.dumps({"actions": []}, ensure_ascii=False)

    LOGGER.warning("request=%s failed to map output; returning noop. preview=%s", request_tag, preview_text(cleaned, 500))
    return json.dumps({"actions": []}, ensure_ascii=False)


def build_prompt(payload: dict, has_screenshot: bool) -> str:
    api_base_url = str(payload.get("api_base_url", "")).strip()
    if api_base_url:
        api_base_url = api_base_url.rstrip("/")

    custom_prompt = os.getenv(
        "ONI_AI_PROMPT",
        (
            "You are an ONI survival operations planner. "
            "Primary objective: keep duplicants alive (oxygen, food, temperature safety, no idle-critical failures). "
            "Secondary objective: stabilize and improve colony reliability. "
            "Use local reference: ./openapi.yaml. "
            "Read the schema directly to understand all supported actions and required fields. "
            "Before planning, fetch live game state from ONI HTTP APIs. "
            "Use only the API paths defined by ./openapi.yaml. "
            "Output MUST be valid JSON with top-level keys: analysis, suggestions, actions, notes. "
            "Return a meaningful prioritized plan with 3-8 actions by default. "
            "Do NOT return a trivial single-action plan (for example only set_speed) unless there is a clear emergency reason; "
            "if so, explain that reason in notes. "
            "Prefer actions that directly improve survival margin and execution clarity: "
            "priority, set_duplicant_priority, set_duplicant_skills, build, dig, research, arrangement, plus speed control when needed. "
            "Assign a reasonable amount of actions, cancel outdated actions, and optionally update action priorities when execution order should change. "
            "Be foreseeable and predictive: push the colony toward final goals of sustainable living and advanced technologies, including aerospace. "
            "Use cancel when a previously proposed action is unsafe or conflicts with survival goals. "
            "Always include stable action ids and concrete params. "
            "Do not run broad exploratory shell scans or commands that print huge outputs. "
            "Do not dump or enumerate full state payload keys. "
            "Never run jq keys/to_entries or broad rg against full state blobs. "
            "Use concise targeted reads and limit shell calls to minimal API checks. "
            "You may query ONI wiki sources for mechanics/building/research facts when needed: wiki.gg/Oxygen_Not_Included, oxygennotincluded.wiki.gg, oni-db.com, and klei.com forums. "
            "Use targeted lookups only; avoid broad web crawling. "
            "When state context is paused and api_base_url is available, you may submit immediate updates via POST /actions and POST /priorities while planning. "
            "If you do live POST, keep it minimal, survival-focused, and still return final JSON plan. "
            "After reading openapi.yaml, first call GET /state and GET /priorities, then plan. "
            "Return ONLY JSON with top-level keys in this order: analysis, suggestions, actions, notes. "
            "analysis should summarize colony risk and why the plan helps survival. "
            "suggestions should be concise human-readable bullets as an array of strings."
        ),
    )

    api_note = ""
    if api_base_url:
        api_note = (
            f"ONI API api_base_url={api_base_url}; "
            "read endpoint paths from ./openapi.yaml. "
            "Use GET /state and GET /priorities as primary source of truth. "
            "Use POST /actions and POST /priorities for live updates when paused."
        )
    else:
        api_note = (
            "No api_base_url provided in this request payload. "
            "Use request payload fields as fallback state input."
        )

    screenshot_note = "screenshot.png is available." if has_screenshot else "screenshot.png is not available."
    return f"{custom_prompt}\n\n{api_note}\nNote: {screenshot_note}\n"


def wait_for_screenshot(request_dir: str, payload: dict, request_tag: str) -> bool:
    screenshot_hint = str(payload.get("screenshot_path", "")).strip() or "screenshot.png"
    screenshot_path = screenshot_hint if os.path.isabs(screenshot_hint) else os.path.join(request_dir, screenshot_hint)
    wait_ms = int(os.getenv("ONI_AI_SCREENSHOT_WAIT_MS", "500"))
    poll_ms = int(os.getenv("ONI_AI_SCREENSHOT_POLL_MS", "50"))

    if wait_ms < 0:
        wait_ms = 0
    if poll_ms <= 0:
        poll_ms = 50

    has_screenshot = os.path.exists(screenshot_path)
    if has_screenshot or wait_ms == 0:
        return has_screenshot

    started_at = time.monotonic()
    deadline = started_at + (wait_ms / 1000.0)

    while time.monotonic() < deadline:
        time.sleep(poll_ms / 1000.0)
        if os.path.exists(screenshot_path):
            elapsed_ms = int((time.monotonic() - started_at) * 1000)
            LOGGER.info(
                "request=%s screenshot became available after wait=%sms path=%s",
                request_tag,
                elapsed_ms,
                screenshot_path,
            )
            return True

    LOGGER.warning(
        "request=%s screenshot unavailable after waiting %sms path=%s",
        request_tag,
        wait_ms,
        screenshot_path,
    )
    return False


def copy_reference_assets_to_request_dir(request_dir: str, request_tag: str) -> None:
    project_root = Path(__file__).resolve().parents[2]

    source_to_target = {
        project_root / "schemas" / "openapi.yaml": Path(request_dir) / "openapi.yaml",
        project_root / "examples" / "request_idle" / "state.json": Path(request_dir) / "state.example.json",
        project_root / "examples" / "request_idle" / "response.json": Path(request_dir) / "response.example.json",
    }

    copied = 0
    missing = []
    for source_path, target_path in source_to_target.items():
        if not source_path.exists():
            missing.append(str(source_path))
            continue

        shutil.copy2(source_path, target_path)
        copied += 1

    if missing:
        LOGGER.warning(
            "request=%s missing reference assets count=%s paths=%s",
            request_tag,
            len(missing),
            missing,
        )

    LOGGER.info(
        "request=%s staged reference assets copied=%s request_dir=%s",
        request_tag,
        copied,
        request_dir,
    )


def read_last_message_output(last_message_path: Path, request_tag: str) -> str:
    if not last_message_path.exists():
        return ""

    try:
        text = last_message_path.read_text(encoding="utf-8")
    except OSError:
        LOGGER.exception("request=%s failed reading output-last-message file=%s", request_tag, last_message_path)
        return ""

    if text.strip():
        LOGGER.info(
            "request=%s loaded codex last-message output path=%s chars=%s",
            request_tag,
            last_message_path,
            len(text),
        )

    return text


def call_codex_exec(payload: dict, request_tag: str = "-") -> str:
    codex_cmd_raw = os.getenv("ONI_AI_CODEX_CMD", "codex").strip() or "codex"
    try:
        codex_cmd_parts = shlex.split(codex_cmd_raw)
    except ValueError:
        LOGGER.exception("request=%s invalid ONI_AI_CODEX_CMD=%s", request_tag, codex_cmd_raw)
        return json.dumps({"actions": []}, ensure_ascii=False)

    if not codex_cmd_parts:
        codex_cmd_parts = ["codex"]

    skip_git_repo_check = is_truthy_env("ONI_AI_CODEX_SKIP_GIT_REPO_CHECK", True)
    timeout_seconds = int(os.getenv("ONI_AI_CODEX_TIMEOUT_SECONDS", "90"))
    codex_sandbox_mode = os.getenv("ONI_AI_CODEX_SANDBOX", "danger-full-access").strip() or "danger-full-access"


    request_dir = str(payload.get("request_dir", "")).strip()
    if not request_dir or not os.path.isdir(request_dir):
        LOGGER.error("request=%s invalid request_dir=%s", request_tag, request_dir)
        return json.dumps({"actions": []}, ensure_ascii=False)

    copy_reference_assets_to_request_dir(request_dir, request_tag)

    has_screenshot = wait_for_screenshot(request_dir, payload, request_tag)
    logs_dir = Path(request_dir) / "logs"
    logs_dir.mkdir(parents=True, exist_ok=True)
    last_message_path = logs_dir / "codex_last_message.json"

    LOGGER.info(
        "request=%s invoking codex cmd=%s timeout=%ss request_dir=%s has_screenshot=%s skip_git_repo_check=%s sandbox=%s",
        request_tag,
        shlex.join(codex_cmd_parts),
        timeout_seconds,
        request_dir,
        has_screenshot,
        skip_git_repo_check,
        codex_sandbox_mode,
    )

    prompt = build_prompt(payload, has_screenshot)
    LOGGER.debug("request=%s prompt_preview=%s", request_tag, preview_text(prompt, 500))
    command = [*codex_cmd_parts, "exec", "-s", codex_sandbox_mode, "-o", str(last_message_path)]
    if skip_git_repo_check:
        command.append("--skip-git-repo-check")
    command.append(prompt)
    started_at = time.monotonic()

    stdout_chunks: list[str] = []
    stderr_chunks: list[str] = []

    def stream_reader(stream, is_stderr: bool) -> None:
        if stream is None:
            return

        level = logging.WARNING if is_stderr else logging.INFO
        prefix = "stderr" if is_stderr else "stdout"

        for raw_line in iter(stream.readline, ""):
            line = raw_line.rstrip("\n")
            if is_stderr:
                stderr_chunks.append(raw_line)
            else:
                stdout_chunks.append(raw_line)

            if line:
                LOGGER.log(level, "request=%s codex %s | %s", request_tag, prefix, line)

        stream.close()

    try:
        process = subprocess.Popen(
            command,
            cwd=request_dir,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
        )
        stdout_thread = threading.Thread(target=stream_reader, args=(process.stdout, False), daemon=True)
        stderr_thread = threading.Thread(target=stream_reader, args=(process.stderr, True), daemon=True)
        stdout_thread.start()
        stderr_thread.start()

        timed_out = False
        try:
            process.wait(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            timed_out = True
            LOGGER.error("request=%s codex timed out after %ss; terminating process", request_tag, timeout_seconds)
            process.kill()
            process.wait()

        stdout_thread.join(timeout=5)
        stderr_thread.join(timeout=5)

        if timed_out:
            stderr_chunks.append(f"Timed out after {timeout_seconds} seconds\n")
    except (subprocess.SubprocessError, OSError, ValueError) as exc:
        elapsed_ms = int((time.monotonic() - started_at) * 1000)
        with open(logs_dir / "codex_invoke_error.txt", "w", encoding="utf-8") as file:
            file.write(str(exc))
        LOGGER.exception("request=%s codex invocation failed after %sms", request_tag, elapsed_ms)
        return json.dumps({"actions": []}, ensure_ascii=False)

    elapsed_ms = int((time.monotonic() - started_at) * 1000)
    return_code = process.returncode if process.returncode is not None else -1
    stdout_text = "".join(stdout_chunks)
    stderr_text = "".join(stderr_chunks)

    with open(logs_dir / "codex_stdout.txt", "w", encoding="utf-8") as file:
        file.write(stdout_text)
    with open(logs_dir / "codex_stderr.txt", "w", encoding="utf-8") as file:
        file.write(stderr_text)
    with open(logs_dir / "codex_exit_code.txt", "w", encoding="utf-8") as file:
        file.write(str(return_code))

    LOGGER.info(
        "request=%s codex finished exit=%s elapsed_ms=%s stdout_chars=%s stderr_chars=%s logs_dir=%s",
        request_tag,
        return_code,
        elapsed_ms,
        len(stdout_text),
        len(stderr_text),
        str(logs_dir),
    )
    if stderr_text:
        LOGGER.warning("request=%s codex stderr preview=%s", request_tag, preview_text(stderr_text, 500))

    raw_last_message = read_last_message_output(last_message_path, request_tag)

    if return_code != 0:
        combined = raw_last_message or (stdout_text + "\n" + stderr_text)
        normalized = normalize_action(combined, request_tag=request_tag)
        LOGGER.info("request=%s normalized nonzero-exit output => %s", request_tag, summarize_actions(normalized))
        return normalized

    normalized = normalize_action(raw_last_message or stdout_text, request_tag=request_tag)
    LOGGER.info("request=%s normalized success output => %s", request_tag, summarize_actions(normalized))
    return normalized


class OniAiHandler(BaseHTTPRequestHandler):
    def send_json(self, status_code: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        trace_id = uuid.uuid4().hex[:8]

        if self.path == "/health":
            uptime_seconds = int(time.monotonic() - SERVER_STARTED_AT)
            self.send_json(200, {"ok": True, "service": "oni_ai", "uptime_seconds": uptime_seconds})
            return

        if self.path == "/state":
            snapshot = get_runtime_state_snapshot()
            last_request = snapshot.get("last_request")
            if last_request is None:
                self.send_json(404, {"error": "no_state"})
                return

            self.send_json(200, {"state": last_request})
            return

        if self.path == "/actions":
            snapshot = get_runtime_state_snapshot()
            response_actions = []
            last_response = snapshot.get("last_response")
            if isinstance(last_response, dict) and isinstance(last_response.get("actions"), list):
                response_actions = last_response.get("actions")

            self.send_json(
                200,
                {
                    "manual_actions": snapshot.get("manual_actions") or [],
                    "last_response_actions": response_actions,
                },
            )
            return

        LOGGER.warning("trace=%s path=%s method=GET => 404", trace_id, self.path)
        self.send_response(404)
        self.end_headers()

    def do_POST(self):
        started_at = time.monotonic()
        trace_id = uuid.uuid4().hex[:8]

        if self.path == "/actions":
            content_length = int(self.headers.get("Content-Length", "0"))
            raw_body = self.rfile.read(content_length)
            try:
                payload = json.loads(raw_body.decode("utf-8"))
            except (json.JSONDecodeError, UnicodeDecodeError):
                self.send_json(400, {"error": "invalid_json"})
                return

            actions = payload.get("actions") if isinstance(payload, dict) else None
            if not isinstance(actions, list):
                self.send_json(400, {"error": "actions_must_be_list"})
                return

            normalized_actions = []
            for item in actions:
                if not isinstance(item, dict):
                    continue
                normalized_item = dict(item)
                item_type = str(normalized_item.get("type") or normalized_item.get("action") or "").strip()
                if item_type:
                    normalized_item["type"] = item_type
                normalized_actions.append(normalized_item)

            set_manual_actions(normalized_actions)
            self.send_json(200, {"accepted": len(normalized_actions)})
            return

        if self.path != "/analyze":
            LOGGER.warning("trace=%s path=%s method=POST => 404", trace_id, self.path)
            self.send_response(404)
            self.end_headers()
            return

        content_length = int(self.headers.get("Content-Length", "0"))
        LOGGER.info("trace=%s incoming request path=%s content_length=%s", trace_id, self.path, content_length)
        raw_body = self.rfile.read(content_length)
        LOGGER.debug("trace=%s raw_body_preview=%s", trace_id, preview_text(raw_body.decode("utf-8", errors="replace"), 400))

        try:
            payload = json.loads(raw_body.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            LOGGER.exception("trace=%s invalid JSON body", trace_id)
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b"Invalid JSON")
            return

        request_tag = str(payload.get("request_id", "")).strip() or trace_id
        request_dir = str(payload.get("request_dir", "")).strip()
        LOGGER.info(
            "trace=%s request=%s payload_keys=%s request_dir=%s",
            trace_id,
            request_tag,
            sorted(payload.keys()),
            request_dir,
        )

        command = call_codex_exec(payload, request_tag=request_tag)
        if not isinstance(command, str) or not command.strip():
            command = json.dumps({"actions": []}, ensure_ascii=False)

        set_last_analyze_payload(payload, command)

        body = command.strip().encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

        elapsed_ms = int((time.monotonic() - started_at) * 1000)
        LOGGER.info(
            "trace=%s request=%s response_status=200 response_bytes=%s elapsed_ms=%s summary=%s",
            trace_id,
            request_tag,
            len(body),
            elapsed_ms,
            summarize_actions(command),
        )

    def log_message(self, fmt, *args):
        return


def main():
    configure_logging()

    bind_host = os.getenv("ONI_AI_BRIDGE_HOST", "127.0.0.1")
    bind_port = int(os.getenv("ONI_AI_BRIDGE_PORT", "8765"))

    server = HTTPServer((bind_host, bind_port), OniAiHandler)
    LOGGER.info("ONI AI bridge listening on %s:%s", bind_host, bind_port)
    LOGGER.info(
        "Logging configured level=%s codex_cmd_default=%s timeout_default=%s",
        os.getenv("ONI_AI_LOG_LEVEL", "INFO"),
        os.getenv("ONI_AI_CODEX_CMD", "codex").strip() or "codex",
        os.getenv("ONI_AI_CODEX_TIMEOUT_SECONDS", "90"),
    )
    server.serve_forever()


if __name__ == "__main__":
    main()
