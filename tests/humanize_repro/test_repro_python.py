"""Real-browser proofs for the humanize review (Python sync wrapper).

Convention for every test:

* ``control(...)`` -- the same action on a stock Playwright page. A control
  failure means the *test* is invalid, not the wrapper.
* ``bug(...)``     -- the humanized page must behave like the control. A
  failure (message starts with ``BUG:``) confirms the reported problem.

Run:  CLOAKBROWSER_HUMANIZE_REPRO=1 pytest tests/humanize_repro -v -rA
"""

from __future__ import annotations

import statistics

import pytest

from .harness import (  # noqa: F401
    events, main_world_hits, requires_repro, reset, short, timed, value,
)

pytestmark = requires_repro


def control(cond: bool, msg: str) -> None:
    assert cond, f"CONTROL FAILED (test is invalid): {msg}"


def bug(cond: bool, msg: str) -> None:
    assert cond, f"BUG: {msg}"


def _frame(page):
    f = page.frame(name="f")
    f.wait_for_load_state()
    return f


# ---------------------------------------------------------------------------
# 1. Text input
# ---------------------------------------------------------------------------

def test_mistype_on_masked_field_deletes_real_character(session):
    """#573 generalised. The page rejects the typo, so the correcting
    Backspace deletes a real character. The digit-only fix covers number
    inputs; any input mask/filter (card, IBAN, hex, phone) still breaks."""
    stock = session.stock_page()
    stock.fill("#hex", "ab")
    control(value(stock, "#hex") == "ab", "stock fill('#hex','ab')")

    ctx = session.human_context({"mistype_chance": 1.0})
    page = session.human_page(ctx=ctx)
    # 'a' and 'b' only have non-hex neighbours (sqwz / vghn): every typo is
    # stripped by the page, deterministically.
    page.fill("#hex", "ab")
    got = value(page, "#hex")
    ctx.close()
    bug(got == "ab", f"fill('#hex', 'ab') with typos enabled produced {got!r}")


@pytest.mark.parametrize("selector,val", [
    ("#date", "2024-01-31"),
    ("#range", "80"),
    ("#color", "#ff0000"),
])
def test_fill_non_text_inputs(session, selector, val):
    stock = session.stock_page()
    stock.fill(selector, val)
    control(value(stock, selector) == val, f"stock fill {selector}")

    page = session.human_page()
    _, exc = timed(lambda: page.fill(selector, val, timeout=5000))
    got = value(page, selector)
    bug(exc is None and got == val,
        f"fill({selector!r}, {val!r}) -> value {got!r} ({short(exc)})")


def test_element_handle_press_does_not_focus_element(session):
    def run(page):
        page.focus("#fa")
        page.query_selector("#fb").press("x")
        return value(page, "#fa"), value(page, "#fb")

    control(run(session.stock_page()) == ("", "x"), "stock handle.press")
    got = run(session.human_page())
    bug(got == ("", "x"),
        f"handle('#fb').press('x') -> #fa={got[0]!r}, #fb={got[1]!r}")


def test_press_sequentially_inserts_at_click_caret(session):
    """Playwright focuses the field (caret at the end); the humanized path
    clicks into the left part of it, so the caret lands mid-text."""
    text = "hello world hello world hello"
    stock = session.stock_page()
    stock.evaluate("t => document.querySelector('#long').value = t", text)
    stock.locator("#long").press_sequentially("X")
    expected = value(stock, "#long")
    control(expected in ("X" + text, text + "X"), f"stock result {expected!r}")

    results = set()
    page = session.human_page()
    for _ in range(4):
        page.evaluate(
            "t => { const e = document.querySelector('#long'); e.value = t; e.blur(); }", text)
        page.locator("#long").press_sequentially("X")
        results.add(value(page, "#long"))
    bug(results == {expected}, f"expected {expected!r}, got {sorted(results)}")


# ---------------------------------------------------------------------------
# 2. Silently dropped options
# ---------------------------------------------------------------------------

