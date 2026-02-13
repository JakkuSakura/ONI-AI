# ONI AI Assistant (local mod)

This project provides a local Oxygen Not Included mod that does the following on button click (or optional hotkey):

1. Pauses the game.
2. Captures a screenshot and game context.
3. Sends the payload to a local AI bridge endpoint.
4. Applies the returned command.
5. Resumes or keeps paused based on the command.

## Paths detected on this machine

- ONI app: `/Users/jakku/Library/Application Support/Steam/steamapps/common/OxygenNotIncluded/OxygenNotIncluded.app`
- ONI mods root: `/Users/jakku/Library/Application Support/unity.Klei.Oxygen Not Included/mods`

## Command protocol returned by bridge

The bridge returns plain text (single line):

- `RESUME`
- `KEEP_PAUSED`
- `SET_SPEED:1` / `SET_SPEED:2` / `SET_SPEED:3`

If the response is unknown, the mod restores the previous pause/speed state.

## Build

```bash
cd ~/Dev/ONI-AI
chmod +x scripts/build.sh scripts/install.sh
./scripts/build.sh
```

## Install

```bash
cd ~/Dev/ONI-AI
./scripts/install.sh
```

This installs the mod to:

`~/Library/Application Support/unity.Klei.Oxygen Not Included/mods/local/jakku.oni_ai_assistant`

## Run bridge (Codex CLI, managed by uv)

```bash
cd ~/Dev/ONI-AI
uv sync
uv run oni-ai-bridge
```

Optional env vars:

- `ONI_AI_CODEX_CMD` (default: `codex`)
- `ONI_AI_CODEX_SKIP_GIT_REPO_CHECK` (default: `1`, adds `--skip-git-repo-check` to `codex exec`)
- `ONI_AI_CODEX_TIMEOUT_SECONDS` (default: `90`)
- `ONI_AI_PROMPT` (custom decision prompt for `codex exec`)
- `ONI_AI_BRIDGE_HOST` (default: `127.0.0.1`)
- `ONI_AI_BRIDGE_PORT` (default: `8765`)
- `ONI_AI_LOG_LEVEL` (default: `INFO`, set `DEBUG` for verbose tracing)
- `ONI_AI_SCREENSHOT_WAIT_MS` (default: `500`, wait before `codex exec` for screenshot flush)
- `ONI_AI_SCREENSHOT_POLL_MS` (default: `50`, poll interval while waiting for screenshot)

The bridge writes request data to a temp directory (`state.json`, optional `screenshot.png`), with `state.json` as the single source of truth including context, duplicants (`status`, `priority`, `skills`), pending actions, priority snapshots, runtime config, assemblies, scenes, and singleton summaries. It stages `schemas/*` and `examples/*` references into that request directory, invokes `codex exec` there, then parses the output into ONI actions.

By default, mod requests are written under system tmp:

- `/tmp/oni_ai_assistant/requests/<request_id>`

You can override this in `mod/oni_ai_config.ini` via `request_root_dir`.

For intensive runtime logs:

```bash
ONI_AI_LOG_LEVEL=DEBUG uv run oni-ai-bridge
```

## In-game usage

1. Launch ONI.
2. Enable `ONI AI Assistant` in mod list.
3. Load a save.
4. Click the top-right `ONI AI Request` button.

Optional hotkey:

- Configure `enable_hotkey=true` in `oni_ai_config.ini`.
- Use `hotkey=BackQuote` (or another `UnityEngine.KeyCode` value).

## Hot Reload (while game is running)

The mod host supports runtime DLL and config hot-reload without restarting ONI.

### Runtime code hot-reload

1. Keep ONI running.
2. Start watcher in this repo:

```bash
cd ~/Dev/ONI-AI
./scripts/hot_reload_runtime.sh
```

3. Edit `runtime/OniAiRuntime.cs`.
4. On save, the script rebuilds and copies `OniAiRuntime.dll` to the installed mod runtime directory.
5. The host mod reloads it automatically (default check interval: 1s).

### Config hot-reload

- Edit installed config file while ONI is running:
  - `~/Library/Application Support/unity.Klei.Oxygen Not Included/mods/local/jakku.oni_ai_assistant/oni_ai_config.ini`
- The mod polls for config file changes and reloads in runtime.

Useful config keys for runtime reload:

- `runtime_dll_path` (default empty -> `runtime/OniAiRuntime.dll` under installed mod dir)
- `runtime_reload_interval_seconds` (default `1.0`)
- `request_root_dir` (default empty -> `/tmp/oni_ai_assistant/requests`)

Screenshot files are saved in the installed mod folder under `captures/`.

## License

This project is open sourced under the MIT License. See `LICENSE`.

## Test without launching ONI

You can test the bridge independently from the game runtime.

Run automated tests:

```bash
cd ~/Dev/ONI-AI
uv sync --group dev
uv run pytest
```


Run runtime hot-reload integration test (build/install + watcher + DLL swap):

```bash
cd ~/Dev/ONI-AI
ONI_AI_RUN_HOT_RELOAD_TEST=1 uv run pytest -m hot_reload -q -s
```

Run high-fidelity integration with real `codex exec` (uses realistic ONI payload files):

```bash
cd ~/Dev/ONI-AI
ONI_AI_RUN_REAL_CODEX=1 uv run pytest -m real_codex -q -s
```

The test writes raw and extracted responses under a temp request directory:

- `logs/codex_stdout.txt`
- `logs/codex_stderr.txt`
- `logs/codex_exit_code.txt`
- `logs/extracted_response.json`

```bash
cd ~/Dev/ONI-AI
mkdir -p /tmp/oni-test
cat >/tmp/oni-test/state.json <<'JSON'
{"request_id":"manual_test","request_dir":".","state_path":"state.json","screenshot_path":"screenshot.png","requested_at_utc":"2026-02-13T00:00:00Z","context":{"cycle":1,"time_since_cycle_start":10.0,"time_in_cycles":1.0,"paused":true,"current_speed":1,"previous_speed":1,"real_time_since_startup_seconds":10.0,"unscaled_time_seconds":10.0},"duplicants":[]}
JSON
cat >/tmp/fake-codex <<'SCRIPT'
#!/usr/bin/env bash
echo '{"actions":[{"id":"t1","type":"set_speed","params":{"speed":2}}]}'
SCRIPT
chmod +x /tmp/fake-codex
ONI_AI_CODEX_CMD=/tmp/fake-codex uv run oni-ai-bridge
```

In another terminal:

```bash
curl -sS -X POST http://127.0.0.1:8765/analyze \
  -H 'Content-Type: application/json' \
  -d '{"request_dir":"/tmp/oni-test"}'
```

Expected output includes a JSON `actions` array.
