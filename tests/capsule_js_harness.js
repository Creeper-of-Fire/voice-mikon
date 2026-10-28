/* 给 capsule.js 造一个最小 DOM,在 node 里驱动它,观察它往 Python 上报了
 * 哪些窗口区域。
 *
 * 为什么要有这个:前端的"节流合并"是**纯 JS 逻辑**(setTimeout + 尾沿触发),
 * Python 侧测不到;而节流最容易出的错就是"掐头"—— 中间几次变化被丢掉,
 * 窗口形状停在旧尺寸上。这个脚本就是用来盯住它的。
 *
 * 用法:node capule_js_harness.js '<json 步骤>'
 *   步骤:{"op":"advance","ms":100}          推进虚拟时钟,触发到期的定时器
 *         {"op":"call","fn":"setPreview","args":["你好"]}  调前端方法
 *         {"op":"fire","event":"resize"}    触发 window 的事件
 * 输出:stdout 上一个 JSON —— {"calls":[{"name":...,"args":[...]}]}
 *
 * 虚拟时钟是手搓的:setTimeout 不真的等,advance() 才推进 —— 测试不sleep,
 * 也不会因为真实计时器的抖动而 flaky。
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
    textContent: '',
    children: [],
    style: {},
    classList: makeClassList(),
    addEventListener: () => {},
    appendChild: (child) => { el.children.push(child); },
    getBoundingClientRect: () => rectOf(el)
  };
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
global.clearTimeout = () => {};
global.addEventListener = (type, fn) => { listeners[type] = fn; };
global.document = {
  readyState: 'complete',
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

process.stdout.write(JSON.stringify({ calls: calls }));
