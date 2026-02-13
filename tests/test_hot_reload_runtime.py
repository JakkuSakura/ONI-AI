import hashlib
import os
import subprocess
import time
import uuid
from pathlib import Path

import pytest


HOT_RELOAD_ENV_FLAG = "ONI_AI_RUN_HOT_RELOAD_TEST"


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


@pytest.mark.hot_reload
def test_runtime_hot_reload_watcher_updates_installed_dll() -> None:
    if os.getenv(HOT_RELOAD_ENV_FLAG) != "1":
        pytest.skip(f"Set {HOT_RELOAD_ENV_FLAG}=1 to run this integration test")

    repo_root = Path(__file__).resolve().parents[1]
    runtime_source = repo_root / "runtime" / "OniAiRuntime.cs"

    oni_mods_dir = Path(os.getenv("ONI_MODS_DIR", str(Path.home() / "Library/Application Support/unity.Klei.Oxygen Not Included/mods")))
    installed_runtime = oni_mods_dir / "local" / "jakku.oni_ai_assistant" / "runtime" / "OniAiRuntime.dll"

    original_source = runtime_source.read_text(encoding="utf-8")
    marker = f"hot-reload-{uuid.uuid4().hex[:8]}"
    patched_source = original_source.replace(
        'public string RuntimeId => "default-runtime-v1";',
        f'public string RuntimeId => "{marker}";',
        1,
    )

    watcher: subprocess.Popen[str] | None = None
    try:
        runtime_source.write_text(patched_source, encoding="utf-8")

        subprocess.run(["./scripts/build.sh"], cwd=repo_root, check=True)
        subprocess.run(["./scripts/install.sh"], cwd=repo_root, check=True)

        if not installed_runtime.exists():
            raise AssertionError(f"Missing installed runtime DLL: {installed_runtime}")

        baseline_hash = sha256(installed_runtime)

        watcher = subprocess.Popen(
            ["./scripts/hot_reload_runtime.sh"],
            cwd=repo_root,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
        )

        deadline = time.time() + 45
        changed = False
        while time.time() < deadline:
            time.sleep(1)
            if installed_runtime.exists() and sha256(installed_runtime) != baseline_hash:
                changed = True
                break

        if not changed:
            watcher_output = ""
            if watcher.stdout is not None:
                watcher_output = watcher.stdout.read()
            raise AssertionError(f"Runtime DLL hash did not change in time. Watcher output:\n{watcher_output}")
    finally:
        if watcher is not None and watcher.poll() is None:
            watcher.terminate()
            try:
                watcher.wait(timeout=5)
            except subprocess.TimeoutExpired:
                watcher.kill()

        runtime_source.write_text(original_source, encoding="utf-8")
        subprocess.run(["./scripts/build_runtime.sh"], cwd=repo_root, check=True)
