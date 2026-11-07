# 前后端桥接契约

前端与 Python 宿主之间的**唯一接口定义**。项目现在有**两个**前端:

| 窗口 | 前端文件 | Python 宿主 | 职责 |
|---|---|---|---|
| 面板 | `static/index.html` + `panel.css` + `panel.js` | `panel.py` | 配置窗口:状态、模型切换、主题切换、退出 |
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
| `__qwen.setTheme(mode)` | `mode` ∈ `"auto"` \| `"light"` \| `"dark"` | 当前主题档位,启动时推一次;换档时再推 |
| `__qwen.setCapsulePosition(mode)` | `mode` ∈ `"follow"` \| `"bottom"` \| `"custom"` | 当前胶囊位置档位,启动时推一次;换档时再推 |
| `__qwen.setPreviewEnabled(enabled)` | `enabled` boolean | 预览文字条开关,启动时推一次;换档时再推 |
| `__qwen.setOrbEnabled(enabled)` | `enabled` boolean | 常驻悬浮球开关,启动时推一次;换档时再推 |
| `__qwen.setOrbPosition(mode)` | `mode` ∈ `"follow"` \| `"pinned"` \| `"custom"` | 悬浮球的位置档位,启动时推一次;换档时再推 |
| `__qwen.setBackend(kind)` | `kind` ∈ `"local"` \| `"dashscope"` | 当前识别后端(本地模型 / 网络 API),启动时推一次;切换后端成功后推 |
| `__qwen.setNotice(text)` | `text` string | 面板上的操作提示(切换后端的进展 / 拒绝 / 失败)。空串表示清空 |

> 面板瘦身后**不再**接收 `setLevels` / `setElapsed` / `setStatus` /
> `setResult` / `setPreview` —— 这些全部归胶囊窗口。
> 推送层(面板的 `_EventPump`)按窗口过滤:面板只推上面这几个,
> 胶囊推全套。

前端在 DOM 就绪后调用 `window.pywebview.api.ready()`;Python 收到 `ready()`
后立即推一次**全量状态**(state / model / hotkey / theme / backend)。

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
| `set_theme(mode)` | None | 切换主题档位,`mode` ∈ `"auto"` / `"light"` / `"dark"` |
| `set_capsule_position(mode)` | None | 切换胶囊位置档位,`mode` ∈ `"follow"` / `"bottom"` / `"custom"` |
| `set_preview(enabled)` | None | 开关录音时的实时预览文字条 |
| `set_orb(enabled)` | None | 开关常驻悬浮球(`true` = 屏幕上常驻一颗球) |
| `set_orb_position(mode)` | None | 切换悬浮球的位置档位,`mode` ∈ `"follow"` / `"pinned"` / `"custom"` |
| `switch_backend(kind)` | None | 切换识别后端,`kind` ∈ `"local"` / `"dashscope"` |

### 识别后端(本地 / 网络)

面板上多一行「后端」分段控件:本地模型 ↔ 阿里云百炼网络接口。
**两者互斥** —— 同一时刻只留一个后端。

| 环节 | 谁做 | 说明 |
|---|---|---|
| 点后端段 | 前端 → Python | `window.pywebview.api.switch_backend(kind)` |
| 切换中 | Python | 后台线程做(不卡 UI);先加载新后端,成功后才卸旧后端 |
| 切换结果 | Python → 前端 | 成功推 `setBackend(kind)`;进展/拒绝/失败推 `setNotice(text)` |
| 拒绝切换 | Python | 录音中 / 识别中切换会被**拒绝**,推一条 `setNotice` 说明原因 |

- **API Key 不进配置文件** —— 只从环境变量 `DASHSCOPE_API_KEY` 读。
  缺 key 时切换会失败并明确报错,**不静默回落到本地**
  (静默回落会让用户以为在用网络)。
