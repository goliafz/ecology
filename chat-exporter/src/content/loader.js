// Classic content script: loads the ES-module content code from the extension package.
// (Manifest content scripts cannot be modules; dynamic import() of a web-accessible
// extension file is the supported way to use modules in a content script.)
(() => {
  const flag = '__chatExporterContentLoaded';
  if (/** @type {any} */ (globalThis)[flag]) return;
  /** @type {any} */ (globalThis)[flag] = true;
  import(chrome.runtime.getURL('src/content/main.js')).catch((err) => {
    console.error('[Chat Exporter] content script failed to load', err);
  });
})();