def test_trial_click_must_not_click(session):
    stock = session.stock_page()
    reset(stock)
    stock.click("#btn", trial=True)
    control(events(stock, "click") == [], "stock trial click dispatches nothing")

    page = session.human_page()
    reset(page)
    page.click("#btn", trial=True)
    clicks = events(page, "click")
    bug(clicks == [], f"click(trial=True) dispatched {len(clicks)} real click(s)")


def test_locator_trial_click_must_not_click(session):
    page = session.human_page()
    reset(page)
    page.locator("#btn").click(trial=True)
    clicks = events(page, "click")
    bug(clicks == [], f"locator.click(trial=True) dispatched {len(clicks)} click(s)")


def test_right_click_button_option(session):
    stock = session.stock_page()
    reset(stock)
    stock.click("#btn", button="right")
    control([e["button"] for e in events(stock, "mousedown")] == [2], "stock right click")

    page = session.human_page()
    reset(page)
    page.click("#btn", button="right")
    buttons = [e["button"] for e in events(page, "mousedown")]
    menus = len(events(page, "contextmenu"))
    bug(buttons == [2] and menus == 1,
        f"click(button='right') -> mousedown buttons {buttons}, contextmenu x{menus}")


def test_click_count_option(session):
    stock = session.stock_page()
    reset(stock)
    stock.click("#btn", click_count=2)
    control(len(events(stock, "dblclick")) == 1, "stock click_count=2 -> dblclick")

    page = session.human_page()
    reset(page)
    page.click("#btn", click_count=2)
    bug(len(events(page, "dblclick")) == 1,
        f"click(click_count=2) -> click x{len(events(page, 'click'))}, "
        f"dblclick x{len(events(page, 'dblclick'))}")


def test_modifiers_option(session):
    stock = session.stock_page()
    reset(stock)
    stock.click("#btn", modifiers=["Shift"])
    control(events(stock, "click")[0]["shift"] is True, "stock shift+click")

    page = session.human_page()
    reset(page)
    page.click("#btn", modifiers=["Shift"])
    clicks = events(page, "click")
    bug(bool(clicks) and clicks[0]["shift"] is True,
        f"click(modifiers=['Shift']) -> shiftKey={clicks[0]['shift'] if clicks else None}")


def test_position_option(session):
    def run(page):
        reset(page)
        box = page.locator("#btn").bounding_box()
        page.click("#btn", position={"x": 3, "y": 3})
        c = events(page, "click")[0]
        return round(c["x"] - box["x"]), round(c["y"] - box["y"])

    # Playwright measures position from the padding box, so compare with stock.
    expected = run(session.stock_page())
    control(expected[0] < 10 and expected[1] < 10, f"stock position click at {expected}")
    got = run(session.human_page())
    bug(got == expected, f"click(position={{x:3,y:3}}) landed at offset {got}, stock {expected}")


def test_mouse_click_button_and_count(session):
    def run(page):
        reset(page)
        b = page.locator("#btn").bounding_box()
        x, y = b["x"] + b["width"] / 2, b["y"] + b["height"] / 2
        page.mouse.click(x, y, button="right")
        page.mouse.click(x, y, click_count=2)
        return [e["button"] for e in events(page, "mousedown")], len(events(page, "dblclick"))

    expected = ([2, 0, 0], 1)  # click_count=2 is two mousedowns (detail 1, 2)
    control(run(session.stock_page()) == expected, "stock mouse.click options")
    got = run(session.human_page())
    bug(got == expected,
        f"mouse.click(button='right'); mouse.click(click_count=2) -> "
        f"mousedown buttons {got[0]}, dblclick x{got[1]}")


def test_mouse_move_steps_option(session):
    def run(page):
        page.mouse.move(10, 10)
        reset(page)
        page.mouse.move(300, 200, steps=1)
        return len(events(page, "mousemove"))

    control(run(session.stock_page()) == 1, "stock mouse.move(steps=1)")
    got = run(session.human_page())
    bug(got == 1, f"mouse.move(..., steps=1) produced {got} mousemove events")


