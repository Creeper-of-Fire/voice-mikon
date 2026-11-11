/* qwen3-voice 语音输入面板 —— 前端逻辑
 *
 * 纯原生 JS：零 npm、零框架、零 CDN、零外部字体/图标请求，离线可用。
 *
 * 面板瘦身为**配置窗口**：只看状态、切模型、退出。
 * 录音过程的反馈(实时预览、电平条、计时)在胶囊窗口那边
 * (capsule.html / capsule.css / capsule.js),那边是另一套独立文件。
 *
 * 桥接契约(方法名必须与 Python 侧 _PanelApi 逐字一致):
 *   Python → JS : window.__panel.setState / setHealth / setModel / setBackend
 *                 / setTheme / setPreviewEnabled / setOrbEnabled / setStandby
 *                 / setNotice / setHotkey
 *   JS → Python : window.pywebview.api.ready / switch_model / switch_backend
 *                 / set_theme / set_preview / set_orb / set_standby
 *                 / close_panel / quit_app
 *                 / begin_drag / move_window / end_drag
 *
 * ⚠ 拖动那三个(begin_drag / move_window / end_drag)是"拖拽会话"的实现
 *   细节:按下 → 拖 → 松手的序列必须成对出现,业务侧别单独调。
 *
 * ⚠ 位置档位(胶囊位置 / 球位置)已随重构退役,这里**不再有**对应的
 *   UI 与调用 —— 悬浮球只留"开不开"和"待不待机"两件事。
 */
