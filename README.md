# qwen3-voice

基于本地 [Qwen3-ASR](https://github.com/QwenLM/Qwen3-ASR) 的全局语音输入工具。

按一次全局热键开始说话,再按一次,识别出的文字直接粘贴到当前光标位置 —— 类似手机输入法,但在 Windows 上跑,音频完全不出本机。

---

## 特点

- **常驻控制面板**:小窗一直开着,大按钮直接开始/停止录音,不用记热键
- **全局热键**:`Ctrl+Alt+Space` 随处可用,系统级注册,不需要管理员权限
- **完全本地**:模型跑在本机 GPU,音频不出本机,断网可用
- **模型常驻**:0.6B / 1.7B 随时切换,切换后热键零等待
- **不污染剪贴板**:粘贴完成后自动恢复原内容
- **静音不误触**:误按热键不说话,只会提示「没有检测到声音」,不会给一段幻觉文字
- **焦点保护**:转写这 1 秒里你切了窗口,文字仍然贴回原来的窗口

---

## 安装

需要 Python 3.12+ 和一块 NVIDIA 显卡(8GB 显存足够)。

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

1. 面板出现在屏幕右下角,可拖动,位置会被记住
2. 点面板上的 **「● 开始录音」** 或按 **Ctrl+Alt+Space**
3. 说话,面板上会显示实时波形和计时
4. 再点一次 **「■ 停止录音」** 或再按一次热键
5. 文字自动出现在你原来光标所在的位置

托盘图标:灰=待机,黄=加载中,红=录音中。

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
| `hotwords` | `[]` | 热词,提升专有名词准确率 |
| `restore_clipboard` | `true` | 粘贴后恢复原剪贴板 |
| `trailing_space` | `true` | 末尾自动补空格 |
| `silence_rms` | `0.002` | 低于此电平视为静音 |
| `max_duration` | `120` | 单次最长录音秒数 |
| `panel_geometry` | `""` | 面板位置,自动保存(逻辑像素) |

环境变量:

- `QWEN3_VOICE_HOME` —— 换掉整个数据目录(便携使用)
- `QWEN3_VOICE_CONFIG` —— 指定配置文件路径
- `QWEN3_VOICE_LOG` —— 指定日志路径

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
│   └── recorder.py      录音采集、原生采样率采集 + 重采样
├── asr/
│   └── engine.py        Qwen3-ASR 封装,支持热切换模型
├── win32/
│   ├── api.py           Win32 API 绑定(ctypes 签名集中声明)
│   ├── dpi.py           DPI 感知(高分屏必需)
│   ├── hotkey.py        全局热键注册与监听
│   ├── clipboard.py     剪贴板读写
│   └── inject.py        文本注入(剪贴板 + Ctrl+V)
└── ui/
    ├── panel.py         tkinter 常驻面板
    └── tray.py          托盘图标
```

**分层原则**:UI 层不直接碰热键,热键不直接碰 UI,都通过 `core.controller`
的订阅机制通信。想换 UI 实现或换触发方式,只动对应那一层。

**可测试性**:`VoiceController` 的文本注入和录音器都是可注入的工厂,
测试时用假实现 —— 测试绝不能真的改用户剪贴板或打开真实音频设备
(后者会让 pytest 退出时崩 0xC0000005)。

**线程分工**:

| 线程 | 职责 | 为什么 |
|---|---|---|
| 主线程 | tkinter mainloop | 所有 tkinter 操作必须在创建它的线程 |
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

**必须用设备原生采样率**
按 16 kHz 直接打开蓝牙设备,WASAPI 会悄悄降速,2 秒只采到 0.38 秒,录音被截断。
正确做法是按原生率采集,自己重采样到 16 kHz(`audio/recorder.py:resample`)。

**热键路径上不能做耗时操作**
早期版本在按热键时同步做 2 秒设备探测,导致用户按下的前 2 秒说的话全丢。
现在设备探测在后台预热,结果走缓存,热键路径零延迟。

**qwen_asr 的音频输入格式**
必须是 `(np.ndarray, sample_rate)` 元组或文件路径,传裸 ndarray 会报
`TypeError: Unsupported audio input type`。

**高分屏必须声明 DPI 感知**
本机 175% 缩放。不声明的话 tkinter 不知道真实缩放,控件和布局全乱。
而且即使声明了,tkinter 也只放大字体(`scaling`),**固定像素不会跟着放大** ——
所以 padding、控件宽高这些常量都要自己乘一遍缩放系数,否则挤成一团。

**grid 在固定尺寸窗口里会重叠**
无边框 + 固定尺寸的窗口里,grid 的行高分配经常算不准,控件互相叠在一起。
改用 pack 顺序放置,行为可预测。

**Radiobutton 创建时会触发 command**
tkinter 的 Radiobutton 在创建时会把绑定的 variable 设成自己的值,连带触发
command —— 面板初始化时会误触发一次模型切换。用一个初始化标志守卫。

**模型路径拼接别搞错**
`model_root / repo` 和 `model_root / model_tag` 是两个不同的路径。
写成前者会去找 `models\Qwen\Qwen3-ASR-0.6B`,找不到就退回联网下载,然后
在离线环境下报一长串 `MaxRetryError`。

**测试不能碰真实剪贴板和真实音频设备**
真设备的 PortAudio 清理会让 pytest 退出时崩 0xC0000005;改剪贴板会污染
用户正在用的剪贴板。用可注入的假实现隔离。

**便携存储**
配置和日志放项目内的 `data/`,不要写 `%APPDATA%` —— 详见仓库根目录
`AGENTS.md`。vibecoding 产物迭代快,系统目录里的旧配置会污染新版本,
而且它不在项目里,`git status` 看不到,删掉项目目录它还在。

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
