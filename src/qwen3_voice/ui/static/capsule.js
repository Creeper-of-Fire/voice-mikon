/* qwen3-voice 录音胶囊 —— 前端逻辑
 *
 * 纯原生 JS：零 npm、零框架、零 CDN、零外部字体/图标请求，离线可用。
 *
 * 胶囊的**外观**(胶囊形状 / 按钮 / 阶段切换)**移植自 OpenType**
 * (https://github.com/ct-jyjntc/opentype-public) 的
 * ``src/renderer/floating-bar.tsx`` —— AGPL-3.0。
 * 本项目整体按 AGPL-3.0-or-later 发布(见 LICENSE)。
 *
 * 与移植版的差异(适配改动,不是照抄):
 *   - **波形是本项目自有的,不是 OpenType 的**:按 MiniMax Code 的
 *     ``asr-volume-bars`` 规格做**滚动电平条**(13 根定宽小条,从右往左
 *     滚、三槽轮转、带过渡插值)。用户最初就定了"音量条保持 MiniMax
 *     Code 规格,其他随便学 OpenType" —— 波形区曾经被"移植 OpenType"
 *     的指令波及,现在修回来(旧面板时代就是这么做的,见 a1a3ea2)。
 *   - 计时是本项目自有的(OpenType 的胶囊没有计时)。
 *   - 图标用内联 SVG(Lucide 的 X / Check / Info / LoaderCircle),
 *     不用 unicode 字符凑合。
 *
 * 桥接契约（唯一权威来源 ../BRIDGE.md），方法名必须逐字一致，不得增删改名：
 *   Python → JS : window.__capsule.setState / setLevels / setElapsed
 *                 / setPreview / setResult / setError / setCancelled
 *                 / resetForRecording / setTheme
 *                 / setPositionMode / setPreviewEnabled
 *                 / setOrbMode / setOrbPosition / setHideDelay
 *   JS → Python : window.pywebview.api.ready / cancel_recording / stop_recording
 *                 / update_regions / move_capsule / begin_drag / end_drag
 *                 / copy_result / start_recording / move_orb
 */