def test_set_default_timeout_is_ignored(session):
    """The element appears after 3 s; a 1 s default timeout must fail fast."""
    def run(page):
        page.set_default_timeout(1000)
        page.evaluate("""() => setTimeout(() => {
            const b = document.createElement('button'); b.id = 'late'; b.textContent = 'late';
            document.body.prepend(b); }, 3000)""")
        return timed(lambda: page.click("#late"))

    dur, exc = run(session.stock_page())
    control(exc is not None and dur < 2.0, f"stock fails in {dur:.1f}s")

    dur, exc = run(session.human_page())
    bug(exc is not None and dur < 2.0,
        f"set_default_timeout(1000) ignored: click took {dur:.1f}s ({short(exc)})")


def test_timeout_zero_means_no_timeout(session):
    stock = session.stock_page()
    _, exc = timed(lambda: stock.click("#btn", timeout=0))
    control(exc is None, f"stock click(timeout=0): {short(exc)}")

    page = session.human_page()
    _, exc = timed(lambda: page.click("#btn", timeout=0))
    bug(exc is None, f"click(timeout=0) -> {short(exc)}")


# ---------------------------------------------------------------------------
# 3. Strictness
# ---------------------------------------------------------------------------

def test_strict_mode_violation_is_swallowed(session):
    stock = session.stock_page()
    _, exc = timed(lambda: stock.locator(".dup").click(timeout=3000))
    control(exc is not None and "strict mode violation" in str(exc), f"stock: {short(exc)}")

    page = session.human_page()
    reset(page)
    _, exc = timed(lambda: page.locator(".dup").click(timeout=3000))
    hit = [e["target"] for e in events(page, "click")]
    bug(exc is not None, f"ambiguous locator('.dup').click() silently clicked {hit}")


# ---------------------------------------------------------------------------
# 4. Selector coverage (#522) and where Playwright resolves selectors
# ---------------------------------------------------------------------------

UNSUPPORTED = {
    "get_by_role": lambda p: p.get_by_role("button", name="Press me").click(timeout=3000),
    "chained": lambda p: p.locator("#buttons").locator("#btn").click(timeout=3000),
    "filter": lambda p: p.locator("button").filter(has_text="Press me").click(timeout=3000),
    "visible_pseudo": lambda p: p.click("#btn:visible", timeout=3000),
    "frame_locator": lambda p: p.frame_locator("#frame").locator("#finput").click(timeout=3000),
}


@pytest.mark.parametrize("name", list(UNSUPPORTED))
def test_standard_locators_rejected(session, name):
    stock = session.stock_page()
    _, exc = timed(lambda: UNSUPPORTED[name](stock))
    control(exc is None, f"stock {name}: {short(exc)}")

    page = session.human_page()
    _, exc = timed(lambda: UNSUPPORTED[name](page))
    bug(exc is None, f"{name} -> {short(exc)}")


def test_playwright_builtin_engines_do_not_touch_main_world(session):
    """Supports the proposed #522 fix (expected to PASS): Playwright resolves
    role/text/has/visible in its *utility* world, so page hooks see nothing.
    A Playwright fallback for unsupported grammar would not leak to the page."""
    stock = session.stock_page()
    reset(stock)
    stock.get_by_role("button", name="Press me").click()
    stock.locator("button").filter(has_text="Press me").click()
    stock.locator("#btn:visible").click()
    hits = main_world_hits(stock)
    assert hits == [], f"Playwright touched the main world: {hits}"


# ---------------------------------------------------------------------------
# 5. Frames and ElementHandles: separate, weaker pipeline
# ---------------------------------------------------------------------------

def test_main_page_actions_read_nothing_in_main_world(session):
    """Baseline (expected to PASS): the selector pipeline is isolated-world."""
    page = session.human_page()
    reset(page)
    page.click("#btn")
    page.fill("#name", "x")
    page.press("#name", "a")
    assert main_world_hits(page) == []


def test_frame_actions_read_dom_in_main_world(session):
    page = session.human_page()
    frame = _frame(page)
    reset(frame)
    frame.click("#finput")
    frame.press("#finput", "a")
    hits = sorted({h["probe"] for h in main_world_hits(frame)})
    bug(hits == [], f"frame.click/press made page-observable reads in the frame: {hits}")


