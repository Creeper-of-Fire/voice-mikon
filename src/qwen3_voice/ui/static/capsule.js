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
 *   JS → Python : window.pywebview.api.ready / cancel_recording / stop_recording
 *                 / update_regions / move_capsule / begin_drag / end_drag
 *                 / copy_result
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
  var BAR_TICK_MS = 80;

  // 元素池 = 可见条数 + 1:多出来的那根已经滑出视口左侧,
  // 用来"回收"(复用同一个 DOM 节点当新条),省得新条横穿屏幕飞进来
  var POOL_SIZE = BAR_COUNT + 1;

  // 静音死区:响度 <= 0.04 视为底噪
  var DEAD_ZONE = 0.04;

  // 条高上下限(px):静音 2px,最响 20px
  var SILENT_HEIGHT = 2;
  var MAX_HEIGHT = 20;

  // 4px 正好是 level = 0.04 对应的高度(MCode 的死区上界);
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
    cancelled: '已取消'
  };

  // 阶段类名(和 OpenType 一样用 class 挂在胶囊上)
  var PHASES = ['recording', 'processing', 'done', 'error', 'cancelled'];

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
    copying: false          // 复制进行中(防连点)
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
    // 只留两位小数:再多只是给 Python 添乱
    return [
      Math.round(r.left * 100) / 100,
      Math.round(r.top * 100) / 100,
      Math.round(r.width * 100) / 100,
      Math.round(r.height * 100) / 100
    ];
  }

  function measureRegions() {
    return {
      textbar: rectOf(el.textbar),
      pill: rectOf(el.pill)
    };
  }

  // 把两块内容的矩形上报给 Python。
  // 节流用"尾沿触发":60ms 内的多次变化合并成一次,而且**最后一次一定
  // 发得出去**(不是掐头)—— 少发一次,窗口形状就停在旧尺寸上。
  function reportRegions() {
    regionTimer = 0;
    if (!apiReady) {
      return;
    }
    var payload = measureRegions();
    // 页面还没排版(两块内容的尺寸全 0)时不上报:那不是"内容隐藏",
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
      el.textbar.classList.remove('is-error', 'is-cancelled');
      el.textbar.classList.add('is-empty');
    }
    if (el.textbarText) {
      el.textbarText.textContent = '';
    }
    scheduleRegionReport();
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

  // 写文字条内容。kind 决定配色：normal / error / cancelled
  //
  // 预览关掉的时候**一个字都不写**:文字条整块都不该出现
  // (CSS 里 .no-preview 把它 display:none,这里保证内容也是空的,
  //  免得切回"开"的时候闪出上一轮的旧内容)。
  function writeText(text, kind) {
    if (!ui.previewEnabled) {
      return;
    }
    var value = text == null ? '' : String(text);
    if (el.textbar) {
      el.textbar.classList.remove('is-empty', 'is-error', 'is-cancelled');
      if (kind === 'error') {
        el.textbar.classList.add('is-error');
      } else if (kind === 'cancelled') {
        el.textbar.classList.add('is-cancelled');
      }
      el.textbar.classList.toggle('is-empty', !value.trim());
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
  //   0 ~ 0.04 : 固定 2px(死区 / 底噪)
  //   0.04 ~ 0.2: 4 → 12px(斜率 50,最灵敏)
  //   0.2  ~ 0.5: 12 → 18px(斜率 20)
  //   0.5  ~ 1  : 18 → 20px(斜率 4,压缩)
  //
  // ⚠️ 输入是**绝对电平**(Python 侧 dbfs_to_unit 的 dBFS 对数映射值),
  // 前端绝不按窗口内最大值归一化 —— 归一化之后条子永远看起来是满的,
  // 用户就没法从条的高低看出"我说话声音大不大"了。
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
    if (ui.positionMode !== 'custom') {
      return;
    }
    if (e.button !== 0) {
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
    }

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
      if (state === 'recording') {
        setPhase('recording');
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
      // 终态(done/error/cancelled)后,控制器还会紧跟一对
      // preview("") 用来清预览 —— 那一下会把刚定格的结果/红底错误
      // 文案抹回占位符(后端同批先发 result 再发 preview)。
      // 终态的清场只认 resetForRecording(新一轮录音)。
      if (ui.phase === 'done' || ui.phase === 'error'
          || ui.phase === 'cancelled') {
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
    },
    setError: function (message, payload) {
      var msg = message == null ? '' : String(message);
      var raw = payload == null ? '' : String(payload);
      ui.errorPayload = raw;
      ui.copying = false;
      // 带原文 = 注入失败那一类:文字条显示**识别原文**(用户要的
      // 就是这段话),胶囊里给一个「复制」按钮。没有原文的错误
      // (没听清 / 识别失败)只显示文案,不给按钮 —— 没东西可复制。
      var canCopy = !!raw.trim();
      if (el.pill) {
        el.pill.classList.toggle('can-copy', canCopy);
      }
      if (canCopy) {
        if (ui.previewEnabled) {
          writeText(raw, 'error');
          setPhase('error', '点右侧复制');
        } else {
          // 预览关着:文字条不出现,全文只能靠这个按钮拿走
          setPhase('error', msg + ' · 点右侧复制');
        }
      } else if (ui.previewEnabled) {
        writeText(msg, 'error');
        setPhase('error');
      } else {
        // 预览关着:错误文案进胶囊的 label 位(一行截断即可)
        setPhase('error', msg);
      }
    },
    setCancelled: function () {
      if (ui.previewEnabled) {
        writeText('已取消', 'cancelled');
      }
      setPhase('cancelled');
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
    }
  };

  /* ==================== 启动 ==================== */

  // 绑定 DOM 引用（可重复调用）
  function bindDom() {
    el.textbar = $('textbar');
    el.textbarText = $('textbarText');
    el.pill = $('pill');
    el.waveform = $('waveform');
    el.barLabel = $('barLabel');
    el.barTimer = $('barTimer');
    el.cancelBtn = $('cancelBtn');
    el.okBtn = $('okBtn');
    el.copyBtn = $('copyBtn');
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
