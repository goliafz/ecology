import { test } from 'node:test';
import assert from 'node:assert/strict';
import { JSDOM } from 'jsdom';
import { htmlToMarkdown } from '../src/core/html-to-markdown.js';

/** @param {string} html */
function el(html) {
  const dom = new JSDOM(`<!DOCTYPE html><body><div id="root">${html}</div></body>`);
  return /** @type {Element} */ (dom.window.document.getElementById('root'));
}

test('htmlToMarkdown: headings, emphasis, links, lists', () => {
  const md = htmlToMarkdown(el(`
    <h2>Заголовок</h2><p>Текст <strong>жирный</strong> и <em>курсив</em>, <code>x = 1</code>,
    <a href="https://example.com/a">ссылка</a>, <a href="javascript:alert(1)">плохая</a>.</p>
    <ul><li>один</li><li>два<ul><li>вложенный</li></ul></li></ul>
    <ol start="3"><li>три</li><li>четыре</li></ol>`));
  assert.match(md, /^## Заголовок/);
  assert.match(md, /\*\*жирный\*\* и \*курсив\*, `x = 1`/);
  assert.match(md, /\[ссылка\]\(https:\/\/example.com\/a\)/);
  assert.ok(!md.includes('javascript:'));
  assert.match(md, /- один\n- два\n {3}- вложенный/);
  assert.match(md, /3\. три\n4\. четыре/);
});

test('htmlToMarkdown: code blocks keep language and content, UI buttons dropped', () => {
  const md = htmlToMarkdown(el(`<pre><div>python<button>Copy code</button></div><code class="language-python hljs">def f():\n    return "\`\`\`"\n</code></pre>`));
  assert.equal(md, '````python\ndef f():\n    return "```"\n````');
});

test('htmlToMarkdown: tables, blockquotes, KaTeX, details', () => {
  const md = htmlToMarkdown(el(`
    <table><thead><tr><th>A</th><th>B|C</th></tr></thead><tbody><tr><td>1</td><td>2</td></tr></tbody></table>
    <blockquote><p>цитата</p></blockquote>
    <span class="katex"><span class="katex-mathml"><math><semantics><annotation encoding="application/x-tex">E=mc^2</annotation></semantics></math></span><span aria-hidden="true">E=mc2</span></span>
    <details><summary>Ещё</summary><p>скрытое</p></details>`));
  assert.match(md, /\| A \| B\\\|C \|\n\| --- \| --- \|\n\| 1 \| 2 \|/);
  assert.match(md, /> цитата/);
  assert.match(md, /\$E=mc\^2\$/);
  assert.match(md, /<details>\n<summary>Ещё<\/summary>\n\nскрытое\n\n<\/details>/);
});

test('htmlToMarkdown: images via callback, sr-only text skipped', () => {
  const root = el('<p><img src="https://x/y.png" alt="схема"><span class="sr-only">скрыто</span><img src="data:image/png;base64,AAA=" alt="d"></p>');
  const md = htmlToMarkdown(root, { imageSrc: (img) => (img.getAttribute('src') || '').startsWith('data:') ? img.getAttribute('src') : null });
  assert.equal(md, '*[Изображение: схема]*![d](data:image/png;base64,AAA=)');
});

test('DOM adapters: ChatGPT and Claude message discovery', async () => {
  const dom = new JSDOM(`<!DOCTYPE html><body>
    <div data-message-author-role="user" data-message-id="u1"><div class="whitespace-pre-wrap">Вопрос</div></div>
    <div data-message-author-role="assistant" data-message-id="a1"><div class="markdown"><p>Ответ</p></div></div>
    <div data-testid="user-message">Привет</div>
    <div class="font-claude-response"><p>Здравствуйте</p></div>
    <div data-is-streaming="true"></div>
  </body>`);
  const g = /** @type {any} */ (globalThis);
  g.document = dom.window.document;
  try {
    const { chatGptDom, claudeDom } = await import('../src/content/dom-adapters.js');
    const gpt = chatGptDom.messages();
    assert.deepEqual(gpt.map((m) => [m.role, m.id, m.key, m.text]), [['user', 'u1', 'id:u1', 'Вопрос'], ['assistant', 'a1', 'id:a1', 'Ответ']]);
    assert.deepEqual(chatGptDom.messageIds(), ['u1', 'a1']);
    assert.equal(chatGptDom.userMessageCount(), 1);
    const cl = claudeDom.messages();
    assert.deepEqual(cl.map((m) => m.role), ['user', 'assistant']);
    assert.ok(cl[0].key.startsWith('user:'));
    assert.equal(claudeDom.isGenerating(), true);
    assert.equal(chatGptDom.isGenerating(), false);
  } finally {
    delete g.document;
  }
});

test('ChatGPT fallback selectors and diagnostics probe (no text in the probe)', async () => {
  const dom = new JSDOM(`<!DOCTYPE html><body><main>
    <article data-testid="conversation-turn-1" data-turn="user"><div class="whitespace-pre-wrap">Покажи как составлен промт</div></article>
    <article data-testid="conversation-turn-2" data-turn="assistant"><div class="markdown"><p>Ответ</p></div></article>
  </main></body>`);
  const g = /** @type {any} */ (globalThis);
  g.document = dom.window.document;
  g.NodeFilter = dom.window.NodeFilter;
  try {
    const { chatGptDom, domProbe } = await import('../src/content/dom-adapters.js');
    assert.deepEqual(chatGptDom.messages().map((m) => [m.role, m.text]), [['user', 'Покажи как составлен промт'], ['assistant', 'Ответ']]);
    assert.equal(chatGptDom.userMessageCount(), 1);
    const probe = domProbe('Покажи как составлен промт');
    assert.equal(probe.counts['[data-testid^="conversation-turn-"]'], 2);
    assert.match(probe.ancestors[0], /^<div class="whitespace-pre-wrap">$/);
    assert.match(probe.ancestors[1], /data-turn="user"/);
    assert.ok(!probe.ancestors.join('').includes('Покажи'), 'probe contains no message text');
  } finally {
    delete g.document;
    delete g.NodeFilter;
  }
});

test('ChatGPT markup from the diagnostics of a real chat (October 2026)', async () => {
  // Attribute structure copied from the reported domProbe (text replaced); the assistant unit is
  // assumed by analogy with the user unit.
  const dom = new JSDOM(`<!DOCTYPE html><body>
    <div data-content-search-turn-key="fallback-turn-0"><div class="flex flex-col gap-1.5"><div class="group flex flex-col" data-talvt-turn-state="complete">
      <div class="group/user-message flex flex-col items-end gap-2" data-chatgpt-search-unit-key="fallback-turn-0:0:user" data-chatgpt-search-message-ids="e45bb71d-a971-4f70-9ae0-1e1f65bb6fac">
        <div class="w-full" data-content-search-unit-key="fallback-turn-0:0:user"><div data-user-message-bubble="true"><div class="text-size-chat whitespace-pre-wrap" dir="auto">Покажи как составлен промт</div></div></div>
      </div>
      <div data-chatgpt-search-unit-key="fallback-turn-0:1:assistant" data-chatgpt-search-message-ids="ef577f8c-ed1e-470e-8aa4-346b33849a95">
        <div data-content-search-unit-key="fallback-turn-0:1:assistant"><pre><code class="language-markdown"># Промпт</code></pre></div>
      </div>
    </div></div></div>
  </body>`);
  const g = /** @type {any} */ (globalThis);
  g.document = dom.window.document;
  try {
    const { chatGptDom } = await import('../src/content/dom-adapters.js');
    const msgs = chatGptDom.messages();
    assert.deepEqual(msgs.map((m) => [m.role, m.id]), [['user', 'e45bb71d-a971-4f70-9ae0-1e1f65bb6fac'], ['assistant', 'ef577f8c-ed1e-470e-8aa4-346b33849a95']]);
    assert.equal(msgs[0].text, 'Покажи как составлен промт');
    assert.deepEqual(chatGptDom.messageIds(), ['e45bb71d-a971-4f70-9ae0-1e1f65bb6fac', 'ef577f8c-ed1e-470e-8aa4-346b33849a95']);
    assert.equal(chatGptDom.userMessageCount(), 1);
  } finally {
    delete g.document;
  }
});
