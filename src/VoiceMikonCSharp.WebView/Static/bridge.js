/* WebView2 桥接层。
 *
 * 作用：把 Python 版 pywebview 的 `window.pywebview.api.*` 契约翻译成
 * WebView2 的 `window.chrome.webview.postMessage`。
 *
 * 契约（方法名与 Python 版**逐字一致**，capsule.js / panel.js 不用改）：
 *   JS → 宿主：ready / cancel_recording / stop_recording / update_regions /
 *               copy_result / start_recording / dismiss / move_orb /
 *               move_capsule / begin_drag / end_drag / open_panel /
 *               quit_app / launch_qr_scanner / selecting_text / focus_textbar /
 *               switch_model / switch_backend / set_theme / set_preview /
 *               set_ui_mode / set_restore_clipboard / close_panel / move_window
 *   宿主 → JS：window.__capsule.* / window.__panel.*（见 capsule.js / panel.js）
 *
 * 返回值：postMessage 没有返回值，所以每个方法都返回 Promise。
 *   - 无返回值的动作：Promise 在消息发出后即 resolve(undefined)。
 *   - 有返回值的动作（copy_result / dismiss）：Promise 由 C# 侧回填
 *     （window.__bridge.resolve(id, value)），前端拿到的就是 true / false。
 *
 * 为什么单独一个文件：pywebview 的注入是"晚到的"（页面加载完才有），
 * WebView2 的 `window.chrome.webview` 在文档创建时就有 —— 所以这里
 * **同步**定义好 window.pywebview，capsule.js 的 notifyReady 一次就通。
 */
(function () {
  'use strict';

  var seq = 0;
  var pending = {};
  var queue = [];
  var flushed = false;

  /* WebView2 的 postMessage 只能在页面里用（chrome.webview 不存在时
     会抛）。文档刚创建时 chrome.webview 就有，但保险起见：发不出去就
     排队，等它出现再冲刷。

     ⚠ 冲刷必须是**每次都把队列清空** —— 不能"只冲一次"。
     chrome.webview 一旦就绪就一直可用，但队列里后来又排进去的消息
     （update_regions / start_recording …）同样要发出去。
     早期版本这里用 flushed 标志让 flush() 第二次起直接 return，
     结果 ready 之后的消息全卡在队列里（宿主永远收不到命中区上报）。 */
  function flush() {
    if (!window.chrome || !window.chrome.webview) {
      return;
    }
    if (!flushed) {
      flushed = true;
      if (window.setInterval) {
        window.clearInterval(flushTimer);
      }
    }
    while (queue.length > 0) {
      window.chrome.webview.postMessage(queue.shift());
    }
  }

  var flushTimer = window.setInterval ? window.setInterval(flush, 50) : 0;

  /* 每个 api 方法：发一条 {id, method, args} 给宿主，返回一个 Promise。
   *
   * ⚠ postMessage 传的是**对象**，不是 JSON 字符串 ——
   * WebView2 的 C# 侧 WebMessageAsJson 给的是"消息本身的 JSON"，
   * 发字符串的话那边拿到的是一个 JSON 字符串值（还得再解一层），
   * 字段全都读不出来。
   */
  function call(method, args) {
    return new Promise(function (resolve) {
      var id = ++seq;
      pending[id] = resolve;
      queue.push({ id: id, method: method, args: args || [] });
      flush();
    });
  }

  /* 宿主回填有返回值的动作结果。 */
  window.__bridge = {
    resolve: function (id, value) {
      var resolver = pending[id];
      if (resolver) {
        delete pending[id];
        resolver(value);
      }
    }
  };

  /* 方法清单：与 Python 版 host/capsule.py、host/panel.py 的 _CapsuleApi /
     _PanelApi 逐字一致。两个页面共用这一份，多定义几个方法无害。 */
  var METHODS = [
    // 胶囊
    'ready', 'cancel_recording', 'stop_recording', 'update_regions',
    'copy_result', 'start_recording', 'dismiss', 'move_orb', 'move_capsule',
    'begin_drag', 'end_drag', 'open_panel', 'quit_app', 'launch_qr_scanner',
    'selecting_text', 'focus_textbar',
    // 面板
    'switch_model', 'switch_backend', 'set_theme', 'set_preview',
    'set_ui_mode', 'set_restore_clipboard', 'close_panel', 'move_window'
  ];

  var api = {};
  for (var i = 0; i < METHODS.length; i++) {
    (function (name) {
      api[name] = function () {
        return call(name, Array.prototype.slice.call(arguments));
      };
    }(METHODS[i]));
  }

  window.pywebview = { api: api };
}());