def test_element_handle_click_reads_dom_in_main_world(session):
    page = session.human_page()
    el = page.query_selector("#btn")
    reset(page)
    el.click()
    hits = sorted({h["probe"] for h in main_world_hits(page)})
    bug(hits == [], f"element_handle.click made page-observable reads: {hits}")


def test_frame_click_below_frame_fold_silently_misses(session):
    """The target sits below the frame's own fold. Stock scrolls the frame and
    clicks it. The humanized frame path scrolls only the outer page, presses
    the mouse outside the viewport and reports success."""
    stock = session.stock_page()
    sf = _frame(stock)
    reset(sf)
    _, exc = timed(lambda: sf.click("#fbottom", timeout=5000))
    control(exc is None and [e["target"] for e in events(sf, "click")] == ["fbottom"],
            f"stock frame.click('#fbottom'): {short(exc)}")

    page = session.human_page()
    frame = _frame(page)
    reset(frame)
    reset(page)
    _, exc = timed(lambda: frame.click("#fbottom", timeout=5000))
    frame_clicks = [e["target"] for e in events(frame, "click")]
    page_downs = [(e["target"], round(e["y"])) for e in events(page, "mousedown")]
    vh = page.evaluate("innerHeight")
    bug(frame_clicks == ["fbottom"] or exc is not None,
        f"frame.click('#fbottom') returned without error but the frame got clicks "
        f"{frame_clicks}; the mouse went down on {page_downs} (viewport height {vh}); "
        f"frame scrollY={frame.evaluate('scrollY')}")


def test_main_page_scroll_uses_wheel(session):
    """Reference for the test above (expected to PASS)."""
    page = session.human_page()
    page.set_viewport_size({"width": 800, "height": 300})
    reset(page)
    page.click("#covered-sec h4")
    assert len(events(page, "wheel")) > 0


def test_frame_click_skips_pointer_events_check(session):
    """Main page refuses to click a covered button; the frame path clicks the
    overlay and reports success."""
    stock = session.stock_page()
    _, exc = timed(lambda: _frame(stock).click("#fcovered", timeout=2000))
    control(exc is not None, "stock refuses the covered click in the frame")

    page = session.human_page()
    _, main_exc = timed(lambda: page.click("#covered", timeout=2000))
    control(main_exc is not None, f"humanized main page refuses covered click: {short(main_exc)}")

    frame = _frame(page)
    reset(frame)
    _, exc = timed(lambda: frame.click("#fcovered", timeout=2000))
    hit = [e["target"] for e in events(frame, "click")]
    bug(exc is not None, f"frame.click on a covered button succeeded; the click landed on {hit}")


def test_frame_type_retypes_whole_text_after_midway_failure(session, monkeypatch):
    """Fault injection: humanized typing fails after 3 characters; the frame
    wrapper falls back to native type() and types the whole text again."""
    import cloakbrowser.human as human

    page = session.human_page()
    frame = _frame(page)
    frame.evaluate("document.querySelector('#finput').value = ''")
    real = human.human_type

    def flaky(pg, raw, text, cfg, cdp_session=None):
        real(pg, raw, text[:3], cfg, cdp_session=cdp_session)
        raise RuntimeError("transient CDP error")

    monkeypatch.setattr(human, "human_type", flaky)
    frame.type("#finput", "secret")
    got = value(frame, "#finput")
    bug(got == "secret", f"frame.type('secret') after a mid-typing error -> {got!r}")


def test_element_handle_check_ignores_human_config(session):
    def hold_ms(page, fn):
        reset(page)
        fn()
        return events(page, "mouseup")[-1]["ts"] - events(page, "mousedown")[-1]["ts"]

    page = session.human_page()
    cfg = {"click_hold_input": (400, 400), "click_hold_button": (400, 400)}
    click_hold = hold_ms(page, lambda: page.query_selector("#name").click(human_config=cfg))
    control(click_hold > 300, f"handle.click honours human_config ({click_hold:.0f} ms)")
    check_hold = hold_ms(page, lambda: page.query_selector("#chk").check(human_config=cfg))
    bug(check_hold > 300, f"handle.check(human_config: hold 400 ms) held {check_hold:.0f} ms")


