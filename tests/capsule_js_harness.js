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
 *         {"op":"mouse","target":"pill","type":"mousedown",
 *               "screenX":100,"screenY":200}            在胶囊上按下鼠标
 *         {"op":"mouse","type":"mousemove","screenX":150,"screenY":260}
 *         {"op":"setCopyResult","value":false}  让「复制」按钮的调用返回失败
 * 输出:stdout 上一个 JSON —— {"calls":[{"name":...,"args":[...]}],
 *                              "html":{"data-theme":"light"},
 *                              "body":["no-preview"],
 *                              "pill":["recording","can-copy"],
 *                              "label":"已输入",
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

/* requestAnimationFrame 的假实现:回调排队,advance() 里统一冲。
   id 要稳定 —— cancelAnimationFrame 得能按 id 撤掉某一帧。 */
let rafSeq = 0;
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
  rafSeq += 1;
  rafQueue.push({ id: rafSeq, fn });
  return rafSeq;
}

function cancelAnimationFrameFake(id) {
  for (let i = 0; i < rafQueue.length; i++) {
    if (rafQueue[i].id === id) {
      rafQueue.splice(i, 1);
      return;
    }
  }
}

function flushRaf() {
  while (rafQueue.length) {
    rafQueue.shift().fn();
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
    // remove 接受多个类名(和 DOM 的 classList.remove 一样)
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

function makeEl(id) {
  const el = {
    id: id,
    _text: '',
    children: [],
    style: {},
    classList: makeClassList(),
    handlers: {},
    isButton: false,
    parent: null,           // 事件冒泡链(按钮的父级是胶囊)
    // 模拟中的 CSS transform:{scale, dx, dy}。
    // 真实浏览器里它会让 getBoundingClientRect() 的返回值缩放/偏移,
    // 但 offset* 系列**不受影响** —— capsule.js 的 rectOf 正是靠这个
    // 区别,量出"未变换"的矩形(见 capsule.js 里 rectOf 的说明)。
    _transform: null,
    addEventListener: (type, fn) => { el.handlers[type] = fn; },
    appendChild: (child) => { el.children.push(child); },
    getBoundingClientRect: () => rectOf(el),
    // 拖拽时用来判断"点的是不是按钮"(点在 ✕/✓ 上不触发拖动)
    closest: (sel) => (sel === 'button' && el.isButton ? el : null)
  };
  // offset* 系列:布局尺寸/位置,不受 transform 影响。
  // 真实浏览器里 capsule.js 的 rectOf 优先用这一组 ——
  // getBoundingClientRect() 会把 transform 算进去,量到的是"动画中"的
  // 矩形,Python 按它裁窗口(SetWindowRgn)会把胶囊描边的两端切掉。
  Object.defineProperties(el, {
    offsetWidth: { get() { return rectOf(el, true).width; } },
    offsetHeight: { get() { return rectOf(el, true).height; } },
    offsetLeft: { get() { return rectOf(el, true).left; } },
    offsetTop: { get() { return rectOf(el, true).top; } }
  });
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
 * 这样"报上去的是不是**最新**一次测量"才测得出来。
 *
 * layout=true 时返回**未变换**的矩形(offset* 系列该拿到的值);
 * 否则返回**带 transform** 的矩形 —— 真实浏览器里
 * getBoundingClientRect() 就是这个语义。 */
function rectOf(el, layout) {
  let r;
  if (el.id === 'textbar') {
    const text = (els.textbarText && els.textbarText.textContent) || '';
    r = { left: 40, top: 20, width: 100 + text.length * 10, height: 40 };
  } else if (el.id === 'pill') {
    const recording = el.classList.contains('recording');
    if (bodyClasses.contains('orb-mode') && bodyClasses.contains('orb-idle')) {
      // 球形态:胶囊收成 40×40 的正圆 —— 和展开态**同一横坐标**
      // (窗口底边居中,见 placement.py 的 orb_inset_x)。
      // 展开态胶囊是 (100,120,180,40),中心 x = 190 → 球 = (170,120,40,40)。
      r = { left: 170, top: 120, width: 40, height: 40 };
    } else {
      r = { left: 100, top: 120, width: recording ? 180 : 120, height: 40 };
    }
  } else {
    r = { left: 0, top: 0, width: 0, height: 0 };
  }
  if (layout || !el._transform) {
    return r;
  }
  // 带上 transform:scale 绕中心缩放 + translate 偏移 ——
  // 跟浏览器对 transform 的处理一致(translate 不缩放描边宽度)。
  const t = el._transform;
  const s = t.scale == null ? 1 : t.scale;
  const cx = r.left + r.width / 2;
  const cy = r.top + r.height / 2;
  const w = r.width * s;
  const h = r.height * s;
  return {
    left: cx - w / 2 + (t.dx || 0),
    top: cy - h / 2 + (t.dy || 0),
    width: w,
    height: h
  };
}

['textbar', 'textbarText', 'pill', 'waveform', 'barLabel', 'barTimer',
  'cancelBtn', 'okBtn', 'copyBtn', 'barOrb'].forEach((id) => { els[id] = makeEl(id); });
// ✕ / ✓ / 复制 是按钮:拖拽时点它们不能触发拖动
els.cancelBtn.isButton = true;
els.okBtn.isButton = true;
els.copyBtn.isButton = true;
// 事件冒泡:点在按钮上,mousedown 会冒泡到胶囊(胶囊才是拖拽把手)
els.cancelBtn.parent = els.pill;
els.okBtn.parent = els.pill;
els.copyBtn.parent = els.pill;
// 球形态的图标住在胶囊**里面**(球就是收窄的胶囊,不是另一个元素)
els.barOrb.parent = els.pill;

global.window = global;
global.setTimeout = setTimeoutFake;
global.clearTimeout = clearTimerFake;
global.setInterval = setIntervalFake;
global.clearInterval = clearTimerFake;
global.requestAnimationFrame = requestAnimationFrameFake;
global.cancelAnimationFrame = cancelAnimationFrameFake;
global.addEventListener = (type, fn) => { listeners[type] = fn; };
// 窗口在屏幕上的位置:拖动时的基准点(capsule.js 用 window.screenX/Y)
global.screenX = 0;
global.screenY = 0;

/* <html> 的属性:主题就靠 data-theme 表达(auto 时这个属性被移除) */
const htmlAttrs = {};
/* body 上的类:预览关掉时是 no-preview,自定义位置时是 can-drag */
const bodyClasses = makeClassList();
/* document 上的监听:拖动时胶囊会往 document 上挂 mousemove/mouseup */
const docListeners = {};

global.document = {
  readyState: 'complete',
  body: { classList: bodyClasses },
  documentElement: {
    setAttribute: (k, v) => { htmlAttrs[k] = v; },
    removeAttribute: (k) => { delete htmlAttrs[k]; },
    getAttribute: (k) => (k in htmlAttrs ? htmlAttrs[k] : null)
  },
  getElementById: (id) => els[id] || null,
  createElement: (tag) => makeEl('new-' + tag),
  addEventListener: (type, fn) => {
    (docListeners[type] = docListeners[type] || []).push(fn);
  },
  removeEventListener: (type, fn) => {
    const list = docListeners[type] || [];
    const idx = list.indexOf(fn);
    if (idx >= 0) {
      list.splice(idx, 1);
    }
  }
};

/* 「复制」按钮调用的返回值:默认成功,步骤里可以改成失败。
   真实环境里 pywebview 会把 Python 的返回值包成 Promise。 */
let copyResultValue = true;

global.pywebview = {
  api: {
    ready: () => { calls.push({ name: 'ready', args: [] }); return true; },
    cancel_recording: () => { calls.push({ name: 'cancel_recording', args: [] }); },
    stop_recording: () => { calls.push({ name: 'stop_recording', args: [] }); },
    update_regions: (payload) => {
      calls.push({ name: 'update_regions', args: [payload] });
      return true;
    },
    move_capsule: (x, y) => {
      calls.push({ name: 'move_capsule', args: [x, y] });
      return true;
    },
    move_orb: (x, y) => {
      calls.push({ name: 'move_orb', args: [x, y] });
      return true;
    },
    start_recording: () => {
      calls.push({ name: 'start_recording', args: [] });
      return true;
    },
    begin_drag: () => { calls.push({ name: 'begin_drag', args: [] }); },
    end_drag: () => { calls.push({ name: 'end_drag', args: [] }); },
    copy_result: (text) => {
      calls.push({ name: 'copy_result', args: [text] });
      return Promise.resolve(copyResultValue);
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

/* 主流程是 async 的:pywebview 的返回值是 Promise,前端会 .then() 一把,
   那边的回调是**微任务** —— 同步跑完就输出的话,回调还没执行,
   断言会看到"什么都没发生"。每步之后用 setImmediate 冲一次微任务。 */
async function main() {
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
    } else if (step.op === 'setCopyResult') {
      // 「复制」按钮调 Python 的返回值:成功(true)还是失败(false)
      copyResultValue = !!step.value;
    } else if (step.op === 'setTransform') {
      // 给某个元素挂上 CSS transform —— 用来复现"动效期间量到的
      // 矩形是变形过的"这个真机 bug(见 capsule.js 里 rectOf 的说明)。
      const el = els[step.target];
      if (!el) {
        throw new Error('没有这个元素: ' + step.target);
      }
      el._transform = (step.scale == null && !step.dx && !step.dy)
        ? null
        : { scale: step.scale, dx: step.dx || 0, dy: step.dy || 0 };
    } else if (step.op === 'fireTransitionEnd') {
      // 触发某个元素的 transitionend —— 动效停下来时前端要重报命中区
      const el = els[step.target];
      if (!el) {
        throw new Error('没有这个元素: ' + step.target);
      }
      const fn = el.handlers.transitionend;
      if (typeof fn === 'function') {
        fn({ type: 'transitionend', target: el });
      }
    } else if (step.op === 'fire') {
      const fn = listeners[step.event];
      if (typeof fn === 'function') {
        fn();
      }
    } else if (step.op === 'mouse') {
      // 指定了 target → 派发给那个元素自己的 handler(mousedown);
      // 没指定 → 派发给 document 上的监听(mousemove / mouseup)
      const evt = {
        type: step.type,
        button: step.button === undefined ? 0 : step.button,
        screenX: step.screenX || 0,
        screenY: step.screenY || 0,
        clientX: step.clientX || 0,
        clientY: step.clientY || 0,
        target: null,
        preventDefault: () => {}
      };
      if (step.target) {
        const el = els[step.target];
        if (!el) {
          throw new Error('没有这个元素: ' + step.target);
        }
        evt.target = el;
        // 冒泡:从目标往上找第一个绑了这个事件的元素
        // (浏览器语义 —— evt.target 始终是最初按下的那个)
        let node = el;
        while (node) {
          const handler = node.handlers[step.type];
          if (handler) {
            handler(evt);
            node = null;
            break;
          }
          node = node.parent;
        }
        if (node) {
          throw new Error('没有元素绑 ' + step.type + ': ' + step.target);
        }
      } else {
        (docListeners[step.type] || []).slice().forEach((fn) => fn(evt));
      }
    } else {
      throw new Error('不认识的步骤: ' + JSON.stringify(step));
    }
    // 冲微任务:Promise 的 then 回调
    await new Promise((resolve) => setImmediate(resolve));
  }

  process.stdout.write(JSON.stringify({
    calls: calls,
    html: htmlAttrs,
    body: bodyClasses.values(),
    pill: els.pill ? els.pill.classList.values() : [],
    label: els.barLabel ? els.barLabel.textContent : '',
    textbar: els.textbarText ? els.textbarText.textContent : '',
    wave: dumpWave()
  }));
}

main();