- 网络后端**不支持实时预览**(兼容端点是整段请求,做不了逐句流式)。
  面板上「模型」和「预览」两行在网络模式下置灰,并有一行说明文字 ——
  **行为差异必须写出来**,不能静默失效。

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
| `__capsule.setError(message, payload)` | `message` string,`payload` string | 出错:文字条转暗红底 + 红字。`payload` 是**识别原文**(只有注入失败时才有);有原文时文字条显示的是原文,胶囊上会多一个「复制」按钮 |
| `__capsule.setCancelled()` | 无 | 用户主动取消:文字条显示「已取消」 |
| `__capsule.resetForRecording()` | 无 | 新一轮录音开始:清掉上一轮的文案与配色 |
| `__capsule.setTheme(mode)` | `mode` ∈ `"auto"` \| `"light"` \| `"dark"` | 主题档位;面板改档位时推,胶囊每次显示前也会补推一次 |
| `__capsule.setPositionMode(mode)` | `mode` ∈ `"follow"` \| `"bottom"` \| `"custom"` | 位置档位;面板改档位时推,胶囊每次显示前也会补推一次 |
| `__capsule.setPreviewEnabled(enabled)` | `enabled` boolean | 预览文字条开关;面板改开关时推,胶囊每次显示前也会补推一次 |
| `__capsule.setOrbMode(enabled)` | `enabled` boolean | **常驻悬浮球模式**开关;面板改开关时推,胶囊前端就绪时也补推一次 |
| `__capsule.setOrbPosition(mode)` | `mode` ∈ `"follow"` \| `"pinned"` \| `"custom"` | 悬浮球的位置档位。pinned/custom **可拖**(位置归用户管,共用一份持久化位置);follow 不可拖(位置由光标决定),前端据此不响应拖动、也不给 `grab` 光标 |
| `__capsule.setHideDelay(ms)` | `ms` number | 本轮结束后停多久再收起(毫秒)。常驻球模式下**前端自己**收回胶囊,所以 Python 把时长推给它,而不是等隐藏定时器响了再回头调一次前端(那是定时器线程去 evaluate_js,会破坏推送线程的串行顺序) |
| `__capsule.setOrbHint(text)` | `text` string | 球的一句话操作反馈(比如点球时模型还在加载,推"模型加载中…"):球上方冒出、约 2 秒后自己淡掉。空串表示立即收掉。`starting` 状态下球会转圈(`body.orb-loading`),提示气泡是它的文字补充 |

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

- 录音过程中,后端把**录音增量**喂给预览位的引擎,引擎吐出的文字
  推给前端的 `setPreview` 是**整体替换**的
  (默认是分句预览:按静音断句、逐句识别,拼好的文本整体替换;
  前端不需要做任何拼接或去重)
- `setPreview("")` 表示清空(录音开始时、以及最终结果到来时)
- `setResult(text)` 是停止录音后的最终文本,文字条定格在它上面。
  它**永远来自整段识别**,与预览层怎么断句无关
- 两者**不同时显示**

### 显隐时机

胶囊的显隐**由 Python 侧管**,前端只管画:

| 时刻 | 胶囊 | 停留 |
|---|---|---|
| 进入 `recording` | 立刻显示(**不抢焦点**) | 直到出结果 |
| `result`(已注入) | 文字条定格结果,胶囊显示「已输入 ✓」 | 1.5s |
| `error`(不带原文:录音太短/没检测到声音/识别失败) | 文字条暗红底 + 浅红字 | 4.5s |
| `error`(带原文 = 注入失败) | 文字条显示**识别原文**,胶囊上出现「复制」按钮 | 12s |
| `cancelled`(用户点 ✕) | 文字条显示「已取消」 | 1s |

新一轮录音开始时,上一轮的隐藏定时器必须作废(否则刚开始的胶囊会被
旧的定时器藏掉)。

常驻悬浮球模式下多一条:**窗口不藏**(球一直杵在那儿),胶囊由**前端**
在终态停留够时间后自己收回球 —— Python 把时长用 `setHideDelay` 推过去,
不在隐藏定时器线程里回头调前端(会破坏推送线程的串行顺序)。

### 注入失败时的回退(不许让刚说出口的话消失)

注入失败有三条路径,每一条都得有"这段文字改去哪"的答案:

| 失败点 | 文字去哪 | 界面出口 |
|---|---|---|
| Ctrl+V 没送出去(常见于 UIPI:目标程序以管理员运行) | **留在剪贴板里**(不归还原内容) | 胶囊的「复制」按钮 |
| 用户正按着 Ctrl/Alt/Shift | 压根没写进剪贴板,只存在内存里 | 胶囊的「复制」按钮 |
| 剪贴板被别的程序占着,写不进去 | 哪都没去 | 胶囊的「复制」按钮 |

所以胶囊的错误态分两种:

- **不带原文**:普通的错(没听清、识别失败)—— 只显示文案,4.5 秒后收起
- **带原文**:注入失败 —— 文字条显示**识别原文**,胶囊上多一个「复制」按钮,
  12 秒后收起(得给用户留出点它的工夫)

「复制」按钮点下去之后:Python 写剪贴板 → **读回校验** → 校验过了就把胶囊
收掉;没校验过就留在原地,让用户再点一次。

