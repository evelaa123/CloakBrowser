"""Why the humanize layer avoids Playwright's own element lookup.

Evidence for the maintainers' design (#108 / #110 / 1164eca), measured with a
page script that only uses what any anti-bot script can use:

* ``injected`` - Playwright installed its InjectedScript in the page's MAIN
  world: it registers capture listeners on ``window`` (mousedown, auxclick, ...)
  through the page's own ``EventTarget.prototype.addEventListener``. The call
  stack even names ``addHitTargetInterceptorListeners``.
* ``events``   - Playwright dispatched page-visible CustomEvents named
  ``__playwright_mark_target__`` / ``__playwright_reset_targets__``.

Expected results (PASS = the claim holds):

* stock Playwright lookups/actions ARE visible  -> reason for the isolated world;
* the humanized selector pipeline is NOT visible -> the design works;
* humanized ElementHandle / frame paths ARE visible -> real leaks (BUG).
"""

from __future__ import annotations

import pytest

from .harness import requires_repro

pytestmark = requires_repro

PROBE = """(() => {
  const add = EventTarget.prototype.addEventListener;
  window.__probe = { injected: 0, events: [], stack: '' };
  for (const n of ['__playwright_mark_target__', '__playwright_reset_targets__'])
    add.call(window, n, () => window.__probe.events.push(n), true);
  EventTarget.prototype.addEventListener = function (t, f, o) {
    if (this === window && t === 'auxclick') {
      window.__probe.injected++;
      window.__probe.stack = (new Error().stack || '').split('\\n').slice(1, 4).join(' | ');
    }
    return add.call(this, t, f, o);
  };
})()"""


def _observe(ctx, url, fn, in_frame=False):
    page = ctx.new_page()
    page.add_init_script(PROBE)
    page.goto(url + "index.html")
    target = page
    if in_frame:
        target = page.frame(name="f")
        target.wait_for_load_state()
    fn(target)
    probe = target.evaluate("window.__probe")
    page.close()
    return probe["injected"] > 0, sorted(set(probe["events"])), probe["stack"]


STOCK_VISIBLE = {
    "locator.bounding_box()": lambda p: p.locator("#btn").bounding_box(),
    "locator.wait_for()": lambda p: p.locator("#btn").wait_for(),
    "locator.evaluate()": lambda p: p.locator("#btn").evaluate("e => 1"),
    "page.query_selector()": lambda p: p.query_selector("#btn"),
    "locator.scroll_into_view_if_needed()": lambda p: p.locator("#btn").scroll_into_view_if_needed(),
    "get_by_role().bounding_box()": lambda p: p.get_by_role("button", name="Press me").bounding_box(),
    "locator.is_enabled()": lambda p: p.locator("#btn").is_enabled(),
    "locator.click(trial=True)": lambda p: p.locator("#btn").click(trial=True),
    "locator.hover(trial=True)": lambda p: p.locator("#btn").hover(trial=True),
    "locator.click()": lambda p: p.locator("#btn").click(),
    "locator.focus()": lambda p: p.locator("#name").focus(),
}


@pytest.mark.parametrize("name", list(STOCK_VISIBLE))
def test_stock_playwright_lookup_is_visible_to_the_page(session, name):
    """Evidence (expected PASS): every way Playwright resolves an element for
    geometry or checks leaves a trace a page script can read. This is why
    'let Playwright find the element' is NOT a stealth-safe refactor."""
    injected, events, _ = _observe(session.stock_ctx, session.url, STOCK_VISIBLE[name])
    assert injected or events, f"{name}: expected a page-visible trace, saw none"


def test_stock_playwright_injection_stack_names_playwright(session):
    """Evidence (expected PASS): the trace is attributable by name."""
    _, _, stack = _observe(session.stock_ctx, session.url, lambda p: p.locator("#btn").bounding_box())
    assert "addHitTargetInterceptorListeners" in stack, stack


