/* qwen3-voice 语音输入面板 —— 前端逻辑
 *
 * 纯原生 JS：零 npm、零框架、零 CDN、零外部字体/图标请求，离线可用。
 *
 * 桥接契约（唯一权威来源 ../BRIDGE.md），方法名必须逐字一致，不得增删改名：
 *   Python → JS : window.__qwen.setState / setLevels / setElapsed / setStatus
 *                 / setResult / setModel / setHotkey
 *   JS → Python : window.pywebview.api.ready / toggle_recording / switch_model
 *                 / get_snapshot / move_window / close_panel / quit_app
 */
(function () {
  'use strict';

  /* ==================== 常量 ==================== */

  // 与契约一致的固定对称包络：5 根条，中间那根最高，两边对称递减。
  // 注意：契约里那组 [0.45, 0.6, 0.78, 0.92, 1] 是 9 根条的包络，
  // 靠镜像索引才是对称的；我们只有 5 根，必须自己取对称的那一半：
  // ENV[0], ENV[2], ENV[4], ENV[2], ENV[0]。
  var ENV = [0.45, 0.78, 1.0, 0.78, 0.45];

  // 条高比例的上下限（契约规定 clamp 到 0.12 ~ 1.0）
  var MIN_FRAC = 0.12;
  var MAX_FRAC = 1.0;

  // 待机呼吸幅度（电平域 0.06 ~ 0.12），让待机时的电平条保持"活着"而不是一条死线
  var IDLE_BREATH_MIN = 0.06;
  var IDLE_BREATH_MAX = 0.12;

  // 待机时条阵的基准电平。
  // 注意：不能直接把 0.06~0.12 喂给契约公式——公式的下限 clamp 是 0.12，
  // 五根条会被压成一样高的一条死线；所以待机另走"基准 + 呼吸"的合成幅度。
  var IDLE_BASE_AMP = 0.32;

  // 峰值保持：每 1/30 秒下降 0.8 dB（等价 24 dB/s）
  var PEAK_FALL_DB_PER_SEC = 0.8 * 30;

  // 录音脉冲动效周期（秒）
  var PULSE_PERIOD = 1.6;

  // 契约定义的状态集合
  var STATES = ['starting', 'idle', 'recording', 'processing', 'error'];

  // 契约定义的 tone 集合
  var TONES = ['dim', 'ok', 'warn', 'err'];

  /* ==================== 运行状态 ==================== */

  var ui = {
    state: 'idle',          // 当前状态机
    model: '0.6B',          // 当前模型
    amp: 0,                 // 最近一帧的绝对电平（0..1，Python 推来）
    hasSignal: false,       // 是否已经收到过真实电平
    elapsed: 0,             // 录音计时（秒）
    result: '',             // 识别结果全文
    // 每个条位各自的峰值高度（比例），按 24 dB/s 回落
    peakFrac: [0, 0, 0, 0, 0],
    fractions: [MIN_FRAC, MIN_FRAC, MIN_FRAC, MIN_FRAC, MIN_FRAC]  // 当前渲染中的条高比例（做平滑用）
  };

  var el = {};             // DOM 缓存
  var ctx = null;          // Canvas 2D 上下文
  var lastFrame = 0;       // 上一帧时间戳，用于计算 dt
  var cssVars = {};        // 从 CSS 变量读取的配色缓存

  /* ==================== 小工具 ==================== */

  function $(id) {
    return document.getElementById(id);
  }

  function clamp(v, lo, hi) {
    return v < lo ? lo : (v > hi ? hi : v);
  }

  function now() {
    return (window.performance && window.performance.now) ? window.performance.now() : Date.now();
  }

  // 读取 CSS 变量（统一配色来源），并解析成 rgba 字符串
  function cssColor(name, fallback) {
    var raw = cssVars[name];
    if (!raw) {
      return fallback;
    }
    return hexToRgba(raw, 1);
  }

  function hexToRgba(hex, alpha) {
    var s = String(hex).trim();
    if (s.charAt(0) === '#') {
      s = s.slice(1);
    }
    if (s.length === 3) {
      s = s.charAt(0) + s.charAt(0) + s.charAt(1) + s.charAt(1) + s.charAt(2) + s.charAt(2);
    }
    if (s.length !== 6 || !/^[0-9a-fA-F]{6}$/.test(s)) {
      return 'rgba(59, 110, 245, ' + alpha + ')';
    }
    var r = parseInt(s.slice(0, 2), 16);
    var g = parseInt(s.slice(2, 4), 16);
    var b = parseInt(s.slice(4, 6), 16);
    return 'rgba(' + r + ', ' + g + ', ' + b + ', ' + alpha + ')';
  }

  function refreshCssVars() {
    var names = [
      '--baseline', '--level-idle-a', '--level-idle-b',
      '--level-rec-a', '--level-rec-b',
      '--level-proc-a', '--level-proc-b',
      '--level-peak'
    ];
    var style = window.getComputedStyle ? window.getComputedStyle(document.documentElement) : null;
    for (var i = 0; i < names.length; i++) {
      var v = style ? style.getPropertyValue(names[i]) : '';
      cssVars[names[i]] = v ? String(v).trim() : '';
    }
  }

  // 圆角矩形路径（不依赖 ctx.roundRect，兼容性更稳）
  function roundRectPath(c, x, y, w, h, r) {
    var rr = Math.min(r, w / 2, h / 2);
    c.beginPath();
    c.moveTo(x + rr, y);
    c.arcTo(x + w, y, x + w, y + h, rr);
    c.arcTo(x + w, y + h, x, y + h, rr);
    c.arcTo(x, y + h, x, y, rr);
    c.arcTo(x, y, x + w, y, rr);
    c.closePath();
  }

  /* ==================== 与 Python 的桥接 ==================== */

  // 调用 window.pywebview.api.<name>；不存在就静默跳过
  function callApi(name) {
    var args = Array.prototype.slice.call(arguments, 1);
    try {
      var api = window.pywebview && window.pywebview.api;
      if (!api) {
        return false;
      }
      var fn = api[name];
      if (typeof fn !== 'function') {
        return false;
      }
      var ret = fn.apply(api, args);
      // pywebview 会包成 Promise，失败不影响面板
      if (ret && typeof ret.then === 'function') {
        ret.then(null, function () {});
      }
      return true;
    } catch (e) {
      return false;
    }
  }

  // DOM 就绪后通知 Python；pywebview 的注入可能比 DOMContentLoaded 晚，做有限次重试
  function notifyReady(attempt) {
    if (callApi('ready')) {
      return;
    }
    if (attempt >= 200) {
      return;
    }
    window.setTimeout(function () {
      notifyReady(attempt + 1);
    }, 50);
  }

  /* ==================== 状态渲染 ==================== */

  var STATE_TEXT = {
    idle: '待机',
    starting: '准备中',
    recording: '录音中',
    processing: '识别中',
    error: '出错'
  };

  // 状态 → 状态行 tone（Python 随后会用 setStatus 覆盖）
  var STATE_TONE = {
    idle: 'dim',
    starting: 'warn',
    recording: 'ok',
    processing: 'dim',
    error: 'err'
  };

  function applyState(state) {
    ensureDom();
    if (STATES.indexOf(state) < 0) {
      state = 'idle';
    }
    ui.state = state;

    // body 上的状态类
    var cls = document.body.className.split(/\s+/);
    var kept = [];
    for (var i = 0; i < cls.length; i++) {
      if (cls[i] && cls[i].indexOf('st-') !== 0) {
        kept.push(cls[i]);
      }
    }
    kept.push('st-' + state);
    document.body.className = kept.join(' ');

    // 状态点
    if (el.dot) {
      el.dot.setAttribute('data-state', state);
      el.dot.setAttribute('title', STATE_TEXT[state] || state);
    }

    // 主按钮三态
    if (el.mainBtn) {
      el.mainBtn.classList.remove('is-recording', 'is-busy');
      el.mainBtn.disabled = false;
      if (state === 'recording') {
        el.mainBtn.textContent = '■ 停止录音';
        el.mainBtn.classList.add('is-recording');
      } else if (state === 'processing') {
        el.mainBtn.textContent = '识别中 ...';
        el.mainBtn.disabled = true;
        el.mainBtn.classList.add('is-busy');
      } else if (state === 'starting') {
        el.mainBtn.textContent = '准备中 ...';
        el.mainBtn.disabled = true;
        el.mainBtn.classList.add('is-busy');
      } else {
        el.mainBtn.textContent = '● 开始录音';
      }
    }

    // 电平卡片与结果卡片的处理中动效
    if (el.levelCard) {
      el.levelCard.classList.toggle('is-recording', state === 'recording');
      el.levelCard.classList.toggle('is-processing', state === 'processing');
    }
    if (el.resultCard) {
      el.resultCard.classList.toggle('is-processing', state === 'processing');
    }

    // 状态行默认文案（Python 随后会用 setStatus 覆盖）
    if (el.statusText) {
      el.statusText.textContent = STATE_TEXT[state] || state;
      el.statusText.className = 'status-text ' + toneClass(STATE_TONE[state]);
    }

    refreshCssVars();
  }

  function toneClass(tone) {
    return TONES.indexOf(tone) >= 0 ? 'tone-' + tone : 'tone-dim';
  }

  function applyModel(tag) {
    ensureDom();
    ui.model = tag;
    if (!el.modelSeg) {
      return;
    }
    var items = el.modelSeg.querySelectorAll('.seg-item');
    for (var i = 0; i < items.length; i++) {
      var isActive = items[i].getAttribute('data-model') === tag;
      items[i].classList.toggle('is-active', isActive);
    }
  }

  function applyResult(text) {
    ensureDom();
    ui.result = text == null ? '' : String(text);
    if (!el.resultCard || !el.resultText) {
      return;
    }
    el.resultText.textContent = ui.result;
    el.resultCard.classList.toggle('is-empty', ui.result.length === 0);
    // 结果变化时把滚动位置拉回顶部
    el.resultText.scrollTop = 0;
  }

  function applyElapsed(seconds) {
    ensureDom();
    var s = Number(seconds);
    if (!isFinite(s) || s < 0) {
      s = 0;
    }
    ui.elapsed = s;
    if (el.timer) {
      el.timer.textContent = s.toFixed(1) + 's';
    }
  }

  function applyStatus(text, tone) {
    ensureDom();
    if (!el.statusText) {
      return;
    }
    el.statusText.textContent = text == null ? '' : String(text);
    el.statusText.className = 'status-text ' + toneClass(tone);
  }

  function applyHotkey(text) {
    ensureDom();
    if (!el.hotkey) {
      return;
    }
    var s = text == null ? '' : String(text).trim();
    if (!s) {
      s = '热键 未设置';
    } else if (s.indexOf('热键') !== 0) {
      // Python 可能只推 "Ctrl+Alt+V"，这里补上前缀
      s = '热键 ' + s;
    }
    el.hotkey.textContent = s;
  }

  /* ==================== 电平数据 ==================== */

  // payload 是 JSON 字符串，形如 "[0.012,0.31,0.44]"，元素是 0..1 的绝对电平
  function parseLevels(payload) {
    var arr = payload;
    if (typeof arr === 'string') {
      try {
        arr = JSON.parse(arr);
      } catch (e) {
        return null;
      }
    }
    if (arr == null) {
      return null;
    }
    if (!Array.isArray(arr)) {
      arr = [arr];
    }
    var vals = [];
    for (var i = 0; i < arr.length; i++) {
      var v = Number(arr[i]);
      if (isFinite(v)) {
        vals.push(clamp(v, 0, 1));
      }
    }
    if (!vals.length) {
      return null;
    }
    var sum = 0;
    for (var j = 0; j < vals.length; j++) {
      sum += vals[j] * vals[j];
    }
    // amp = 这一帧样本的均方根电平（绝对值，不是按窗口最大值归一化的结果）
    return { rms: Math.sqrt(sum / vals.length) };
  }

  function setLevels(payload) {
    var parsed = parseLevels(payload);
    if (!parsed) {
      return;
    }
    ui.amp = parsed.rms;
    ui.hasSignal = true;
    // 峰值保持不在这里维护：条高每帧都会重算，由 updatePeakFractions 统一处理
  }

  /* ==================== 电平条渲染 ==================== */

  // 生成镜像对称的抖动系数：[r0, r1, r2, r1, r0]
  // 左右成对共用同一个随机数，5 根条的高度才会严格对称（最左=最右、次外=次内）
  function mirroredJitter() {
    var r0 = 0.25 + Math.random() * 0.75;
    var r1 = 0.25 + Math.random() * 0.75;
    var r2 = 0.25 + Math.random() * 0.75;
    return [r0, r1, r2, r1, r0];
  }

  // 计算本帧 5 根条的目标高度比例
  function computeFractions(t) {
    var fr = [0, 0, 0, 0, 0];
    var i;

    if (ui.state === 'recording' && ui.hasSignal) {
      // 录音中：直接使用 Python 推来的绝对电平，绝不在前端做窗口归一化
      var amp = ui.amp;
      var jit = mirroredJitter();
      for (i = 0; i < 5; i++) {
        fr[i] = clamp(ENV[i] * amp * jit[i], MIN_FRAC, MAX_FRAC);
      }
      return fr;
    }

    // 待机 / 识别中 / 出错：低幅度呼吸动画，不要画成一条死线
    var breath = IDLE_BREATH_MIN + (IDLE_BREATH_MAX - IDLE_BREATH_MIN) *
      (0.5 + 0.5 * Math.sin(t / 1400));
    var idleAmp = IDLE_BASE_AMP + breath;
    var jitIdle = mirroredJitter();
    for (i = 0; i < 5; i++) {
      fr[i] = clamp(ENV[i] * idleAmp * jitIdle[i], MIN_FRAC, MAX_FRAC);
    }
    return fr;
  }

  // 当前状态下的条形配色
  function levelColors() {
    if (ui.state === 'recording') {
      return [cssVars['--level-rec-a'] || '#fda4af', cssVars['--level-rec-b'] || '#e11d48'];
    }
    if (ui.state === 'processing') {
      return [cssVars['--level-proc-a'] || '#a9c4ff', cssVars['--level-proc-b'] || '#3b6ef5'];
    }
    return [cssVars['--level-idle-a'] || '#7fe7d7', cssVars['--level-idle-b'] || '#14b8a6'];
  }

  // 同步画布的后备缓冲与布局尺寸，并重置变换。
  // 每帧都要做：Python 侧会异步 resize 窗口，布局尺寸变了而后备缓冲还没跟上时，
  // 绘制用的坐标系和画布实际显示的盒子就不是一回事，条形会整体偏移。
  function syncCanvas() {
    if (!ctx || !el.canvas) {
      return null;
    }
    var dpr = window.devicePixelRatio || 1;
    var rect = el.canvas.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) {
      return null;
    }
    var w = Math.max(1, Math.round(rect.width * dpr));
    var h = Math.max(1, Math.round(rect.height * dpr));
    if (el.canvas.width !== w || el.canvas.height !== h) {
      el.canvas.width = w;
      el.canvas.height = h;
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    return { w: rect.width, h: rect.height };
  }

  function draw(t, dt) {
    var size = syncCanvas();
    if (!size) {
      return;
    }
    var W = size.w;
    var H = size.h;

    ctx.clearRect(0, 0, W, H);

    var baseline = H / 2;
    // 条形的最大半高：留 3px 余量，视口变矮（画布只有 20 多像素）时条形也还有空间
    var maxHalf = Math.max(4, baseline - 3);

    // 目标值 + 峰值保持 + 轻微平滑
    var target = computeFractions(t);
    updatePeakFractions(target, dt);
    for (var i = 0; i < 5; i++) {
      ui.fractions[i] += (ui.peakFrac[i] - ui.fractions[i]) * 0.4;
    }

    // 基线
    ctx.save();
    ctx.strokeStyle = cssVars['--baseline'] || 'rgba(22, 28, 45, 0.10)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(0, Math.round(baseline) + 0.5);
    ctx.lineTo(W, Math.round(baseline) + 0.5);
    ctx.stroke();
    ctx.restore();

    // 5 根竖条作为一组，在画布内水平居中（整组居中，不是第一根对齐左边）
    var n = ENV.length;
    var gap = 8;
    var bw = Math.max(5, Math.min(20, (W - gap * (n - 1)) / n));
    var groupW = bw * n + gap * (n - 1);
    var x0 = Math.round((W - groupW) / 2);
    var radius = Math.min(bw / 2, 4);

    var colors = levelColors();
    var grad = ctx.createLinearGradient(0, baseline - maxHalf, 0, baseline + maxHalf);
    grad.addColorStop(0, hexToRgba(colors[0], 1));
    grad.addColorStop(1, hexToRgba(colors[1], 1));

    // 录音中：轻微脉冲动效（辉光强度随节拍起伏）
    var pulse = 0;
    if (ui.state === 'recording') {
      pulse = 0.5 + 0.5 * Math.sin((t / 1000) * (2 * Math.PI / PULSE_PERIOD));
    }

    ctx.save();
    ctx.fillStyle = grad;
    if (pulse > 0) {
      ctx.shadowColor = hexToRgba(colors[1], 0.55);
      ctx.shadowBlur = 3 + 9 * pulse;
    }
    var tallest = 0;
    for (var j = 0; j < n; j++) {
      var half = Math.max(1.5, ui.fractions[j] * maxHalf);
      var x = x0 + j * (bw + gap);
      roundRectPath(ctx, x, baseline - half, bw, half * 2, radius);
      ctx.fill();
      if (ui.fractions[j] > ui.fractions[tallest]) {
        tallest = j;
      }
    }
    ctx.restore();

    // 峰值刻度：只画在最高的那根条上，一小段横向刻线，不贯穿全宽
    var tickHalf = Math.max(1.5, ui.fractions[tallest] * maxHalf);
    drawPeakTick(ctx, x0 + tallest * (bw + gap), bw, tickHalf, baseline);
  }

  // 在最高的那根条顶端画一小段峰值刻线
  function drawPeakTick(c, x, bw, half, baseline) {
    var color = cssVars['--level-peak'] || '#f59e0b';
    c.save();
    c.strokeStyle = hexToRgba(color, 0.5);
    c.lineWidth = 1.5;
    c.beginPath();
    c.moveTo(x - 1, baseline - half);
    c.lineTo(x + bw + 1, baseline - half);
    c.stroke();
    c.restore();
  }

  // 峰值保持：每个条位记住自己最近达到过的高度，按 24 dB/s 缓慢回落到当前高度
  function updatePeakFractions(target, dt) {
    // 每 1/30 秒下降 0.8 dB，换算成线性衰减系数（与帧率无关）
    var k = Math.pow(10, -(PEAK_FALL_DB_PER_SEC * dt) / 20);
    for (var i = 0; i < 5; i++) {
      var p = ui.peakFrac[i] * k;
      // 回落到当前高度为止：当前更高就直接抬上去，更低则继续缓慢下落
      ui.peakFrac[i] = p > target[i] ? p : target[i];
    }
  }

  // 主循环
  function tick(t) {
    var dt = lastFrame ? Math.min(0.1, (t - lastFrame) / 1000) : 0.016;
    lastFrame = t;

    draw(t, dt);
    window.requestAnimationFrame(tick);
  }

  /* ==================== 画布尺寸 ==================== */

  // 窗口尺寸变化时同步画布（draw 每帧也会自行同步一次，这里只是提前做掉）
  function resizeCanvas() {
    syncCanvas();
  }

  /* ==================== 拖动 ==================== */

  var drag = {
    active: false,
    lastX: 0,
    lastY: 0
  };

  function onDragStart(e) {
    if (e.button !== 0) {
      return;
    }
    // 点在按钮上时不触发拖动
    if (e.target && e.target.closest && e.target.closest('button')) {
      return;
    }
    drag.active = true;
    drag.lastX = e.clientX;
    drag.lastY = e.clientY;
    document.body.classList.add('dragging');
    document.addEventListener('mousemove', onDragMove, false);
    document.addEventListener('mouseup', onDragEnd, false);
    e.preventDefault();
  }

  function onDragMove(e) {
    if (!drag.active) {
      return;
    }
    var dx = e.clientX - drag.lastX;
    var dy = e.clientY - drag.lastY;
    if (dx === 0 && dy === 0) {
      return;
    }
    drag.lastX = e.clientX;
    drag.lastY = e.clientY;
    // 契约要求 move_window 收物理像素，CSS 像素要乘上系统缩放系数
    var scale = window.devicePixelRatio || 1;
    callApi('move_window', dx * scale, dy * scale);
  }

  function onDragEnd() {
    if (!drag.active) {
      return;
    }
    drag.active = false;
    document.body.classList.remove('dragging');
    document.removeEventListener('mousemove', onDragMove, false);
    document.removeEventListener('mouseup', onDragEnd, false);
  }

  /* ==================== 事件绑定 ==================== */

  function bindEvents() {
    // 主按钮：开始 / 停止录音
    if (el.mainBtn) {
      el.mainBtn.addEventListener('click', function () {
        callApi('toggle_recording');
      });
    }

    // 关闭按钮：只隐藏面板，不退出程序
    if (el.closeBtn) {
      el.closeBtn.addEventListener('click', function () {
        callApi('close_panel');
      });
    }

    // 退出：结束整个程序
    if (el.quitBtn) {
      el.quitBtn.addEventListener('click', function () {
        callApi('quit_app');
      });
    }

    // 模型切换
    if (el.modelSeg) {
      el.modelSeg.addEventListener('click', function (e) {
        var item = e.target && e.target.closest ? e.target.closest('.seg-item') : null;
        if (!item) {
          return;
        }
        var tag = item.getAttribute('data-model');
        if (!tag) {
          return;
        }
        applyModel(tag);   // 先本地高亮，Python 随后会用 setModel 校正
        callApi('switch_model', tag);
      });
    }

    // 标题栏拖动
    if (el.titlebar) {
      el.titlebar.addEventListener('mousedown', onDragStart, false);
    }

    // 画布尺寸跟随布局
    window.addEventListener('resize', resizeCanvas, false);
    if (window.ResizeObserver) {
      var ro = new ResizeObserver(resizeCanvas);
      ro.observe(el.canvas);
    }
  }

  /* ==================== 对外接口 ==================== */

  // Python → JS：严格按 BRIDGE.md 的签名
  window.__qwen = {
    setState: function (state) {
      applyState(state);
    },
    setLevels: function (payload) {
      setLevels(payload);
    },
    setElapsed: function (seconds) {
      applyElapsed(seconds);
    },
    setStatus: function (text, tone) {
      applyStatus(text, tone);
    },
    setResult: function (text) {
      applyResult(text);
    },
    setModel: function (tag) {
      applyModel(tag);
    },
    setHotkey: function (text) {
      applyHotkey(text);
    }
  };

  /* ==================== 启动 ==================== */

  // 绑定 DOM 引用（可重复调用）
  function bindDom() {
    el.dot = $('stateDot');
    el.titlebar = $('titlebar');
    el.closeBtn = $('closeBtn');
    el.quitBtn = $('quitBtn');
    el.mainBtn = $('mainBtn');
    el.levelCard = $('levelCard');
    el.canvas = $('levelCanvas');
    el.statusText = $('statusText');
    el.timer = $('timer');
    el.modelSeg = $('modelSeg');
    el.resultCard = $('resultCard');
    el.resultText = $('resultText');
    el.resultEmpty = $('resultEmpty');
    el.hotkey = $('hotkeyLabel');

    if (el.canvas && el.canvas.getContext && !ctx) {
      ctx = el.canvas.getContext('2d');
    }
  }

  // Python 可能在 DOMContentLoaded 之前就推状态，这里做惰性绑定兜底
  function ensureDom() {
    if (!el.canvas) {
      bindDom();
    }
  }

  function init() {
    bindDom();

    refreshCssVars();
    applyState('idle');
    applyModel('0.6B');
    applyResult('');
    applyElapsed(0);
    applyHotkey('热键 Ctrl+Alt+V');

    bindEvents();
    resizeCanvas();
    window.requestAnimationFrame(tick);

    // DOM 就绪后通知 Python，Python 收到后立即推一次全量状态
    notifyReady(0);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init, false);
  } else {
    init();
  }
})();