**核心原则**:失败分支的语义是"这段文字改去别处",不是"这段文字没了"。
谎报成功比失败更糟 —— 用户以为文字已经进了输入框,就不会再去别处找。

### 胶囊的位置三档

配置项 `capsule_position`:`follow`(默认)/ `bottom` / `custom`。
面板上是一行三段控件,改完即生效并持久化。

| 档位 | 行为 |
|---|---|
| `follow` | 按热键那一刻捕获一次定位信号,胶囊贴在信号点**下方**(像输入法候选框);下方空间不够就翻到上方;最后一定夹回工作区 |
| `bottom` | 工作区底部居中(原来的行为) |
| `custom` | 用户拖到哪就记在哪,下次录音从记忆位置出现 |

> 常驻悬浮球模式下这三档**不生效** —— 位置归悬浮球,胶囊在球的位置
> 原地展开(理由见「常驻悬浮球」一节)。

#### 定位信号的四级回退

"光标在哪"没有单一可靠的 API,按**越准越优先**往下退:

| 级别 | 手段 | 覆盖 | 需要 |
|---|---|---|---|
| 1 | UIA `TextPattern` 选区反推 | Chrome / VSCode / Office / Electron 等现代应用 | `comtypes`(可选依赖 `pip install -e ".[uia]"`) |
| 2 | `GetGUIThreadInfo` 的 `rcCaret` | 记事本等传统 Edit 控件 | 无 |
| 3 | `GetCursorPos` | 谁都能用,但指的是**鼠标** | 无 |
| 4 | 前台窗口矩形 | 兜底:只知道窗口在哪 | 无 |

四级都拿不到就退回"主屏底部居中"。

> UIA 那级需要可选依赖。没装就自动降到第 2 级 —— 功能少一档,
> 不该让整个跟随模式不可用。

两个真机踩过的坑(都在 `win32/caret.py` 里处理了):

1. **UIA 上不能直接用 `GetCaretRange`** —— 那个方法只在
   `IUIAutomationTextPattern2` 上,而实测绝大多数应用(包括 Electron
   的编辑器)只实现 v1,QI 到 v2 直接 `E_NOINTERFACE`。v1 上拿光标
   只能借"选区":插入点时 `GetSelection()` 返回一个**空**选区
   (自己没有矩形),把它**扩到所在的字符**
   (`ExpandToEnclosingUnit(TextUnit_Character)`)就有了 —— 那个字符
   矩形的左下角就是光标位置。
2. **`rcCaret` 的坐标基准不是屏幕坐标**。MSDN 写的是屏幕坐标,
   实测是**焦点控件客户区的相对坐标**(窗体在屏幕 (200,200)、
   焦点控件在 (208,231) 时,`rcCaret` 给的是 (0,0))。不换算的话
   胶囊会跑到屏幕左上角 —— 这里用 `ClientToScreen` 换回屏幕坐标。

#### 捕获时机与稳定性

- 捕获发生在**胶囊显示之前**(胶囊的 `RECORDING` 分支里),要的是
  "按热键那一刻"的光标位置,不是"画完再问"的稍晚一点的位置
- **同一轮录音内位置不再动** —— 位置可预期比位置聪明更重要
- 位置计算是纯逻辑,全在 `ui/placement.py`(可单测);
  Win32 调用在 `win32/caret.py`(找光标)与 `win32/monitors.py`(找屏)

#### 自定义档的位置记忆

存的是**相对所在工作区左上角的逻辑像素偏移**(`"dx,dy"`),不是绝对
屏幕坐标:

- 存逻辑偏移 → 换缩放率,胶囊相对工作区的位置不变
- 存相对工作区 → 换显示器,副屏的物理原点不会污染坐标
- 另存**显示器设备名**(`\\.\DISPLAY1`):还原时先找那块屏,
  设备名对不上就退回当前屏

#### 拖动胶囊

- 只有「自定义」档允许拖(其它档拖了也没意义:跟随档下一轮还是回到
  光标那里,固定底部档会被下次录音直接掰回去)
- 把手是**胶囊本体(非按钮区)**,点在 ✕ / ✓ 上不触发拖动
- 发的是**绝对目标位置**,不是增量 —— 和面板同一套机制:
  `目标 = mousedown 时的 window.screenX/Y + (当前 screenX/Y − 起点)`
  增量在 IPC 慢的时候会漂,绝对目标天然不漂
- 拖动期间 Python 侧的焦点守卫**不抢回焦点**(焦点来回跳会拖垮
  WebView2 的鼠标捕获),松手时统一归还一次
