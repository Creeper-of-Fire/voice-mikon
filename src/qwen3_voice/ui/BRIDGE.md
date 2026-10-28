# 前后端桥接契约

前端与 Python 宿主之间的**唯一接口定义**。项目现在有**两个**前端:

| 窗口 | 前端文件 | Python 宿主 | 职责 |
|---|---|---|---|
| 面板 | `static/index.html` + `panel.css` + `panel.js` | `panel.py` | 配置窗口:状态、模型切换、退出 |
| 胶囊 | `static/capsule.html` + `capsule.css` + `capsule.js` | `capsule.py` | 录音反馈:实时预览、波形、计时 |

两侧实现必须严格遵守,不得自行增删、改名或改语义。改这里就要同时改两边。

---

## 技术形态

- Python 侧:`pywebview`(Windows 上走 WebView2 / Edge 内核)
- 前端:**纯静态 HTML + CSS + 原生 JS**
  - **禁止**引入 React / Vue / Tailwind / npm / 构建步骤
  - 理由:项目定位是"绿色软件"——`git clone` + `pip install -e .` 就能跑,
    不应该出现 node_modules。前端产物就是仓库里的静态文件。
- 两个窗口共用**一次** `webview.start()`;第一个 `create_window` 的窗口是
  master(GUI 循环跑在 pywebview 自己起的 STA 线程上,主线程只是阻塞
  等它返回),后面的窗口由 pywebview 另起一个线程创建
- 透明窗口的坑(pywebview 自带抢焦点 hack、三层背景)见下方
  「胶囊必须不抢焦点」一节

---

# 面板窗口(`window.__qwen`)

## Python → JS

Python 侧通过 `window.evaluate_js(...)` 调用 JS 全局命名空间 `window.__qwen` 上的方法。

**每次调用前必须判断目标方法存在**(前端可能尚未加载完成),不存在就跳过。

| 方法 | 参数 | 说明 |
|---|---|---|
| `__qwen.setState(state)` | `state` ∈ `"starting"` \| `"idle"` \| `"recording"` \| `"processing"` \| `"error"` | 状态机变化时调用(面板只拿它点亮标题栏的状态点) |
| `__qwen.setModel(tag)` | `tag` ∈ `"0.6B"` \| `"1.7B"` | 当前模型 |
| `__qwen.setHotkey(text)` | `text` string | 热键显示文本,启动时推一次 |

> 面板瘦身后**不再**接收 `setLevels` / `setElapsed` / `setStatus` /
> `setResult` / `setPreview` —— 这些全部归胶囊窗口。
> 推送层(面板的 `_EventPump`)按窗口过滤:面板只推上面这三个,
> 胶囊推全套。

前端在 DOM 就绪后调用 `window.pywebview.api.ready()`;Python 收到 `ready()`
后立即推一次**全量状态**(state / model / hotkey)。

## JS → Python

通过 `window.pywebview.api.<方法>`。这些是普通 Python 方法,pywebview 会自动
包装成 Promise。

| 方法 | 返回 | 说明 |
|---|---|---|
| `ready()` | None | 前端已就绪,可以开始推状态 |
| `switch_model(tag)` | None | `tag` ∈ `"0.6B"` / `"1.7B"` |
| `get_snapshot()` | dict | `{state, model, hotkey}` |
| `move_window(x, y)` | None | 窗口左上角移到**绝对位置** (x, y),CSS(逻辑)像素 |
| `close_panel()` | None | 隐藏面板(**不是**退出程序) |
| `quit_app()` | None | 退出整个程序 |

### 拖动的单位与节流

前端拖动时**每次都从 mousedown 起点算绝对目标**:

    目标 = mousedown 时的 window.screenX/Y + (当前 clientX/Y − 起点_clientX/Y)

