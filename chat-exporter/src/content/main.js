// Content-script entry (ES module, loaded by loader.js). Answers commands from the service
// worker and runs the activity tracker. Holds no state that must survive navigation.
import { chatGptIdFromUrl } from '../adapters/chatgpt/normalize.js';
import { claudeIdFromUrl } from '../adapters/claude/normalize.js';
import { serializeError, ErrorCode, ExportError } from '../core/util.js';
import * as chatgpt from './chatgpt-client.js';
import * as claude from './claude-client.js';
import { domAdapterFor } from './dom-adapters.js';
import { extractViaDom } from './dom-extract.js';
import { startActivityTracker } from './activity.js';

const host = location.hostname;
/** @type {import('../core/model.js').ServiceId | null} */
const service = host === 'chatgpt.com' || host === 'chat.openai.com' ? 'chatgpt' : host === 'claude.ai' ? 'claude' : null;
const client = service === 'chatgpt' ? chatgpt : claude;
const dom = service ? domAdapterFor(service) : null;

function currentChatId() {
  if (service === 'chatgpt') return chatGptIdFromUrl(location.href);
  if (service === 'claude') return claudeIdFromUrl(location.href);
  return null;
}

function pageTitle() {
  return document.title.replace(/\s*[-|–]\s*(ChatGPT|Claude)\s*$/i, '').trim();
}

function pageInfo() {
  return {
    service,
    url: location.href,
    chatId: currentChatId(),
    title: pageTitle(),
    generating: dom ? dom.isGenerating() : false,
    messageIds: dom ? dom.messageIds() : [],
    userMessageCount: dom ? dom.userMessageCount() : 0,
  };
}

/** @type {Record<string, (msg: any) => Promise<any> | any>} */
const commands = {
  ping: () => pageInfo(),
  listChats: (msg) => client.listChats({ stopBefore: msg.stopBefore, maxPages: msg.maxPages, includeProjects: msg.includeProjects }),
  async fetchChat(msg) {
    const data = await client.fetchChat(String(msg.id), { includeImages: Boolean(msg.includeImages), maxImageBytes: Number(msg.maxImageBytes) || 8 * 1024 * 1024 });
    return { ...data, page: pageInfo() };
  },
  async extractDom() {
    if (!service || !dom) throw new ExportError(ErrorCode.NOT_A_CHAT, 'Неподдерживаемый сайт');
    const chatId = currentChatId();
    if (!chatId) throw new ExportError(ErrorCode.NOT_A_CHAT, 'Во вкладке не открыт чат');
    return extractViaDom(dom, {
      service,
      chatId,
      title: pageTitle() || 'Без названия',
      url: location.href,
      onProgress: (text) => chrome.runtime.sendMessage({ type: 'content-progress', text }).catch(() => {}),
    });
  },
};

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (!msg || typeof msg.cmd !== 'string' || !(msg.cmd in commands)) return false;
  Promise.resolve()
    .then(() => commands[msg.cmd](msg))
    .then((data) => sendResponse({ ok: true, data }), (err) => sendResponse({ ok: false, error: serializeError(err) }));
  return true;
});

if (service && dom) startActivityTracker({ service, dom, currentChatId, pageTitle });