- `SetWindowRgn` 的区域是**窗口坐标系**,随窗口移动,不用跟着更新

### 预览显隐开关

配置项 `show_preview`(默认开)。面板上是一个开关行。

| 档位 | 行为 |
|---|---|
| 开 | 现状:录音时预览文字条自动展开,停止后定格最终结果 1.5s |
| 关 | 预览条**彻底不出现** —— 胶囊只剩波形+计时+按钮,不挡屏幕 |

关掉之后:

- Python 侧**一个字都不往前端送**(省掉每句都来一次 IPC)
- 「已输入」「已取消」等提示走胶囊内原有的 label 位
- 错误提示也进 label 位,截断一行即可(`.bar-label` 自带 ellipsis)
- 前端给 `<body>` 加 `no-preview` 类,`.textbar` 整块 `display:none`
  —— 窗口命中区也会跟着少一块(文字条不在并集里)

## JS → Python

| 方法 | 返回 | 说明 |
|---|---|---|
| `ready()` | None | 前端已就绪 |
| `cancel_recording()` | None | 放弃这一轮:**不识别、不注入** |
| `stop_recording()` | None | 结束录音,进入识别(等同再按一次热键) |
| `start_recording()` | None | **点悬浮球开始录音**。Python 侧会先把前台还给用户原先所在的窗口,再调 `controller.start_recording()` |
| `update_regions(payload)` | None | 上报可见内容的矩形,Python 侧据此裁窗口命中区(见「窗口区域裁形」) |
| `move_capsule(x, y)` | None | 拖动胶囊:窗口左上角移到**绝对位置** (x, y),CSS(逻辑)像素 |
| `move_orb(x, y)` | None | 拖动悬浮球:窗口左上角移到**绝对位置** (x, y),CSS(逻辑)像素(球的位置会被夹回允许的范围) |
| `begin_drag()` | None | 用户开始拖胶囊/悬浮球(Python 侧拖动期间不抢回焦点) |
| `end_drag()` | None | 用户松手(Python 侧归还焦点 + 记住位置) |
| `copy_result(text)` | bool | 把识别结果**手动**写进剪贴板(注入失败时的兜底出口)。写完要**读回校验**:读回来不是同一份就算失败。成功就把胶囊收掉 —— 用户拿到东西了,胶囊继续杵着就是干扰 |
| `open_panel()` | None | 球菜单里的「设置」:打开设置面板。面板引用由入口注入(`set_panel_opener`),胶囊只认回调;没注入就忽略 |
| `quit_app()` | None | 球菜单里的「退出」:退出整个程序。走入口注入的回调(`set_quit_handler`),和托盘「退出」同一条清理路径(热键注销 → 窗口销毁 → 主循环返回),不是简单 kill |
| `selecting_text(selecting)` | None | 前端上报"用户正在/结束选文本"。选区非空期间守卫**不抢前台** —— 抢了 WebView2 就失去键盘焦点,用户按 Ctrl+C 复制不了(框选了却复制不了,实测踩过) |

---

## 常驻悬浮球(胶囊的另一种形态)

配置项 `floating_orb`(默认**关**)。开着时,屏幕上常驻一颗小球:
点它 → 球**原地**展开成现在的录音胶囊 → 开始录音;录完胶囊收回成球,
继续常驻等下一轮。拖球可以挪位置,松手记住。

**它不是另一个窗口,也不是另一个元素** —— 是**同一个固定尺寸窗口**里、
**同一颗胶囊**的另一种形态(见「透明窗口不能 resize」:窗口一旦被 resize,
透明就废了)。

那颗球**就是这颗胶囊收窄成的正圆**(40×40,和胶囊的高度一样大):
- 前端只有一颗元素(`.recording-pill`),待机时它收窄成 40px 的圆,
  展开时横向长回胶囊的宽度;
- 所以"球在哪"= "窗口摆哪":球固定待在窗口的**底边居中**处,胶囊也在
  底边居中,两者横坐标一致 —— 展开时球"长成"胶囊,位置不用挪;
- **球和胶囊必须一样大**:不等大的话,球长开时走的轨迹和胶囊对不上,
  窗口的命中区(按上报矩形裁)会切到球上 —— 真机症状是"球的上半截
  多一条白线",像被切出一个球冠。`placement.ORB_SIZE` 和 CSS 里的
  `height` / `max-width` 由一条测试盯着。

