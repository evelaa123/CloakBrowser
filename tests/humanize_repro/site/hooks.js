// Main-world probes.
//
// Page scripts can observe anything that runs in the page's main JS world.
// These hooks record DOM reads that only a *main-world* evaluate makes.
// Playwright's built-in selector engines run in its utility world and the
// humanize layer's isolated world never touches these prototypes, so a clean
// run leaves window.__mainWorldHits empty.
(function () {
  const hits = [];
  window.__mainWorldHits = hits;
  const note = (probe, detail) => hits.push({ probe, detail: String(detail ?? '') });

  const origGetAttribute = Element.prototype.getAttribute;
  Element.prototype.getAttribute = function (name) {
    if (name === 'contenteditable') note('getAttribute(contenteditable)', this.id || this.tagName);
    return origGetAttribute.call(this, name);
  };

  const activeDesc = Object.getOwnPropertyDescriptor(Document.prototype, 'activeElement');
  Object.defineProperty(Document.prototype, 'activeElement', {
    configurable: true,
    get() {
      note('document.activeElement', '');
      return activeDesc.get.call(this);
    },
  });

  const origFromPoint = Document.prototype.elementFromPoint;
  Document.prototype.elementFromPoint = function (x, y) {
    note('elementFromPoint', `${x},${y}`);
    return origFromPoint.call(this, x, y);
  };

  const origQS = Document.prototype.querySelector;
  Document.prototype.querySelector = function (sel) {
    note('document.querySelector', sel);
    return origQS.call(this, sel);
  };
})();
