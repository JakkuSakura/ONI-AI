import json
import os
from urllib import error, request

import pytest


ONI_LIVE_ENV_FLAG = "ONI_AI_RUN_ONI_LIVE"
DEFAULT_BASE_URL = "http://127.0.0.1:8766"


def _http_json(url: str, method: str = "GET", body: dict | None = None) -> tuple[int, dict]:
    data = None
    headers: dict[str, str] = {}
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"

    req = request.Request(url, method=method, data=data, headers=headers)
    with request.urlopen(req, timeout=5) as response:
        return response.getcode(), json.loads(response.read().decode("utf-8"))


def _oni_base_url() -> str:
    return (os.getenv("ONI_AI_ONI_BASE_URL") or DEFAULT_BASE_URL).rstrip("/")


def _require_live_oni() -> str:
    if os.getenv(ONI_LIVE_ENV_FLAG) != "1":
        pytest.skip(f"Set {ONI_LIVE_ENV_FLAG}=1 to run live ONI HTTP tests")

    base_url = _oni_base_url()
    try:
        status, payload = _http_json(f"{base_url}/health")
    except (error.URLError, TimeoutError) as exception:
        pytest.fail(
            "Cannot reach ONI C# HTTP server. "
            f"Expected at {base_url}. Ensure game is open and paused. Error: {exception}"
        )

    assert status == 200
    assert payload.get("ok") is True
    return base_url


@pytest.mark.oni_live
def test_oni_http_state_reports_paused_context() -> None:
    base_url = _require_live_oni()

    status, payload = _http_json(f"{base_url}/state")
    assert status == 200

    state = payload.get("state")
    assert isinstance(state, dict)

    context = state.get("context")
    assert isinstance(context, dict)
    assert context.get("paused") is True


@pytest.mark.oni_live
def test_oni_http_post_speed_then_query_speed() -> None:
    base_url = _require_live_oni()

    post_status, post_payload = _http_json(
        f"{base_url}/speed",
        method="POST",
        body={"speed": 1},
    )
    assert post_status == 200
    assert post_payload.get("status") == "applied"
    assert post_payload.get("speed") == 1

    get_status, get_payload = _http_json(f"{base_url}/speed")
    assert get_status == 200
    assert get_payload.get("speed") == 1
    assert isinstance(get_payload.get("paused"), bool)


@pytest.mark.oni_live
def test_oni_http_post_priorities_then_query_priorities() -> None:
    base_url = _require_live_oni()

    post_status, post_payload = _http_json(
        f"{base_url}/priorities",
        method="POST",
        body={
            "priorities": [
                {
                    "duplicant_name": "Ada",
                    "values": {
                        "dig": 8,
                        "build": 6,
                    },
                }
            ]
        },
    )
    assert post_status == 200
    assert isinstance(post_payload.get("accepted"), int)

    get_status, get_payload = _http_json(f"{base_url}/priorities")
    assert get_status == 200

    priorities = get_payload.get("priorities")
    assert isinstance(priorities, list)
    assert len(priorities) >= 1