| 环节 | 谁做 | 说明 |
|---|---|---|
| 显示球 | Python `capsule.py` | 前端就绪后**等 0.3s** 再露脸:窗口是固定尺寸的透明窗,必须等前端把"球"那一小块报上来、Python 裁好命中区之后才能露脸 |
| 命中区 | 前端 → Python | 待机时只上报 `pill` 一块(球就是收窄的胶囊,Python 侧按**椭圆**裁);展开后上报 `textbar` + `pill` 两块(和原来一样) |
| 点击/拖动 | 前端 | pinned/custom 档:按下后位移超过 **5 个逻辑像素**才算拖动,否则算点击。跟随档**没有拖动**(球拖不动),但仍要分清"点了一下"和"按着划走" —— 后者不该触发录音 |
| 点球开始录音 | 前端 → Python → 控制器 | **先把前台还给用户**,再 `controller.start_recording()`;模型还在加载(`STARTING`)时控制器会拒绝,Python 侧随即推 `setOrbHint("模型加载中…")` —— "点了没反应"在用户眼里就是"球坏了",必须给一句提示 |
| 右键菜单 | 前端 → Python | 球形态下右键弹通用功能菜单(展示框 + 一排小圆圈,以后加新功能都往这里放)。现有两项:「设置」→ `open_panel()`、「退出」→ `quit_app()`。菜单显示时命中区要把菜单**单独报一块**(`overlay`),不能并进球的包围盒(并进去就是一大片灰底,菜单还会被裁断) |
| 模型加载态 | Python → 前端 | `setState('starting')` 时球上显示转圈(麦克风让位给 loader),就绪后回 `setState('idle')` 自动恢复麦克风 |
| 拖球挪位置 | 前端 → Python | 发**绝对目标位置**(和面板/胶囊同一套机制),球的位置被夹回允许的范围 |
| 记住位置 | Python | 存**球**的偏移(相对所在工作区的逻辑像素)+ 显示器设备名 |
| 收回成球 | 前端 | 终态(`result`/`error`/`cancelled`)后停 `setHideDelay` 推来的时长,再收回球 |

### 位置档位(球自己的三档,和胶囊的「位置」是两套独立设计)

配置项 `orb_position`,取值三档(设计由用户拍板):

| 档位 | 露脸时机 | 位置 | 可拖 |
|---|---|---|---|
| `follow`(默认) | 光标聚焦在输入处 | 光标旁边(像输入法候选框) | 否 |
| `pinned` | 光标聚焦在输入处(和 follow 同一套一致性) | 记住的位置(持久化) | **是** |
| `custom` | **常驻** | 记住的位置(持久化) | **是** |

pinned 和 custom **共用同一份** `orb_geometry`:位置都是用户拖出来
记住的,两档的差别只在显隐。球的档里**没有 bottom** —— 和胶囊的
`capsule_position`(follow/bottom/custom)档位名不同、含义不同,
两边各自演化,不许互相参照。

**怎么判断"光标聚焦在输入处"**:`win32/caret.py` 的 `capture_anchor()`
本来是四级回退 —— UIA 真光标 → 传统控件真光标 → **鼠标位置** → 前台窗口
中心。后两级说的都不是光标,拿它们冒充的话,用户只是把鼠标挪过来球就冒出来。

所以加了 `capture_anchor(require_caret=True)`:UIA / 传统光标之外还有
`edit` 级兜底 —— 焦点在可编辑输入框里但一个字符都没有(空输入框,
UIA 扩不到字符矩形)时,退化成输入框的左下角。判定标准是
`anchor.kind in ("uia", "caret", "edit")`,拿不到就收球。

**切档竞态(修过的坑)**:follow/pinned 档靠轮询线程(250ms)维持显隐,
线程里 UIA 捕获一笔就是几十到几百毫秒 —— 切到 custom 的瞬间,旧线程可能
正攥着一个刚捕获的锚点,醒来把球又挪回光标处(或把球藏掉),盖掉新档位
的摆位,看起来就是"切了档没反应"。修法是**线程代数(epoch)**:起/停线程
都让计数 +1,线程在动窗口前发现代数变了就立刻收手;切档侧先
`_stop_follow_thread()`(join 到线程退出)再摆位。

**只有 custom 档可拖**:follow 的位置由光标决定、bottom 固定在底部,
拖了也会被拉回去 —— 前端靠 `setOrbPosition` 知道当前档位
(不给 `grab` 光标、不挂 mousemove)。但**点击仍然开始录音** ——
球就是录音入口,这个不能因为"不能拖"就一起没了。

### 位置规则