(function () {
  'use strict';

  /* ==================== 常量 ==================== */

  // 契约定义的状态集合(core 的 AppState.state)
  var STATES = ['starting', 'idle', 'recording', 'processing'];

  // 主题档位：auto = 跟随系统,light = 白天,dark = 黑夜
  var THEMES = ['auto', 'light', 'dark'];

  // 识别后端：local = 本地模型,minimax = MiniMax 网络接口（互斥）
  var BACKENDS = ['local', 'minimax'];

  // 悬浮球待机策略：resident = 窗口常驻,on_demand = 待机时销毁窗口
  var STANDBY = ['resident', 'on_demand'];

  // 后端健康三态：loading = 加载中,ready = 就绪,failed = 失败
  var HEALTHS = ['loading', 'ready', 'failed'];

  // 提示语自动消失的时间（毫秒）
  var NOTICE_MS = 8000;

  // Python 还没推热键时的兜底文案
  var DEFAULT_HOTKEY = 'Ctrl+Alt+Space';

  /* ==================== 运行状态 ==================== */

  var ui = {
    state: 'idle',          // 当前状态机
    health: 'loading',      // 后端健康:loading / ready / failed
    model: '0.6B',          // 当前模型
    hotkey: '',             // 当前热键（原始字符串，不含"热键"前缀）
    theme: 'auto',          // 当前主题档位
    preview: true,          // 实时预览文字条是否显示
    orb: false,             // 常驻悬浮球是否打开
    standby: 'resident',    // 悬浮球待机策略
    backend: 'local'        // 当前识别后端
  };

  var el = {};             // DOM 缓存
  var noticeTimer = 0;     // 提示语自动消失的定时器

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
    processing: '识别中'
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
    // 状态文字（面板的状态区）
    if (el.stateText) {
      el.stateText.textContent = STATE_TEXT[state] || state;
    }
  }

  /* 后端健康：loading / ready / failed(+ 原因)。
   *
   * 三态都要看得见 —— "切换/加载失败要有反馈"是这块的正身:
   * 只在失败时才提示的话,用户等模型加载的那几十秒里什么也看不到,
   * 分不清是"在加载"还是"卡死了"。 */
  var HEALTH_TEXT = {
    loading: '加载中',
    ready: '就绪',
    failed: '失败'
  };

  function applyHealth(status, reason) {
    ensureDom();
    var s = HEALTHS.indexOf(status) >= 0 ? status : 'loading';
    var why = reason == null ? '' : String(reason);
    ui.health = s;

    if (el.healthBadge) {
      el.healthBadge.setAttribute('data-health', s);
      el.healthBadge.textContent = HEALTH_TEXT[s] || s;
      el.healthBadge.title = why || HEALTH_TEXT[s] || s;
    }
    // 失败原因常驻显示：原因才是有用的那半截
    if (el.healthReason) {
      el.healthReason.textContent = (s === 'failed') ? why : '';
      if ('hidden' in el.healthReason) {
        el.healthReason.hidden = !(s === 'failed' && why);
      }
    }
  }

  function applyModel(tag) {
    ensureDom();
    ui.model = tag;
    if (el.modelSeg) {
      var items = el.modelSeg.querySelectorAll('.seg-item');
      for (var i = 0; i < items.length; i++) {
        var isActive = items[i].getAttribute('data-model') === tag;
        items[i].classList.toggle('is-active', isActive);
      }
    }
    // 网络后端下这一行显示的是远端模型名,不是档位 —— 跟着刷新。
    renderNetModel();
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
   *
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

  /* ==================== 预览开关 / 悬浮球 ==================== */

  /* 预览开关:关掉之后录音时不再显示实时预览文字条,
   * 胶囊里只剩波形+计时+按钮(提示改在胶囊内的 label 位)。 */
  function applyPreview(enabled) {
    ensureDom();
    ui.preview = !!enabled;
    if (el.previewToggle) {
      el.previewToggle.checked = ui.preview;
    }
  }

  /* 常驻悬浮球开关:打开后屏幕上常驻一颗小球,点它开始录音,
   * 拖它挪位置;关掉后回到"按热键才弹胶囊"的老样子。 */
  function applyOrb(enabled) {
    ensureDom();
    ui.orb = !!enabled;
    if (el.orbToggle) {
      el.orbToggle.checked = ui.orb;
    }
  }

  /* 悬浮球的待机策略:resident(窗口常驻) / on_demand(待机时销毁窗口)。 */
  function applyStandby(mode) {
    ensureDom();
    var m = STANDBY.indexOf(mode) >= 0 ? mode : 'resident';
    ui.standby = m;

    if (el.standbySeg) {
      var items = el.standbySeg.querySelectorAll('.seg-item');
      for (var i = 0; i < items.length; i++) {
        var isActive = items[i].getAttribute('data-standby') === m;
        items[i].classList.toggle('is-active', isActive);
      }
    }
  }

  /* 识别后端:local(本地模型)/ minimax(MiniMax 网络接口)。
   *
   * 两者**互斥**:切到网络会卸载本地模型(释放显存),切回来再重新加载。
   *
   * **预览开关不在这里被置灰** —— 有没有预览只由用户开关决定,
   * 引擎不参与:网络后端同样跑分句伪流式,与本地流式平级。 */
  function applyBackend(kind) {
    ensureDom();
    var k = BACKENDS.indexOf(kind) >= 0 ? kind : 'local';
    ui.backend = k;

    if (el.backendSeg) {
      var items = el.backendSeg.querySelectorAll('.seg-item');
      for (var i = 0; i < items.length; i++) {
        var isActive = items[i].getAttribute('data-backend') === k;
        items[i].classList.toggle('is-active', isActive);
      }
    }

    // 模型行:本地后端下是 0.6B/1.7B 的切换档,网络后端下是
    // **远端模型名**(只读)。同一行显示不同的东西 —— 后端决定它是什么。
    var isNet = (k === 'minimax');
    if (el.modelSeg) {
      el.modelSeg.hidden = isNet;
    }
    renderNetModel();
  }

  /* 网络后端下的模型名(远端模型,如 asr-1.0)。
   *
   * 值来自 Python 推来的 setModel —— 后端是网络时它推的是远端模型名,
   * 不是本地模型规格,所以不用再单独开一条推送通道。 */
  function renderNetModel() {
    if (!el.netModelText) {
      return;
    }
    var isNet = (ui.backend === 'minimax');
    el.netModelText.textContent = isNet ? (ui.model || '') : '';
    el.netModelText.hidden = !isNet;
  }

  /* 提示语(切换后端的进展/拒绝/失败)。过一会儿自己淡掉,
   * 不占地方也不挡住用户点别的。 */
  function applyNotice(text) {
    ensureDom();
    if (!el.notice) {
      return;
    }
    var s = text == null ? '' : String(text);
    el.notice.textContent = s;
    if (noticeTimer) {
      clearTimeout(noticeTimer);
      noticeTimer = 0;
    }
    if (s) {
      noticeTimer = setTimeout(function () {
        if (el.notice) {
          el.notice.textContent = '';
        }
        noticeTimer = 0;
      }, NOTICE_MS);
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
   * 架构下死锁 js_api 调用链,整个面板点什么都卡。)
   *
   * 阈值:标题栏是纯拖动把手,按下即拖(threshold=0)—— 面板没有
   * "点一下要干别的事"的球形态,不需要位移判据。 */
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
    // 告诉 Python:这一轮开始拖了(Python 侧据此冻结/归还焦点,
    // 并在松手时记住位置)
    callApi('begin_drag');
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
    // 松手:Python 侧归还焦点 + 记住位置(拖过才记)
    callApi('end_drag');
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

    // 预览开关
    if (el.previewToggle) {
      el.previewToggle.addEventListener('change', function () {
        var on = !!el.previewToggle.checked;
        applyPreview(on);             // 先本地生效，Python 随后会用 setPreviewEnabled 校正
        callApi('set_preview', on);
      });
    }

    // 常驻悬浮球开关
    if (el.orbToggle) {
      el.orbToggle.addEventListener('change', function () {
        var on = !!el.orbToggle.checked;
        applyOrb(on);                 // 先本地生效，Python 随后会用 setOrbEnabled 校正
        callApi('set_orb', on);
      });
    }

    // 悬浮球的待机策略
    if (el.standbySeg) {
      el.standbySeg.addEventListener('click', function (e) {
        var item = e.target && e.target.closest ? e.target.closest('.seg-item') : null;
        if (!item) {
          return;
        }
        var mode = item.getAttribute('data-standby');
        if (!mode) {
          return;
        }
        applyStandby(mode);   // 先本地高亮，Python 随后会用 setStandby 校正
        callApi('set_standby', mode);
      });
    }

    // 识别后端切换（本地 ↔ 网络）。切换在 Python 侧的后台线程里做，
    // 成功后 Python 会用 setBackend 校正档位；
    // 录音/识别中被拒绝时，Python 会用 setNotice 说明原因。
    if (el.backendSeg) {
      el.backendSeg.addEventListener('click', function (e) {
        var item = e.target && e.target.closest ? e.target.closest('.seg-item') : null;
        if (!item) {
          return;
        }
        var kind = item.getAttribute('data-backend');
        if (!kind) {
          return;
        }
        applyBackend(kind);   // 先本地高亮，Python 随后会用 setBackend 校正
        callApi('switch_backend', kind);
      });
    }
  }

  /* ==================== 对外接口 ==================== */

  // Python → JS：方法名必须与 host/panel.py 的推送一致
  window.__panel = {
    setState: function (state) {
      applyState(state);
    },
    setHealth: function (status, reason) {
      applyHealth(status, reason);
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
    setPreviewEnabled: function (enabled) {
      applyPreview(enabled);
    },
    setOrbEnabled: function (enabled) {
      applyOrb(enabled);
    },
    setStandby: function (mode) {
      applyStandby(mode);
    },
    setBackend: function (kind) {
      applyBackend(kind);
    },
    setNotice: function (text) {
      applyNotice(text);
    }
  };

  /* ==================== 启动 ==================== */

  // 绑定 DOM 引用（可重复调用）
  function bindDom() {
    el.dot = $('stateDot');
    el.stateText = $('stateText');
    el.healthBadge = $('healthBadge');
    el.healthReason = $('healthReason');
    el.titlebar = $('titlebar');
    el.closeBtn = $('closeBtn');
    el.quitBtn = $('quitBtn');
    el.backendSeg = $('backendSeg');
    el.modelSeg = $('modelSeg');
    el.netModelText = $('netModelText');
    el.themeSeg = $('themeSeg');
    el.previewToggle = $('previewToggle');
    el.orbToggle = $('orbToggle');
    el.standbySeg = $('standbySeg');
    el.hotkey = $('hotkeyLabel');
    el.notice = $('notice');
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
    applyHealth('loading', '');
    applyModel('0.6B');
    applyHotkey(DEFAULT_HOTKEY);
    applyTheme('auto');        // Python 就绪后会用 setTheme 推真实档位
    applyPreview(true);        // 同理:setPreviewEnabled 会推真实值
    applyOrb(false);           // 同理:setOrbEnabled 会推真实值
    applyStandby('resident');  // 同理:setStandby 会推真实档位
    applyBackend('local');     // 同理:setBackend 会推真实后端

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
