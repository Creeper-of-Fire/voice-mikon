/* qwen3-voice 语音输入面板 —— 前端逻辑
 *
 * 纯原生 JS：零 npm、零框架、零 CDN、零外部字体/图标请求，离线可用。
 *
 * 面板瘦身为**配置窗口**：只看状态、切模型、退出。
 * 录音过程的反馈(实时预览、电平条、计时)在胶囊窗口那边
 * (capsule.html / capsule.css / capsule.js),那边是另一套独立文件。
 *
 * 桥接契约（唯一权威来源 ../BRIDGE.md），方法名必须逐字一致，不得增删改名：
 *   Python → JS : window.__qwen.setState / setModel / setHotkey
 *   JS → Python : window.pywebview.api.ready / switch_model / get_snapshot
 *                 / move_window / close_panel / quit_app
 */
(function () {
  'use strict';

  /* ==================== 常量 ==================== */

  // 契约定义的状态集合
  var STATES = ['starting', 'idle', 'recording', 'processing', 'error'];

  // Python 还没推热键时的兜底文案
  var DEFAULT_HOTKEY = 'Ctrl+Alt+Space';

  /* ==================== 运行状态 ==================== */

  var ui = {
    state: 'idle',          // 当前状态机
    model: '0.6B',          // 当前模型
    hotkey: ''              // 当前热键（原始字符串，不含"热键"前缀）
  };

  var el = {};             // DOM 缓存

  /* ==================== 小工具 ==================== */

  function $(id) {
    return document.getElementById(id);
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

  function applyHotkey(text) {
    ensureDom();
    var s = text == null ? '' : String(text).trim();
    // 原始热键（去掉"热键 "前缀）
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
  }

  /* ==================== 拖动 ==================== */

  /* 拖动 = 发**绝对目标位置**,不是增量:
   *   目标 = mousedown 时的窗口位置 + (当前鼠标 screenX − 起点鼠标 screenX)
   * 必须用 e.screenX(鼠标的**屏幕**坐标)而不是 clientX —— 窗口自己
   * 一动 clientX(视口内坐标)就变,会被窗口移动污染,形成正反馈
   * 震荡(实测:剧烈抖动、不跟鼠标)。screenX 不受窗口移动影响。
   * rAF 节流:mousemove 可能 100Hz+,每帧只发最新一次,IPC 不超 60Hz。
   * (另一个已证伪方案:SendMessage(SC_MOVE) 原生拖动 —— 在 WebView2
   * 架构下死锁 js_api 调用链,整个面板点什么都卡。) */
  var drag = {
    active: false,
    startScreenX: 0,
    startScreenY: 0,
    startWinX: 0,
    startWinY: 0,
    pending: null,
    raf: 0
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
    drag.startScreenX = e.screenX;   // 鼠标的屏幕坐标(CSS 像素)
    drag.startScreenY = e.screenY;
    drag.startWinX = window.screenX; // 窗口在屏幕上的位置(CSS 像素)
    drag.startWinY = window.screenY;
    drag.pending = null;
    document.addEventListener('mousemove', onDragMove, false);
    document.addEventListener('mouseup', onDragEnd, false);
    e.preventDefault();
  }

  function onDragMove(e) {
    if (!drag.active) {
      return;
    }
    drag.pending = e;
    if (!drag.raf) {
      drag.raf = requestAnimationFrame(flushDrag);
    }
  }

  function flushDrag() {
    drag.raf = 0;
    var e = drag.pending;
    drag.pending = null;
    if (!e || !drag.active) {
      return;
    }
    var x = drag.startWinX + (e.screenX - drag.startScreenX);
    var y = drag.startWinY + (e.screenY - drag.startScreenY);
    // 位移为零不发(首帧) —— window.screenX 与 WinForms 窗口原点
    // 若有微小偏差,发零位移会让窗口跳一下
    if (x === drag.startWinX && y === drag.startWinY) {
      return;
    }
    callApi('move_window', x, y);
  }

  function onDragEnd() {
    if (!drag.active) {
      return;
    }
    drag.active = false;
    document.removeEventListener('mousemove', onDragMove, false);
    document.removeEventListener('mouseup', onDragEnd, false);
    if (drag.raf) {
      cancelAnimationFrame(drag.raf);
      drag.raf = 0;
    }
    flushDrag();   // 把最后一次位置补上
  }

  /* ==================== 事件绑定 ==================== */

  function bindEvents() {
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
    el.modelSeg = $('modelSeg');
    el.hotkey = $('hotkeyLabel');
  }

  // Python 可能在 DOMContentLoaded 之前就推状态，这里做惰性绑定兜底
  function ensureDom() {
    if (!el.modelSeg) {
      bindDom();
    }
  }

  function init() {
    bindDom();

    applyState('idle');
    applyModel('0.6B');
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