发绝对位置而不是增量:某一次调用丢了或慢了,下一次直接覆盖,
天然不漂。mousemove 用 `requestAnimationFrame` 节流,每帧只发
最新一次(IPC ≤ 60Hz),Python 侧直接 `SetWindowPos`(物理 =
逻辑 × 缩放)。

两个已证伪的方案,勿复辟:
1. mousemove 发增量、Python 累计 —— IPC 一慢就漂;
2. `SendMessage(SC_MOVE)` 原生拖动 —— 在 WebView2 架构下死锁
   js_api 调用链,整个面板点什么都卡(实测)。

---

# 胶囊窗口(`window.__capsule`)

胶囊是录音过程的**唯一**视觉载体:上方一条实时预览文字,下方一颗胶囊
(取消 / 波形+计时 / 结束)。面板常驻与否都不影响录音体验。

## Python → JS

命名空间是 `window.__capsule`(和面板分开,避免两边方法名打架)。

| 方法 | 参数 | 说明 |
|---|---|---|
| `__capsule.setState(state)` | `state` ∈ `"starting"` \| `"idle"` \| `"recording"` \| `"processing"` \| `"error"` | 状态机变化时调用;`recording` 重建波形条,`processing` 切成"识别中" |
| `__capsule.setLevels(payload)` | `payload` 是 **JSON 字符串**,形如 `"[0.012,0.31,0.44]"` | 录音中 30fps 推送 |
| `__capsule.setElapsed(seconds)` | number | 录音中 30fps,显示成 `m:ss` |
| `__capsule.setPreview(text)` | `text` string | **实时预览文字**:录音过程中已识别出的文字,空串表示清空 |
| `__capsule.setResult(text)` | `text` string | 识别完成并已注入:文字条**定格**最终结果 |
| `__capsule.setError(text)` | `text` string | 出错:文字条转暗红底 + 红字 |
| `__capsule.setCancelled()` | 无 | 用户主动取消:文字条显示「已取消」 |
| `__capsule.resetForRecording()` | 无 | 新一轮录音开始:清掉上一轮的文案与配色 |

### `setLevels` 的语义(重要)

和面板时代的规则**完全一致**,一个字都没改:

`payload` 是 **JSON 字符串**,内容是**最近若干帧的音量序列**(0..1),
由 Python 侧做 dBFS 对数域映射(-70 dB → 0,0 dB → 1)。
**数组最后一个元素是最新值**。

- 底噪 ≈ 0,正常说话 ≈ 0.5,很响 ≈ 1
- **前端绝不做窗口内归一化** —— 归一化之后电平条永远看起来是满的,
  用户就没法从条的高低看出"我说话声音大不大"了
- 前端可以加**平滑**(指数插值)和**非线性高度映射**,但不能凭空加抖动
  或随机数 —— 那是在伪造数据

### preview 与 result 的关系

- 录音过程中,后端把**累积到目前的全部音频**重新喂给模型识别一次,
  模型每次都**重新生成完整文本**,所以推给前端的 `setPreview` 是
  **整体替换**的 —— 不存在拼接、去重、切句
- `setPreview("")` 表示清空(录音开始时、以及最终结果到来时)
- `setResult(text)` 是停止录音后的最终文本,文字条定格在它上面
- 两者**不同时显示**

### 显隐时机

胶囊的显隐**由 Python 侧管**,前端只管画:

| 时刻 | 胶囊 | 停留 |
|---|---|---|
| 进入 `recording` | 立刻显示(**不抢焦点**) | 直到出结果 |
| `result`(已注入) | 文字条定格结果,胶囊显示「已输入 ✓」 | 1.5s |
| `error`(录音太短/没有检测到声音/识别失败/注入失败) | 文字条暗红底 + 浅红字 | 4.5s |
| `cancelled`(用户点 ✕) | 文字条显示「已取消」 | 1s |

新一轮录音开始时,上一轮的隐藏定时器必须作废(否则刚开始的胶囊会被
旧的定时器藏掉)。

## JS → Python

