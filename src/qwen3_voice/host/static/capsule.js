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
 *   Python → JS : window.__capsule.setMode / setLevels / setElapsed
 *                 / setPreview / setTheme / setPreviewEnabled
 *                 / setOrbMode / setOrbPosition / setOrbHint
 *   JS → Python : window.pywebview.api.ready / cancel_recording / stop_recording
 *                 / update_regions / copy_result / start_recording / dismiss
 *
 * ⚠ **模块私有**(拖拽 composable 内部使用,业务侧勿直接调用):
 *                 move_orb / move_capsule / begin_drag / end_drag
 *   这四个是"拖拽怎么落到窗口上"的实现细节。消费侧只需要
 *   makeDraggable(把手, {...}) —— 自己拼 begin/move/end 序列的话,
 *   焦点冻结、位置记忆这两处很容易跟着错。
 *
 * ⚠ 显示状态的**唯一入口**是 setMode(mode, payload):Python(presenter)
 * 推 mode,前端只演、不决策 —— 展开/收回/淡入的速度与曲线都是前端
 * 的事,「什么时候发生」(停多久、到点回哪)归 presenter 的 dwell 表
 * (见 apps/voicebar/protocol.py)。**前端不再有任何收回定时器。**
 * setOrbHint 是纯视觉的提示气泡(球上方冒一句),不是状态机命令。
 *
 * ⚠ 胶囊的**位置档位**（follow / bottom / custom）已随重构退役：
 * Python 侧不再推 setPositionMode，前端只留一个空转的兼容入口。球和
 * 胶囊是同一个窗口、共用同一份位置记忆（config.orb.geometry），
 * 所以拖胶囊（move_capsule）和拖球（move_orb）在 Python 侧落到
 * **同一条**移动路径上。
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

  /* 胶囊位置档位(follow / bottom / custom)已随重构退役:
   * 球和胶囊是同一个窗口、共用同一份位置记忆(config.orb.geometry),
   * 没有"位置归谁管"的分档,也就没有"不可拖"的档位。 */

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
    mode: 'orb',            // 显示状态(Python 推的;唯一真相在 presenter)
    phase: 'recording', // 胶囊自己的阶段
    level: 0,           // 当前（平滑后）的响度 0..1
    elapsed: 0,         // 录音计时（秒）
    theme: 'auto',      // 主题档位
    previewEnabled: true,   // 实时预览文字条是否显示
    errorPayload: '',       // 错误态里可复制的原文(注入失败时带着识别文本)
    errorDismissable: false,// 带原文的错误态:✕ 按钮 = 关掉提示(而不是取消录音)
    copying: false,         // 复制进行中(防连点)
    orbMode: false,         // 常驻悬浮球模式(球形态 ↔ 胶囊形态)
    expanded: false,        // 球有没有展开成胶囊
    orbPosition: 'follow',  // 球的位置档位(follow / pinned / custom)
    orbDraggable: false     // 只有 follow 不能拖(pinned/custom 的位置归用户管)
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

  /* 位置档位:已退役(见上面常量区的那段说明)。
   *
   * 现在这里只干一件事:恒挂 can-drag —— 让"光标长什么样"和"能不能
   * 拖"由**同一个状态**驱动。有 grab 手势就必须真能拖:胶囊上出现
   * 把手却拖不动,样式和行为分叉在物理上不该可能(V1 就是恒可拖)。 */
  function applyPositionMode(mode) {
    // mode 参数只为兼容桥接契约里 setPositionMode 的调用而留着,
    // 不再有档位语义。
    if (document.body) {
      document.body.classList.add('can-drag');
    }
  }

  /* ==================== 拖拽 composable ==================== */

  /* makeDraggable(handleEl, opts):把"按住 → 拖 → 松手"封成一块能复用的。
   *
   * 为什么要有这层:球和胶囊拖的是**同一个窗口**、走的是**同一条**
   * Python 路径,差别只有"什么时候算拖动"和"拖动中发哪个 API"。以前
   * 两边各写一套 mousedown/mousemove/mouseup,于是"5px 判据 / 排除
   * 按钮 / 位置换算 / 样式类 / begin-move-end 序列"这些约定要各记一遍,
   * 改一边容易漏另一边。现在只有这一份,消费侧一行 makeDraggable。
   *
   * opts:
   *   threshold     位移判据(px):按下后位移超过它才算"在拖动"。球=5
   *                 (区分"点球开始录音"和"拖球挪位置");胶囊=0,非按钮
   *                 区按下即拖。
   *   move          拖动中推送位置的 API 名(move_orb / move_capsule)。
   *   draggingClass 拖动期间挂到 body 的样式类(球/胶囊各一个)。
   *   throttle      位置推送的发法:'sync' = 每个 mousemove 事件同步发
   *                 一次;'raf' = 每帧最多一次(rAF 节流)。
   *   filter        function(e):返回 true 表示这次按下**不**启动拖动
   *                 (胶囊/球都要排除 closest('button'))。
   *   enabled       function():当前形态是否响应这套拖动(球形态 vs
   *                 胶囊形态)。默认恒响应。
   *   dragEnabled   function():当前是否**允许**真的拖。false 时仍然
   *                 跟踪按下/松手(球在跟随档拖不动,但"点球开始录音"
   *                 还得靠按下/松手判定)。
   *   onClick       function(e):松手时判定为"点击而非拖动"的回调
   *                 (球的"点一下开始录音"走这里)。 */
  function makeDraggable(handleEl, opts) {
    if (!handleEl) {
      return;
    }
    var threshold = opts.threshold || 0;
    var useRaf = (opts.throttle || 'raf') === 'raf';
    var state = {
      pressing: false,     // 按下了,还在等松手
      dragging: false,     // 已经算"在拖动"(越过阈值;粘滞,不回退)
      dragEnabled: false,  // 这一轮允许真的拖
      startScreenX: 0,
      startScreenY: 0,
      startWinX: 0,
      startWinY: 0,
      pending: null,       // 还没发出去的那一帧鼠标位置
      raf: 0
    };

    // 从按下点到当前点的位移(屏幕坐标 = CSS 逻辑像素)
    function moved(e) {
      var dx = e.screenX - state.startScreenX;
      var dy = e.screenY - state.startScreenY;
      return Math.sqrt(dx * dx + dy * dy);
    }

    // 发**绝对目标位置**,不是增量:某一次 IPC 丢了也不漂
    // (某一次调用丢了或慢了,下一次直接覆盖)。
    function send(e) {
      var x = state.startWinX + (e.screenX - state.startScreenX);
      var y = state.startWinY + (e.screenY - state.startScreenY);
      if (useRaf && x === state.startWinX && y === state.startWinY) {
        // 位置没变就不发
        return;
      }
      callApi(opts.move, x, y);
    }

    // 把积攒的那一帧发出去(rAF 节流路径)
    function flush() {
      state.raf = 0;
      var e = state.pending;
      state.pending = null;
      if (!e || !state.pressing) {
        return;
      }
      send(e);
    }

    function onMove(e) {
      if (!state.pressing || !state.dragEnabled) {
        return;
      }
      if (!state.dragging) {
        // 阈值内还只是"可能手抖":不算拖动
        if (threshold > 0 && moved(e) <= threshold) {
          return;
        }
        state.dragging = true;
        if (document.body) {
          document.body.classList.add(opts.draggingClass);
        }
      }
      if (!useRaf) {
        send(e);
        return;
      }
      state.pending = e;
      if (!state.raf) {
        state.raf = requestAnimationFrame(flush);
      }
    }

    function onUp(e) {
      if (!state.pressing) {
        return;
      }
      state.pressing = false;
      document.removeEventListener('mousemove', onMove, false);
      document.removeEventListener('mouseup', onUp, false);
      if (document.body) {
        document.body.classList.remove(opts.draggingClass);
      }
      if (state.raf) {
        cancelAnimationFrame(state.raf);
        state.raf = 0;
      }
      state.pending = null;
      var wasDragging = state.dragging;
      state.dragging = false;
      if (!wasDragging && !state.dragEnabled && e) {
        // 这一轮压根不能拖,但**按着划走**也不算"点了一下" ——
        // 否则用户随手一划就莫名其妙开始录音。
        wasDragging = moved(e) > threshold;
      }
      // 松手:归还焦点(点球那一下把前台激活到胶囊了)
      callApi('end_drag');
      if (!wasDragging && opts.onClick) {
        opts.onClick(e);
      }
    }

    function onDown(e) {
      if (e.button !== 0) {
        return;
      }
      if (opts.filter && opts.filter(e)) {
        return;
      }
      if (opts.enabled && !opts.enabled()) {
        return;
      }
      state.pressing = true;
      state.dragging = false;
      state.dragEnabled = opts.dragEnabled ? !!opts.dragEnabled() : true;
      state.startScreenX = e.screenX;
      state.startScreenY = e.screenY;
      // 窗口在屏幕上的位置:拖动的基准点。窗口自己会动,基准只能取
      // 按下那一刻的值,之后一律发绝对目标。
      state.startWinX = window.screenX;
      state.startWinY = window.screenY;
      state.pending = null;
      if (state.dragEnabled && threshold <= 0) {
        // 没有位移判据(胶囊):按下就算在拖,样式类立刻挂上,
        // 和"按住胶囊马上变 grabbing 手势"一致。
        state.dragging = true;
        if (document.body) {
          document.body.classList.add(opts.draggingClass);
        }
      }
      document.addEventListener('mousemove', onMove, false);
      document.addEventListener('mouseup', onUp, false);
      if (state.dragEnabled) {
        // 告诉 Python:拖动期间别抢回焦点(焦点来回跳会拖垮
        // WebView2 的鼠标捕获),松手时统一还回去。
        callApi('begin_drag');
      }
      e.preventDefault();
    }

    handleEl.addEventListener('mousedown', onDown, false);
  }

  /* 点在按钮上(✕ / ✓ / 复制)时不启动拖动 —— 按钮自己有点击语义。 */
  function noButtons(e) {
    return !!(e.target && e.target.closest && e.target.closest('button'));
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
   * 注意:**条子本身早就生成好了**(applyMode('recording') 里先生成),
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
      setExpanded(false);
      return;
    }
    // 开着:球在,胶囊收着(正在录音就展开)
    setExpanded(ui.mode === 'recording');
    // 命中区立刻重报一次(球的形状和胶囊完全不同,不能等节流)
    reportRegions();
  }

  /* 收回球**不再由前端定时器管**。

   * 以前这里是"一轮结束后停一会儿再收回球",停多久由 Python 推过来。
   * 现在"什么时候收回"归 presenter 的 dwell 表 ——
   * 到点了 Python 会再推一次 mode 过来,前端照着演。
   * 前端只留"怎么收"(动画、类名)。 */

  /* 球的拖动/点击:球形态下按住球 → 挪位置;按着没动 → 点球开始录音。
   *
   * 球和胶囊拖的是同一个窗口、走同一条 Python 路径,差别只有"什么时候
   * 算拖动":球要 5px 判据(否则想点球录音的人一碰就变成拖球),胶囊按住
   * 即拖。所以这里只剩一份参数,拖动流程全在 makeDraggable 里。 */
  var ORB_DRAG_OPTS = {
    threshold: ORB_DRAG_THRESHOLD,   // 5px 判据:区分"点球"和"拖球"
    move: 'move_orb',
    draggingClass: 'orb-dragging',
    // 球的推送维持"每个 mousemove 事件一次":拖动跟手的程度全靠它,
    // 换成 rAF 节流会整体慢一帧。
    throttle: 'sync',
    filter: noButtons,               // 展开态的 ✕ / ✓ / 复制按钮不拖球
    // 只有**球形态**(常驻模式且没展开)走这一套
    enabled: function () { return ui.orbMode && !ui.expanded; },
    // 跟随档的位置由光标决定,拖不动 —— 但"点球开始录音"还得用
    // 按下/松手来判定,所以那一档只是不启动拖动。
    dragEnabled: function () { return ui.orbDraggable; },
    // 松手时判定为"点击而非拖动":点球 → 开始录音
    onClick: function () {
      if (!ui.expanded) {
        callApi('start_recording');
      }
    }
  };

  /* ==================== 球的右键菜单与操作提示 ==================== */

  /* 右键菜单是球的**通用功能入口**(用户定的形态:展示框 + 一排小
   * 圆圈,以后加新功能都往这里放)。现在三项:打开设置面板 /
   * 二维码扫描(附属工具,独立进程) / 退出。
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

  /* ==================== 显示状态机 ==================== */

  /* 显示状态的**唯一入口**:Python(presenter)推 setMode(mode, payload),
   * 前端只负责"怎么呈现" —— 动画、类名、文案都是前端的事。
   *
   * "什么时候发生"(停多久、到点回哪个状态)归 presenter 的 dwell 表
   * (见 apps/voicebar/protocol.py):**前端一个收回定时器都不留**。
   * 要收回,Python 会再推一次 mode 过来。
   *
   * payload 字段(说明见 apps/voicebar/protocol.py):
   *   message  提示文案(错误原因 / 识别中 / 已输入 / 已取消 / 被拒原因)
   *   text     识别正文(结果态是识别结果;带原文的错误态是可复制的原文)
   *   can_copy 有没有可复制的原文(带原文的错误态 = 注入失败,可反复重试)
   *   reason   机器可读的原因码(暂不参与渲染)
   */

  // payload 走的是 JSON 字符串(和 push_levels 的电平数组一个套路):
  // Python 侧 json.dumps 过来,前端自己解析。
  function parsePayload(payload) {
    var data = payload;
    if (typeof data === 'string') {
      try {
        data = JSON.parse(data);
      } catch (e) {
        data = {};
      }
    }
    if (!data || typeof data !== 'object') {
      data = {};
    }
    return data;
  }

  // 清掉上一轮错误态留下的东西(原文、复制按钮、✕ 的语义)。
  // 回到待机、或开新一轮时都要清干净。
  function resetErrorFlags() {
    ui.errorPayload = '';
    ui.copying = false;
    ui.errorDismissable = false;
    if (el.cancelBtn) {
      el.cancelBtn.setAttribute('aria-label', '取消录音');
    }
    if (el.pill) {
      el.pill.classList.remove('can-copy');
    }
  }

  function applyMode(mode, payload) {
    var data = parsePayload(payload);
    var message = data.message == null ? '' : String(data.message);
    var text = data.text == null ? '' : String(data.text);
    var canCopy = !!data.can_copy;

    ui.mode = mode;
    // 模型加载态(球上转圈):只在 starting 挂,离开即摘
    if (document.body) {
      document.body.classList.toggle('orb-loading', mode === 'starting');
    }

    if (mode === 'orb') {
      // 回到待机:波形停、收回球形态、上一轮的错误残留清干净
      stopBars();
      barsRunning = false;
      resetErrorFlags();
      setExpanded(false);
      return;
    }

    if (mode === 'starting') {
      // 球在转圈(等模型就绪):胶囊形态不动
      return;
    }

    if (mode === 'recording') {
      // 新一轮录音:清场 —— 上一轮的文案、配色、波形、错误残留全清
      closeOrbMenu(true);
      hideOrbHint();
      writeText('', 'normal');
      resetLevelBars();
      barsRunning = true;
      setElapsed(0);
      resetErrorFlags();
      setPhase('recording');
      if (ui.orbMode) {
        // 常驻球:球原地展开成胶囊
        setExpanded(true);
      }
      return;
    }

    if (mode === 'processing') {
      setPhase('processing');
      return;
    }

    if (mode === 'result_terminal') {
      // 最终结果:文字条定格(预览关着时只剩胶囊里的「已输入」)
      if (ui.previewEnabled) {
        writeText(text, 'normal');
      }
      setPhase('done');
      return;
    }

    if (mode === 'error_terminal') {
      // 错误**可能带着识别原文**(注入失败时):带原文是**警告**态
      // (识别是成功的,内容也拿到了,只是没送进目标窗口),没有原文
      // 才是真的失败(错误色)。
      ui.errorPayload = canCopy ? text : '';
      ui.copying = false;
      // 带原文的错误态**常驻**:用户可能要反复点「复制」重试
      // (剪贴板被占、目标窗口不给焦点)。收不收由 Python 说了算
      // (它不排 dwell),前端不排任何定时器。
      ui.errorDismissable = canCopy;
      if (el.cancelBtn) {
        // 这个 ✕ 在带原文的错误态里是"关掉提示",不是"取消录音"
        // (那一轮早就结束了,没什么可取消的)—— 语义要说清楚。
        el.cancelBtn.setAttribute('aria-label',
          canCopy ? '关闭提示' : '取消录音');
      }
      if (el.pill) {
        el.pill.classList.toggle('can-copy', canCopy);
      }
      if (canCopy) {
        if (ui.previewEnabled) {
          writeText(text, 'warning', message);
          setPhase('warning');
        } else {
          // 预览关着:文字条整块不显示,报告只能进胶囊的 label 位
          // (一行截断即可)。
          setPhase('warning', message);
        }
      } else {
        if (ui.previewEnabled) {
          writeText(message, 'error');
          setPhase('error');
        } else {
          setPhase('error', message);
        }
      }
      return;
    }

    if (mode === 'cancelled_terminal') {
      if (ui.previewEnabled) {
        writeText(message || '已取消', 'cancelled');
      }
      setPhase('cancelled');
      return;
    }

    if (mode === 'rejected_bounce') {
      // 这轮被拒:球上方冒一句提示。气泡的淡出是**纯视觉**,留前端
      // 自己演;状态本身 0.6s 后回 orb 是 Python 的事。
      showOrbHint(message);
      return;
    }
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

  /* ==================== 错误/警告态的复制 ==================== */

  // 注入失败时,那段话可能哪儿都没有:目标窗口没收到,剪贴板里
  // 也未必留下(送达判定只认"Ctrl+V 事件投出去了",目标窗口没真正
  // 收到时,程序以为是成功,还会把原剪贴板恢复回去)。复制按钮是
  // 用户捞回内容的出口 —— 点一下 → Python 写剪贴板 → **读回校验**
  // → 确认写进去了,胶囊自己关掉。写不进去就留在原地,再试一次。
  // 反馈只改胶囊 label 文案,**不动 phase**:警告态下复制失败若把
  // phase 切到 error,琥珀会变错误红,用户会误以为整轮识别都失败
  // 了(实际只是这一次没写进去,可以再试)。
  function setCopyNote(text) {
    if (el.barLabel) {
      el.barLabel.textContent = text;
    }
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
    setCopyNote('复制中…');
    callApiAsync('copy_result', text).then(function (ok) {
      ui.copying = false;
      if (ok) {
        // Python 侧已经把胶囊收掉了,这里把状态清干净
        ui.errorPayload = '';
        ui.errorDismissable = false;
        if (el.pill) {
          el.pill.classList.remove('can-copy');
        }
        if (ui.orbMode) {
          // 常驻球:拿到了就立刻收成球,不用等那一轮的超时
          setExpanded(false);
        }
      } else {
        setCopyNote('复制失败,再试一次');
      }
    });
  }

  /* 用户点 ✕ 关掉带原文的错误提示(注入失败那种)。 */

  // 带原文的错误态是**常驻**的:用户可以反复点「复制」重试,自动收回
  // 会把重试的口子堵上。只有点 ✕(或开始下一轮录音)才收。
  //
  // 为什么不在前端自己收回:现在是不是"错误常驻态"只有 Python 侧
  // 知道(presenter 手里才有这一轮的状态),前端自己收可能收错 ——
  // 所以前端只负责**演**,收不收由 Python 说了算(dismiss 返回
  // true 才收)。其它状态点 ✕ 时 dismiss 静默忽略(幂等)。
  function onDismissClick() {
    if (!ui.errorDismissable) {
      return;
    }
    callApiAsync('dismiss').then(function (ok) {
      if (!ok) {
        // Python 侧不认(状态已经变了):什么都不做
        return;
      }
      ui.errorDismissable = false;
      ui.errorPayload = '';
      ui.copying = false;
      if (el.pill) {
        el.pill.classList.remove('can-copy');
      }
      if (el.cancelBtn) {
        el.cancelBtn.setAttribute('aria-label', '取消录音');
      }
      // 常驻球:收回球形态(非常驻模式的窗口由 Python 侧藏掉)
      setExpanded(false);
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

  /* 胶囊的拖动:胶囊本体(非按钮区)按住即拖。
   *
   * 位置档位(follow / bottom / custom)已随重构退役 —— 没有"不可拖"的
   * 档位了,胶囊本体恒可拖。所以这一套没有位移判据(threshold=0)。
   *
   * ⚠ 拖动期间前台会留在胶囊上 —— begin_drag 冻结了守卫的焦点归还
   * (焦点来回跳会拖垮 WebView2 的鼠标捕获),松手时 end_drag 统一
   * 还回去。这是"按下即拖"的代价,换来"看到把手就能拖"。
   *
   * 位置用 e.screenX(鼠标的**屏幕**坐标)而不是 clientX —— 窗口自己
   * 一动 clientX(视口内坐标)就变,会被窗口移动污染;发的是**绝对
   * 目标位置**,不发增量(增量在 IPC 慢的时候会漂)。 */
  var CAPSULE_DRAG_OPTS = {
    threshold: 0,                   // 按下即拖,没有位移判据
    move: 'move_capsule',
    draggingClass: 'capsule-dragging',  // 拖动期间光标换成 grabbing
    throttle: 'raf',                // 每帧最多发一次,别把 IPC 塞爆
    filter: noButtons,              // 点在 ✕ / ✓ 按钮上时不触发拖动
    // 只有**胶囊形态**(非常驻模式,或常驻模式但已展开)走这一套
    enabled: function () { return !ui.orbMode || ui.expanded; }
  };

  /* ==================== 事件绑定 ==================== */

  function bindEvents() {
    // 取消：放弃这一轮
    if (el.cancelBtn) {
      el.cancelBtn.addEventListener('click', function () {
        // 带原文的错误常驻态：这个 ✕ 是"关掉提示"，不是"取消这一轮"
        // —— 那一轮早就结束了,取消没有意义。带原文的错误态不自动
        // 收回,只有用户点这个 ✕(或开始下一轮录音)才收。
        if (ui.errorDismissable) {
          onDismissClick();
          return;
        }
        callApi('cancel_recording');
      });
    }

    // 结束：等同再按一次热键
    if (el.okBtn) {
      el.okBtn.addEventListener('click', function () {
        callApi('stop_recording');
      });
    }

    // 胶囊本体当拖拽把手(恒可拖:位置档位已退役)
    if (el.pill) {
      // 球形态 / 胶囊形态:同一个元素,两套"什么时候算拖动"的参数。
      // 流程(按下 → 拖 → 松手、桥接序列、坐标换算)全在 makeDraggable 里。
      makeDraggable(el.pill, ORB_DRAG_OPTS);
      makeDraggable(el.pill, CAPSULE_DRAG_OPTS);
      // 球形态右键 → 通用功能菜单(展开成胶囊后右键无效)
      el.pill.addEventListener('contextmenu', onOrbContextMenu, false);
    }

    // 菜单里的三个出口(点击后 Python 侧才真正执行)
    if (el.menuSettings) {
      el.menuSettings.addEventListener('click', function () {
        closeOrbMenu(true);
        callApi('open_panel');
      });
    }
    if (el.menuQr) {
      el.menuQr.addEventListener('click', function () {
        closeOrbMenu(true);
        callApi('launch_qr_scanner');
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
    if (el.textbar) {
      // 按下文字条:让胶囊临时成为前台、WebView2 拿到键盘焦点 ——
      // 窗口默认 NOACTIVATE(不抢用户输入焦点),不主动送焦点的话,
      // 选字后的 Ctrl+C、右键菜单的复制全都使不上劲(实测踩过)。
      el.textbar.addEventListener('mousedown', function () {
        callApi('focus_textbar');
      }, false);
    }
  }

  /* ==================== 对外接口 ==================== */

  // Python → JS：严格按 BRIDGE.md 的签名
  window.__capsule = {
    setMode: function (mode, payload) {
      // 显示状态的唯一入口:所有 DOM/类名/动画都收敛到 applyMode 里。
      // 前端不决定"什么时候到哪个状态" —— 那是 presenter 的事。
      applyMode(mode, payload);
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
      // 终态的清场只认 applyMode('recording')(新一轮录音)。
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
    setTheme: function (mode) {
      // 主题档位:面板改的。Python 侧会在每次录音开始前再补推一次,
      // 所以这里只需要幂等地设上属性就行。
      applyTheme(mode);
    },
    setPositionMode: function (mode) {
      // 胶囊位置档位:**已退役** —— Python 侧不再推这个档位,这里只
      // 留一个空转的兼容入口(顺带确保 can-drag 挂着)。胶囊的拖动
      // 位置和球共用同一份记忆,拖胶囊走 move_capsule。
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
      // 纯视觉入口,不是显示状态机命令 —— 状态机走 setMode。
      showOrbHint(text);
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
    el.menuQr = $('menuQr');
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
    applyPositionMode('follow');   // 位置档位已退役:这里只负责挂上 can-drag
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
