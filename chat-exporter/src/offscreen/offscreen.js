// Offscreen document (reason BLOBS): turns bytes into blob: URLs for chrome.downloads.
// Only chrome.runtime is available here, so it talks to the service worker via messages.

/** @type {Set<string>} */
const urls = new Set();

chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
  if (!message || message.target !== 'offscreen') return false;
  try {
    if (message.type === 'createBlobUrl') {
      const binary = atob(message.base64);
      const bytes = new Uint8Array(binary.length);
      for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
      const url = URL.createObjectURL(new Blob([bytes], { type: message.mime || 'application/octet-stream' }));
      urls.add(url);
      sendResponse({ ok: true, url });
    } else if (message.type === 'revokeBlobUrl') {
      if (urls.delete(message.url)) URL.revokeObjectURL(message.url);
      sendResponse({ ok: true });
    } else {
      sendResponse({ ok: false, error: `unknown type ${message.type}` });
    }
  } catch (err) {
    sendResponse({ ok: false, error: err instanceof Error ? err.message : String(err) });
  }
  return false;
});
