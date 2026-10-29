/* 给 capsule.js 造一个最小 DOM,在 node 里驱动它,观察它往 Python 上报了
 * 哪些窗口区域、<html> 上的 data-theme 是什么、以及滚动电平条的状态。
 *
 * 为什么要有这个:前端的"节流合并"、"主题切换"、"滚动电平条"都是
 * **纯 JS 逻辑**(setTimeout 尾沿触发 / 属性切换 / setInterval 推进),
 * Python 侧测不到;而这几处最容易出的错就是"掐头"—— 中间几次变化被丢掉,
 * 窗口形状停在旧尺寸上。这个脚本就是用来盯住它们的。
 *
 * 用法:node capsule_js_harness.js '<json 步骤>'
 *   步骤:{"op":"advance","ms":100}          推进虚拟时钟,触发到期的定时器
 *         {"op":"call","fn":"setPreview","args":["你好"]}  调前端方法
 *         {"op":"fire","event":"resize"}    触发 window 的事件
 * 输出:stdout 上一个 JSON —— {"calls":[{"name":...,"args":[...]}],
 *                              "html":{"data-theme":"light"},
 *                              "wave":[{"age":0,"offset":0,"height":"2.00px",
 *                                       "silent":true}, ...]}
 *
 * "wave" 是波形区里每根条的状态,按 age 升序(age=0 是**最新**的那根,
 * 也就是最靠右的一根;age 越大越靠左、越老)。
 *
 * 虚拟时钟是手搓的:setTimeout / setInterval 不真的等,advance() 才推进 ——
 * 测试不 sleep,也不会因为真实计时器的抖动而 flaky。
 */
'use strict';

const fs = require('fs');
const path = require('path');

/* ---------------- 虚拟时钟 ---------------- */

let now = 0;
let timerSeq = 0;
const timers = [];

/* requestAnimationFrame 的假实现:回调排队,advance() 里统一冲 */
const rafQueue = [];

function setTimeoutFake(fn, ms) {
  timerSeq += 1;
  timers.push({ id: timerSeq, at: now + (ms || 0), fn, repeat: 0 });
  return timerSeq;
}

function setIntervalFake(fn, ms) {
  timerSeq += 1;
  const period = ms || 1;
  timers.push({ id: timerSeq, at: now + period, fn, repeat: period });
  return timerSeq;
}

function clearTimerFake(id) {
  for (let i = 0; i < timers.length; i++) {
    if (timers[i].id === id) {
      timers.splice(i, 1);
      return;
    }
  }
}

function requestAnimationFrameFake(fn) {
  rafQueue.push(fn);
  return rafQueue.length;
}

function flushRaf() {
  while (rafQueue.length) {
    rafQueue.shift()();
  }
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
    // 周期定时器:到点了就续期(下一轮再排一次)
    if (timer.repeat > 0) {
      timer.at = now + timer.repeat;
      timers.push(timer);
    }
    // 动画帧在定时器回调之后冲 —— 浏览器里 rAF 也是这一帧的末尾才跑
    flushRaf();
  }
  now = target;
  flushRaf();
}

/* ---------------- 最小 DOM ---------------- */

const els = {};
const listeners = {};
const calls = [];

/* 条的步进 = 条宽 3px + 间隙 3px = 6px(与 capsule.js / capsule.css 一致)。
   这里只是把 translateX 的像素值换算成"第几根",方便断言。 */
const BAR_STEP = 6;

function makeClassList() {
  const set = new Set();
  return {
    add: (c) => set.add(c),
    remove: (c) => set.delete(c),
    toggle: (c, force) => {
      const on = force === undefined ? !set.has(c) : !!force;
      if (on) {
        set.add(c);
      } else {
        set.delete(c);
      }
      return on;
    },
    contains: (c) => set.has(c)
  };
}

function makeEl(id) {
  const el = {
    id: id,
    _text: '',
    children: [],
    style: {},
    classList: makeClassList(),
    addEventListener: () => {},
    appendChild: (child) => { el.children.push(child); },
    getBoundingClientRect: () => rectOf(el),
    offsetWidth: 0
  };
  // 写 textContent = 清掉子节点:波形区靠 `textContent = ''` 清场,
  // 假 DOM 得跟上,不然旧条会越积越多
  Object.defineProperty(el, 'textContent', {
    get() { return el._text; },
    set(v) {
      el._text = v == null ? '' : String(v);
      el.children.length = 0;
    }
  });
  return el;
}