- 自定义档:球默认在**工作区右下角**;拖过之后记住上次的位置
  (`orb_geometry` + `orb_monitor`,和胶囊自定义档一个路子,但**两套互不干扰**)
- **球的上方要留出展开后胶囊的高度**:窗口是固定尺寸的,球拖得太靠上,
  展开后文字条会出屏 —— 所以纵向的夹取比横向严
- 换缩放率、换显示器都不飞(存的是相对工作区的逻辑偏移)

### 与「胶囊的位置三档」的关系

**常驻模式下 `capsule_position` 三档不生效**:胶囊的位置归**悬浮球**,
胶囊在球的位置**原地**展开。球自己的档位由 `orb_position` 决定
(跟随光标 / 用户拖的位置) —— 球在哪儿由球说了算,胶囊跟着球走。

非常驻模式(开关关掉)下,胶囊的三档行为一个字都没变。

### 点击球开始录音时的焦点陷阱

鼠标点在 WebView2 的子窗口上会激活整棵窗口树,前台被胶囊抢走。
注入靠"粘贴到前台窗口",前台是胶囊的话,**这一轮的文字会贴到胶囊
自己身上**,而不是用户的输入框 —— 功能级的致命 bug。

所以点球的路径是:**先归还前台,再开始录音**。用户原先所在的窗口由
守卫线程动态跟踪(`_last_foreground`),不靠猜。

---

## 状态与视觉规格