(function () {
  'use strict';

  /* ==================== 常量 ==================== */

  /* ---- 滚动电平条(对齐 MiniMax Code 的 asr-volume-bars)----
     13 根定宽小条,从右往左滚动。旧面板时代就是这么做的
     (面板那 22 根是因为面板更宽;胶囊窄,取 MCode 原版的 13 根)。 */

  // 同时显示的条数:13 根(MiniMax Code 原版规格)
  var BAR_COUNT = 13;

  // 条宽 / 条间距(px):步进 = 宽 + 间距 = 6px,与 capsule.css 保持一致
  var BAR_WIDTH = 3;
  var BAR_GAP = 3;
  var BAR_STEP = BAR_WIDTH + BAR_GAP;

  // 每推进一根条的周期(ms)
  //
  // 140ms ≈ 7 根/秒,13 根可见 → 1.8s 滚一圈。
  // 原先是 80ms(12.5 根/秒,1.04s 一圈) —— 用户嫌"动得很快",
  // 底噪那几条还在动,整条看着像一直有反应。慢下来之后,
  // 说话的起伏才看得出来。
  var BAR_TICK_MS = 140;

  // 元素池 = 可见条数 + 1:多出来的那根已经滑出视口左侧,
  // 用来"回收"(复用同一个 DOM 节点当新条),省得新条横穿屏幕飞进来
  var POOL_SIZE = BAR_COUNT + 1;

  // 静音死区:响度 <= 0.05 视为"没有可显示的声音",画成 2px 的灰条
  //
  // 静音判定的大头在**后端**:Recorder 持有底噪估计(滑动窗口 p10),
  // 推给前端的电平已经按"底噪×1.5"的门归过零 —— 底噪到前端就是 0。
  // (固定阈值在环境面前活不过一次换麦:曾按 -62 dBFS 标定 0.04,
  // 换环境后底噪抬高 9 dB,安静时电平条 65% 满格。)
  //
  // 这里只剩两个小职责:
  //   1. 门限边缘的过渡帧(比"底噪×1.5"略高一点点)映射出来接近 0,
  //      死区让它们不闪成小条;
  //   2. 电平映射曲线最低端(DEAD_ZONE ~ 0.3)保持灵敏度,别让轻声
  //      开头看着像没说话。
  var DEAD_ZONE = 0.05;

  // 条高上下限(px):静音 2px,最响 20px
  var SILENT_HEIGHT = 2;
  var MAX_HEIGHT = 20;

  // 4px 正好是死区上界对应的高度(见 mapHeight);
  // 不高于它就算静音条,画灰色
  var SILENT_MAX_PX = 4;

  /* 主题档位:auto = 跟随系统,light = 白天,dark = 黑夜 */
  var THEMES = ['auto', 'light', 'dark'];

  /* 胶囊位置档位:follow = 跟随输入光标,bottom = 固定底部,custom = 自定义 */
  var POSITION_MODES = ['follow', 'bottom', 'custom'];

  /* 胶囊各阶段的中文文案 */
  var PHASE_TEXT = {
    recording: '',
    processing: '识别中…',
    done: '已输入',
    error: '出错了',
    /* 警告态:识别是成功的,内容也在(剪贴板里),只是没送进目标窗口。
       胶囊里只放这么一句短的 —— 详细报告在上面的文字条里,
       一长串塞进胶囊会把胶囊撑得很宽,看着奇怪。 */
    warning: '没送出去',
    cancelled: '已取消'
  };

  // 阶段类名(和 OpenType 一样用 class 挂在胶囊上)
  var PHASES = ['recording', 'processing', 'done', 'error', 'warning', 'cancelled'];

  // 文字条在还没有内容时的占位
  var PLACEHOLDER = '正在聆听…';

  /* ==================== 运行状态 ==================== */

  var ui = {
    state: 'idle',      // 控制器状态机
    phase: 'recording', // 胶囊自己的阶段
    level: 0,           // 当前（平滑后）的响度 0..1
    elapsed: 0,         // 录音计时（秒）
    theme: 'auto',      // 主题档位
    positionMode: 'follow', // 胶囊位置档位（只有 custom 模式允许拖动）
    previewEnabled: true,   // 实时预览文字条是否显示
    errorPayload: '',       // 错误态里可复制的原文(注入失败时带着识别文本)
    copying: false,         // 复制进行中(防连点)
    orbMode: false,         // 常驻悬浮球模式(球形态 ↔ 胶囊形态)
    expanded: false,        // 球有没有展开成胶囊
    orbPosition: 'follow',  // 球的位置档位(follow / pinned / custom)
    orbDraggable: false,    // 只有 follow 不能拖(pinned/custom 的位置归用户管)
    hideDelay: 1500         // 一轮结束后停多久再收回球(毫秒,Python 推的)
  };

  var el = {};              // DOM 缓存

  // 滚动电平条的运行状态
  var pool = [];            // 条的 DOM 节点:pool[0] 最新,pool[末位] 最老
  var ages = [];            // 每根条的"年龄":0 = 最新,越大越老
  var slotIndex = 0;        // 三槽轮转指针
  var lastBarHeight = 0;    // 上一根条的像素高度(做过渡插值用)
  var barTimer = 0;         // 推进条的定时器
  var barsRunning = false;  // 条是不是正在跑(避免重复调用时重置)
  var smoother = { current: 0 };   // 电平滑器当前值

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
      // pywebview 会包成 Promise，失败不影响胶囊
      if (ret && typeof ret.then === 'function') {
        ret.then(null, function () {});
      }
      return true;
    } catch (e) {
      return false;
    }
  }

  // 调 Python 并**等它的返回值**。
  // pywebview 会把 Python 函数的返回值包成 Promise ——「复制」按钮要靠
  // 这个返回值判断"到底写进去了没有",不能像别处那样把 Promise 吞掉。
  function callApiAsync(name) {
    var args = Array.prototype.slice.call(arguments, 1);
    try {
      var api = window.pywebview && window.pywebview.api;
      if (!api || typeof api[name] !== 'function') {
        return Promise.resolve(false);
      }
      return Promise.resolve(api[name].apply(api, args)).then(function (ret) {
        return ret === true;
      }, function () {
        return false;
      });
    } catch (e) {
      return Promise.resolve(false);
    }
  }

  // DOM 就绪后通知 Python；pywebview 的注入可能比 DOMContentLoaded 晚，做有限次重试
  function notifyReady(attempt) {
    if (callApi('ready')) {
      apiReady = true;
      // 桥接通了才谈得上上报区域(之前发了 Python 也收不到)
      scheduleRegionReport();
      return;
    }
    if (attempt >= 200) {
      return;
    }
    window.setTimeout(function () {
      notifyReady(attempt + 1);
    }, 50);
  }

  /* ==================== 窗口区域上报(SetWindowRgn 裁形) ==================== */

  /* 胶囊窗口是**固定尺寸**的透明窗口 —— 不能 resize,resize 透明就废
     (见 capsule.py 的 WIN_H 注释)。可见内容只有"文字条 + 胶囊"两块,
     其余是透明空白;那片空白虽然看不见,却**属于窗口** —— 鼠标点上去
     会被胶囊吃掉,底下的应用就点不到了。

     Python 侧用 SetWindowRgn 把窗口形状裁成这两块内容的并集,空白区
     就不再属于窗口,点击直接穿透。这里负责把矩形量出来上报:坐标用
     getBoundingClientRect()(CSS 逻辑像素、相对视口),Python 侧再乘
     DPI 缩放换成物理像素。 */

  // 合并窗口:preview 高频刷新时不用每次都往 Python 发一次
  var REGION_THROTTLE_MS = 60;
  var regionTimer = 0;
  var apiReady = false;

  // 量一个元素的矩形;隐藏(display:none)时返回全 0
  function rectOf(node) {
    if (!node) {
      return [0, 0, 0, 0];
    }
    var r = node.getBoundingClientRect();
    if (!r || (r.width <= 0 && r.height <= 0)) {
      return [0, 0, 0, 0];
    }
    /* 位置和尺寸都取 offset* —— 它们是**布局**尺寸/位置,不受 transform 影响。
     *
     * getBoundingClientRect() 会把 transform 一起算进去,量到的是
     * "动画中"的矩形;Python 侧按它裁窗口(SetWindowRgn),等于按缩小
     * 后的形状去裁 —— 描边的两端就被切掉了(实测:上下边线在两端断开)。
     *
     * 球 ↔ 胶囊的形变现在**没有 transform**(宽度靠 max-width 过渡),
     * 所以布局尺寸 = 视觉尺寸;但仍取 offset*:万一以后给谁加回
     * transform,这条规矩就是最后一道保险。
     *
     * html/body 都没有 margin 也没有 border,offsetParent 就是视口原点,
     * offsetLeft/offsetTop 与视口坐标一致。
     * 拿不到 offset*(老浏览器、测试用的假 DOM)时退回 getBoundingClientRect。 */
    var w = node.offsetWidth || r.width;
    var h = node.offsetHeight || r.height;
    var x = (typeof node.offsetLeft === 'number') ? node.offsetLeft : r.left;
    var y = (typeof node.offsetTop === 'number') ? node.offsetTop : r.top;
    // 只留两位小数:再多只是给 Python 添乱
    return [
      Math.round(x * 100) / 100,
      Math.round(y * 100) / 100,
      Math.round(w * 100) / 100,
      Math.round(h * 100) / 100
    ];
  }

  function measureRegions() {
    // 常驻球模式下:待机时窗口里只有"球"那一小块可见 —— 而球**就是**
    // 胶囊收窄后的样子(40×40),所以命中区报胶囊那一块就够了。
    //
    // 文字条报全 0:待机时它虽然还占着布局(opacity:0),但那一块
    // 是透明的,不能吃底下应用的点击。
    //
    // 浮层(右键菜单 / 操作提示)单独报一块 **overlay**,**不能**并进
    // 球的矩形:并进去就成了"球到菜单"的大包围盒,那块中间全是空白,
    // 空白在窗口裁形里会露出 WebView2 客户区的灰底(实测:球上方罩着
    // 一大片灰色)。分开报,Python 才能一块裁成圆、一块裁成方。
    if (ui.orbMode && !ui.expanded) {
      var overlay = [0, 0, 0, 0];
      var menuRect = rectOf(el.orbMenu);
      if (menuRect[2] > 0 && menuRect[3] > 0) {
        overlay = menuRect;
      } else {
        var hintRect = rectOf(el.orbHint);
        if (hintRect[2] > 0 && hintRect[3] > 0) {
          overlay = hintRect;
        }
      }
      return {
        textbar: [0, 0, 0, 0],
        pill: rectOf(el.pill),
        overlay: overlay
      };
    }
    return {
      textbar: rectOf(el.textbar),
      pill: rectOf(el.pill),
      overlay: [0, 0, 0, 0]
    };
  }

  // 把可见内容的矩形上报给 Python。
  // 节流用"尾沿触发":60ms 内的多次变化合并成一次,而且**最后一次一定
  // 发得出去**(不是掐头)—— 少发一次,窗口形状就停在旧尺寸上。
  function reportRegions() {
    regionTimer = 0;
    if (!apiReady) {
      return;
    }
    var payload = measureRegions();
    // 页面还没排版(可见内容的尺寸全 0)时不上报:那不是"内容隐藏",
    // 是"还没量准",报上去会把可见内容裁没了
    if (payload.textbar[2] <= 0 && payload.textbar[3] <= 0
        && payload.pill[2] <= 0 && payload.pill[3] <= 0) {
      return;
    }
    callApi('update_regions', payload);
  }

  function scheduleRegionReport() {
    if (regionTimer) {
      return;   // 已经在等尾沿了,这次变化跟下一次一起发
    }
    regionTimer = window.setTimeout(reportRegions, REGION_THROTTLE_MS);
  }

  /* 动效**结束**后要重新量一次命中区。
   *
   * 为什么:展开/收起期间胶囊带着 transform(translateY / scale),
   * getBoundingClientRect() 会把 transform 一起算进去 —— 量到的是
   * "动画中"的矩形。Python 侧按这个矩形裁窗口(SetWindowRgn),
   * 等于按缩小 10% 的形状去裁,胶囊描边的两端就被切掉了。
   * 更糟的是动画停下来之后**没人再上报**,裁形就永久停在那个
   * 错位状态 —— 实测展开后胶囊上下边线在两端断开。
   *
   * 两道保险:
   *   1. transitionend —— 动画真正停下来的那一刻重报;
   *   2. 定时器兜底 —— prefers-reduced-motion 下 transition 是 none,
   *      压根不会触发 transitionend,只能靠定时器。
   *
   * 1060ms:形变最长是 .8s(球拉成胶囊),留够余量。 */
  var REGION_REPORT_AFTER_MOTION_MS = 1060;

  function scheduleRegionReportAfterMotion() {
    window.setTimeout(reportRegions, REGION_REPORT_AFTER_MOTION_MS);
  }

  /* 形变期间要**持续**上报命中区。
   *
   * 球 ↔ 胶囊是同一个元素长开:宽度在 0.8s 里从 40px 变到 ~190px。
   * 过渡期间布局宽度每帧都在变(浏览器会重排),命中区也得跟着变 ——
   * 只在头尾各报一次的话,中间那 0.8s 里窗口形状停在"球"那一小块,
   * 胶囊多出来的那部分点不到(会穿透到底下的应用)。
   *
   * 50ms 一次:够跟得上,又不会把 SetWindowRgn 打成幻灯片。 */
  var MORPH_REPORT_MS = 50;
  /* 形变最长 .8s(CSS 里的 max-width 过渡),这个兜底必须比它长 ——
     早先是 .3s 的过渡配 .45s 的兜底,正好卡在边上。 */
  var MORPH_REPORT_MAX_MS = 1060;
  var morphTimer = 0;
  var morphStopTimer = 0;

  function startMorphReport() {
    stopMorphReport();
    morphTimer = window.setInterval(reportRegions, MORPH_REPORT_MS);
    // 兜底:过渡最长 .8s,1.06s 之后无论如何都停(别让定时器一直跑)
    morphStopTimer = window.setTimeout(stopMorphReport, MORPH_REPORT_MAX_MS);
  }

  function stopMorphReport() {
    if (morphTimer) {
      window.clearInterval(morphTimer);
      morphTimer = 0;
    }
    if (morphStopTimer) {
      window.clearTimeout(morphStopTimer);
      morphStopTimer = 0;
    }
  }

  // 动效停下来时重报命中区(见 scheduleRegionReportAfterMotion 的说明)。
  // 只认**自己**身上的 transitionend —— 事件会冒泡,子元素(按钮)的
  // 过渡冒上来不算。
  function bindMotionEnd(node) {
    if (!node || typeof node.addEventListener !== 'function') {
      return;
    }
    node.addEventListener('transitionend', function (e) {
      if (e && e.target && e.target !== node) {
        return;
      }
      reportRegions();
    });
  }

  /* ==================== 主题 ==================== */

  /* 主题切换:在 <html> 上设 data-theme。

   * auto 时**移除属性** —— 这样 CSS 的 prefers-color-scheme 媒体查询
   * 自然接管(跟随系统),不需要 JS 去监听系统主题的变化。
   * 和 panel.js 是同一套机制(见 capsule.css 的主题说明)。 */
  function applyTheme(mode) {
    var m = THEMES.indexOf(mode) >= 0 ? mode : 'auto';
    ui.theme = m;
    if (!document.documentElement) {
      return;
    }
    if (m === 'auto') {
      document.documentElement.removeAttribute('data-theme');
    } else {
      document.documentElement.setAttribute('data-theme', m);
    }
  }

  /* ==================== 位置模式 / 预览开关 ==================== */

  /* 位置模式:只有 custom 模式允许拖动胶囊。
   * 跟随输入模式下拖了也没意义 —— 下一轮录音还是回到光标那里。 */
  function applyPositionMode(mode) {
    var m = POSITION_MODES.indexOf(mode) >= 0 ? mode : 'follow';
    ui.positionMode = m;
    if (document.body) {
      document.body.classList.toggle('can-drag', m === 'custom');
    }
  }

  /* 预览开关:关掉之后文字条整块消失(胶囊只剩波形+计时+按钮),
   * "已输入"/错误提示改在胶囊里的 label 位显示。 */
  function applyPreviewEnabled(enabled) {
    ui.previewEnabled = !!enabled;
    if (document.body) {
      document.body.classList.toggle('no-preview', !ui.previewEnabled);
    }
    // 两种情况都要把文字条清空:留着上一轮的旧内容,
    // 切回"开"的时候会闪一下
    if (el.textbar) {
      el.textbar.classList.remove('is-error', 'is-warning', 'is-cancelled', 'has-report');
      el.textbar.classList.add('is-empty');
    }
    if (el.textbarReport) {
      el.textbarReport.textContent = '';
    }
    if (el.textbarText) {
      el.textbarText.textContent = '';
    }
    scheduleRegionReport();
  }

  /* ==================== 常驻悬浮球 ==================== */

  /* 悬浮球和胶囊是**同一个窗口**里的两种形态,而且是**同一个元素**:
     球就是这颗胶囊收窄成 40px 的正圆(见 capsule.css 的「常驻悬浮球」)。
     待机时点它 → 胶囊原地横向长开 → 开始录音;录完收回成球,
     继续常驻等下一轮。
     常驻模式关掉时,这一整段都不生效(按热键才弹胶囊的老样子)。

     位置:跟随档下球跟着输入光标走;记住位置档在用户拖出来的地方
    露脸(光标聚焦到输入处才出现);常驻档一直在(两档共用那个
    记住的位置)。 */

  // 点击与拖动的分界:按下后位移超过这么多**逻辑**像素才算拖动。
  //
  // e.screenX/Y 就是 CSS 像素(= 逻辑像素),5px 的意思就是"手抖一下"。
  // 太小:想点击却被当成拖动,球不响应;太大:想拖动却触发了录音。
  var ORB_DRAG_THRESHOLD = 5;

  var orbDrag = {
    pressing: false,
    dragging: false,
    startScreenX: 0,
    startScreenY: 0,
    startWinX: 0,
    startWinY: 0
  };

  // 球形态 ↔ 胶囊形态。动效全在 CSS 里(见 capsule.css 的 orb-mode 一节),
  // 这里只切 class。
  function setExpanded(expanded) {
    ui.expanded = !!expanded;
    if (document.body) {
      // 只有常驻球模式才有"球形态 / 胶囊形态"两个 class ——
      // 非常驻模式下胶囊一直就是胶囊,不该带上 orb-idle。
      if (ui.orbMode) {
        document.body.classList.toggle('orb-expanded', ui.expanded);
        document.body.classList.toggle('orb-idle', !ui.expanded);
      } else {
        document.body.classList.remove('orb-expanded');
        document.body.classList.remove('orb-idle');
        stopMorphReport();
      }
    }
    // 形状变了 → 命中区要重报。
    if (ui.orbMode) {
      // 形变是 0.8s 的宽度过渡,期间宽度一直在变,所以要**持续**上报,
      // 不能只在头尾报一次(见 startMorphReport)。
      startMorphReport();
    }
    scheduleRegionReport();
    scheduleRegionReportAfterMotion();
    // 波形条:收起时立刻停(看不见,白烧 CPU);
    // 展开时**等形变落定**再启动 —— 条子在动 + 胶囊在变宽,
    // 两件事叠在一起,在透明窗口上容易糊成一片残影。
    if (ui.orbMode) {
      if (ui.expanded) {
        scheduleWaveformStart();
      } else {
        stopBars();
        barsRunning = false;
      }
    }
  }

  /* 展开后什么时候启动波形条。
   *
   * 形变期间胶囊在变宽,这时条子也在动 —— 两件事叠在一起,
   * 在 WebView2 的透明窗口上容易糊成一片。所以等形变落定
   * (80% + 余量)再启动条子。
   *
   * 注意:**条子本身早就生成好了**(resetForRecording 里先生成),
   * 占好了宽度 —— 胶囊的宽度一次到位,不会在动画启动前后再跳一截。
   * 这里延后的只是"条子开始动"这件事。 */
  var WAVEFORM_START_DELAY_MS = 620;

  function scheduleWaveformStart() {
    stopBars();
    barsRunning = false;
    window.setTimeout(function () {
      // 这期间可能已经取消录音了(点错了/又按了热键)
      if (ui.phase === 'recording') {
        // 条子应该早就生成好了(占宽度);万一没有,补生成一次。
        if (!pool.length) {
          resetLevelBars();
        } else {
          startBars();
        }
        barsRunning = true;
      }
    }, WAVEFORM_START_DELAY_MS);
  }

  /* 球的位置档位(follow / pinned / custom)。
   *
   * 只有 follow 档不响应拖动 —— pinned/custom 的位置都归用户管
   * (共用一份持久化的 orb_geometry),拖了会记住。follow 的位置由
   * 光标决定,拖了也会被拉回去:直接不响应,光标形状也跟着改掉,
   * 别给出"能拖"的假象(orb-follow class 给 CSS 压 cursor 用)。 */
  var ORB_POSITION_MODES = ['follow', 'pinned', 'custom'];

  function applyOrbPosition(mode) {
    var m = ORB_POSITION_MODES.indexOf(mode) >= 0 ? mode : 'follow';
    ui.orbPosition = m;
    ui.orbDraggable = (m !== 'follow');
    if (document.body) {
      document.body.classList.toggle('orb-follow', m === 'follow');
    }
  }

  function applyOrbMode(enabled) {
    ui.orbMode = !!enabled;
    if (document.body) {
      document.body.classList.toggle('orb-mode', ui.orbMode);
    }
    if (!ui.orbMode) {
      // 关掉常驻模式:回到"按热键才弹胶囊"的老样子
      cancelCollapse();
      setExpanded(false);
      return;
    }
    // 开着:球在,胶囊收着(正在录音就展开)
    setExpanded(ui.state === 'recording');
    // 命中区立刻重报一次(球的形状和胶囊完全不同,不能等节流)
    reportRegions();
  }

  // 一轮结束后停一会儿再收回球。停多久由 Python 推过来
  // (setHideDelay):错误带着原文时要停 12 秒,让用户有时间点「复制」。
  var collapseTimer = 0;

  function cancelCollapse() {
    if (collapseTimer) {
      window.clearTimeout(collapseTimer);
      collapseTimer = 0;
    }
  }

  function scheduleCollapse() {
    if (!ui.orbMode) {
      return;
    }
    cancelCollapse();
    collapseTimer = window.setTimeout(function () {
      collapseTimer = 0;
      // 录音中不许收:新一轮刚开始就被上一轮的定时器收掉,胶囊就没了
      if (ui.orbMode && ui.state !== 'recording') {
        setExpanded(false);
      }
    }, ui.hideDelay);
  }

  function onOrbMouseDown(e) {
    if (!ui.orbMode || e.button !== 0) {
      return;
    }
    // 点在按钮上(展开态的 ✕ / ✓ / 复制)不算拖球
    if (e.target && e.target.closest && e.target.closest('button')) {
      return;
    }
    orbDrag.pressing = true;
    orbDrag.dragging = false;
    orbDrag.startScreenX = e.screenX;
    orbDrag.startScreenY = e.screenY;
    // 窗口在屏幕上的位置:拖动的基准点(和面板/胶囊同一套机制)
    orbDrag.startWinX = window.screenX;
    orbDrag.startWinY = window.screenY;
    if (ui.orbDraggable) {
      // 自定义档才真的拖:挂 mousemove 实时挪球,并通知 Python "开始拖"
      // (按下到松手这段时间里守卫别抢回焦点 —— 焦点来回跳会拖垮
      //  WebView2 的鼠标捕获,拖到一半就断了)
      document.addEventListener('mousemove', onOrbMouseMove, false);
      callApi('begin_drag');
    }
    // 跟随档**不挂 mousemove** —— 那里压根没有"拖动"这回事
    // (位置由光标决定,拖了也拖不动),跟胶囊在跟随档下
    // 干脆不响应拖动是一个道理。pinned/custom 都可拖。
    //
    // 但 mouseup 两种情况都要听:点球开始录音这个功能不能因为
    // "不能拖"就一起没了。
    document.addEventListener('mouseup', onOrbMouseUp, false);
    e.preventDefault();
  }

  function onOrbMouseMove(e) {
    if (!orbDrag.pressing) {
      return;
    }
    var dx = e.screenX - orbDrag.startScreenX;
    var dy = e.screenY - orbDrag.startScreenY;
    if (!orbDrag.dragging) {
      // 阈值内还只是"可能手抖":不算拖动
      if (Math.sqrt(dx * dx + dy * dy) <= ORB_DRAG_THRESHOLD) {
        return;
      }
      orbDrag.dragging = true;
      if (document.body) {
        document.body.classList.add('orb-dragging');
      }
    }
    // 发**绝对目标位置**,不是增量:某一次 IPC 丢了也不漂
    callApi('move_orb', orbDrag.startWinX + dx, orbDrag.startWinY + dy);
  }

  function onOrbMouseUp(e) {
    if (!orbDrag.pressing) {
      return;
    }
    orbDrag.pressing = false;
    document.removeEventListener('mousemove', onOrbMouseMove, false);
    document.removeEventListener('mouseup', onOrbMouseUp, false);
    if (document.body) {
      document.body.classList.remove('orb-dragging');
    }
    var wasDragging = orbDrag.dragging;
    orbDrag.dragging = false;
    if (!wasDragging && !ui.orbDraggable && e) {
      // 不可拖的档位没有"拖动"这回事,但**按着划走**也不算"点球" ——
      // 否则用户随手一划就莫名其妙开始录音。
      // 这里现算一下起终点的位移,超过阈值就当没点。
      var dx = e.screenX - orbDrag.startScreenX;
      var dy = e.screenY - orbDrag.startScreenY;
      wasDragging = Math.sqrt(dx * dx + dy * dy) > ORB_DRAG_THRESHOLD;
    }
    // 松手:归还焦点(点球那一下把前台激活到胶囊了)
    callApi('end_drag');
    if (!wasDragging && !ui.expanded) {
      // 阈值内松手 = 点球 → 开始录音
      // (Python 那边会先把前台还给用户,否则文字会贴到胶囊自己身上)
      // 展开态下不触发:那时松手只是"点了一下胶囊",不能再开一轮录音。
      callApi('start_recording');
    }
  }

  /* ==================== 球的右键菜单与操作提示 ==================== */

  /* 右键菜单是球的**通用功能入口**(用户定的形态:展示框 + 一排小
   * 圆圈,以后加新功能都往这里放)。现在两项:打开设置面板 / 退出。
   * 退出走 Python 侧注入的回调,和托盘「退出」同一条清理路径。 */

  var hintTimer = 0;
  var menuFadeTimer = 0;

  function closeOrbMenu(immediate) {
    if (!el.orbMenu || el.orbMenu.hidden) {
      return;
    }
    el.orbMenu.classList.remove('is-open');
    var finish = function () {
      if (el.orbMenu && !el.orbMenu.classList.contains('is-open')) {
        el.orbMenu.hidden = true;
        // 布局变了 → 命中区跟着收(菜单那块要还给底下的应用)
        scheduleRegionReport();
      }
    };
    if (menuFadeTimer) {
      window.clearTimeout(menuFadeTimer);
      menuFadeTimer = 0;
    }
    if (immediate) {
      finish();
    } else {
      // 等淡出过渡(.12s)走完再彻底隐藏
      menuFadeTimer = window.setTimeout(finish, 140);
    }
  }

  function openOrbMenu() {
    if (!el.orbMenu || !el.orbMenu.hidden) {
      return;
    }
    // 提示气泡和菜单不同时出现
    hideOrbHint();
    if (menuFadeTimer) {
      window.clearTimeout(menuFadeTimer);
      menuFadeTimer = 0;
    }
    el.orbMenu.hidden = false;
    window.requestAnimationFrame(function () {
      el.orbMenu.classList.add('is-open');
    });
    // 命中区要把菜单框进来,不然看得见点不到(见 measureRegions)
    scheduleRegionReport();
  }

  function onOrbContextMenu(e) {
    // 只在**球形态**响应:展开成胶囊后右键没有菜单(那是录音操作区)
    if (!ui.orbMode || ui.expanded) {
      return;
    }
    e.preventDefault();
    openOrbMenu();
  }

  // 点菜单以外的地方(含左键点球)收菜单;点菜单内部不算
  function onDocMouseDownForMenu(e) {
    if (!el.orbMenu || el.orbMenu.hidden) {
      return;
    }
    if (e.target && e.target.closest && e.target.closest('.orb-menu')) {
      return;
    }
    closeOrbMenu();
  }

  function showOrbHint(text) {
    if (!el.orbHint) {
      return;
    }
    var value = text == null ? '' : String(text);
    if (!value.trim()) {
      hideOrbHint();
      return;
    }
    el.orbHint.textContent = value;
    if (hintTimer) {
      window.clearTimeout(hintTimer);
      hintTimer = 0;
    }
    el.orbHint.hidden = false;
    window.requestAnimationFrame(function () {
      el.orbHint.classList.add('is-open');
    });
    scheduleRegionReport();
    hintTimer = window.setTimeout(hideOrbHint, 2200);
  }

  function hideOrbHint() {
    if (hintTimer) {
      window.clearTimeout(hintTimer);
      hintTimer = 0;
    }
    if (!el.orbHint || el.orbHint.hidden) {
      return;
    }
    el.orbHint.classList.remove('is-open');
    window.setTimeout(function () {
      if (el.orbHint && !el.orbHint.classList.contains('is-open')) {
        el.orbHint.hidden = true;
        scheduleRegionReport();
      }
    }, 240);
  }

  /* 用户在文字条里选文本时,通知 Python 别抢前台。
   *
   * 为什么:选文本是鼠标拖,那一下会把 WebView2 激活成前台窗口;
   * Python 侧的守卫线程发现"前台是胶囊"就立刻抢回去(正常情况这是
   * 好事 —— 防止用户的键盘输入跑进胶囊)。但抢回去 WebView2 就失去
   * 了**键盘焦点**,用户再按 Ctrl+C 就复制不了 —— 用户手动框选
   * 兜底拿回识别结果这条路直接断掉(实测踩过)。
   *
   * 所以选区非空期间,守卫让焦点留在 WebView2 上。 */
  var selectingText = false;

  function reportSelectingText(on) {
    if (selectingText === on) {
      return;
    }
    selectingText = on;
    callApi('selecting_text', on);
  }

  function onSelectionChange() {
    var sel = window.getSelection();
    var text = sel ? String(sel.toString()) : '';
    // 选区非空 = 用户正在选;空 = 选完了(或者没选)
    reportSelectingText(!!text.trim());
  }

  /* ==================== 胶囊阶段 ==================== */

  // 切换胶囊阶段。phase 以 class 的形式挂在胶囊上(和 OpenType 一致),
  // CSS 靠它决定显示波形还是状态文字、右侧是结束钮还是状态图标。
  //
  // labelText 是**覆盖文案**:预览关掉的时候,提示要显示在胶囊里的
  // label 位(而不是文字条上),比如错误文案 —— 一行截断即可。
  function setPhase(phase, labelText) {
    ui.phase = phase;
    if (el.pill) {
      for (var i = 0; i < PHASES.length; i++) {
        el.pill.classList.toggle(PHASES[i], PHASES[i] === phase);
      }
    }
    if (el.barLabel) {
      var text = labelText != null ? String(labelText)
                                  : (PHASE_TEXT[phase] || '');
      el.barLabel.textContent = text;
    }
    // 滚动电平条只在**录音阶段**跑 —— 别的阶段胶囊里换状态文字,
    // 条子停在那儿不动(反正 waveform 被 CSS 藏了)
    if (phase === 'recording') {
      if (!barsRunning) {
        resetLevelBars();
        barsRunning = true;
      }
    } else if (barsRunning) {
      stopBars();
      barsRunning = false;
    }
    // 阶段换了,胶囊的宽度会变(波形 ↔ 状态文字)→ 窗口形状要跟着变
    scheduleRegionReport();
  }

  /* ==================== 文字条 ==================== */

  // 写文字条内容。kind 决定配色：normal / warning / error / cancelled
  //
  // report 是**失败报告**(可选):显示在正文**上方**,中间有分隔。
  // 有原文时,正文是"识别出来的内容",报告是"为什么没送出去" ——
  // 两段话混在一起,用户分不清哪句是自己要的。
  //
  // 预览关掉的时候**一个字都不写**:文字条整块都不该出现
  // (CSS 里 .no-preview 把它 display:none,这里保证内容也是空的,
  //  免得切回"开"的时候闪出上一轮的旧内容)。
  function writeText(text, kind, report) {
    if (!ui.previewEnabled) {
      return;
    }
    var value = text == null ? '' : String(text);
    var note = report == null ? '' : String(report);
    if (el.textbar) {
      el.textbar.classList.remove('is-empty', 'is-error', 'is-warning', 'is-cancelled');
      if (kind === 'error') {
        el.textbar.classList.add('is-error');
      } else if (kind === 'warning') {
        el.textbar.classList.add('is-warning');
      } else if (kind === 'cancelled') {
        el.textbar.classList.add('is-cancelled');
      }
      el.textbar.classList.toggle('is-empty', !value.trim() && !note.trim());
      el.textbar.classList.toggle('has-report', !!note.trim());
    }
    if (el.textbarReport) {
      el.textbarReport.textContent = note.trim() ? note : '';
    }
    if (el.textbarText) {
      el.textbarText.textContent = value.trim() ? value : PLACEHOLDER;
    }
    // 文字条的长宽会变 → 窗口形状要跟着变
    scheduleRegionReport();
  }

  /* ==================== 错误态的复制 ==================== */

  // 注入失败时,那段话可能哪儿都没有:目标窗口没收到,剪贴板里
  // 也未必留下。这个按钮是用户唯一能把内容捞回来的出口 ——
  // 点一下 → Python 写剪贴板 → **读回校验** → 确认写进去了,
  // 胶囊自己关掉。写不进去就留在原地,让用户再试一次。
  function setCopyHint(text) {
    setPhase('error', text);
  }

  function onCopyClick() {
    if (ui.copying) {
      return;
    }
    var text = ui.errorPayload;
    if (!text || !text.trim()) {
      return;
    }
    ui.copying = true;
    setCopyHint('复制中…');
    callApiAsync('copy_result', text).then(function (ok) {
      ui.copying = false;
      if (ok) {
        // Python 侧已经把胶囊收掉了,这里把状态清干净
        ui.errorPayload = '';
        if (el.pill) {
          el.pill.classList.remove('can-copy');
        }
        if (ui.orbMode) {
          // 常驻球:拿到了就立刻收成球,不用等那一轮的超时
          cancelCollapse();
          setExpanded(false);
        }
      } else {
        setCopyHint('复制失败,再试一次');
      }
    });
  }

  /* ==================== 电平数据 ==================== */

  // payload 是 JSON 字符串，内容是**最近若干帧的音量序列**（0..1，
  // dBFS 对数域映射后的值）。**数组最后一个元素是最新值。**
  function parseLevels(payload) {
    var arr = payload;
    if (typeof arr === 'string') {
      try {
        arr = JSON.parse(payload);
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
      // 空序列:这一帧没有新样本,不动平滑器
      return;
    }
    // Python 侧 30fps 推一串滑动窗口,而条子 80ms 才出一根,
    // 所以只取最新的一个值(数组末尾)喂给平滑器,
    // **不做窗口内归一化** —— 归一化之后条子永远看起来是满的,
    // 用户就没法从条的高低看出"我说话声音大不大"了。
    ui.level = smoothLevel(vals[vals.length - 1]);
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

  /* ==================== 高度映射 ==================== */

  // 响度(0..1) → 条高(px)。4 段非线性,这是"对比度明显"的来源:
  //   0   ~ DEAD_ZONE : 固定 2px(死区 / 底噪)
  //   DEAD_ZONE ~ 0.5 : 4 → 12px(最灵敏的一段)
  //   0.5 ~ 0.75      : 12 → 18px
  //   0.75 ~ 1        : 18 → 20px(压缩)
  //
  // ⚠️ 输入是**绝对电平**(Python 侧 dbfs_to_unit 的 dBFS 对数映射值),
  // 前端绝不按窗口内最大值归一化 —— 归一化之后条子永远看起来是满的,
  // 用户就没法从条的高低看出"我说话声音大不大"了。
  function mapHeight(level) {
    // ⚠️ 分段边界必须跟 DEAD_ZONE 联动:死区一改,下面第一段的起点
    // 跟着走。写死 0.2 的话,死区一旦大于 0.2,这第一段就成了死代码,
    // 死区边界上会出现 2px 直接跳到 12px 的台阶。
    if (level <= DEAD_ZONE) {
      return SILENT_HEIGHT;
    }
    if (level <= 0.5) {
      return 4 + (level - DEAD_ZONE) / (0.5 - DEAD_ZONE) * 8;
    }
    if (level <= 0.75) {
      return 12 + (level - 0.5) / 0.25 * 6;
    }
    return Math.min(MAX_HEIGHT, 18 + (level - 0.75) / 0.25 * 2);
  }

  /* ==================== 三槽轮转 ==================== */

  // 每次算出三个高度(过渡 / 当前 / 衰减)轮流用,
  // 相邻条因此天然错开,不会是一排单调的长短 —— "流动感"的来源。
  //
  // MCode 那边 r / decay 是"响度"(j() 的结果),这里直接保留像素高度:
  // I() 与 j() 互为反函数,往返一次结果不变,省一层换算。
  function nextBarHeight(curPx, level) {
    var prev = lastBarHeight;
    // 过渡条:与上一根做 65% 插值 → 缓升 / 缓降
    var trans = prev + (curPx - prev) * 0.65;
    // 衰减条:回声感,越响掉得越多
    var decay = curPx <= SILENT_MAX_PX
      ? curPx
      : Math.max(SILENT_MAX_PX, curPx - (0.75 + 3.75 * level));
    var slots = [trans, curPx, decay];
    var h = slots[slotIndex % 3];
    slotIndex++;
    lastBarHeight = h;
    // 死区下界:静音条恒为 2px
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

  // 推进一根新条:新条从右侧进,旧的从左侧出(滑出视口即被裁掉)
  function advanceBar() {
    if (!el.waveform) {
      return;
    }
    var level = ui.level;
    var curPx = mapHeight(level);
    var height = nextBarHeight(curPx, level);

    var recycled;
    if (pool.length < POOL_SIZE) {
      // 池子还没满(正常情况下一进录音就填满了,这里只是兜底)
      recycled = createBar();
      el.waveform.appendChild(recycled);
    } else {
      // 条够了:回收最老的一根(它已经在视口左侧外,回收时不会被人看见)
      recycled = pool.pop();
      ages.pop();
    }
    pool.unshift(recycled);
    ages.unshift(0);
    // 其余各根年龄 +1(也就是整体向左滑一个步距)
    for (var i = 1; i < ages.length; i++) {
      ages[i] += 1;
    }

    // 写位置:年龄 a 的条平移 -a × 步距
    // 回收的那根要"瞬移"到最右边:先关掉它的过渡,
    // 否则它会从屏幕左边一路飞过来
    recycled.style.transition = 'none';
    recycled.style.height = SILENT_HEIGHT + 'px';
    for (var j = 0; j < pool.length; j++) {
      pool[j].style.transform = 'translateX(' + (-ages[j] * BAR_STEP) + 'px)';
    }

    // 强制回流,让浏览器认下"瞬时"的位置(后面再放开过渡)
    void el.waveform.offsetWidth;

    // 颜色:静音条画灰色;高度下一帧再给,触发 75ms 的高度过渡
    var silent = height <= SILENT_MAX_PX;
    recycled.classList.toggle('is-silent', silent);
    var target = height.toFixed(2) + 'px';
    window.requestAnimationFrame(function () {
      recycled.style.transition = '';
      recycled.style.height = target;
    });
  }

  // 进入录音:重来一遍,不保留上一次的残留高度
  function resetLevelBars() {
    stopBars();
    pool = [];
    ages = [];
    slotIndex = 0;
    lastBarHeight = 0;
    smoother.current = 0;
    ui.level = 0;
    if (el.waveform) {
      el.waveform.textContent = '';
      // 预填满 13 根(外加 1 根屏外的回收位):
      // 组宽一开始就是满的,和 MCode 那个定宽小条一致;
      // 初始都是 2px 的静音基线,等真实音量来了再长起来
      for (var i = 0; i < POOL_SIZE; i++) {
        var bar = createBar();
        bar.style.transform = 'translateX(' + (-i * BAR_STEP) + 'px)';
        el.waveform.appendChild(bar);
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

  /* ==================== 计时 ==================== */

  // 秒 -> m:ss
  function formatTime(seconds) {
    var s = Math.max(0, Math.floor(Number(seconds) || 0));
    var m = Math.floor(s / 60);
    var r = s % 60;
    return m + ':' + (r < 10 ? '0' + r : String(r));
  }

  function setElapsed(seconds) {
    var s = Number(seconds);
    if (!isFinite(s) || s < 0) {
      s = 0;
    }
    ui.elapsed = s;
    if (el.barTimer) {
      el.barTimer.textContent = formatTime(s);
    }
    // 滚动条自己按 80ms 推进,不依赖计时 —— 秒数只管显示
  }

  /* ==================== 拖动(自定义位置) ==================== */

  /* 胶囊本体(非按钮区)当拖拽把手 —— 只有「自定义」模式才允许:
   * 跟随输入模式下拖了也没意义(下一轮还是回到光标那里),
   * 固定底部模式下拖了会被下一次录音直接掰回去。
   *
   * 发**绝对目标位置**,不发增量 —— 面板那套已经验证过:
   * 增量在 IPC 慢的时候会漂,而绝对目标天然不漂
   * (某一次调用丢了或慢了,下一次直接覆盖)。
   *
   * 坐标用 e.screenX(鼠标的**屏幕**坐标)而不是 clientX ——
   * 窗口自己一动 clientX(视口内坐标)就变,会被窗口移动污染。 */
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
    // 常驻球模式:只有**球形态**(未展开)才响应按下 ——
    // 展开后的胶囊不参与拖动:位置归球,想挪位置先收回球再拖。
    //
    // 展开态还响应按下会有个副作用:按下就调 begin_drag,守卫在按住
    // 期间**不归还前台** —— 按住不放的话前台就留在胶囊上,
    // 用户的键盘输入会打到胶囊里(功能级问题)。
    if (ui.orbMode) {
      if (!ui.expanded) {
        onOrbMouseDown(e);
      }
      return;
    }
    if (ui.positionMode !== 'custom') {
      return;
    }
    // 点在 ✕ / ✓ 按钮上时不触发拖动
    if (e.target && e.target.closest && e.target.closest('button')) {
      return;
    }
    drag.active = true;
    drag.startScreenX = e.screenX;
    drag.startScreenY = e.screenY;
    drag.startWinX = window.screenX;
    drag.startWinY = window.screenY;
    drag.pending = null;
    // 拖动期间光标换成"抓"的手势(grabbing),放手恢复 grab
    if (document.body) {
      document.body.classList.add('capsule-dragging');
    }
    document.addEventListener('mousemove', onDragMove, false);
    document.addEventListener('mouseup', onDragEnd, false);
    // 告诉 Python:拖动期间别抢回焦点(焦点来回跳会拖垮鼠标捕获)
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
    if (x === drag.startWinX && y === drag.startWinY) {
      return;
    }
    callApi('move_capsule', x, y);
  }

  function onDragEnd() {
    if (!drag.active) {
      return;
    }
    drag.active = false;
    document.removeEventListener('mousemove', onDragMove, false);
    document.removeEventListener('mouseup', onDragEnd, false);
    if (document.body) {
      document.body.classList.remove('capsule-dragging');
    }
    if (drag.raf) {
      cancelAnimationFrame(drag.raf);
      drag.raf = 0;
    }
    flushDrag();       // 把最后一次位置补上
    callApi('end_drag');   // Python 侧:归还焦点 + 记住位置
  }

  /* ==================== 事件绑定 ==================== */

  function bindEvents() {
    // 取消：放弃这一轮
    if (el.cancelBtn) {
      el.cancelBtn.addEventListener('click', function () {
        callApi('cancel_recording');
      });
    }

    // 结束：等同再按一次热键
    if (el.okBtn) {
      el.okBtn.addEventListener('click', function () {
        callApi('stop_recording');
      });
    }

    // 胶囊本体当拖拽把手(只有「自定义」位置模式才真的拖得动)
    if (el.pill) {
      el.pill.addEventListener('mousedown', onDragStart, false);
      // 球形态右键 → 通用功能菜单(展开成胶囊后右键无效)
      el.pill.addEventListener('contextmenu', onOrbContextMenu, false);
    }

    // 菜单里的两个出口(点击后 Python 侧才真正执行)
    if (el.menuSettings) {
      el.menuSettings.addEventListener('click', function () {
        closeOrbMenu(true);
        callApi('open_panel');
      });
    }
    if (el.menuQuit) {
      el.menuQuit.addEventListener('click', function () {
        closeOrbMenu(true);
        callApi('quit_app');
      });
    }
    // 点菜单以外的地方收菜单
    document.addEventListener('mousedown', onDocMouseDownForMenu, false);

    // 选文本时告诉 Python 别抢焦点(否则 Ctrl+C 复制不了)
    document.addEventListener('selectionchange', onSelectionChange, false);

    // 复制：注入失败时把识别结果手动写进剪贴板
    if (el.copyBtn) {
      el.copyBtn.addEventListener('click', onCopyClick);
    }
  }

  /* ==================== 对外接口 ==================== */

  // Python → JS：严格按 BRIDGE.md 的签名
  window.__capsule = {
    setState: function (state) {
      ui.state = state;
      // 模型加载态(球上转圈):starting → body.orb-loading,离开即摘
      if (document.body) {
        document.body.classList.toggle('orb-loading', state === 'starting');
      }
      if (state === 'recording') {
        // 录音开始:菜单和提示都收掉(上一轮的浮层不该带进新一轮)
        closeOrbMenu(true);
        hideOrbHint();
        setPhase('recording');
        cancelCollapse();
        if (ui.orbMode) {
          // 常驻球:球原地展开成胶囊
          setExpanded(true);
        }
      } else if (state === 'processing') {
        setPhase('processing');
      } else {
        // 待机 / 出错:胶囊这会儿是隐藏的,波形不必再跑
        // (阶段不变 —— 阶段决定的是"胶囊长什么样",不是"跑不跑")
        stopBars();
        barsRunning = false;
      }
    },
    setLevels: function (payload) {
      setLevels(payload);
    },
    setElapsed: function (seconds) {
      setElapsed(seconds);
    },
    setPreview: function (text) {
      // 实时预览：每次都是整体替换（后端每次重新识别全部音频）。
      // 终态(done/error/warning/cancelled)后,控制器还会紧跟一对
      // preview("") 用来清预览 —— 那一下会把刚定格的结果/警告色
      // 文案抹回占位符(后端同批先发 result 再发 preview)。
      // 终态的清场只认 resetForRecording(新一轮录音)。
      if (ui.phase === 'done' || ui.phase === 'error'
          || ui.phase === 'warning' || ui.phase === 'cancelled') {
        return;
      }
      // 预览关着:文字条整块都不出现,一个字都不写
      if (!ui.previewEnabled) {
        return;
      }
      writeText(text == null ? '' : String(text), 'normal');
    },
    setResult: function (text) {
      // 最终结果：文字条定格(预览关着时只剩胶囊里的「已输入」)
      if (ui.previewEnabled) {
        writeText(text == null ? '' : String(text), 'normal');
      }
      setPhase('done');
      scheduleCollapse();
    },
    setError: function (message, payload) {
      var msg = message == null ? '' : String(message);
      var raw = payload == null ? '' : String(payload);
      ui.errorPayload = raw;
      ui.copying = false;
      // 带原文 = 注入失败那一类:识别是**成功**的,内容也拿到了
      // (字在剪贴板里),只是没送进目标窗口。这不是"彻底失败",
      // 是**警告** —— 文字条用琥珀色,报告排在正文上方,中间有分隔,
      // 胶囊里只留一句短提示(长文案会把胶囊撑宽,看着奇怪)。
      // 没有原文的错误(没听清 / 识别失败)才是真的失败 —— 错误色。
      var canCopy = !!raw.trim();
      if (el.pill) {
        el.pill.classList.toggle('can-copy', canCopy);
      }
      if (canCopy) {
        if (ui.previewEnabled) {
          writeText(raw, 'warning', msg);
          setPhase('warning');
        } else {
          // 预览关着:文字条整块不显示,报告只能进胶囊的 label 位
          // (一行截断即可)。
          setPhase('warning', msg);
        }
      } else if (ui.previewEnabled) {
        writeText(msg, 'error');
        setPhase('error');
      } else {
        setPhase('error', msg);
      }
      scheduleCollapse();
    },
    setCancelled: function () {
      if (ui.previewEnabled) {
        writeText('已取消', 'cancelled');
      }
      setPhase('cancelled');
      scheduleCollapse();
    },
    resetForRecording: function () {
      // 新一轮录音:清掉上一轮的文案与配色
      writeText('', 'normal');
      // 滚动条也从头再来 —— 上一轮残留的高度不该带过来
      resetLevelBars();
      barsRunning = true;
      setElapsed(0);
      // 上一轮错误态留下的原文与「复制」按钮也要清干净
      ui.errorPayload = '';
      ui.copying = false;
      if (el.pill) {
        el.pill.classList.remove('can-copy');
      }
      setPhase('recording');
      cancelCollapse();
      if (ui.orbMode) {
        // 常驻球:新一轮开始 → 球展开成胶囊
        setExpanded(true);
      }
    },
    setTheme: function (mode) {
      // 主题档位:面板改的。Python 侧会在每次录音开始前再补推一次,
      // 所以这里只需要幂等地设上属性就行。
      applyTheme(mode);
    },
    setPositionMode: function (mode) {
      // 胶囊位置档位:面板改的。Python 侧也会在录音开始前补推一次。
      applyPositionMode(mode);
    },
    setPreviewEnabled: function (enabled) {
      // 预览开关:面板改的。
      applyPreviewEnabled(enabled);
    },
    setOrbMode: function (enabled) {
      // 常驻悬浮球模式:面板改的(启动时也会推一次)。
      applyOrbMode(enabled);
    },
    setOrbPosition: function (mode) {
      // 球的位置档位(follow / custom):面板改的,Python 侧也会推一次。
      // 跟随档下球不可拖(位置由光标决定)。
      applyOrbPosition(mode);
    },
    setOrbHint: function (text) {
      // 球的一句操作反馈(点球时模型还在加载等):球上方冒出、几秒后淡掉。
      showOrbHint(text);
    },
    setHideDelay: function (ms) {
      // 一轮结束后停多久再收回球(毫秒)。
      var v = Number(ms);
      ui.hideDelay = (isFinite(v) && v >= 0) ? v : 1500;
    }
  };

  /* ==================== 启动 ==================== */

  // 绑定 DOM 引用（可重复调用）
  function bindDom() {
    el.textbar = $('textbar');
    el.textbarText = $('textbarText');
    el.textbarReport = $('textbarReport');
    el.pill = $('pill');
    el.waveform = $('waveform');
    el.barLabel = $('barLabel');
    el.barTimer = $('barTimer');
    el.cancelBtn = $('cancelBtn');
    el.okBtn = $('okBtn');
    el.copyBtn = $('copyBtn');
    el.barOrb = $('barOrb');
    el.orbMenu = $('orbMenu');
    el.menuSettings = $('menuSettings');
    el.menuQuit = $('menuQuit');
    el.orbHint = $('orbHint');

    // 参与球↔胶囊动效的两个元素 —— 动效停下来时要重报命中区
    // (见 scheduleRegionReportAfterMotion 的说明)。
    bindMotionEnd(el.pill);
    bindMotionEnd(el.textbar);
  }

  // Python 可能在 DOMContentLoaded 之前就推状态，这里做惰性绑定兜底
  function ensureDom() {
    if (!el.waveform) {
      bindDom();
    }
  }

  function init() {
    bindDom();

    writeText('', 'normal');
    setElapsed(0);
    // 波形条在进入录音阶段时才建(见 setPhase),这里只定阶段
    setPhase('recording');
    applyTheme('auto');   // Python 就绪后会用 setTheme 推真实档位
    applyPositionMode('follow');   // 同理:setPositionMode 会推真实档位
    applyPreviewEnabled(true);      // 同理:setPreviewEnabled 会推真实值
    applyOrbMode(false);             // 同理:setOrbMode 会推真实值
    applyOrbPosition('follow');       // 同理:setOrbPosition 会推真实值

    bindEvents();

    // 视口尺寸变了(哪怕只是建窗后 WinForms AutoScale 事后缩水了一次),
    // 内容贴底的位置也会变 → 重新量一次窗口形状
    window.addEventListener('resize', scheduleRegionReport, false);

    // DOM 就绪后通知 Python
    notifyReady(0);
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init, false);
  } else {
    init();
  }
})();
