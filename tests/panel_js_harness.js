/* 给 panel.js 造一个最小 DOM,在 node 里驱动它,观察:
 *   - 面板往 Python 发了什么(桥接调用)
 *   - <html> 上的 data-theme 是什么(主题三档的落地方式)
 *   - 分段控件里当前高亮的是哪一档
 *
 * 为什么要有这个:主题切换的前端逻辑(setTheme → data-theme 属性、
 * auto 时移除属性)是**纯 JS**,Python 侧测不到 —— 而"强制白天被系统
 * 深色压回去"这种错,只有真正跑一遍 JS 才抓得住。
 *
 * 用法:node panel_js_harness.js '<json 步骤>'
 *   步骤:{"op":"call","fn":"setTheme","args":["light"]}   调前端方法
 *         {"op":"click","group":"themeSeg","index":2}       点第 3 个分段项
 *         {"op":"toggle","id":"previewToggle","checked":false}  拨开关
 * 输出:stdout 上一个 JSON
 *   {"calls":[{"name":...,"args":[...]}],
 *    "html":{"data-theme":"light"},
 *    "active":{"model":"0.6B","theme":"dark","position":"follow"},
 *    "preview":true}
 */
'use strict';

const fs = require('fs');
const path = require('path');

/* ---------------- 虚拟时钟 ---------------- */

let now = 0;
let timerSeq = 0;
const timers = [];

function setTimeoutFake(fn, ms) {
  timerSeq += 1;
  timers.push({ id: timerSeq, at: now + (ms || 0), fn: fn });
  return timerSeq;
}

function advance(ms) {
  const target = now + ms;
  for (;;) {
    let idx = -1;
    for (let i = 0; i < timers.length; i++) {
      if (timers[i].at > target) {
        continue;
      }
      if (idx < 0 || timers[i].at < timers[idx].at
          || (timers[i].at === timers[idx].at && timers[i].id < timers[idx].id)) {
        idx = i;
      }
    }
    if (idx < 0) {
      break;
    }
    const timer = timers.splice(idx, 1)[0];
    now = timer.at;
    timer.fn();
  }
  now = target;
}

/* ---------------- 最小 DOM ---------------- */

const els = {};
const listeners = {};
const calls = [];

function makeClassList() {
  const set = new Set();
  return {
    add: (c) => set.add(c),
    remove: (...cs) => { cs.forEach((c) => set.delete(c)); },
    toggle: (c, force) => {
      const on = force === undefined ? !set.has(c) : !!force;
      if (on) {
        set.add(c);
      } else {
        set.delete(c);
      }
      return on;
    },
    contains: (c) => set.has(c),
    values: () => Array.from(set)
  };
}

function makeEl(id, attrs) {
  const el = {
    id: id,
    attrs: Object.assign({}, attrs || {}),
    textContent: '',
    checked: false,
    children: [],
    style: {},
    classList: makeClassList(),
    handlers: {},
    isSegItem: false,
    addEventListener: (type, fn) => { el.handlers[type] = fn; },
    getAttribute: (k) => (k in el.attrs ? el.attrs[k] : null),
    setAttribute: (k, v) => { el.attrs[k] = v; },
    removeAttribute: (k) => { delete el.attrs[k]; },
    closest: (sel) => (sel === '.seg-item' && el.isSegItem ? el : null),
    querySelectorAll: () => []
  };
  return el;
}

/* 分段控件:一组 .seg-item,点哪个由 closest('.seg-item') 决定 */
function makeSegGroup(id, items) {
  const group = makeEl(id);
  group.items = items.map((attrs) => {
    const item = makeEl(id + '-item-' + attrs.value, attrs);
    item.isSegItem = true;
    return item;
  });
  group.querySelectorAll = () => group.items;
  return group;
}

els.stateDot = makeEl('stateDot');
els.titlebar = makeEl('titlebar');
els.closeBtn = makeEl('closeBtn');
els.quitBtn = makeEl('quitBtn');
els.hotkeyLabel = makeEl('hotkeyLabel');
els.modelSeg = makeSegGroup('modelSeg', [
  { 'data-model': '0.6B', value: '0.6B' },
  { 'data-model': '1.7B', value: '1.7B' }
]);
els.themeSeg = makeSegGroup('themeSeg', [
  { 'data-theme-mode': 'auto', value: 'auto' },
  { 'data-theme-mode': 'light', value: 'light' },
  { 'data-theme-mode': 'dark', value: 'dark' }
]);
els.positionSeg = makeSegGroup('positionSeg', [
  { 'data-position-mode': 'follow', value: 'follow' },
  { 'data-position-mode': 'bottom', value: 'bottom' },
  { 'data-position-mode': 'custom', value: 'custom' }
]);
/* 悬浮球的位置档位:跟随输入 / 记住位置 / 常驻 三档 */
els.orbPositionSeg = makeSegGroup('orbPositionSeg', [
  { 'data-orb-position': 'follow', value: 'follow' },
  { 'data-orb-position': 'pinned', value: 'pinned' },
  { 'data-orb-position': 'custom', value: 'custom' }
]);
els.previewToggle = makeEl('previewToggle');
els.orbToggle = makeEl('orbToggle');
/* 「位置 / 球位置」两行(互斥显示,跟悬浮球开关联动) */
els.positionRow = makeEl('positionRow');
els.orbPositionRow = makeEl('orbPositionRow');

