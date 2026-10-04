// Event recorder shared by every humanize repro page.
//
// Records input events (capture phase) into window.__log so the Python, JS and
// .NET suites can read exactly what the page observed. When the page is opened
// by hand it also shows a live log panel (add ?nolog to hide it).
(function () {
  const log = [];
  window.__log = log;
  window.__reset = () => { log.length = 0; render(); };

  const TYPES = [
    'pointerdown', 'pointerup', 'mousedown', 'mouseup', 'click', 'dblclick',
    'contextmenu', 'auxclick', 'mousemove', 'keydown', 'keyup', 'beforeinput',
    'input', 'wheel', 'focus',
  ];
  const idOf = (el) => {
    if (el === window) return 'window';
    if (!el || el === document) return 'document';
    return el.id || (el.tagName ? el.tagName.toLowerCase() : '?');
  };
  for (const type of TYPES) {
    document.addEventListener(type, (e) => {
      const rec = { t: type, ts: e.timeStamp, trusted: e.isTrusted, target: idOf(e.target) };
      if ('clientX' in e) { rec.x = e.clientX; rec.y = e.clientY; }
      if ('button' in e) rec.button = e.button;
      if ('detail' in e && type !== 'focus') rec.detail = e.detail;
      if ('key' in e) { rec.key = e.key; rec.code = e.code; }
      if ('shiftKey' in e) {
        rec.shift = e.shiftKey; rec.ctrl = e.ctrlKey; rec.meta = e.metaKey; rec.alt = e.altKey;
      }
      if ('inputType' in e) { rec.inputType = e.inputType; rec.data = e.data; }
      if (type === 'wheel') { rec.dx = e.deltaX; rec.dy = e.deltaY; }
      log.push(rec);
      if (type !== 'mousemove') render();
    }, true);
  }
  window.addEventListener('scroll', () => {
    log.push({ t: 'scroll', ts: performance.now(), sx: scrollX, sy: scrollY });
  }, true);

  let panel = null;
  function render() {
    if (!panel) return;
    const tail = log.filter((r) => r.t !== 'mousemove').slice(-14);
    panel.textContent = tail.map((r) => JSON.stringify(r)).join('\n');
  }
  window.addEventListener('DOMContentLoaded', () => {
    if (new URLSearchParams(location.search).has('nolog')) return;
    panel = document.createElement('pre');
    panel.id = '__panel';
    panel.style.cssText = 'position:fixed;right:0;bottom:0;width:420px;max-height:180px;' +
      'overflow:hidden;margin:0;font:10px monospace;background:#111;color:#0f0;' +
      'opacity:.85;pointer-events:none;z-index:2147483647';
    document.body.appendChild(panel);
    render();
  });
})();
