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

  /* ---- 滚动电平条（对齐 MiniMax Code 的 asr-volume-bars）---- */

  // 同时显示的条数：22 根（MCode 那个小条是 13 根，我们的面板更宽）
  var BAR_COUNT = 22;

  // 条宽 / 条间距（px）：步进 = 宽 + 间距 = 6px，与 panel.css 保持一致
  var BAR_WIDTH = 3;
  var BAR_GAP = 3;
  var BAR_STEP = BAR_WIDTH + BAR_GAP;

  // 每推进一根条的周期（ms）
  var BAR_TICK_MS = 80;

  // 元素池 = 可见条数 + 1：多出来的那根已经滑出视口左侧，
  // 用来"回收"（复用同一个 DOM 节点当新条），省得新条横穿屏幕飞进来
  var POOL_SIZE = BAR_COUNT + 1;

  // 静音死区：响度 <= 0.04 视为底噪
  var DEAD_ZONE = 0.04;

  // 条高上下限（px）：静音 2px，最响 20px
  var SILENT_HEIGHT = 2;
  var MAX_HEIGHT = 20;

  // 4px 正好是 level = 0.04 对应的高度（MCode 的死区上界）；
  // 不高于它就算静音条，画灰色
  var SILENT_MAX_PX = 4;

  // 待机 / 识别中不显示条形，只显示这行提示文字（idle 按当前热键动态生成）
  var HINT_TEXT = {
    starting: '准备中 …',
    processing: '正在识别 …',
    error: '出错了，请重试'
  };

  // Python 还没推热键时的兜底文案
  var DEFAULT_HOTKEY = 'Ctrl+Alt+Space';

  // 契约定义的状态集合
  var STATES = ['starting', 'idle', 'recording', 'processing', 'error'];

  // 契约定义的 tone 集合
  var TONES = ['dim', 'ok', 'warn', 'err'];

  /* ==================== 运行状态 ==================== */

  var ui = {
    state: 'idle',          // 当前状态机
    model: '0.6B',          // 当前模型
    level: 0,               // 当前（平滑后）的响度 0..1
    elapsed: 0,             // 录音计时（秒）
    result: '',             // 识别结果全文
    hotkey: ''              // 当前热键（原始字符串，不含"热键"前缀）
  };

  var el = {};             // DOM 缓存

  // 电平条的运行状态
  var pool = [];           // 条的 DOM 节点：pool[0] 最新，pool[末位] 最老
  var ages = [];           // 每根条的"年龄"：0 = 最新，越大越老
  var slotIndex = 0;       // 三槽轮转指针
  var lastBarHeight = 0;   // 上一根条的像素高度（做过渡插值用）
  var smoother = { current: 0 };  // 电平滑器当前值
  var barTimer = 0;        // 推进条的定时器
  var clearTimer = 0;      // 离开录音后清空条的延时

  /* ==================== 小工具 ==================== */

  function $(id) {
    return document.getElementById(id);
  }

  function clamp(v, lo, hi) {
    return v < lo ? lo : (v > hi ? hi : v);
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
    var prevState = ui.state;
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

    // 录音中才滚动电平条；其它状态一条都不显示（不画任何假动画）
    if (state === 'recording') {
      if (prevState !== 'recording') {
        resetLevelBars();
      }
    } else if (prevState === 'recording') {
      stopBars();
      scheduleClearBars();
    }

    // 频谱区的提示文字（待机 / 识别中显示，录音中清空）
    updateWaveHint();

    // 状态行默认文案（Python 随后会用 setStatus 覆盖）
    if (el.statusText) {
      el.statusText.textContent = STATE_TEXT[state] || state;
      el.statusText.className = 'status-text ' + toneClass(STATE_TONE[state]);
    }
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
    var s = text == null ? '' : String(text).trim();
    // 原始热键（去掉"热键 "前缀），供待机提示语使用
    ui.hotkey = s.replace(/^热键\s*/, '').trim();
    if (!s) {
      s = '热键 未设置';
    } else if (s.indexOf('热键') !== 0) {
      // Python 可能只推 "Ctrl+Alt+Space"，这里补上前缀
      s = '热键 ' + s;
    }
    if (el.hotkey) {
      el.hotkey.textContent = s;
    }
    updateWaveHint();
  }

  /* ==================== 电平数据 ==================== */

  // payload 是 JSON 字符串，内容是**最近若干帧的音量序列**（0..1，
  // dBFS 对数域映射后的值）。**数组最后一个元素是最新值。**
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
      vals.push(isFinite(v) ? clamp(v, 0, 1) : 0);
    }
    return vals;
  }

  function setLevels(payload) {
    var vals = parseLevels(payload);
    if (!vals || !vals.length) {
      // 空序列：这一帧没有新样本，不动平滑器
      return;
    }
    // Python 侧 30fps 推一串滑动窗口，而条子 80ms 才出一根，
    // 所以只取最新的一个值（数组末尾）喂给平滑器，
    // 不做窗口内归一化 —— 归一化之后条子永远看起来是满的。
    ui.level = smoothLevel(vals[vals.length - 1]);
  }

  /* ==================== 高度映射 ==================== */

  // 响度(0..1) → 条高(px)。4 段非线性，这是"对比度明显"的来源：
  //   0 ~ 0.04 : 固定 2px（死区 / 底噪）
  //   0.04 ~ 0.2: 4 → 12px（斜率 50，最灵敏）
  //   0.2  ~ 0.5: 12 → 18px（斜率 20）
  //   0.5  ~ 1  : 18 → 20px（斜率 4，压缩）
  function mapHeight(level) {
    if (level <= DEAD_ZONE) {
      return SILENT_HEIGHT;
    }
    if (level <= 0.2) {
      return 4 + (level - DEAD_ZONE) / 0.16 * 8;
    }
    if (level <= 0.5) {
      return 12 + (level - 0.2) / 0.3 * 6;
    }
    return Math.min(MAX_HEIGHT, 18 + (level - 0.5) / 0.5 * 2);
  }

  /* ==================== 电平滑 ==================== */

  // 升快降慢（上升 0.7、下降 0.5），再叠一层自适应增益：
  // 电平越低增益越大，底噪附近的小起伏也能看出来
  function smoothLevel(target) {
    var t = smoother.current;
    var r = t + (target - t) * (target > t ? 0.7 : 0.5);
    smoother.current = r;
    var out = r + (target - r) * (0.3 + 0.5 * r);
    return clamp(out, 0, 1);
  }

  /* ==================== 三槽轮转 ==================== */

  // 每次算出三个高度（过渡 / 当前 / 衰减）轮流用，
  // 相邻条因此天然错开，不会是一排单调的长短 —— "流动感"的来源。
  //
  // MCode 那边 r / decay 是"响度"（j() 的结果），这里直接保留像素高度：
  // I() 与 j() 互为反函数，往返一次结果不变，省一层换算。
  function nextBarHeight(curPx, level) {
    var prev = lastBarHeight;
    // 过渡条：与上一根做 65% 插值 → 缓升 / 缓降
    var trans = prev + (curPx - prev) * 0.65;
    // 衰减条：回声感，越响掉得越多
    var decay = curPx <= SILENT_MAX_PX
      ? curPx
      : Math.max(SILENT_MAX_PX, curPx - (0.75 + 3.75 * level));
    var slots = [trans, curPx, decay];
    var h = slots[slotIndex % 3];
    slotIndex++;
    lastBarHeight = h;
    // 死区下界：静音条恒为 2px
    return clamp(h, SILENT_HEIGHT, MAX_HEIGHT);
  }

  /* ==================== 滚动电平条 ==================== */

  // 新建一根条
  function createBar() {
    var bar = document.createElement('i');
    bar.style.height = SILENT_HEIGHT + 'px';
    bar.style.transform = 'translateX(0px)';
    bar.classList.add('is-silent');
    return bar;
  }

  // 推进一根新条：新条从右侧进，旧的从左侧出（滑出视口即被裁掉）
  function advanceBar() {
    if (!el.waveBars) {
      return;
    }
    var level = ui.level;
    var curPx = mapHeight(level);
    var height = nextBarHeight(curPx, level);

    var recycled;
    if (pool.length < POOL_SIZE) {
      // 池子还没满（正常情况下一进录音就填满了，这里只是兜底）
      recycled = createBar();
      el.waveBars.appendChild(recycled);
    } else {
      // 条够了：回收最老的一根（它已经在视口左侧外，回收时不会被人看见）
      recycled = pool.pop();
      ages.pop();
    }
    pool.unshift(recycled);
    ages.unshift(0);
    // 其余各根年龄 +1（也就是整体向左滑一个步距）
    for (var i = 1; i < ages.length; i++) {
      ages[i] += 1;
    }

    // 写位置：年龄 a 的条平移 -a × 步距
    // 回收的那根要"瞬移"到最右边：先关掉它的过渡，
    // 否则它会从屏幕左边一路飞过来
    recycled.style.transition = 'none';
    recycled.style.height = SILENT_HEIGHT + 'px';
    for (var j = 0; j < pool.length; j++) {
      pool[j].style.transform = 'translateX(' + (-ages[j] * BAR_STEP) + 'px)';
    }

    // 强制回流，让浏览器认下"瞬时"的位置（后面再放开过渡）
    void el.waveBars.offsetWidth;

    // 颜色：静音条画灰色；高度下一帧再给，触发 75ms 的高度过渡
    var silent = height <= SILENT_MAX_PX;
    recycled.classList.toggle('is-silent', silent);
    var target = height.toFixed(2) + 'px';
    window.requestAnimationFrame(function () {
      recycled.style.transition = '';
      recycled.style.height = target;
    });
  }

  // 清空所有条（离开录音时用）
  function clearBars() {
    stopBars();
    pool = [];
    ages = [];
    if (el.waveBars) {
      el.waveBars.textContent = '';
    }
  }

  // 进入录音：重来一遍，不保留上一次的残留高度
  function resetLevelBars() {
    if (clearTimer) {
      window.clearTimeout(clearTimer);
      clearTimer = 0;
    }
    stopBars();
    pool = [];
    ages = [];
    slotIndex = 0;
    lastBarHeight = 0;
    smoother.current = 0;
    ui.level = 0;
    if (el.waveBars) {
      el.waveBars.textContent = '';
      // 预填满 22 根（外加 1 根屏外的回收位）：
      // 组宽一开始就是满的，和 MCode 那个定宽小条一致；
      // 初始都是 2px 的静音基线，等真实音量来了再长起来
      for (var i = 0; i < POOL_SIZE; i++) {
        var bar = createBar();
        bar.style.transform = 'translateX(' + (-i * BAR_STEP) + 'px)';
        el.waveBars.appendChild(bar);
        pool.push(bar);
        ages.push(i);
      }
    }
    startBars();
  }

  function startBars() {
    stopBars();
    barTimer = window.setInterval(advanceBar, BAR_TICK_MS);
  }

  function stopBars() {
    if (barTimer) {
      window.clearInterval(barTimer);
      barTimer = 0;
    }
  }

  // 离开录音后，等淡出动画走完再把条清掉
  function scheduleClearBars() {
    if (clearTimer) {
      window.clearTimeout(clearTimer);
    }
    clearTimer = window.setTimeout(function () {
      clearTimer = 0;
      clearBars();
    }, 200);
  }

  /* ==================== 电平区提示文字 ==================== */

  // "ctrl+alt+space" -> "Ctrl+Alt+Space"
  function prettyHotkey(raw) {
    var parts = String(raw || '').split('+');
    var out = [];
    for (var i = 0; i < parts.length; i++) {
      var p = parts[i].trim();
      out.push(p ? p.charAt(0).toUpperCase() + p.slice(1) : p);
    }
    return out.join('+');
  }

  // 待机 / 准备中 / 识别中 / 出错：显示提示文字；录音中不显示
  function waveHintText() {
    if (ui.state === 'recording') {
      return '';
    }
    if (ui.state === 'idle') {
      return '按 ' + (prettyHotkey(ui.hotkey) || DEFAULT_HOTKEY) + ' 开始说话';
    }
    return HINT_TEXT[ui.state] || '';
  }

  function updateWaveHint() {
    if (!el.waveHint) {
      return;
    }
    el.waveHint.textContent = waveHintText();
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
    el.waveViewport = $('waveViewport');
    el.waveBars = $('waveBars');
    el.waveHint = $('waveHint');
    el.statusText = $('statusText');
    el.timer = $('timer');
    el.modelSeg = $('modelSeg');
    el.resultCard = $('resultCard');
    el.resultText = $('resultText');
    el.resultEmpty = $('resultEmpty');
    el.hotkey = $('hotkeyLabel');
  }

  // Python 可能在 DOMContentLoaded 之前就推状态，这里做惰性绑定兜底
  function ensureDom() {
    if (!el.waveBars) {
      bindDom();
    }
  }

  function init() {
    bindDom();

    applyState('idle');
    applyModel('0.6B');
    applyResult('');
    applyElapsed(0);
    applyHotkey('热键 Ctrl+Alt+V');

    bindEvents();

    // DOM 就绪后通知 Python，Python 收到后立即推一次全量状态
    notifyReady(0);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init, false);
  } else {
    init();
  }
})();
