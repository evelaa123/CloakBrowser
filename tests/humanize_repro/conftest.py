"""Every repro test gets a hard wall-clock limit so one hang can't stall the run."""

import pytest


def pytest_collection_modifyitems(items):
    for item in items:
        if "humanize_repro" in str(item.fspath) and item.get_closest_marker("timeout") is None:
            item.add_marker(pytest.mark.timeout(90))


# Fixtures live in harness.py; expose them to every module in this package.
from .harness import _module_session, server, session  # noqa: E402,F401
