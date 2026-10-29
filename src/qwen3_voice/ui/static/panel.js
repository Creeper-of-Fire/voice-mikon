/* qwen3-voice 语音输入面板 —— 前端逻辑
 *
 * 纯原生 JS：零 npm、零框架、零 CDN、零外部字体/图标请求，离线可用。
 *
 * 面板瘦身为**配置窗口**：只看状态、切模型、退出。
 * 录音过程的反馈(实时预览、电平条、计时)在胶囊窗口那边
 * (capsule.html / capsule.css / capsule.js),那边是另一套独立文件。
 *
 * 桥接契约（唯一权威来源 ../BRIDGE.md），方法名必须逐字一致，不得增删改名：
 *   Python → JS : window.__qwen.setState / setModel / setHotkey / setTheme
 *                 / setCapsulePosition / setPreviewEnabled
 *   JS → Python : window.pywebview.api.ready / switch_model / get_snapshot
 *                 / move_window / close_panel / quit_app / set_theme
 *                 / set_capsule_position / set_preview
 */
(function () {
  'use strict';

  /* ==================== 常量 ==================== */

  // 契约定义的状态集合
  var STATES = ['starting', 'idle', 'recording', 'processing', 'error'];

  // 主题档位：auto = 跟随系统,light = 白天,dark = 黑夜
  var THEMES = ['auto', 'light', 'dark'];

  // 胶囊位置档位：follow = 跟随输入光标,bottom = 固定底部,custom = 自定义
  var POSITION_MODES = ['follow', 'bottom', 'custom'];

  // Python 还没推热键时的兜底文案
  var DEFAULT_HOTKEY = 'Ctrl+Alt+Space';

  /* ==================== 运行状态 ==================== */

  var ui = {
    state: 'idle',          // 当前状态机
    model: '0.6B',          // 当前模型
    hotkey: '',             // 当前热键（原始字符串，不含"热键"前缀）
    theme: 'auto',          // 当前主题档位
    position: 'follow',     // 当前胶囊位置档位
    preview: true           // 实时预览文字条是否显示
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
    // 原始热键(去掉"热键 "前缀)
    ui.hotkey = s.replace(/^热键\s*/, '').trim();
    if (el.hotkey) {
      // 只把按键组合写进 <kbd>:"热键"两个字是 HTML 里的静态标签
      el.hotkey.textContent = ui.hotkey || '未设置';
    }
  }

  /* 主题切换:在 <html> 上设 data-theme。

   * auto 时**移除属性** —— 这样 CSS 的 prefers-color-scheme 媒体查询
   * 自然接管(跟随系统),不需要 JS 去监听系统主题的变化。
   * CSS 那边三段:浅色是 :root,深色由
   *   @media(dark){:root:not([data-theme="light"])} 和
   *   :root[data-theme="dark"]
   * 给出 —— 强制白天时媒体查询那一段会被 :not 排除掉,压不过我们。 */
  function applyTheme(mode) {
    ensureDom();
    var m = THEMES.indexOf(mode) >= 0 ? mode : 'auto';
    ui.theme = m;

    if (m === 'auto') {
      document.documentElement.removeAttribute('data-theme');
    } else {
      document.documentElement.setAttribute('data-theme', m);
    }

    // 当前档位高亮
    if (el.themeSeg) {
      var items = el.themeSeg.querySelectorAll('.seg-item');
      for (var i = 0; i < items.length; i++) {
        var isActive = items[i].getAttribute('data-theme-mode') === m;
        items[i].classList.toggle('is-active', isActive);
      }
    }
  }

  /* ==================== 胶囊位置 / 预览开关 ==================== */

  /* 胶囊位置档位:follow(跟随输入光标) / bottom(固定底部) / custom(自定义)。
   * 改的是**胶囊**的行为,面板只负责把档位记下来并广播出去。 */
  function applyCapsulePosition(mode) {
    ensureDom();
    var m = POSITION_MODES.indexOf(mode) >= 0 ? mode : 'follow';
    ui.position = m;

    if (el.positionSeg) {
      var items = el.positionSeg.querySelectorAll('.seg-item');
      for (var i = 0; i < items.length; i++) {
        var isActive = items[i].getAttribute('data-position-mode') === m;
        items[i].classList.toggle('is-active', isActive);
      }
    }
  }

  /* 预览开关:关掉之后录音时不再显示实时预览文字条,
   * 胶囊里只剩波形+计时+按钮(提示改在胶囊内的 label 位)。 */
  function applyPreview(enabled) {
    ensureDom();
    ui.preview = !!enabled;
    if (el.previewToggle) {
      el.previewToggle.checked = ui.preview;
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

    // 主题切换
    if (el.themeSeg) {
      el.themeSeg.addEventListener('click', function (e) {
        var item = e.target && e.target.closest ? e.target.closest('.seg-item') : null;
        if (!item) {
          return;
        }
        var mode = item.getAttribute('data-theme-mode');
        if (!mode) {
          return;
        }
        applyTheme(mode);   // 先本地生效，Python 随后会用 setTheme 校正
        callApi('set_theme', mode);
      });
    }

    // 标题栏拖动
    if (el.titlebar) {
      el.titlebar.addEventListener('mousedown', onDragStart, false);
    }

    // 胶囊位置档位
    if (el.positionSeg) {
      el.positionSeg.addEventListener('click', function (e) {
        var item = e.target && e.target.closest ? e.target.closest('.seg-item') : null;
        if (!item) {
          return;
        }
        var mode = item.getAttribute('data-position-mode');
        if (!mode) {
          return;
        }
        applyCapsulePosition(mode);   // 先本地高亮，Python 随后会用 setCapsulePosition 校正
        callApi('set_capsule_position', mode);
      });
    }

    // 预览开关
    if (el.previewToggle) {
      el.previewToggle.addEventListener('change', function () {
        var on = !!el.previewToggle.checked;
        applyPreview(on);             // 先本地生效，Python 随后会用 setPreviewEnabled 校正
        callApi('set_preview', on);
      });
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
    },
    setTheme: function (mode) {
      applyTheme(mode);
    },
    setCapsulePosition: function (mode) {
      applyCapsulePosition(mode);
    },
    setPreviewEnabled: function (enabled) {
      applyPreview(enabled);
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
    el.themeSeg = $('themeSeg');
    el.positionSeg = $('positionSeg');
    el.previewToggle = $('previewToggle');
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
    applyTheme('auto');   // Python 就绪后会用 setTheme 推真实档位
    applyCapsulePosition('follow');   // 同理:setCapsulePosition 会推真实档位
    applyPreview(true);               // 同理:setPreviewEnabled 会推真实值

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
