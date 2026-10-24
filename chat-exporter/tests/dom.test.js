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
