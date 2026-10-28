# 面板前后端桥接契约

前端(`static/index.html` + `panel.css` + `panel.js`)与 Python 宿主
(`panel.py`)之间的**唯一接口定义**。

两侧实现必须严格遵守,不得自行增删、改名或改语义。改这里就要同时改两边。

---

## 技术形态

- Python 侧:`pywebview`(Windows 上走 WebView2 / Edge 内核)
- 前端:**纯静态 HTML + CSS + 原生 JS**
  - **禁止**引入 React / Vue / Tailwind / npm / 构建步骤
  - 理由:项目定位是"绿色软件"——`git clone` + `pip install -e .` 就能跑,
    不应该出现 node_modules。前端产物就是仓库里的三个静态文件。

---

## Python → JS

Python 侧通过 `window.evaluate_js(...)` 调用 JS 全局命名空间 `window.__qwen` 上的方法。

**每次调用前必须判断目标方法存在**(前端可能尚未加载完成),不存在就跳过。

| 方法 | 参数 | 说明 |
|---|---|---|
| `__qwen.setState(state)` | `state` ∈ `"starting"` \| `"idle"` \| `"recording"` \| `"processing"` \| `"error"` | 状态机变化时调用 |
| `__qwen.setLevels(payload)` | `payload` 是 **JSON 字符串**,形如 `"[0.012,0.31,0.44]"` | 录音中 30fps 推送 |
| `__qwen.setElapsed(seconds)` | number | 录音中 30fps |
| `__qwen.setStatus(text, tone)` | `text` string,`tone` ∈ `"dim"` \| `"ok"` \| `"warn"` \| `"err"` | 状态行文字与颜色 |
| `__qwen.setResult(text)` | `text` string | 识别结果全文;空串表示清空 |
| `__qwen.setModel(tag)` | `tag` ∈ `"0.6B"` \| `"1.7B"` | 当前模型 |
| `__qwen.setHotkey(text)` | `text` string | 热键显示文本,启动时推一次 |

### `setLevels` 的语义(重要)

数组元素是 **0..1 的绝对电平**,由 Python 侧做 dBFS 映射(-70 dB → 0,
0 dB → 1,对数域)。

- **前端绝不做"按窗口内最大值归一化"** —— 那会把"声音大不大"这个信息抹掉,
  电平条永远看起来是满的,就只剩装饰作用了。
- 前端取数组的 **RMS** 作为当前幅度,取 **最大值** 作为峰值保持的输入。

前端在 DOM 就绪后调用 `window.pywebview.api.ready()`;Python 收到 `ready()`
后立即推一次**全量状态**(state / levels / elapsed / status / result / model / hotkey)。

---

## JS → Python

通过 `window.pywebview.api.<方法>`。这些是普通 Python 方法,pywebview 会自动
包装成 Promise。

| 方法 | 返回 | 说明 |
|---|---|---|
| `ready()` | None | 前端已就绪,可以开始推状态 |
| `toggle_recording()` | None | 开始 / 停止录音 |
| `switch_model(tag)` | None | `tag` ∈ `"0.6B"` / `"1.7B"` |
| `get_snapshot()` | dict | `{state, elapsed, model, last_result, hotkey}` |
| `move_window(dx, dy)` | None | 拖动窗口,**相对位移,单位物理像素** |
| `close_panel()` | None | 隐藏面板(**不是**退出程序) |
| `quit_app()` | None | 退出整个程序 |

### 拖动的单位

前端发的是**物理像素**(`dx * devicePixelRatio`)。
pywebview 6.x 的 `Window.move(x, y)` 吃的是**逻辑像素**(内部自己乘缩放),
所以 Python 侧必须做一次 physical → logical 换算,否则在 175% 屏幕上
窗口会比光标快 1.75 倍。

---

## 状态与视觉规格

参考对象是 OpenType(https://www.opentype.top/)的观感,但**不能照抄它的品牌名、
logo、文案、配色**。只学交互与视觉语言。

### 电平条

3 个成分叠加,各司其职 —— 这才是它好看又诚实的原因:

```js
var ENV = [0.45, 0.78, 1.0, 0.78, 0.45];   // ① 固定对称包络,中间最高
var h = env.map(function (e) {
  return Math.max(0.12, e * amp * (0.25 + rand * 0.75));
});
//                    ② 真实绝对电平            ③ 随机抖动
```

- 5 根竖条,水平基线上下对称生长,**中间那根最高**
- 抖动系数按镜像成对共享(`[r0, r1, r2, r1, r0]`),让最左和最右**逐像素严格等高**
- **峰值保持作用在条形高度上**(每根条记住自己的峰值高度,24 dB/s 回落),
  不是画一条独立的横线 —— 全宽横线标不出"最近的峰值",反而喧宾夺主
- 待机时走"基准 + 呼吸"的合成幅度(直接喂呼吸幅度会被公式的下限 clamp
  成一条死线)

### 布局

- 面板是常驻、可拖动、可隐藏的小窗,无系统边框,自绘标题栏
- **窗口本身是矩形**(WebView2 做不到真透明圆角),窗口背景 = 卡片底色
- CSS 用变量统一管理配色;零外链(离线必须可用);字体全用系统栈

---

## 约束

- 不引入任何 npm 依赖、前端框架、CDN 外链
- 所有资源本地化,不能引用外部字体/图标服务
- 前端不得直接访问 Python 内部对象,只能走 `window.pywebview.api`
- Python 侧暴露给 JS 的桥接对象要**单独一个类**,只放方法。
  pywebview 的 `get_functions` 会遍历公开属性,遇到非 callable 的对象还会
  **递归**下去 —— 直接把面板对象交出去,控制器和配置的内部属性都会泄漏进网页。