参考对象是 OpenType(https://www.opentype.top/)的观感,但**不能照抄它的品牌名、
logo、文案、配色**。只学交互与视觉语言。

### 波形条

胶囊里的波形是**滚动电平条**,规格对齐 MiniMax Code 的
`asr-volume-bars`:13 根定宽小条,从右往左滚。

> 规格来源是 **MiniMax Code 的 `asr-volume-bars`**,不是 OpenType。
> 用户最初就定了"音量条保持 MiniMax Code 规格,其他随便学 OpenType"——
> 波形区曾被"移植 OpenType"的指令波及(变成 9 根固定对称包络),
> 现在修回滚动条。旧面板时代就是这么做的(见 `a1a3ea2` 的 `panel.js`)。

| 项 | 值 |
|---|---|
| 条数 | **13 根**(MCode 原版;面板那 22 根是因为面板更宽,胶囊窄) |
| 条宽 / 间距 | 3px / 3px(步进 6px),可视宽度 `13×3 + 12×3 = 75px` |
| 推进周期 | `BAR_TICK_MS = 140ms`(每 140ms 出一根新条) |
| 滚动方向 | **从右往左**:新条从右边进,旧的往左滑,滑出视口被裁掉 |
| 池大小 | 14 根(13 根可见 + 1 根屏外的回收位,复用同一个 DOM 节点当新条) |

**三槽轮转**:每次算出三个高度(过渡 / 当前 / 衰减)轮流用,相邻条因此
天然错开,不会是一排单调的长短 —— "流动感"的来源。电平恒定时三根一组
地重复:衰减 < 过渡 < 当前。

**过渡插值**:过渡槽的高度 = `上一根 + 65% × (当前 − 上一根)`,缓升缓降,
不是硬跳。高度过渡 `75ms`、位移过渡 `140ms`。

**静音判定在后端,前端死区只防抖**:Python 侧用噪声底估计(滑动窗口 p10,门 = 估计 × 1.5)把底噪映射成 0 再推送 —— 门限随环境走,不是按某次录音标定的固定值(固定门被换环境后的底噪穿透过)。前端 `DEAD_ZONE = 0.05` 只挡门限边缘的过渡帧,并保住映射曲线最低端的灵敏度。

**高度映射**(分段非线性,"对比度明显"的来源):

| 绝对电平 | 条高 |
|---|---|
| `0 ~ DEAD_ZONE` | 固定 2px(静音),画灰色(`--wave-dim`) |
| `DEAD_ZONE ~ 0.5` | 4 → 12px(最灵敏) |
| `0.5 ~ 0.75` | 12 → 18px |
| `0.75 ~ 1` | 18 → 20px(压缩) |

条高是**绝对电平**(Python 侧 `dbfs_to_unit` 的 dBFS 对数映射值),
前端**绝不做窗口内归一化** —— 归一化之后条子永远看起来是满的,
"我说话声音大不大"这个信息就没了。可以平滑(上升 0.7 / 下降 0.5,
再叠一层自适应增益),但**不能凭空加抖动或随机数** —— 那是在伪造数据。

非录音阶段(识别中 / 完成 / 出错 / 取消)胶囊里换状态文字,条子停住不跑。

### 主题(三档:跟随系统 / 白天 / 黑夜)

面板和胶囊**共用同一套主题机制**,面板上有一行"主题"分段控件
(自动 / 白天 / 黑夜),改哪边两边都跟着换。

| 档位 | 含义 | 落地方式 |
|---|---|---|
| `auto` | 跟随系统(Windows 的"应用模式") | 前端**不设** `data-theme`,CSS 的 `prefers-color-scheme` 媒体查询自然接管 |
| `light` | 强制白天 | `<html data-theme="light">` |
| `dark` | 强制黑夜 | `<html data-theme="dark">` |

**为什么 auto 时不设属性**:设了属性就得靠 JS 监听系统主题变化
(`matchMedia` 的 change 事件)才能跟随 —— 而 WebView2 的
`prefers-color-scheme` 本来就直接跟着 Windows 走,媒体查询一条就够。
不设属性 = 少一个监听、少一处状态。

CSS 侧三段(`panel.css` / `capsule.css` 各一份,结构一样):

```css
:root { /* 浅色 token */ }
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) { /* 深色 token */ }
}
:root[data-theme="dark"] { /* 深色 token */ }
```

`[data-theme="light"]` 的那个 `:not` 是关键:强制白天时,系统的深色
媒体查询照样会命中,不加 `:not` 就会被系统深色压回去。

> ⚠️ 胶囊(`capsule.css`)**不设** `color-scheme`:它是透明窗口,
> html/body 背景都是 transparent,声明了 `color-scheme` 可能让浏览器
> 给 canvas 刷一层不透明底色,透明就废了。

### 主题的推送链路

```
面板前端点主题段
   → window.pywebview.api.set_theme(mode)
   → panel.py  _set_theme():存 config.json(config.theme)
                              → 推自己的前端 __qwen.setTheme(mode)
                              → 往 _EventPump 投一个 kind="theme" 事件
   → 胶囊 handle_event(kind="theme") → __capsule.setTheme(mode)
```

- 面板自己**不**走推送线程:它就是当前窗口,直接 `evaluate_js` 即可
- 胶囊**必须**走推送线程:WebView2 的调用要串行,在推送线程之外去
  `evaluate_js` 同一个 WebView2,顺序会乱
- 胶囊**每次录音开始前**(即 `resetForRecording` 之前)会再补推一次
  当前主题 —— 面板改主题是异步的,用户改完马上按热键时
  `setTheme` 可能还堵在队列里,补推一次配色才不会错
- 胶囊隐藏时也能推:`evaluate_js` 对隐藏窗口照样执行
- 建窗底色(`background_color`)按档位给:`light`/`dark` 用对应色,
  `auto` 读系统 —— 免得页面加载前闪一下别的底色
  (胶囊是透明窗,`background_color` 本来就无效,不用管)

### 胶囊窗口的视觉规格

- 透明窗口,可见的只有两块:上方**文字条**、下方**胶囊**,中间留透明间隙
  (常驻悬浮球模式下,可见的只有**球**那一小块)
- **窗口尺寸固定**(512×260 逻辑像素,请求值会被 WinForms AutoScale
  缩水,建窗时放大补偿),**不随内容高度变化**。
  原因见下方「透明窗口不能 resize」—— 一旦 resize,WebView2 的透明
  背景就失效,窗口外的区域会变成 WinForms 的灰底。
  内容用 CSS 贴着窗口底部排(`.stack` 的 `justify-content: flex-end`),
  文字条到 `max-height` 就自己内部滚动
- **透明窗口上不能有 `box-shadow` / `backdrop-filter`** —— 它们会渲染成
  一条灰带。层次只靠"底色 + 半透明描边"表达
- 文字条:圆角 14px、内边距 10~14px、字号 14px、行高 1.8、
  `white-space: pre-wrap` + `overflow-wrap: anywhere`、
  最大宽 480px / 最大高 120px(超出内部滚动)
- 胶囊:高 40px、圆角 999px、同款底色与描边,内容是
  `[✕ 取消(30×30 圆钮)] [波形区 + 计时] [✓ 结束(30×30 圆钮,
  底色与波形同色 #ff5a5a)]`
- 配色**全部走 `:root` 的 token**,深浅两套只换值不改结构:

  | token | 深色 | 浅色 |
  |---|---|---|
  | `--deep`(文字条 + 胶囊底色) | `#0b0b0c` | `#fff` |
  | `--fg`(字色) | `#f4f4f5` | `#111113` |
  | `--stroke`(描边) | `rgba(255,255,255,0.2)` | `rgba(17,17,19,0.12)` |
  | `--cancel-btn` | `#3a3a3e` | `#ececee` |
  | `--err-bg` / `--err-stroke` / `--err-fg` | `#2a0f11` / `rgba(255,107,107,0.3)` / `#fecaca` | `#fef2f2` / `rgba(229,72,77,0.35)` / `#b91c1c` |
  | `--wave`(录音红) | `#ff5a5a` | `#ff5a5a`(两套一样 —— 红是语义色,不是主题色) |
  | `--wave-dim`(波形里的静音条) | `rgba(244,244,245,0.28)` | `rgba(17,17,19,0.22)` |

  浅色胶囊是本项目自定的(OpenType 只有深色胶囊):白卡 + 深字,
  描边比深色那套更深 —— 浅底上 `rgba(255,255,255,.2)` 那种白描边
  根本看不见
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
| 量矩形 | 前端 `capsule.js` | `offsetLeft/Top/Width/Height`(布局尺寸,**不受 transform 影响**),**CSS 逻辑像素**、相对视口 |
| 上报 | 前端 → Python | `window.pywebview.api.update_regions(payload)`,60ms 合并一次;球↔胶囊形变期间**每 50ms 报一次**(宽度一直在变) |
| 换算 | Python `capsule.py` | 逻辑 × DPI 缩放 = 物理像素,每边再外扩 8 物理像素(椭圆/浮层用 2) |
| 裁窗口 | `win32/api.py` | `CreateRectRgn` / `CreateEllipticRgn` ×N → `CombineRgn(RGN_OR)` → `SetWindowRgn` |

payload 格式:

```json
{"textbar": [x, y, w, h], "pill": [x, y, w, h], "overlay": [x, y, w, h]}
```

全 0 表示那块隐藏(不参与并集);全隐藏时裁成 1×1 的最小区域。

常驻悬浮球模式下,待机时只有 `pill` 那一块非零 —— 那颗球**就是**收窄到
40×40 的胶囊;展开成胶囊后回到 `textbar` + `pill` 两块。
`overlay` 是球的浮层(右键菜单 / 操作提示),**只在它可见时非零**。

几个坑:

- **裁形必须贴合可见内容,否则会露出 Form 灰底**:pywebview 的
  transparent 分支**不设 `Form.BackColor`**,默认就是 WinForms 灰
  `#F0F0F0` —— WebView2 透明的地方透出的是这块灰,不是桌面。裁出来的
  形状比不透明内容大一点,就露一圈灰(用户看到的"球外面包着一块灰色
  矩形")。所以:
  - 球是**圆形** → 用**椭圆**裁(`CreateEllipticRgn`),矩形裁必然露四个角;
  - 浮层**单独一块**上报,**不能并进 `pill` 的包围盒** —— 并进去就是
    "球到菜单"的大矩形,中间全空白,整块都是灰(实测:球上方罩着一大片
    灰色,菜单还被裁断)。
- **量矩形要用 `offset*`,不能用 `getBoundingClientRect()`**:后者会把
  CSS `transform` 一起算进去,量到的是"动画中"的矩形。按它裁窗口,
  描边两端就被切掉(实测:上下边线在两端断开)。现在球↔胶囊的形变
  不用 transform(宽度靠 `max-width` 过渡),但这条规矩留着当保险。
  → 推论:**定位也别用 `transform: translateX(-50%)`** —— 那样
  `offset*` 量到的位置会偏半个身位,裁出来就把东西切掉(菜单被截断
  就是这么来的)。用 `left/right + margin:auto` 居中。
- **形变期间要持续上报**:球↔胶囊是 0.3s 的宽度过渡,布局宽度每帧
  都在变。只在头尾各报一次的话,中间那 0.3s 里窗口形状停在"球"那一
  小块,胶囊多出来的那部分点不到(会穿透到底下的应用)。
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
渲染成一条灰带。层次只靠"底色 + 半透明描边"表达。

同理也**不能给胶囊的 `:root` 设 `color-scheme`** —— html/body 背景都是
transparent,canvas 跟着根元素走,声明了 `color-scheme` 可能让浏览器给
canvas 刷一层不透明底色,透明就废了。

### 布局

- 面板是可拖动、可隐藏的小窗,无系统边框,自绘标题栏
- 胶囊的位置有**三档**(见「胶囊的位置三档」):跟随输入光标 / 固定底部 /
  自定义拖动。只有「自定义」档允许拖动胶囊
- 摆放一律用**工作区**而不是整块屏幕,否则会被任务栏挡住
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
