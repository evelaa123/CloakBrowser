"""macOS persona proof, isolated in its own module: sync Playwright allows only
one driver per thread, so this module gets its own browser process.

Select-all is chosen from the *host* OS (``sys.platform``), not from the
browser persona. On a Linux/Windows host with ``--fingerprint-platform=macos``
the browser expects Meta+A, so ``fill()`` appends to the old value.
"""

from __future__ import annotations

from typing import Iterator

import pytest

from .harness import Session, SiteServer, requires_repro, value

pytestmark = requires_repro


@pytest.fixture(scope="module")
def _module_session(server: SiteServer) -> Iterator[Session]:  # overrides conftest
    s = Session(server.url, args=["--fingerprint-platform=macos"])
    yield s
    s.close()


def test_fill_does_not_clear_under_macos_persona(session):
    stock = session.stock_page()
    assert stock.evaluate("navigator.platform") == "MacIntel", "CONTROL FAILED: persona is not macOS"
    stock.fill("#name", "Shaho")
    assert value(stock, "#name") == "Shaho", "CONTROL FAILED: stock fill under the macOS persona"

    page = session.human_page()
    page.fill("#name", "Shaho")
    got = value(page, "#name")
    assert got == "Shaho", (
        f"BUG: fill('#name', 'Shaho') over 'firstname' with the macOS persona -> {got!r}"
    )
