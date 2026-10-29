# qwen3-voice

基于本地 [Qwen3-ASR](https://github.com/QwenLM/Qwen3-ASR) 的全局语音输入工具。

按一次全局热键开始说话,再按一次,识别出的文字直接粘贴到当前光标位置 —— 类似手机输入法,但在 Windows 上跑,音频完全不出本机。

---

> **许可证:AGPL-3.0-or-later**。录音胶囊的样式与波形算法移植自
> [OpenType](https://github.com/ct-jyjntc/opentype-public)(同为 AGPL-3.0),
> 见 [`LICENSE`](LICENSE)。

## 特点

- **录音胶囊悬浮窗**:按热键后屏幕底部弹出一颗胶囊 —— 上方是实时识别文字,
  下方是真实电平驱动的波形条和计时。识别完停留片刻自动隐藏,全程不抢焦点
- **真实电平反馈**:波形条由**真实音量数据**驱动,新条从右进旧条从左出,
  跟着声音流动。高度用非线性映射,小声也看得见;
  安静时就是安静的,不做假的抖动动画
- **随时可以反悔**:胶囊左边一颗 ✕,点了就丢弃这一轮 —— 不识别、不注入,
  一个字都不会贴出去
- **轻量配置面板**:面板只管状态、切换模型、切换主题、退出,默认不常驻(托盘打开),
  需要时再点出来
- **三档主题**:面板上一行「自动 / 白天 / 黑夜」,面板和胶囊**一起换** ——
  「自动」跟随 Windows 的应用模式,「白天」「黑夜」强制固定
- **全局热键**:`Ctrl+Alt+Space` 随处可用,系统级注册,不需要管理员权限
- **完全本地**:模型跑在本机 GPU,音频不出本机,断网可用
- **模型常驻**:0.6B / 1.7B 随时切换,切换后热键零等待
- **不吞剪贴板**:借用了剪贴板就一定还回去;但你在这期间复制了新内容,
  我们的恢复会主动让位,绝不覆盖你刚复制的东西
- **静音不误触**:误按热键不说话,只会提示「没有检测到声音」,不会给一段幻觉文字
- **焦点保护**:转写这 1 秒里你切了窗口,文字仍然贴回原来的窗口

---

## 安装

需要 Python 3.12+ 和一块 NVIDIA 显卡(8GB 显存足够)。
面板基于 WebView2 渲染,Windows 10/11 一般都自带这个运行时,一般不用额外装。

```powershell
# 1. 建环境
uv venv --python 3.12 .venv

# 2. 装依赖
uv pip install --python .venv\Scripts\python.exe -e .

# 3. 下载模型权重(国内走 ModelScope 更快)
modelscope download --model Qwen/Qwen3-ASR-0.6B --local_dir D:\Dev\Environments\models\Qwen3-ASR-0.6B
modelscope download --model Qwen/Qwen3-ASR-1.7B --local_dir D:\Dev\Environments\models\Qwen3-ASR-1.7B
```

模型权重路径写在 `config.example.json` 的 `model_root` 里。

---

## 使用

```powershell
python -m qwen3_voice
```

启动后:

1. 托盘图标出现在右下角(灰=待机,黄=加载中,红=录音中)
2. 按 **Ctrl+Alt+Space**,屏幕底部弹出一颗录音胶囊:
   上方是实时识别文字,下方是波形条和计时
3. 说话。再按一次热键(或点胶囊右边的 ✓)结束,文字自动出现在你原来
   光标所在的位置,胶囊定格一下「已输入」然后自动隐藏
4. 想放弃这一轮,点胶囊左边的 ✕ —— 不识别、不注入
5. 需要切模型、切主题或退出,点托盘图标打开面板(面板默认不常驻)

出错了(录音太短、没检测到声音、识别失败、注入失败)时,胶囊上的文字条
会变成红底并显示原因(深色主题下是暗红底,浅色主题下是浅红底),停 4.5 秒再自动隐藏。

> 面板位置会被记住;胶囊固定在屏幕底部居中,不能拖。

---

## 配置

配置和日志都在项目内的 `data/` 目录(portable,跟着项目走,见仓库根目录
`AGENTS.md`)。首次运行会自动创建。

`config.example.json` 是模板。改了之后复制到 `data/config.json` 即可,
或者直接改 `data/config.json`。

| 键 | 默认值 | 说明 |
|---|---|---|
| `hotkey` | `ctrl+alt+space` | 全局热键 |
| `model` | `0.6B` | `0.6B` 更快,`1.7B` 更准 |
| `model_root` | `D:\Dev\Environments\models` | 模型权重目录 |
| `input_device` | `null` | 固定麦克风编号;`null` = 自动挑选 |
| `device_name_hints` | USB 优先 | 优先匹配这些名称的麦克风 |
| `language` | `null` | `null` = 自动检测语种 |
| `hotwords` | `[]` | 术语表(热词),识别时作为上下文提示喂给模型,见下文 |
| `restore_clipboard` | `true` | 粘贴后恢复原剪贴板 |
| `trailing_space` | `true` | 末尾自动补空格 |
| `silence_rms` | `0.002` | 低于此电平视为静音 |
| `max_duration` | `120` | 单次最长录音秒数 |
| `panel_geometry` | `""` | 面板位置,自动保存(逻辑像素) |
| `panel_visible` | `false` | 面板启动时是否常驻(`true` = 启动就显示,默认靠托盘打开) |
| `theme` | `"auto"` | 主题档位:`auto` 跟随系统 / `light` 白天 / `dark` 黑夜(面板和胶囊一起换) |

环境变量:

- `QWEN3_VOICE_HOME` —— 换掉整个数据目录(便携使用)
- `QWEN3_VOICE_CONFIG` —— 指定配置文件路径
- `QWEN3_VOICE_LOG` —— 指定日志路径

### 术语表(热词)

Qwen3-ASR 内置一个叫 **Context Biasing** 的能力:可以传任意文本
(API 上限 10000 token)把转写**偏向**特定术语 —— 公司名、产品代号、
医疗术语、法律行话、领域黑话都靠它校准。官方原话是「当音频模糊时,
模型会优先选这些词」。

配置里填词、逗号分隔即可:

```json
"hotwords": ["杀鲸乾坤道", "终极真身", "迦勒底"]
```

会拼成 `Vocabulary: 杀鲸乾坤道, 终极真身, 迦勒底.` 送进模型的 system role
(这个格式是官方 HF 版的写法,模型在 SFT 阶段就是用它训练上下文偏置的,
自己编一句提示效果会打折)。

**这是识别阶段的偏置,不是识别完再改写** —— 所以不会凭空猜错别字,
也不会改掉原意,和你不想要的「LLM 润色」是两回事。

#### 效果(实测)

同一段音频,专有名词 `Qwen3-ASR`:

| context | 识别结果 |
|---|---|
| 空 | `请帮我查一下服务器上那份叫做"Queen三ASR"的模型权重文件。` ❌ |
| `Vocabulary: Qwen3-ASR, Qwen3, ASR.` | `请帮我查一下服务器上那份叫做 Qwen3-ASR 的模型权重文件。` ✅ |

所以术语表值得认真维护。

#### 术语表管理器(打分器工作流)

术语表最难的是「哪些词值得收录」—— 这个判断不该由代码或 agent 代劳,
所以提供的是**打分器**而不是筛选器:全量候选公开给你,你来定夺。

```bash
python scripts/hotwords_manager.py scan     # 聊天记录 → 全量候选(带分数)
python scripts/hotwords_manager.py build    # 候选 + 你的编辑 → 最终词表
python scripts/hotwords_manager.py show     # 查看最终词表
```

三个文件,一条流水线(全部在 `data/` 下):

| 文件 | 谁写 | 作用 |
|---|---|---|
| `hotwords_candidates.tsv` | scan 产出 | **全量候选,零筛选**:分数/频次/类型/示例上下文 |
| `hotwords_user.tsv` | **你** | 手动编辑,永不被覆盖:`=` 强制收录 / `!` 拉黑 / `+N` 加分 |
| `hotwords.txt` | build 产出 | 最终词表,**每次识别时实时读取**(mtime 缓存),改完立即生效 |

scan 的中文部分用 **jieba 分词**(需要 `pip install jieba`),不是 n-gram ——
n-gram 会产生"我还/不知/道怎"这类跨词碎片,分词以词为单位,
"固有/歧义/采样率"都是完整词。试过把 Qwen tokenizer 词表喂给 jieba
做增强,负收益(专业术语在 BPE 里本来就是拆开的,词表里全是通用组合),放弃。

只取 `role == "user"` 的正文(用户手打的内容才是术语的真实形态),
拉丁 token 按「紧邻汉字(+15)/cmdlet 形态(-10)/文件名(-20)」等规则打分;
分数只是排序建议,**不替你做决定**。

你的编辑示例(`data/hotwords_user.tsv`,Tab 分隔):

```
玉藻前	=	老师
固有	+40	常用表达
Monte Carlo	!	太通用
```

**词条要克制** —— 词越多偏置越强,但也越容易把没说的话写出来。
build 默认取分数前 120(`--top` 可调),强制收录不受限。
优先收录「读音和写法差别大」的词(Queen/Qwen、三ASR/ASR),
拼写本来就对得上的(`API`、`PDF`)帮助不大。

仓库里有编辑文件模板:`hotwords_user.example.tsv`。

---

## 架构

```
src/qwen3_voice/
├── __main__.py          入口,组装各层并管理线程
├── config.py            配置模型与持久化
├── paths.py             运行时路径(portable,见 AGENTS.md)
├── logging_setup.py     日志
├── core/
│   └── controller.py    状态机 + 编排(UI 和热键都只跟这层打交道)
├── audio/
│   ├── devices.py       设备枚举与自动选择(带可用性实测)
│   └── recorder.py      录音采集(原生采样率采集,重采样交给 qwen_asr)
├── asr/
│   └── engine.py        Qwen3-ASR 封装,支持热切换模型
├── win32/
│   ├── api.py           Win32 API 绑定(ctypes 签名集中声明)
│   ├── dpi.py           DPI 感知(高分屏必需)
│   ├── hotkey.py        全局热键注册与监听
│   ├── clipboard.py     剪贴板读写(+ 变更序号)
│   └── inject.py        文本注入(剪贴板 + Ctrl+V,带归属守卫)
└── ui/
    ├── panel.py         pywebview 宿主:建窗、推送线程(多窗口共用一条)、前端桥接
    ├── capsule.py       录音胶囊宿主:透明悬浮窗、不抢焦点、固定尺寸、显隐控制
    ├── static/
    │   ├── index.html   面板结构
    │   ├── panel.css    面板配色与布局(CSS 变量统一管理,浅/深两套)
    │   ├── panel.js     面板状态、模型切换、主题切换、拖动
    │   ├── capsule.html 胶囊结构(文字条 + 胶囊)
    │   ├── capsule.css  胶囊配色与布局(底色 + 半透明描边,浅/深两套)
    │   └── capsule.js   胶囊状态机、波形条绘制
    └── tray.py          托盘图标

scripts/
└── hotwords_manager.py 从聊天记录提取术语表候选(只读)
```

运行时数据都在项目内的 `data/`(portable,见 AGENTS.md):
`config.json`、`hotwords.txt`、`voice_input.log`、`webview/`。

前端是**纯静态 HTML/CSS/JS**,零 npm、零框架、零 CDN —— 项目定位是绿色软件,
`git clone` + `pip install -e .` 就能跑,不该有 node_modules。Python 与前端之间
是双向桥接,方法名两边严格对齐(契约见 `ui/static/` 旁的说明)。

**分层原则**:UI 层不直接碰热键,热键不直接碰 UI,都通过 `core.controller`
的订阅机制通信。想换 UI 实现或换触发方式,只动对应那一层。

**可测试性**:`VoiceController` 的文本注入和录音器都是可注入的工厂,
测试时用假实现 —— 测试绝不能真的改用户剪贴板或打开真实音频设备
(后者会让 pytest 退出时崩 0xC0000005)。

**线程分工**:

| 线程 | 职责 | 为什么 |
|---|---|---|
| 主线程 | `webview.start()` 的 GUI 循环 | pywebview 要求在主线程启动 |
| ui-push 线程 | 控制器事件 + 30fps 电平 → `evaluate_js`(面板和胶囊共用一条) | `evaluate_js` 是阻塞的,放主线程会卡住窗口;一条线程还能保证事件顺序 |
| hotkey 线程 | RegisterHotKey + GetMessage | Win32 消息队列是线程局部的 |
| tray 线程 | pystray 消息循环 | pystray 自带循环 |
| model / mic 线程 | 后台初始化 | 不阻塞启动 |
| asr 线程 | 识别与注入 | 不阻塞 UI |

---

## 踩过的坑

**ctypes 句柄截断**
`GlobalLock` 不声明 `restype` 时,ctypes 默认按 `c_int` 处理返回值,64 位句柄被截断,
写剪贴板会写到非法地址,进程直接崩(0xC0000005)。所有 Win32 签名在 `win32/api.py` 里显式声明。

**蓝牙耳机采不到数据**
EDIFIER W800BT Plus 这类蓝牙耳机,在 WASAPI 共享模式下会被 Steam / 网易云占住音频端点。
设备声称支持某采样率,实际打开后只拿到零星数据。所以 `audio/devices.py` 会**实测**每个设备
(录 2 秒看能采到多少),而不是只看名字。

**必须用设备原生采样率采集,但不要自己降采样**
按 16 kHz 直接打开蓝牙设备,WASAPI 会悄悄降速,2 秒只采到 0.38 秒,录音被截断。
所以按**设备原生率**采集(本机实测 48 kHz)。但降采样**不要自己做** ——
把 `(原生音频, 原生采样率)` 直接交给 `qwen_asr`,它内部用 librosa 做高质量重采样
(与官方评测路径完全一致)。

自己用 `np.interp` 线性插值降采样有两个问题:
1. 没有抗混叠低通滤波,8~24 kHz 的成分会折叠回低频,叠在人声基频上
   (实测失真能量占比 98.71%,其中 88% 落在 0~4 kHz 语音主频带)
2. 单条短句上差异不明显,但长句会明显退化

**int16 混音时别把整数当浮点缩放**
`sounddevice` 回调给的是 `(frames, channels)` 的 int16。`arr.mean(axis=1)` 会把
int16 提升成 float64,此时若再走「浮点音频 × 32768」那条分支,原始刻度
(比如 1000)会被当成 [-1,1] 浮点乘 32768 → 32768000 → 削波成 32767。
实测**46.5% 的样本变成满刻度**,音频被削成方波,模型看到的是严重失真的信号。
`audio/recorder.py:frames_to_int16` 里对 int16 单独走 `astype(np.int16)` 还原刻度。

**dtype 判断要在混音之前**
上面那个坑的通用形态 —— `mean` 会把 int16 提升成 float64,所以
**先记下原始 dtype,再混音,最后按原始刻度还原**。
`asr/engine.py:_to_float32` 里如果写成「先 mean 再判断是不是整数」,
判断就永远不成立,int16 会被当成已归一化的浮点,直接 clip 成 ±1(全幅方波)。

**传给 qwen_asr 的必须是浮点音频**
`qwen_asr` 内部用 librosa 重采样,而 librosa 只接受浮点 —— 传 int16 会报
`ParameterError: Audio data must be floating-point`。
所以 `asr/engine.py:_to_float32` 负责把 int16 转成 [-1,1] 的 float32。

**热键路径上不能做耗时操作**
早期版本在按热键时同步做 2 秒设备探测,导致用户按下的前 2 秒说的话全丢。
现在设备探测在后台预热,结果走缓存,热键路径零延迟。

**qwen_asr 的音频输入格式**
必须是 `(np.ndarray, sample_rate)` 元组或文件路径,传裸 ndarray 会报
`TypeError: Unsupported audio input type`。

**高分屏必须声明 DPI 感知**
本机 175% 缩放。声明之后,`GetDeviceCaps(GetDC(0), LOGPIXELSX)` 就**不可靠了** ——
进程是 Per-Monitor V2 感知时,这个值取决于调用线程的 DPI 上下文,不同线程拿到的
结果不一样(实测主线程 96、探测线程 168),拿它算位置会把窗口甩到屏幕外。
改用 `GetDpiForSystem()`(主显示器 DPI,与线程无关)和 `GetDpiForWindow(hwnd)`
(窗口所在显示器)。

**pywebview 给的初始窗口尺寸会被 WinForms 再缩一次**
请求 300x260 逻辑像素,实际客户区只有 286x224 —— 底部几个像素的内容被裁掉了。
根因是 WinForms 的 `AutoScaleMode.Dpi`。但 `window.resize()` 走的是另一条路径,
尺寸是准的(实测 `resize(300,320)` 得到的正好是 320 逻辑像素)。
所以面板的做法是:**显示后自己量一次 `document.body.scrollHeight`,再 resize 到刚好**,
而不是相信建窗时给的尺寸。

**canvas 的 flex-basis:auto 会被它自己的默认尺寸撑大**
`<canvas>` 没有 CSS 尺寸时,默认属性尺寸是 300x150。在 flex 容器里
`flex-basis: auto` 取的就是这个属性高度 —— 电平条卡片会被撑到 162px,
把结果卡片挤成 0 高度,文字直接看不见。
必须给 canvas 一个确定的 flex-basis(`flex: 1 1 44px`)。

**WebView2 的用户数据目录要显式指定**
WebView2 默认把 Cookies / 缓存 / LocalStorage 写到 `%LOCALAPPDATA%`,
那违反 portable 约定。而且只设 `WEBVIEW2_USER_DATA_FOLDER` 环境变量**不够** ——
pywebview 会在 `start()` 里显式设置 `UserDataFolder` 把它覆盖掉,
所以两个都要给:环境变量 + `webview.start(private_mode=False, storage_path=...)`。

**电平必须推绝对值**
电平条如果按「窗口内最大值」归一化,波形永远看起来是满的,
用户就没法从条的高低看出「我说话声音大不大」—— 那条电平条就只剩装饰作用了。
所以 Python 侧把 RMS 映射成 dBFS(下限 -70 dB,上限 0 dB)再推给前端,
前端不做任何窗口归一化。下限定在 -60 也不行:实测安静房间的底噪就有 -62 dBFS,
会被压成 0,电平条看着像死了。

**模型路径拼接别搞错**
`model_root / repo` 和 `model_root / model_tag` 是两个不同的路径。
写成前者会去找 `models\Qwen\Qwen3-ASR-0.6B`,找不到就退回联网下载,然后
在离线环境下报一长串 `MaxRetryError`。

**测试不能碰真实剪贴板和真实音频设备**
真设备的 PortAudio 清理会让 pytest 退出时崩 0xC0000005;改剪贴板会污染
用户正在用的剪贴板。用可注入的假实现隔离。

**电平条要加噪声门,否则安静时也像在响**
实测安静房间的底噪是 -62 dBFS,映射到 0..1 还有 0.11,超过前端 0.04 的死区 ——
结果电平条一直显示 7px 高度,看着像一直在响。所以映射前先过一道噪声门
(用 `config.silence_rms`),低于门限直接判 0。加了之后实测:
安静房间录 3 秒,映射后全是 0.000;有人声时均值 0.42、最高 0.71。

**静音时也要诚实地显示**
录音中但没声音(电平 ≤ 0.04)时,显示 2px 的灰条并继续滚动,而不是冻住或隐藏 ——
"在录但没声"和"没在录"是两回事。反过来,**待机(根本没开始录音)时就不该显示
条形**,改成一行状态文字;显示会动的假动画是在骗人。

**胶囊显示绝不能抢焦点**
抢了焦点,后面注入的文字就没有落点了 —— 光标会被抢到胶囊上,文字全丢。
pywebview 的 `window.show()` 内部会调 WinForms 的 `Activate()`,必然抢焦点。
所以胶囊显示一律走 Win32:先给 hwnd 加 `WS_EX_NOACTIVATE`,
再用 `ShowWindow(hwnd, SW_SHOWNOACTIVATE)`。

**pywebview 对透明窗口自带一个抢焦点的 hack**
winforms 后端的 `EdgeChrome.on_navigation_start` 里有一段:透明窗口在
页面**开始导航**时会 `form.Show()` + `form.Activate()`(源码注释写着
"no idea why this works")。后果是胶囊在**启动时**就自己冒出来,
还占住前台,用户的键盘输入全进了胶囊。
`WS_EX_NOACTIVATE` 拦不住它 —— `Activate()` 走的是 `SetForegroundWindow`,
那是程序显式激活,不是"用户点击"。

定点修法:在 `before_show` 事件里把 `window.transparent` 置 `False`。
这个时机唯一能同时满足两件事:BrowserForm 的构造函数**已经**按
`transparent=True` 设好了 WebView2 的 `DefaultBackgroundColor = Transparent`
(透明不受影响),而导航还没开始(那段 hack 因此不执行)。
`before_show` 是同步事件,没有竞态。

**透明窗口一旦被 resize,透明就失效**
pywebview 的透明窗口只要被 `SetWindowPos` 改过尺寸,WebView2 就会
用 Form 的背景色(`#F0F0F0`)把整块客户区填掉 —— 窗口外的区域从
"透出桌面"变成"一块灰底"。实测:resize 前透明、resize 后灰底。

色键(`WS_EX_LAYERED` + `LWA_COLORKEY`)对 WebView2 这个子窗口无效
(灰底是 WebView2 自己画的);resize 后再重设 `DefaultBackgroundColor`
又会被 CoreWebView2 的"只能从 UI 线程访问"挡下来。

所以胶囊窗口**固定尺寸**(512×260 逻辑像素 —— 请求值会被 WinForms
AutoScale 缩水,建窗时放大补偿),不做高度自适应 ——
内容用 CSS 贴着窗口底部排,文字条到 `max-height` 就自己内部滚动。
窗口空白区不属于命中区:前端把"文字条+胶囊"的矩形上报给 Python,
`SetWindowRgn` 把窗口裁成内容形状,空白处点击直接穿透到底下应用。

**透明窗口的背景要三层都透明**
窗口(html / body)少写一层 `background: transparent` 就会露出一块
不透明的底色 —— 根元素的背景会传播到 canvas,所以 `html` 这一层
不能省。

**还原焦点不许模拟按键**
把焦点还给用户原来的窗口,只能用 `AttachThreadInput` +
`SetForegroundWindow`。绝不能用 `keybd_event` / `SendInput` 之类
"模拟 Alt 键"的老套路 —— 那会往用户的输入流里插东西,正在打字的人
会打出多余的 Alt 组合。

**透明窗口上不能有 box-shadow / backdrop-filter**
它们在透明窗口上会渲染成一条灰带,非常难看。层次只靠"深底 + 半透明描边"。

**面板和胶囊共用一条推送线程**
两条线程并发推同一个 WebView2,事件顺序会乱 —— 胶囊的前端是状态机
(setState → setPreview → setResult 必须按顺序到达)。而且电平采样一次
就能分发给所有窗口,没必要重复采样。

**便携存储**
配置和日志放项目内的 `data/`,不要写 `%APPDATA%` —— 详见仓库根目录
`AGENTS.md`。vibecoding 产物迭代快,系统目录里的旧配置会污染新版本,
而且它不在项目里,`git status` 看不到,删掉项目目录它还在。

**注入要用 SendInput,不要用 keybd_event**
`keybd_event` 是老 API,投递成功与否完全不可知,被 UIPI 拦掉时也照样"成功",
表现出来就是"按了热键没反应"。`SendInput` 会返回**实际投递**的事件数,
据此能知道到底发出去几个;投递不全时只补发抬起事件,绝不重贴(会重复文本)。

**恢复剪贴板的时机**
粘贴是异步的:`SendInput` 只是把 Ctrl+V 塞进系统输入队列,目标应用
什么时候真正去读剪贴板不由我们决定。恢复得太早,目标读到的就是
还没还回去的旧内容 —— 实测表现是"粘贴出来的是上一次的内容"。
所以粘贴后必须等目标消费完才能恢复(这里 1.0s)。

**剪贴板要还,但不能吞掉用户的新内容**
剪贴板只有一份,借了必须还。但无脑还回去会覆盖用户在这期间复制的新内容,
那才是真的丢数据。所以恢复前先比对**剪贴板变更序号**(任何改动都会让它递增)
和当前内容,对不上就**放弃恢复** —— 宁可让用户的新内容留着,也不覆盖它。
最坏情况是"用户原内容没还回去",而不是"用户新内容被吞掉"。

**为什么不用 SendInput + KEYEVENTF_UNICODE 逐字打字**
那种方式不碰剪贴板,看着更"干净",但有两个坑:
打字路径下 `\r` `\n` `\t` 会被 Windows 当成真实按键(回车在聊天软件里
就是"发送消息");部分应用(记事本、WordPad)接受 Unicode 键事件但丢弃字形,
表现是"光标在动但没字"。
参考实现 [OpenType](https://github.com/ct-jyjntc/opentype-public)(AGPL-3.0)
敢用这条路,是因为它有 UI Automation 可以**读回控件内容校验**,知道有没有
部分成功(部分成功时它不重贴,避免重复文本)。我们没有这个校验能力,
盲目走打字路径,一旦部分失败就无法判断该不该重贴 —— 所以保持剪贴板为主路径。

---

## 开发

```powershell
# 跑测试
uv pip install --python .venv\Scripts\python.exe pytest
.venv\Scripts\python.exe -m pytest

# 单独跑某个模块
.venv\Scripts\python.exe -m qwen3_voice
```

---

## 相关

- 模型仓库: https://github.com/QwenLM/Qwen3-ASR
- 论文: https://arxiv.org/abs/2601.21337
- 交互与视觉规格参考: https://github.com/ct-jyjntc/opentype-public
  (AGPL-3.0)。胶囊的**样式与波形算法是直接移植**它的
  `src/renderer/floating-bar.{tsx,css}`(见 `src/qwen3_voice/ui/static/capsule.*`
  里的出处说明),剪贴板归属判定、"部分成功不重贴"这类策略则只参考了思路。
  因为搬了它的源码,本项目整体按 **AGPL-3.0-or-later** 发布
  (见仓库根目录 `LICENSE`)。
- 频谱条的交互思路参考 MiniMax Code 的录音波形(真实 RMS 驱动 + 滚动条形图)