| 方法 | 返回 | 说明 |
|---|---|---|
| `ready()` | None | 前端已就绪 |
| `cancel_recording()` | None | 放弃这一轮:**不识别、不注入** |
| `stop_recording()` | None | 结束录音,进入识别(等同再按一次热键) |
| `update_regions(payload)` | None | 上报"文字条 + 胶囊"的矩形,Python 侧据此裁窗口命中区(见「窗口区域裁形」) |

---

## 状态与视觉规格

参考对象是 OpenType(https://www.opentype.top/)的观感,但**不能照抄它的品牌名、
logo、文案、配色**。只学交互与视觉语言。

### 波形条

胶囊里的波形是**9 根固定小条**的对称包络(移植自 OpenType 的
`recording-pill`),不滚动、不轮转:

- 条宽 3px、间距 3px、`border-radius: 3px`、纯色、垂直居中
- **底高包络** `[5, 8, 12, 16, 19, 16, 12, 8, 5]`(两端短、中间高)
- 高度公式:`3 + level × base × (0.75 + 0.25 × sin(|i-4|×1.3 + 秒×2))`
  —— 底高是包络,正弦项让整体在"呼吸",秒数让相位流动
- 高度过渡 `transition: height 90ms`
- 高度**由真实电平驱动**:电平是 Python 侧 dBFS 对数域映射后的绝对值
  (人声约 0.4~0.7),饱和因子是 `min(1, level)` —— 前端**绝不做窗口内
  归一化**,否则波形永远看起来是满的,"我说话声音大不大"这个信息就没了
- 可以平滑(上升 0.7 / 下降 0.5,再叠一层自适应增益),
  但**不能凭空加抖动或随机数** —— 那是在伪造数据

> 面板时代的"滚动条形图"(22 根、三槽轮转、4 段非线性映射)已经随面板
> 瘦身一起删掉了,现在只有胶囊这一套。

### 日夜模式

- **胶囊固定深色**,不跟随系统 —— 它是压在别人窗口上的浮层,
  深底在任何背景上都读得清
- **面板跟随系统**:`prefers-color-scheme` 媒体查询,WebView2 直接
  跟随 Windows 的"应用模式",不需要 JS、不需要监听切换事件
- 配色全部收敛在 `:root` 的 CSS 变量里,深色模式只覆盖变量值,
  两套主题共用同一份结构
- 窗口底色(`background_color`)也按系统主题给,免得深色系统下
  页面加载前闪一下白

### 胶囊窗口的视觉规格

- 透明窗口,可见的只有两块:上方**文字条**、下方**胶囊**,中间留透明间隙
- **窗口尺寸固定**(512×260 逻辑像素,请求值会被 WinForms AutoScale
  缩水,建窗时放大补偿),**不随内容高度变化**。
  原因见下方「透明窗口不能 resize」—— 一旦 resize,WebView2 的透明
  背景就失效,窗口外的区域会变成 WinForms 的灰底。
  内容用 CSS 贴着窗口底部排(`.stack` 的 `justify-content: flex-end`),
  文字条到 `max-height` 就自己内部滚动
- **透明窗口上不能有 `box-shadow` / `backdrop-filter`** —— 它们会渲染成
  一条灰带。层次只靠"深底 + 半透明白描边"表达
- 文字条:底色 `#0b0b0c`、字色 `#f4f4f5`、描边 `rgba(255,255,255,0.2)`、
  圆角 14px、内边距 10~14px、字号 14px、行高 1.8、
  `white-space: pre-wrap` + `overflow-wrap: anywhere`、
  最大宽 480px / 最大高 120px(超出内部滚动)
- 胶囊:高 40px、圆角 999px、同款深底与描边,内容是
  `[✕ 取消(30×30 圆钮,底色 #3a3a3e)] [波形区 + 计时] [✓ 结束(30×30 圆钮,
  底色与波形同色 #ff5a5a)]`
- 错误态:文字条底色 `#2a0f11`、描边 `rgba(255,107,107,0.3)`、字色 `#fecaca`
- 识别中:波形区换成「识别中…」+ 纯 CSS 旋转动画(不引外部图标库)

### 胶囊必须不抢焦点(硬性要求)

抢了焦点,后面注入的文字就没有落点了(光标被抢到胶囊上,文字全丢)。

- 窗口扩展样式加 `WS_EX_NOACTIVATE`,并走 tool window(不进任务栏)
- 显示一律用 `ShowWindow(hwnd, SW_SHOWNOACTIVATE)`,
  **不能**用 pywebview 的 `window.show()` —— 它内部会调 WinForms 的
  `Activate()`,必然抢焦点

#### pywebview 对透明窗口自带一个抢焦点的 hack(实测)

winforms 后端的 `EdgeChrome.on_navigation_start` 里有一段:

```python
if self.pywebview_window.transparent:
    self.form.Show()
    self.form.Activate()      # ← 抢焦点
```

源码注释写着 "no idea why this works"。代价是:

- 胶囊在**启动时**就自己显示出来了(用户还没按热键就能看见它)
- 而且它成了**前台窗口**,用户的键盘输入全进了胶囊

`WS_EX_NOACTIVATE` **拦不住**它 —— `Form.Activate()` 走的是
`SetForegroundWindow`,那是程序显式激活,不是"用户点击"。

**定点修法**:在 `before_show` 事件里把 `window.transparent` 置 `False`。
这个时机唯一能同时满足两件事:

- BrowserForm / EdgeChrome 的构造函数**已经**按 `transparent=True`
  设好了 WebView2 的 `DefaultBackgroundColor = Transparent`
  → **透明效果不受影响**(实测:胶囊外的桌面照常透出来)
- 而导航还没开始,`on_navigation_start` 读到的 `transparent` 已是
  `False` → 那段 hack 不再执行

`before_show` 是**同步**事件(`Event(self, True)`),所以这个修改一定
早于导航开始,没有竞态。

另外还有一层兜底:建窗后的头几秒跑一个**守卫线程**,窗口不该露面就
藏回去、焦点被抢就还回去(还原到"用户当时所在的窗口",而不是程序
启动时的那个)。录音期间守卫**不抢焦点** —— 那时用户可能正要点胶囊上的
✕ / ✓,焦点在胶囊上是他故意的。

#### 透明窗口不能 resize

pywebview 的透明窗口一旦被 `SetWindowPos` 改过尺寸,WebView2 就会
用 Form 的背景色(`#F0F0F0`)把整块客户区填掉 —— 窗口外的区域
从"透出桌面"变成"一块灰底"。实测:resize 前透明、resize 后灰底。

两条看着能走的路都堵死:

| 方案 | 结果 |
|---|---|
| Win32 色键(`WS_EX_LAYERED` + `LWA_COLORKEY`) | 对 WebView2 这个**子窗口**无效 —— 灰底是 WebView2 自己画的 |
| resize 后重设 `DefaultBackgroundColor` | 被 CoreWebView2 的"只能从 UI 线程访问"挡下(`InvalidOperationException`) |

所以胶囊窗口**固定尺寸**,`capsule.py` 里没有 fit 线程、没有
`evaluate_js` 量高度、没有 `resize` —— 高度是常量,内容用 CSS 贴底排。

代价:窗口的**空白区域也属于窗口**(鼠标命中会被它吃掉)。录音期间
屏幕底部有一块透明区域点不到 —— 已用 `SetWindowRgn` 把窗口区域
裁成"文字条 + 胶囊"的形状解决(见下节),既贴合内容,又不用 resize。

#### 窗口区域裁形(SetWindowRgn)

窗口是**固定尺寸**的,所以可见内容(文字条 + 胶囊)之外的透明空白
**也属于窗口** —— 鼠标点上去会被胶囊吃掉,底下的应用就点不到。

解法是把窗口的**命中区域**裁成两块内容的并集:区域外的像素既不画
也不吃鼠标,点击直接穿透。既不 resize,也不动透明实现。

| 环节 | 谁做 | 说明 |
|---|---|---|
| 量矩形 | 前端 `capsule.js` | `getBoundingClientRect()`,**CSS 逻辑像素**、相对视口 |
| 上报 | 前端 → Python | `window.pywebview.api.update_regions(payload)`,60ms 合并一次 |
| 换算 | Python `capsule.py` | 逻辑 × DPI 缩放 = 物理像素,每边再外扩 8 物理像素 |
| 裁窗口 | `win32/api.py` | `CreateRectRgn` ×N → `CombineRgn(RGN_OR)` → `SetWindowRgn` |

payload 格式:

```json
{"textbar": [x, y, w, h], "pill": [x, y, w, h]}
```

全 0 表示那块隐藏(不参与并集);两块都隐藏时裁成 1×1 的最小区域。

几个坑:

- **区域句柄的生命周期**:`SetWindowRgn` **成功后区域归系统所有**,
  而且系统**不拷贝** —— 调用方绝不能再 `DeleteObject`(MSDN 明说)。
  只有**失败**时区域还在我们手里,必须自己释放。
- **坐标是相对窗口左上角**的物理像素(不是客户区)。胶囊是 frameless
  的,窗口原点 == 客户区原点,前端的视口坐标可以直接用。
- **前端第一次上报之前不裁**(保持整窗):那时还没量准,裁了会把可见
  内容切掉。区域是窗口自己的属性,隐藏/显示不用重设。
- **节流是"尾沿触发"**:60ms 内的多次变化合并成一次,而且最后一次
  一定发得出去 —— 掐头会让窗口形状停在旧尺寸上。

#### 还原焦点不许模拟按键

`restore_foreground()` 用的是 `AttachThreadInput` + `SetForegroundWindow`,
**不碰键盘输入队列**。绝不能用 `keybd_event` / `SendInput` 之类
"模拟 Alt 键"的老套路 —— 那会往用户的输入流里插东西,
正在打字的人会打出多余的 Alt 组合。

### 透明窗口的三层背景

透明窗口的背景必须**三层都透明**,少一层就露出一块不透明的底色:

| 层 | 谁负责 |
|---|---|
| 窗口本身 | pywebview `transparent=True` → WebView2 的 `DefaultBackgroundColor = Transparent` |
| `html` | CSS `background: transparent`(根元素的背景会传播到 canvas,不能省) |
| `body` | CSS `background: transparent` |

另外:透明窗口上**不能有 `box-shadow` / `backdrop-filter`** —— 它们会
渲染成一条灰带。层次只靠"深底 + 半透明白描边"表达。

### 布局

- 面板是可拖动、可隐藏的小窗,无系统边框,自绘标题栏
- 胶囊**不可拖拽、不记位置**,固定在**工作区底部居中**
  (用工作区而不是整块屏幕,否则会被任务栏挡住)
- **窗口本身是矩形**(WebView2 做不到真透明圆角),面板背景 = 卡片底色
- CSS 用变量统一管理配色;零外链(离线必须可用);字体全用系统栈

---

## 约束

- 不引入任何 npm 依赖、前端框架、CDN 外链
- 所有资源本地化,不能引用外部字体/图标服务
- 前端不得直接访问 Python 内部对象,只能走 `window.pywebview.api`
- Python 侧暴露给 JS 的桥接对象要**单独一个类**,只放方法。
  pywebview 的 `get_functions` 会遍历公开属性,遇到非 callable 的对象还会
  **递归**下去 —— 直接把面板对象交出去,控制器和配置的内部属性都会泄漏进网页。
- 面板和胶囊各有一个桥接类(`_PanelApi` / `_CapsuleApi`),
  私有引用一律用下划线开头(pywebview 会跳过)
