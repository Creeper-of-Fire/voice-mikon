/* qwen3-voice 录音胶囊 —— 前端逻辑
 *
 * 纯原生 JS：零 npm、零框架、零 CDN、零外部字体/图标请求，离线可用。
 *
 * 波形渲染（9 根对称包络 + 高度算法）与阶段切换**移植自 OpenType**
 * (https://github.com/ct-jyjntc/opentype-public) 的
 * ``src/renderer/floating-bar.tsx`` —— AGPL-3.0。
 * 本项目整体按 AGPL-3.0-or-later 发布(见 LICENSE)。
 *
 * 与移植版的差异(适配改动,不是照抄):
 *   - 电平的量纲不同:OpenType 的 level 是它自己的线性电平(0.1 就饱和),
 *     我们的是 dBFS 对数域映射后的绝对值(人声约 0.4~0.7)。直接照搬
 *     ``level / 0.1`` 会让波形永远是满的 —— 那就把"我说话声音大不大"
 *     这个信息抹掉了。所以饱和因子改成 ``min(1, level)``,
 *     **包络形状与相位波动完全照搬**。
 *   - 计时是本项目自有的(OpenType 的胶囊没有计时)。
 *   - 图标用内联 SVG(Lucide 的 X / Check / Info / LoaderCircle),
 *     不用 unicode 字符凑合。
 *
 * 桥接契约（唯一权威来源 ../BRIDGE.md），方法名必须逐字一致，不得增删改名：
 *   Python → JS : window.__capsule.setState / setLevels / setElapsed
 *                 / setPreview / setResult / setError / setCancelled
 *                 / resetForRecording / setTheme
 *   JS → Python : window.pywebview.api.ready / cancel_recording / stop_recording
 *                 / update_regions
 */
(function () {
  'use strict';

  /* ==================== 常量 ==================== */

  /* ---- 波形包络（移植自 OpenType）----
     对称包络:两端短、中间高。9 根固定的小条,不滚动。 */
  var WAVE = [5, 8, 12, 16, 19, 16, 12, 8, 5];

  /* 主题档位:auto = 跟随系统,light = 白天,dark = 黑夜 */
  var THEMES = ['auto', 'light', 'dark'];

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
    theme: 'auto'       // 主题档位
  };

  var el = {};              // DOM 缓存
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

  /* ==================== 胶囊阶段 ==================== */

  // 切换胶囊阶段。phase 以 class 的形式挂在胶囊上(和 OpenType 一致),
  // CSS 靠它决定显示波形还是状态文字、右侧是结束钮还是状态图标。
  function setPhase(phase) {
    ui.phase = phase;
    if (el.pill) {
      for (var i = 0; i < PHASES.length; i++) {
        el.pill.classList.toggle(PHASES[i], PHASES[i] === phase);
      }
    }
    if (el.barLabel) {
      el.barLabel.textContent = PHASE_TEXT[phase] || '';
    }
    // 阶段换了,胶囊的宽度会变(波形 ↔ 状态文字)→ 窗口形状要跟着变
    scheduleRegionReport();
  }

  /* ==================== 文字条 ==================== */

  // 写文字条内容。kind 决定配色：normal / error / cancelled
  function writeText(text, kind) {
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
      // 空序列：这一帧没有新样本，不动平滑器
      return;
    }
    // Python 侧 30fps 推一串滑动窗口，只取最新的一个值喂给平滑器，
    // **不做窗口内归一化** —— 归一化之后波形永远看起来是满的，
    // 用户就没法从条的高低看出"我说话声音大不大"了。
    ui.level = smoothLevel(vals[vals.length - 1]);
    renderWave();
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

  /* ==================== 波形（移植自 OpenType）================ */

  // 9 根对称包络的小条:两端短、中间高,高度由真实电平驱动,
  // 相位随秒数流动(整体在"呼吸")。
  //
  // 高度公式移植自 OpenType 的 floating-bar.tsx:
  //   height = 3 + min(1, level/0.1) * base * (0.75 + 0.25*sin(...))
  // 我们的 level 是 dBFS 对数域的绝对值(人声 0.4~0.7),直接用
  // level/0.1 会永远饱和 —— 包络照搬,饱和因子改成 min(1, level)。
  function renderWave() {
    if (!el.waveform) {
      return;
    }
    var bars = el.waveform.children;
    if (!bars.length) {
      return;
    }
    var lvl = clamp(ui.level, 0, 1);
    var sec = Math.floor(ui.elapsed);
    for (var i = 0; i < bars.length; i++) {
      var base = WAVE[i] || 5;
      var swing = 0.75 + 0.25 * Math.sin(Math.abs(i - 4) * 1.3 + sec * 2);
      var height = 3 + lvl * base * swing;
      bars[i].style.height = height.toFixed(2) + 'px';
    }
  }

  // 建一次 9 根条(固定不滚动 —— 包络靠高度变化表达流动)
  function buildWave() {
    if (!el.waveform) {
      return;
    }
    el.waveform.textContent = '';
    for (var i = 0; i < WAVE.length; i++) {
      var bar = document.createElement('i');
      bar.style.height = '3px';
      el.waveform.appendChild(bar);
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
    // 秒数变了,包络的相位也跟着变(OpenType 就是这么做的)
    renderWave();
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
      writeText(text == null ? '' : String(text), 'normal');
    },
    setResult: function (text) {
      // 最终结果：文字条定格
      writeText(text == null ? '' : String(text), 'normal');
      setPhase('done');
    },
    setError: function (text) {
      writeText(text == null ? '' : String(text), 'error');
      setPhase('error');
    },
    setCancelled: function () {
      writeText('已取消', 'cancelled');
      setPhase('cancelled');
    },
    resetForRecording: function () {
      // 新一轮录音:清掉上一轮的文案与配色
      writeText('', 'normal');
      smoother.current = 0;
      ui.level = 0;
      setElapsed(0);
      setPhase('recording');
    },
    setTheme: function (mode) {
      // 主题档位:面板改的。Python 侧会在每次录音开始前再补推一次,
      // 所以这里只需要幂等地设上属性就行。
      applyTheme(mode);
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
  }

  // Python 可能在 DOMContentLoaded 之前就推状态，这里做惰性绑定兜底
  function ensureDom() {
    if (!el.waveform) {
      bindDom();
    }
  }

  function init() {
    bindDom();
    buildWave();

    writeText('', 'normal');
    setElapsed(0);
    setPhase('recording');
    applyTheme('auto');   // Python 就绪后会用 setTheme 推真实档位

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