@pytest.mark.parametrize("name,fn", [
    ("locator.count()", lambda p: p.locator("#btn").count()),
    ("locator.is_visible()", lambda p: p.locator("#btn").is_visible()),
])
def test_stock_calls_that_stay_invisible(session, name, fn):
    """Evidence (expected PASS): only these leave no trace - and neither returns
    geometry, so neither can drive a click."""
    injected, events, _ = _observe(session.stock_ctx, session.url, fn)
    assert not injected and not events, f"{name}: injected={injected} events={events}"


HUMAN_CLEAN = {
    "page.click": lambda p: p.click("#btn"),
    "page.fill": lambda p: p.fill("#name", "abc"),
    "page.check": lambda p: p.check("#chk"),
    "locator.click": lambda p: p.locator("#btn").click(),
    "locator.press": lambda p: p.locator("#name").press("a"),
}


@pytest.mark.parametrize("name", list(HUMAN_CLEAN))
def test_humanized_selector_pipeline_is_invisible(session, name):
    """The isolated-world design works (expected PASS)."""
    injected, events, _ = _observe(session.human_ctx, session.url, HUMAN_CLEAN[name])
    assert not injected and not events, f"{name}: injected={injected} events={events}"


def test_humanized_element_handle_click_leaks(session):
    injected, events, stack = _observe(
        session.human_ctx, session.url, lambda p: p.query_selector("#btn").click())
    assert not injected and not events, (
        f"BUG: element_handle.click is page-visible: injected={injected} events={events} ({stack[:120]})")


def test_humanized_frame_click_leaks(session):
    injected, events, stack = _observe(
        session.human_ctx, session.url, lambda f: f.click("#finput"), in_frame=True)
    assert not injected and not events, (
        f"BUG: frame.click is page-visible inside the frame: injected={injected} events={events} ({stack[:120]})")


def test_isolated_world_can_reach_frames_and_handles(session):
    """Feasibility for the fix (expected PASS): an isolated world can be created
    inside the iframe, and a node can be resolved into an isolated world by
    backendNodeId - both without any page-visible trace."""
    page = session.stock_ctx.new_page()
    page.add_init_script(PROBE)
    page.goto(session.url + "index.html")
    frame = page.frame(name="f")
    frame.wait_for_load_state()
    cdp = session.stock_ctx.new_cdp_session(page)
    tree = cdp.send("Page.getFrameTree")
    main_id = tree["frameTree"]["frame"]["id"]
    child_id = tree["frameTree"]["childFrames"][0]["frame"]["id"]

    fw = cdp.send("Page.createIsolatedWorld", {"frameId": child_id, "worldName": ""})
    val = cdp.send("Runtime.evaluate", {"contextId": fw["executionContextId"], "returnByValue": True,
                                        "expression": "document.querySelector('#finput').value"})
    assert val["result"]["value"] == "old"

    doc = cdp.send("DOM.getDocument", {"depth": 0})
    node = cdp.send("DOM.querySelector", {"nodeId": doc["root"]["nodeId"], "selector": "#btn"})
    backend = cdp.send("DOM.describeNode", {"nodeId": node["nodeId"]})["node"]["backendNodeId"]
    mw = cdp.send("Page.createIsolatedWorld", {"frameId": main_id, "worldName": ""})
    obj = cdp.send("DOM.resolveNode", {"backendNodeId": backend,
                                       "executionContextId": mw["executionContextId"]})["object"]
    hit = cdp.send("Runtime.callFunctionOn", {
        "objectId": obj["objectId"], "returnByValue": True,
        "functionDeclaration": "function(){ const b = this.getBoundingClientRect();"
                               " return document.elementFromPoint(b.x + 5, b.y + 5) === this; }"})
    assert hit["result"]["value"] is True

    for t in (page, frame):
        probe = t.evaluate("window.__probe")
        assert probe["injected"] == 0 and probe["events"] == [], probe
    page.close()
