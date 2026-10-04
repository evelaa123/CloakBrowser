"""Shared harness for the real-browser humanize repro suite.

Every test drives the real CloakBrowser binary against the static pages in
``site/`` and compares the humanized page against a *stock* Playwright page
from the same browser process:

* ``human`` context -- patched with ``cloakbrowser.human.patch_context``, the
  same call ``launch(humanize=True)`` makes via ``patch_browser``;
* ``stock`` context -- untouched, used as the reference ("control").

Run:  CLOAKBROWSER_HUMANIZE_REPRO=1 pytest tests/humanize_repro -v -rA
"""

from __future__ import annotations

import functools
import http.server
import os
import socketserver
import threading
import time
from pathlib import Path
from typing import Any, Iterator

import pytest

SITE_DIR = Path(__file__).parent / "site"

ENABLED = os.environ.get("CLOAKBROWSER_HUMANIZE_REPRO") == "1"
requires_repro = pytest.mark.skipif(
    not ENABLED, reason="real-browser repro suite; set CLOAKBROWSER_HUMANIZE_REPRO=1"
)

# Fast, deterministic humanize settings: same code paths, no long sleeps, no
# random typos. Tests that need specific behaviour override per context/call.
FAST: dict[str, Any] = {
    "mistype_chance": 0.0,
    "field_switch_delay": (0, 0),
    "typing_delay": 5,
    "typing_delay_spread": 0,
    "typing_pause_chance": 0.0,
    "key_hold": (5, 5),
    "shift_down_delay": (5, 5),
    "shift_up_delay": (5, 5),
    "mouse_min_steps": 4,
    "mouse_max_steps": 4,
    "mouse_burst_pause": (0, 0),
    "mouse_overshoot_chance": 0.0,
    "click_aim_delay_input": (5, 5),
    "click_aim_delay_button": (5, 5),
    "click_hold_input": (20, 20),
    "click_hold_button": (20, 20),
    "idle_between_actions": False,
    "scroll_pause_fast": (5, 5),
    "scroll_pause_slow": (5, 5),
    "scroll_settle_delay": (50, 50),
    "scroll_pre_move_delay": (5, 5),
    "scroll_overshoot_chance": 0.0,
}


class _QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args: Any) -> None:  # pragma: no cover
        pass


class SiteServer:
    """Serve ``site/`` on 127.0.0.1 with a random port."""

    def __init__(self) -> None:
        handler = functools.partial(_QuietHandler, directory=str(SITE_DIR))
        self._srv = socketserver.ThreadingTCPServer(("127.0.0.1", 0), handler)
        self._srv.daemon_threads = True
        self.url = f"http://127.0.0.1:{self._srv.server_address[1]}/"
        threading.Thread(target=self._srv.serve_forever, daemon=True).start()

    def close(self) -> None:
        self._srv.shutdown()
        self._srv.server_close()


class Session:
    """One CloakBrowser process with a humanized and a stock context."""

    def __init__(self, url: str, args: list[str] | None = None) -> None:
        from cloakbrowser import launch

        self.url = url
        self.browser = launch(headless=True, humanize=False, args=args or [])
        self.stock_ctx = self.browser.new_context()
        self.human_ctx = self.human_context()

    def human_context(self, overrides: dict[str, Any] | None = None,
                      preset: str = "default", fast: bool = True) -> Any:
        from cloakbrowser.human import patch_context
        from cloakbrowser.human.config import resolve_config

        ctx = self.browser.new_context()
        merged: dict[str, Any] = dict(FAST) if fast else {}
        merged.update(overrides or {})
        patch_context(ctx, resolve_config(preset, merged))  # type: ignore[arg-type]
        return ctx

    def human_page(self, path: str = "index.html", ctx: Any = None) -> Any:
        page = (ctx or self.human_ctx).new_page()
        page.goto(self.url + path)
        return page

    def stock_page(self, path: str = "index.html") -> Any:
        page = self.stock_ctx.new_page()
        page.goto(self.url + path)
        return page

    def close(self) -> None:
        self.browser.close()


def events(target: Any, *types: str) -> list[dict]:
    """Recorded events (``site/recorder.js``), optionally filtered by type."""
    log = target.evaluate("window.__log.slice()")
    return [e for e in log if not types or e["t"] in types]


def reset(target: Any) -> None:
    target.evaluate("window.__reset(); window.__mainWorldHits.length = 0")


def main_world_hits(target: Any) -> list[dict]:
    return target.evaluate("window.__mainWorldHits.slice()")


def value(target: Any, selector: str) -> str:
    return target.evaluate(
        "s => { const e = document.querySelector(s);"
        " return e.isContentEditable ? e.textContent : e.value; }",
        selector,
    )


def timed(fn: Any) -> tuple[float, BaseException | None]:
    """Run ``fn``; return (seconds, exception-or-None)."""
    t0 = time.monotonic()
    try:
        fn()
    except Exception as exc:  # noqa: BLE001 - we report whatever happened
        return time.monotonic() - t0, exc
    return time.monotonic() - t0, None


def short(exc: BaseException | None) -> str:
    if exc is None:
        return "no exception"
    return f"{type(exc).__name__}: {str(exc).splitlines()[0][:160]}"


@pytest.fixture(scope="module")
def server() -> Iterator[SiteServer]:
    srv = SiteServer()
    yield srv
    srv.close()


@pytest.fixture(scope="module")
def _module_session(server: SiteServer) -> Iterator[Session]:
    s = Session(server.url)
    yield s
    s.close()


@pytest.fixture
def session(_module_session: Session) -> Iterator[Session]:
    """Module-scoped browser; pages opened by a test are closed afterwards so
    focus, scroll and listeners never leak between tests."""
    yield _module_session
    for ctx in (_module_session.stock_ctx, _module_session.human_ctx):
        for page in list(ctx.pages):
            try:
                page.close()
            except Exception:
                pass