# ---------------------------------------------------------------------------
# 6. Behavioural signals
# ---------------------------------------------------------------------------

def test_locator_check_teleports_cursor_to_origin(session):
    ctx = session.human_context({
        "idle_between_actions": True,
        "idle_between_duration": (0.3, 0.3),
        "idle_pause_range": (20, 20),
    })
    page = session.human_page(ctx=ctx)
    page.mouse.move(500, 300)
    reset(page)
    page.locator("#chk").check()
    near_origin = [(e["x"], e["y"]) for e in events(page, "mousemove")
                   if e["x"] < 10 and e["y"] < 10]
    ctx.close()
    bug(near_origin == [], f"locator.check() moved the cursor to {near_origin[:5]} (page origin)")


def test_dblclick_event_sequence(session):
    def seq(page):
        reset(page)
        page.dblclick("#btn")
        return [(e["t"], e["detail"]) for e in events(page, "mousedown", "click", "dblclick")]

    expected = [("mousedown", 1), ("click", 1), ("mousedown", 2), ("click", 2), ("dblclick", 2)]
    control(seq(session.stock_page()) == expected, "stock dblclick sequence")
    got = seq(session.human_page())
    bug(got == expected, f"dblclick sequence {got}; a real double click is {expected}")


def test_uppercase_typo_is_typed_without_shift(session):
    ctx = session.human_context({"mistype_chance": 1.0})
    page = session.human_page(ctx=ctx)
    page.fill("#name", "")
    reset(page)
    page.locator("#name").press_sequentially("AB")
    unshifted = [e["key"] for e in events(page, "keydown")
                 if len(e["key"]) == 1 and e["key"].isupper() and not e["shift"]]
    ctx.close()
    bug(unshifted == [], f"uppercase keydowns with shiftKey=false: {unshifted}")


def test_non_ascii_has_no_key_events(session):
    """Signal, not a Playwright divergence: stock keyboard.type also uses
    insertText for these. A real keyboard emits keydown/keyup per character."""
    page = session.human_page()
    page.fill("#name", "")
    reset(page)
    page.locator("#name").press_sequentially("привет")
    kd = events(page, "keydown")
    inputs = sorted({str(e.get("inputType")) for e in events(page, "input")})
    bug(len(kd) > 0, f"typing 'привет' produced {len(kd)} keydown events, input types {inputs}")


def test_mousemove_arrives_in_zero_interval_bursts(session):
    """Claim from the review: 3-5 events with ~0 ms between them, then a pause.
    REFUTED in practice (this test PASSES): Chromium aligns mousemove to
    animation frames, so the page sees 2-35 ms gaps. Kept as a regression
    guard for the observable timing."""
    ctx = session.human_context(fast=False)
    page = session.human_page(ctx=ctx)
    page.mouse.move(100, 100)
    reset(page)
    page.mouse.move(900, 600)
    ts = [e["ts"] for e in events(page, "mousemove")]
    ctx.close()
    gaps = [b - a for a, b in zip(ts, ts[1:])]
    zero = sum(1 for g in gaps if g < 1.0) / max(1, len(gaps))
    bug(zero < 0.2, f"{zero:.0%} of {len(gaps)} mousemove intervals are <1 ms "
        f"(median {statistics.median(gaps):.2f} ms)")


# ---------------------------------------------------------------------------
# 7. Timeouts / error messages (#329)
# ---------------------------------------------------------------------------

def test_unreachable_element_overruns_timeout_with_misleading_error(session):
    stock = session.stock_page()
    _, exc = timed(lambda: stock.fill("#neg", "hello", timeout=3000))
    control(exc is None, f"stock fill on the off-viewport input: {short(exc)}")

    page = session.human_page()
    dur, exc = timed(lambda: page.fill("#neg", "hello", timeout=3000))
    msg = short(exc)
    bug(exc is None or (dur < 4.0 and "covered by <none>" not in msg),
        f"fill(timeout=3000) on an element at (-600,-600) took {dur:.1f}s -> {msg}")
