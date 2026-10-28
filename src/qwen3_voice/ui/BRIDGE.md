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
| `__qwen.setPreview(text)` | `text` string | **实时预览文字**:录音过程中已识别出的文字,空串表示清空 |
| `__qwen.setModel(tag)` | `tag` ∈ `"0.6B"` \| `"1.7B"` | 当前模型 |
| `__qwen.setHotkey(text)` | `text` string | 热键显示文本,启动时推一次 |

### `setLevels` 的语义(重要)

`payload` 是 **JSON 字符串**,内容是**最近若干帧的音量序列**(0..1),
由 Python 侧做 dBFS 对数域映射(-70 dB → 0,0 dB → 1)。
**数组最后一个元素是最新值**。

- 底噪 ≈ 0,正常说话 ≈ 0.5,很响 ≈ 1
- **前端绝不做窗口内归一化** —— 归一化之后电平条永远看起来是满的,
  用户就没法从条的高低看出"我说话声音大不大"了
- 前端可以加**平滑**(指数插值)和**非线性高度映射**,但不能凭空加抖动
  或随机数 —— 那是在伪造数据

前端在 DOM 就绪后调用 `window.pywebview.api.ready()`;Python 收到 `ready()`
后立即推一次**全量状态**(state / levels / elapsed / status / result / model / hotkey)。

### preview 与 result 的关系

- 录音过程中,后端把**累积到目前的全部音频**重新喂给模型识别一次,
  模型每次都**重新生成完整文本**,所以推给前端的 `setPreview` 是
  **整体替换**的 —— 不存在拼接、去重、切句
  (做法与官方 `qwen_asr` 的 `streaming_transcribe` 一致)
- `setPreview("")` 表示清空(录音开始时、以及最终结果到来时)
- `setResult(text)` 是停止录音后的最终文本,前端收到它时应当清掉 preview
- 两者**不同时显示**:有 preview 就不显示 final,final 来了就替换 preview

### preview 是怎么算出来的

- 每隔一段时间(约 1.5 秒)取一次录音器的**当前快照**(不消费音频,
  录音继续),重采样到 16 kHz 后整段送模型识别
- 停止录音时,用**整段音频**再识别一次作为最终结果 —— 最终结果的
  质量与预览无关,预览只是提前给用户看
- 因此预览文字在录音过程中可能"变来变去"(后一次整体替换前一次),
  这是预期行为,不是 bug

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

**滚动条形图**,参照 MiniMax Code 的 `asr-volume-bars` 组件:

- 22 根竖条,右对齐,**新条从右进、旧的从左出**(每 80ms 推进一根)
- 条宽 3px、间距 3px、`border-radius: 3px`(胶囊)、纯色、底对齐
- **4 段非线性高度映射**(这是"对比度明显"的来源,不能简化成线性):
  - `0 ~ 0.04` → 固定 2px(死区)
  - `0.04 ~ 0.2` → 4 ~ 12px(斜率 50,最灵敏)
  - `0.2 ~ 0.5` → 12 ~ 18px(斜率 20)
  - `0.5 ~ 1.0` → 18 ~ 20px(斜率 4,压缩)
- **三槽轮转**:每次算出三个高度(插值态 / 当前态 / 衰减态)轮流用,
  相邻条高度天然错开,不会是一排单调的长短
- **升降不对称平滑**:上升系数 0.7、下降系数 0.5(有惯性,不快回零)
- 高度过渡 `transition: height 75ms ease-out`
- 左侧盖一个 16px 渐变遮罩(卡片底色 → 透明),让条子"从虚无中浮现"
- **静音(level ≤ 0.04)→ 2px 灰条,但仍然继续滑** —— 诚实表达"在录但没声音"
- **待机 / 识别中不显示条形**,改成一行状态文字
  (不录音时显示会动的假动画属于骗人)

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