/* 文字条的宽度跟着文字长度变,胶囊的宽度跟着阶段变 ——
 * 这样"报上去的是不是**最新**一次测量"才测得出来 */
function rectOf(el) {
  if (el.id === 'textbar') {
    const text = (els.textbarText && els.textbarText.textContent) || '';
    return { left: 40, top: 20, width: 100 + text.length * 10, height: 40 };
  }
  if (el.id === 'pill') {
    const recording = el.classList.contains('recording');
    return { left: 100, top: 120, width: recording ? 180 : 120, height: 40 };
  }
  return { left: 0, top: 0, width: 0, height: 0 };
}

['textbar', 'textbarText', 'pill', 'waveform', 'barLabel', 'barTimer',
  'cancelBtn', 'okBtn'].forEach((id) => { els[id] = makeEl(id); });

global.window = global;
global.setTimeout = setTimeoutFake;
global.clearTimeout = clearTimerFake;
global.setInterval = setIntervalFake;
global.clearInterval = clearTimerFake;
global.requestAnimationFrame = requestAnimationFrameFake;
global.addEventListener = (type, fn) => { listeners[type] = fn; };

/* <html> 的属性:主题就靠 data-theme 表达(auto 时这个属性被移除) */
const htmlAttrs = {};
global.document = {
  readyState: 'complete',
  documentElement: {
    setAttribute: (k, v) => { htmlAttrs[k] = v; },
    removeAttribute: (k) => { delete htmlAttrs[k]; },
    getAttribute: (k) => (k in htmlAttrs ? htmlAttrs[k] : null)
  },
  getElementById: (id) => els[id] || null,
  createElement: (tag) => makeEl('new-' + tag),
  addEventListener: () => {}
};
global.pywebview = {
  api: {
    ready: () => { calls.push({ name: 'ready', args: [] }); return true; },
    cancel_recording: () => { calls.push({ name: 'cancel_recording', args: [] }); },
    stop_recording: () => { calls.push({ name: 'stop_recording', args: [] }); },
    update_regions: (payload) => {
      calls.push({ name: 'update_regions', args: [payload] });
      return true;
    }
  }
};

/* ---------------- 波形区快照 ---------------- */

/* 把波形区里每根条的状态倒出来:
   age 越大 = 越老 = 越靠左(从右往左滚)。
   transform 是 translateX(-age × 6px),直接反映"往左滚了多少"。 */
function dumpWave() {
  const bars = (els.waveform && els.waveform.children) || [];
  const out = [];
  for (const bar of bars) {
    const t = bar.style.transform || '';
    const m = /translateX\((-?[\d.]+)px\)/.exec(t);
    const offset = m ? parseFloat(m[1]) : null;
    out.push({
      age: offset === null ? null : Math.round(-offset / BAR_STEP),
      offset: offset,
      height: bar.style.height || '',
      silent: bar.classList.contains('is-silent'),
      // 这根条被 tick 改写过没有:
      // 预填的条高度是整数 "2px",被 advanceBar 改写过的条是两位小数
      // "X.XXpx"(capsule.js 里前者写整数、后者走 toFixed(2))
      touched: /^\d+\.\d{2}px$/.test(bar.style.height || '')
    });
  }
  out.sort((a, b) => (a.age === null ? 1e9 : a.age) - (b.age === null ? 1e9 : b.age));
  return out;
}

/* ---------------- 跑 capsule.js ---------------- */

const src = fs.readFileSync(
  path.join(__dirname, '..', 'src', 'qwen3_voice', 'ui', 'static', 'capsule.js'),
  'utf8');
// 间接 eval:在全局作用域里跑,window.__capsule 才挂得上
(0, eval)(src);

const steps = JSON.parse(process.argv[2] || '[]');
for (const step of steps) {
  if (step.op === 'advance') {
    advance(step.ms || 0);
  } else if (step.op === 'call') {
    const fn = global.__capsule[step.fn];
    if (typeof fn !== 'function') {
      throw new Error('前端没有这个方法: ' + step.fn);
    }
    fn.apply(null, step.args || []);
  } else if (step.op === 'fire') {
    const fn = listeners[step.event];
    if (typeof fn === 'function') {
      fn();
    }
  } else {
    throw new Error('不认识的步骤: ' + JSON.stringify(step));
  }
}

process.stdout.write(JSON.stringify({
  calls: calls,
  html: htmlAttrs,
  wave: dumpWave()
}));