const htmlAttrs = {};

global.window = global;
global.setTimeout = setTimeoutFake;
global.clearTimeout = () => {};
global.requestAnimationFrame = (fn) => { fn(); return 1; };
global.cancelAnimationFrame = () => {};
global.addEventListener = (type, fn) => { listeners[type] = fn; };
global.document = {
  readyState: 'complete',
  body: { className: '' },
  documentElement: {
    setAttribute: (k, v) => { htmlAttrs[k] = v; },
    removeAttribute: (k) => { delete htmlAttrs[k]; },
    getAttribute: (k) => (k in htmlAttrs ? htmlAttrs[k] : null)
  },
  getElementById: (id) => els[id] || null,
  addEventListener: () => {}
};
global.pywebview = {
  api: {
    ready: () => { calls.push({ name: 'ready', args: [] }); return true; },
    switch_model: (tag) => { calls.push({ name: 'switch_model', args: [tag] }); },
    set_theme: (mode) => { calls.push({ name: 'set_theme', args: [mode] }); },
    set_capsule_position: (mode) => {
      calls.push({ name: 'set_capsule_position', args: [mode] });
    },
    set_preview: (on) => { calls.push({ name: 'set_preview', args: [on] }); },
    set_orb: (on) => { calls.push({ name: 'set_orb', args: [on] }); },
    set_orb_position: (mode) => {
      calls.push({ name: 'set_orb_position', args: [mode] });
    },
    close_panel: () => { calls.push({ name: 'close_panel', args: [] }); },
    quit_app: () => { calls.push({ name: 'quit_app', args: [] }); }
  }
};

/* ---------------- 跑 panel.js ---------------- */

const src = fs.readFileSync(
  path.join(__dirname, '..', 'src', 'qwen3_voice', 'ui', 'static', 'panel.js'),
  'utf8');
// 间接 eval:在全局作用域里跑,window.__qwen 才挂得上
(0, eval)(src);

const steps = JSON.parse(process.argv[2] || '[]');
for (const step of steps) {
  if (step.op === 'advance') {
    advance(step.ms || 0);
  } else if (step.op === 'call') {
    const fn = global.__qwen[step.fn];
    if (typeof fn !== 'function') {
      throw new Error('前端没有这个方法: ' + step.fn);
    }
    fn.apply(null, step.args || []);
  } else if (step.op === 'click') {
    const group = els[step.group];
    if (!group) {
      throw new Error('没有这个分段控件: ' + step.group);
    }
    const item = group.items[step.index];
    const handler = group.handlers['click'];
    if (!handler) {
      throw new Error('分段控件没绑 click: ' + step.group);
    }
    // 事件目标是那一项本身,closest('.seg-item') 会找到它
    handler({ target: item });
  } else if (step.op === 'toggle') {
    // 拨开关:先把 checked 设好,再触发 change
    const el = els[step.id];
    if (!el) {
      throw new Error('没有这个控件: ' + step.id);
    }
    el.checked = !!step.checked;
    const handler = el.handlers['change'];
    if (!handler) {
      throw new Error('控件没绑 change: ' + step.id);
    }
    handler({ target: el });
  } else if (step.op === 'fire') {
    const fn = listeners[step.event];
    if (typeof fn === 'function') {
      fn();
    }
  } else {
    throw new Error('不认识的步骤: ' + JSON.stringify(step));
  }
}

/* 当前高亮的是哪一档 */
function activeOf(group, attr) {
  const hit = group.items.filter((it) => it.classList.contains('is-active'));
  return hit.length ? hit[0].getAttribute(attr) : null;
}

process.stdout.write(JSON.stringify({
  calls: calls,
  html: htmlAttrs,
  active: {
    model: activeOf(els.modelSeg, 'data-model'),
    theme: activeOf(els.themeSeg, 'data-theme-mode'),
    position: activeOf(els.positionSeg, 'data-position-mode'),
    orbPosition: activeOf(els.orbPositionSeg, 'data-orb-position')
  },
  preview: !!els.previewToggle.checked,
  orb: !!els.orbToggle.checked,
  rows: {
    position: !!els.positionRow.hidden,
    orbPosition: !!els.orbPositionRow.hidden
  }
}));
