#!/usr/bin/env python3
import json
import os
import re
import subprocess
from http.server import BaseHTTPRequestHandler, HTTPServer


def strip_fence(text: str) -> str:
    match = re.search(r"```(?:json)?\s*(.*?)\s*```", text, re.IGNORECASE | re.DOTALL)
    if match:
        return match.group(1).strip()
    return text.strip()


def normalize_action(raw_output: str) -> str:
    if not isinstance(raw_output, str) or not raw_output.strip():
        return json.dumps({"actions": []}, ensure_ascii=False)

    cleaned = strip_fence(raw_output)

    try:
        parsed = json.loads(cleaned)
        if isinstance(parsed, dict):
            action = str(parsed.get("action", "")).strip().lower()
            if action == "resume" or action == "keep_paused":
                return json.dumps({"actions": []}, ensure_ascii=False)
            if action == "set_speed":
                speed = parsed.get("speed", 0)
                if isinstance(speed, int) and 1 <= speed <= 3:
                    return json.dumps({"actions": [{"id": "set_speed_1", "type": "set_speed", "params": {"speed": speed}}]}, ensure_ascii=False)
            if isinstance(parsed.get("actions"), list):
                return json.dumps(parsed, ensure_ascii=False)
    except json.JSONDecodeError:
        pass

    upper = cleaned.upper()
    if "KEEP_PAUSED" in upper:
        return json.dumps({"actions": []}, ensure_ascii=False)

    speed_match = re.search(r"SET_SPEED\s*:\s*([1-3])", upper)
    if speed_match:
        return json.dumps({"actions": [{"id": "set_speed_1", "type": "set_speed", "params": {"speed": int(speed_match.group(1))}}]}, ensure_ascii=False)

    if "RESUME" in upper:
        return json.dumps({"actions": []}, ensure_ascii=False)

    return json.dumps({"actions": []}, ensure_ascii=False)


def build_prompt(has_screenshot: bool) -> str:
    custom_prompt = os.getenv(
        "ONI_AI_PROMPT",
        (
            "You are an ONI planning agent. Read all files under ./snapshot and request.json in cwd. "
            "Return ONLY JSON with this shape: "
            "{\"actions\":[{\"id\":\"a1\",\"type\":\"set_speed\",\"params\":{\"speed\":2}}],\"notes\":\"...\"}. "
            "Allowed types for now: set_speed, no_op, build, dig, deconstruct, priority, arrangement, research. "
            "For unsupported or uncertain operations, still include them in actions; executor may mark unsupported."
        ),
    )

    screenshot_note = "screenshot.png is available." if has_screenshot else "screenshot.png is not available."
    return f"{custom_prompt}\n\nNote: {screenshot_note}\n"


def call_codex_exec(payload: dict) -> str:
    codex_cmd = os.getenv("ONI_AI_CODEX_CMD", "codex").strip() or "codex"
    timeout_seconds = int(os.getenv("ONI_AI_CODEX_TIMEOUT_SECONDS", "90"))

    request_dir = str(payload.get("request_dir", "")).strip()
    if not request_dir or not os.path.isdir(request_dir):
        return json.dumps({"actions": []}, ensure_ascii=False)

    snapshot_dir = os.path.join(request_dir, "snapshot")
    screenshot_path = os.path.join(snapshot_dir, "screenshot.png")
    has_screenshot = os.path.exists(screenshot_path)

    prompt = build_prompt(has_screenshot)
    command = [codex_cmd, "exec", prompt]

    try:
        result = subprocess.run(
            command,
            cwd=request_dir,
            capture_output=True,
            text=True,
            timeout=timeout_seconds,
            check=False,
        )
    except (subprocess.SubprocessError, OSError, ValueError) as exc:
        logs_dir = os.path.join(request_dir, "logs")
        os.makedirs(logs_dir, exist_ok=True)
        with open(os.path.join(logs_dir, "codex_invoke_error.txt"), "w", encoding="utf-8") as file:
            file.write(str(exc))
        return json.dumps({"actions": []}, ensure_ascii=False)

    logs_dir = os.path.join(request_dir, "logs")
    os.makedirs(logs_dir, exist_ok=True)
    with open(os.path.join(logs_dir, "codex_stdout.txt"), "w", encoding="utf-8") as file:
        file.write(result.stdout or "")
    with open(os.path.join(logs_dir, "codex_stderr.txt"), "w", encoding="utf-8") as file:
        file.write(result.stderr or "")
    with open(os.path.join(logs_dir, "codex_exit_code.txt"), "w", encoding="utf-8") as file:
        file.write(str(result.returncode))

    if result.returncode != 0:
        combined = (result.stdout or "") + "\n" + (result.stderr or "")
        return normalize_action(combined)

    return normalize_action(result.stdout or "")


class OniAiHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path != "/analyze":
            self.send_response(404)
            self.end_headers()
            return

        content_length = int(self.headers.get("Content-Length", "0"))
        raw_body = self.rfile.read(content_length)

        try:
            payload = json.loads(raw_body.decode("utf-8"))
        except (json.JSONDecodeError, UnicodeDecodeError):
            self.send_response(400)
            self.end_headers()
            self.wfile.write(b"Invalid JSON")
            return

        command = call_codex_exec(payload)
        if not isinstance(command, str) or not command.strip():
            command = json.dumps({"actions": []}, ensure_ascii=False)

        body = command.strip().encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        return


def main():
    bind_host = os.getenv("ONI_AI_BRIDGE_HOST", "127.0.0.1")
    bind_port = int(os.getenv("ONI_AI_BRIDGE_PORT", "8765"))

    server = HTTPServer((bind_host, bind_port), OniAiHandler)
    print(f"ONI AI bridge listening on {bind_host}:{bind_port}")
    server.serve_forever()


if __name__ == "__main__":
    main()
