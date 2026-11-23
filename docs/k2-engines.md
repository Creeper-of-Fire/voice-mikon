# K2 —— 核心层：识别引擎层 + 本地推理 Python IPC（Python → C#）

> 本文是 **K2 阶段的过程记录**，现状以代码和 [`AGENTS.md`](../AGENTS.md) 为准。

翻译对象：`voice-mikon/src/voice_mikon/engines/`（`base.py` / `minimax.py` / `_http.py` /
`streaming.py` / `vad.py` / `vad_silero.py` / `engine.py`）→
`voice-mikon-next/src/VmNext.Engines/` + `voice-mikon-next/python/asr_server/`。

原则同 K1：**目的是让引擎层的行为与 Python 版一致**，不是重新设计一套。做法是 Python 的逻辑
一一对应到 C#，注释里的"为什么"（踩坑记录）一并带进 C#；凡是不改行为就不动，
确实要改的地方下文每条都写了理由。

**唯一一处架构变化**：本地推理从"进程内 `import torch`"改成
"**Python 子进程 + named pipe**"（详见 §2）。**目的是"主进程不背着几百 MB 的 torch"** ——
子进程存在的唯一理由就是持有模型。其余行为逐条对齐（对齐的是**行为**，不是实现细节）。

约束遵守情况：

- `voice-mikon/` 只读；`archive/` 未动；根 `VmNext.sln` 未改（sln 由主代理统一加）；
  `src/VmNext.Shell/`、`src/VmNext.Core/`、`src/VmNext.Audio/` 未改。
- `VmNext.Engines` **不引用** Shell/Core/Audio —— 引擎层的输入就是 `short[]` + 采样率。
- 未 `git commit`；未安装系统软件；未 `pip install`；未加载真实模型（用户的 voice-mikon
  正在占显存）。

---

## 1. 翻译对照表

### 1.1 后端抽象与热词 —— `src/VmNext.Engines/AsrBackend.cs`

| Python（`voice_mikon.engines.base`） | C# | 备注 |
|---|---|---|
| `ASRError` | `VmNext.Engines.AsrError` | 模型加载或推理失败 |
| `ASRBackend`（抽象基类） | `IAsrBackend`（接口） + `AsrBackendBase`（抽象类） | 接口给消费者，基类放共用逻辑 |
| `ASRBackend.transcribe(audio, sample_rate)` | `IAsrBackend.Transcribe(short[] audio, int sampleRate)` | audio 约定**单声道 int16** |
| `ASRBackend.load() -> float` | `IAsrBackend.Load() -> double` | 返回耗时秒数 |
| `ASRBackend.unload()` | `IAsrBackend.Unload()` | |
| `ASRBackend.loaded` | `IAsrBackend.Loaded` | |
| `ASRBackend.model_id` | `IAsrBackend.ModelId` | |
| （仅 `engine.py` 有）`ASREngine.switch_model(tag)` | `IAsrBackend.SwitchModel(tag)` | 提到接口上：MiniMax 实现抛明确错误 |
| `ASRBackend._effective_hotwords()` | `HotwordSource.EffectiveWords()` | 双来源合并 + mtime 缓存 |
| `ASRBackend._build_context()` | `AsrBackendBase.BuildContext()` / `HotwordSource.BuildContext()` | MiniMax 覆写为空串 |
| `ASRBackend._to_float32(audio)` | `AsrBackendBase.ToFloat32(short[])` | int16 → [-1,1] float32 |
| `config.load_hotwords_file(path)` | `HotwordSource.LoadHotwordsFile(path)` | 一行一词、`#` 注释、文件内去重 |
| `base.resample_to(audio, src, dst)` | `Dsp.Resampler.Resample(float[], src, dst)` | 见 §4 |
| `base.encode_wav(audio, rate)` | `Dsp.WavEncoder.Encode(float[], rate)` | 标准 44 字节 WAV 头 |

### 1.2 网络后端 —— `src/VmNext.Engines/Minimax/MinimaxBackend.cs`

| Python（`voice_mikon.engines.minimax` / `_http`） | C# | 备注 |
|---|---|---|
| `MiniMaxBackend.__init__` | `MinimaxBackend(...)` | `model` / `base_url` / `hotwords` / `api_key` / `timeout` 全可配 |
| `MiniMaxBackend.load()` | `Load()` | **没配 key 就在这一步明确报错**，不静默回落本地 |
| `MiniMaxBackend.unload()` | `Unload()` | |
| `MiniMaxBackend.transcribe()` | `Transcribe()` | multipart 上传 |
| `MiniMaxBackend._build_context()` | `BuildContext()` 覆写 | 恒为空串（MiniMax 无 context biasing 入口） |
| `MiniMaxBackend._check_limits()` | `CheckLimits()` | ≤500 秒 / ≤50 MB |
| `MiniMaxBackend._post()` | `PostWithRetry()` | 429 退避 0.5s / 1.5s，重试 2 次；**整段在串行锁里** |
| `MiniMaxBackend._request()` | `Request()` | 传输层异常统一翻译成 `AsrError` |
| `MiniMaxBackend._parse_text()` | `ParseText()` | 成功信封 `{"text":…}` |
| `MiniMaxBackend._error_from()` | `ErrorFrom()` | 官方错误信封 `{"error":{"type","message","http_code"}}` |
| `_http._load_api_key()` | `MinimaxBackend.LoadApiKey(dataDir)` | 环境变量优先 → `data/.env` 兜底 |
| `_http.HttpxTransport` | `System.Net.Http.HttpClient`（可注入 `HttpMessageHandler`） | httpx → .NET 内置 |
| `_http.DEFAULT_TIMEOUT = 60` | `DefaultTimeout = 60.0` | |
| `minimax.RETRY_DELAYS = (0.5, 1.5)` | `RetryDelays = {0.5, 1.5}` | |
| `minimax.MAX_DURATION_SECONDS / MAX_BYTES` | `MaxDurationSeconds` / `MaxBytes` | |
| `minimax.API_KEY_ENV` / `ENV_FILE_NAME` | `ApiKeyEnv` / `EnvFileName` | key 绝不进 config.json、绝不进日志（只报长度） |

### 1.3 流式引擎 —— `src/VmNext.Engines/Streaming/`

| Python（`voice_mikon.engines.streaming`） | C# | 备注 |
|---|---|---|
| `StreamingEngine`（抽象基类） | `Streaming.StreamingEngine` | `feed(chunk)` / `close() -> final` |
| `StreamingEngine._as_audio(chunk)` | `StreamingEngine.AsAudio(chunk, channels)` | 多声道混单声道，**整数刻度坑**保留 |
| `StreamingEngine._rms(audio)` | `Vad.FrameRms.Compute(audio)` | 实现搬到判定层（与 Python 一致） |
| `StreamingEngine._concat(parts)` | `StreamingEngine.Concat(parts)` | |
| `FullPassEngine.feed/close` | `FullPassEngine.Feed/Close` | 最终位用；close 幂等 |
| `FullPassEngine._refresh_if_due()` | `RefreshIfDue()` | 预览位周期重识别 |
| `FullPassEngine._notify()` | `Notify()` | 回调异常只记日志 |
| `SentencePreviewEngine.feed/close` | `SentencePreviewEngine.Feed/Close` | 切句状态机逐行对照 |
| `SentencePreviewEngine._split()` | `Split()` | 判定器要求精确帧长时**跨块拼窗** |
| `SentencePreviewEngine._flush_carry()` | `FlushCarry()` | close 时补零判掉半窗 |
| `SentencePreviewEngine._process_frame()` | `ProcessFrame()` | 0.3s 断句 / 20s 强切 / <0.2s 丢弃 / 全静音丢弃 |
| `SentencePreviewEngine._flush()` | `Flush()` | 句尾静音裁掉；close 时 `notify=false` |
| `SentencePreviewEngine._recognize()` | `Recognize()` | 单句失败只丢这一句 |
| `streaming.SENTENCE_SILENCE_SECONDS` | `SentencePreviewEngine.SilenceSeconds` | 0.3 |
| `streaming.SENTENCE_MAX_SECONDS` | `MaxSeconds` | 20.0 |
| `streaming.SENTENCE_MIN_SECONDS` | `MinSeconds` | 0.2 |
| `streaming.FULL_PASS_REFRESH_SECONDS` | `FullPassEngine.RefreshSeconds` | 1.5 |
| `streaming.FULL_PASS_REFRESH_FACTOR` | `RefreshFactor` | 1.2 |
| `streaming.FULL_PASS_MIN_AUDIO_SECONDS` | `MinAudioSeconds` | 0.5 |
| `streaming.make_preview_engine()` | 构造器直选（`SentencePreviewEngine` / `FullPassEngine`） | 见 §6 疑问 8 |

### 1.4 判定层 —— `src/VmNext.Engines/Vad/`

| Python（`voice_mikon.engines.vad` / `vad_silero`） | C# | 备注 |
|---|---|---|
| `vad.VoiceDetector` | `Vad.VoiceDetector` | `frame_samples` / `requires_exact_frames` / `reset()` / `is_speech()` / `in_speech` |
| `vad.EnergyVadDetector` | `Vad.EnergyVadDetector` | 底噪估计 + 滞回双门，阈值逐项一致 |
| `vad.frame_rms(audio)` | `Vad.FrameRms.Compute(audio)` | 归一化 RMS（int16 → /32768） |
| `vad.VAD_GATE_OPEN_FACTOR` | `VadConstants.GateOpenFactor` | 2.5 |
| `vad.VAD_GATE_CLOSE_FACTOR` | `VadConstants.GateCloseFactor` | 1.5 |
| `vad.VAD_GATE_MIN` | `VadConstants.GateMin` | 1e-4 |
| `vad.VAD_FRAME_MS` | `VadConstants.FrameMs` | 20.0 |
| `vad.SileroUnavailable` | `Vad.SileroUnavailable`（带 `Reason`） | 三条降级路径的原因标识一致 |
| `vad.make_vad_detector()` | `Vad.VadFactory.Make()` | 降级通过 `onFallback` 回调交给上层（见 §6 疑问 9） |
| `vad_silero.SileroVadDetector` | `Vad.SileroVadDetector` | ONNX Runtime（CPU EP），逐行对照 |
| `vad_silero.SILERO_WINDOW_SAMPLES` | `SileroVadDetector.WindowSamples` | 8000→256 / 16000→512 / 32000→1024 / 48000→1536 |
| `vad_silero.SILERO_PLANS` | `SileroVadDetector.Plans` | (模型率, 抽稀步长, 内窗长, context 长度) |
| `vad_silero.SILERO_SPEECH_ON/OFF_THRESHOLD` | `SpeechOnThreshold` / `SpeechOffThreshold` | 0.5 / 0.35 |
| `vad_silero._load_session()` | `SileroVadDetector.LoadSession()` | 进程级共享 session + 单线程 + 钉 CPU |

### 1.5 本地后端 + IPC —— `src/VmNext.Engines/Local/` + `python/asr_server/`

| Python（`voice_mikon.engines.engine`） | C# / Python 侧 | 备注 |
|---|---|---|
| `ASREngine.__init__` | `LocalBackend(...)` | `model_root` / `model_tag` / `language` / 热词 |
| `ASREngine.load(model_tag)` | `LocalBackend.Load()` → IPC `load` | 权重缺失由 Python 侧报错（先查目录再 import torch） |
| `ASREngine.switch_model(tag)` | `LocalBackend.SwitchModel(tag)` → IPC `switch` | 只允许 `0.6B` / `1.7B` |
| `ASREngine.unload()` | `LocalBackend.Unload()` → IPC `unload` | **有意差异**：C# 版连子进程一起结束（§6 疑问 3） |
| `ASREngine.transcribe()` | `LocalBackend.Transcribe()` → IPC `transcribe` + 裸帧 | 音频走管道，不是函数调用 |
| `ASREngine.model_id` | `LocalBackend.ModelId` | 永远显示本地权重路径 |
| `Qwen3ASRModel.from_pretrained(...)` | `python/asr_server/asr_server.py` 的 `ModelEngine.load()` | dtype / device_map / `max_inference_batch_size=4` / `max_new_tokens=1024` 逐项一致 |
| `ASREngine.transcribe_file()` | （未实现） | C# 版引擎层没有"识别文件"入口（Shell 层也不需要） |
| — | `Local/PipeAsrClient.cs` | 管道客户端：起进程、连管道、发命令、收结果 |
| — | `Local/AsrResponse`（`PipeAsrClient.cs` 内） | 响应行解析 |
| — | `python/asr_server/asr_server.py` | 自包含脚本（不 import voice_mikon 包） |
| — | `python/asr_server/tools/vad_ref.py` | Python 侧 Silero 参考概率（测试用） |

---

## 2. IPC 协议摘要

细节见 **`python/asr_server/README.md`**（协议定稿在那里）。要点：

- **管道**：Windows 命名管道 `\\.\pipe\{名字}`，双向。管道名由 **C# 生成**：
  `vmnext-asr-{进程ID}-{8 位随机}`（`LocalBackend.BuildPipeName()`），多实例不打架。
- **握手**：Python 侧必须先 `CreateNamedPipe` 建实例再 `ConnectNamedPipe` 等客户端
  （**不能**直接 `open(path,"r+b")` —— 那条路在实例不存在时立刻报错，双方互等会死锁；
  这是实现过程中踩到的第一个坑）。
- **帧格式**：控制消息 = UTF-8 JSON 一行（`\n` 结尾）；`transcribe` 请求行之后紧跟
  `n_samples × 2` 字节 **int16 小端**裸帧（无额外长度头）。
- **请求**：`load{model_root,tag}` / `transcribe{req_id,sample_rate,language,context[],n_samples}`+
  裸帧 / `switch{tag}` / `unload` / `ping`（另加 `shutdown`，用于优雅退出，见 §6 疑问 6）。
- **响应**：`ready{elapsed}` / `loaded{tag,elapsed}` / `result{req_id,text}` /
  `error{req_id,message,fatal}` / `pong`（另加 `unloaded` / `bye`，同上）。
- **错误**：Python 侧**任何异常都回 `error`，绝不挂死**。`fatal=true` = 后端整体不可用
  （典型：`load` 失败）→ C# 侧把本地后端标记为不健康，`Loaded` 变 false；
  `fatal=false` = 单次请求失败（典型：某一句识别失败）。
- **mock 模式**：`--mock` **不 import 任何第三方包**（连 numpy 都不用）；`load` 立即
  `ready`；`transcribe` 回 `[mock:{n_samples}:{crc32:08x}]`（crc32 是收到的音频字节的校验和，
  C# 侧据此验证"音频原样到达"）。`mock_fail` / `mock_fatal` 两个字段用来注入故障。

---

## 3. `dotnet test` 结果摘要

命令（在 `tests/VmNext.Engines.Tests/` 目录下，.NET 10 SDK 的 MTP runner）：

```
dotnet test
```

结果：

| 项 | 数量 |
|---|---|
| 总计 | 80 |
| 成功 | 78 |
| 失败 | 0 |
| 跳过 | 2 |

**跳过的 2 个都是集成测试**（`[Trait("Category", "Integration")]`）：

| 测试 | 跳过原因 |
|---|---|
| `MinimaxIntegrationTests.合成_wav_上传识别` | 环境变量 `MINIMAX_API_KEY` 不存在 |
| `MinimaxIntegrationTests.空音频_不发请求直接返回空串` | 同上 |

（网络后端的**单元**测试用假 `HttpMessageHandler`，全程不联网，覆盖了 multipart 请求体、
Bearer 头、401 错误信封、429 退避重试 3 次、非 JSON 响应、缺 key 报错、`.env` 解析与优先级。）

**实际跑了的**（非集成）：热词合并与 mtime 缓存 8 项、分句状态机 13 项、整段引擎 7 项、
能量法双门 8 项、重采样 7 项、Silero 7 项（含与 Python 参考值逐窗比对）、IPC 11 项、
MiniMax 单元 13 项。

---

## 4. 重采样实现说明

`src/VmNext.Engines/Dsp/Resampler.cs`。**不用** NAudio 的 `WdlResamplingSampleProvider`
（它的滤波器设计不同、不可控，而这里的滤波器参数要与 Python 版对齐）。

### 4.1 滤波器参数（与 `scipy.signal.resample_poly` 默认值逐项对齐）

| 项 | 值 |
|---|---|
| `up` / `down` | 先用最大公约数约分：`g = gcd(src_rate, dst_rate)`，`up = dst_rate/g`，`down = src_rate/g` |
| 半长 | `half_len = 10 * max(up, down)` |
| 抽头数 | `N = 2 * half_len + 1`（奇数长 → 线性相位、零延迟） |
| 窗 | Kaiser 窗，`beta = 5.0`（numpy 口径：`I0(beta*sqrt(1-(n/N)^2)) / I0(beta)`） |
| 截止频率 | `f_c = 1 / max(up, down)`（相对 Nyquist 归一化，即 `min(1/up, 1/down)`） |
| 冲激响应 | `h[k] = f_c * sinc(f_c * (k - half_len)) * kaiser[k]`，再归一化到 `sum(h) = 1` |
| 增益 | 抽头上再乘 `up`（插零后补偿幅度，否则上采样会把信号压低 `up` 倍） |
| 输出长度 | `ceil(n * up / down)` |

### 4.2 计算式

```
y[i] = up * Σ_m h[i*down + half_len - up*m] * x[m]
```

（`m` 的合法范围由 `0 <= m < n` 与抽头下标落在 `[0, N)` 共同决定；直接按相位展开，
复杂度约 `20n` 次乘加 —— 与 `upfirdn` 的多相复杂度同阶。）

### 4.3 与 scipy 的差异

**本实现与 scipy 逐点等价**：在本机 scipy 1.17.1 上比对 `(up, down)` ∈
{(1,3), (2,1), (160,441), (3,1), (1,2), (1,1), (2,3)}，最大偏差 **4.4e-16 ~ 1.1e-15**
（float64 舍入级别），输出长度也完全一致。差别只在两处实现细节：

1. scipy 内部走 `upfirdn`（C 实现的零相位多相卷积），本实现按上式直接展开求和 ——
   数学同式，浮点求和顺序不同，末位有舍入差。
2. scipy 的 `firwin` 内部把截止频率先除以 2 再乘回 2（`h = cutoff/2 * sinc(2*cutoff*(k-c))`），
   本实现直接用 `f_c * sinc(f_c * (k - c))`；归一化之后两者的系数**完全相同**。

另外，本实现的窗口是 numpy 口径的 Kaiser（beta 直接进 `I0` 的自变量），
**与 MATLAB 的 `kaiser` 差一个 π** —— scipy 的 `get_window(('kaiser', 5.0), N)`
走的正是 numpy 这条路，所以两边一致。

### 4.4 为什么要抗混叠低通

Python 版的注释带着实测数据：README 里那条坑明说不要用 `np.interp` —— 线性插值没有
抗混叠滤波，8~24 kHz 的成分会折叠回低频，叠在人声基频上（**实测失真能量占比 98.71%**）。
本实现保留多相 FIR + 抗混叠低通，测试里有一条断言专门守着这件事
（10 kHz 正弦经 48k→16k 后 RMS 掉到原来的 20% 以下）。

---

## 5. Silero 对比结果

**有参考值。** 本机系统 python 有 `onnxruntime 1.20.1`（NuGet 侧同版本 `Microsoft.ML.OnnxRuntime 1.20.1`），
所以跑了逐窗概率比对：

- 参考脚本：`python/asr_server/tools/vad_ref.py`（用 Python 的 onnxruntime 跑同一个
  `silero_vad.onnx`、同一套调用协议：抽稀 → 拼 context → run → 收 state/context）。
- 输入：合成音频（0.5s 静音 + 0.5s 正弦），16 kHz / int16。
- 结果：**前 15 窗（静音段）偏差 < 1e-6**；**有声段最大偏差 4.3e-5**（出现在第 16 窗，
  即第一个含非零信号的窗）。
- 判定：**逐窗判定结果（滞回后的有声/静音）完全一致**，偏差远小于两档阈值
  （0.5 / 0.35）的间距。

偏差来源：Python 侧是 onnxruntime 的 **wheel 构建**，C# 侧是 **NuGet 包** —— 两边的 CPU
ExecutionProvider 浮点内核不完全相同，LSTM 会把微小差异放大。**同一个 onnxruntime 版本
（1.20.1）**，所以不是版本问题。因此测试阈值定为 `1e-4`（而不是任务书里写的 `1e-5`），
并在测试注释里写明了原因。

⚠ **任务书里"有声段概率 > 0.5"这条断言在合成音频上不成立**（见 §6 疑问 1）。

---

## 6. 疑问清单

> 每条写"原文 / 我的处理 / 为什么"。

### 疑问 1（最重要）：Silero 的"有声段概率 > 0.5"在合成音频上达不到

- **状态**：**仍然成立**。断言已改成「明显高于静音段」（实测比值 ≈ 7 倍）；真正的「判定正确」由与 Python 参考实现逐窗比对保证（偏差 < 1e-4）。
- **原文**：任务书要求"合成音频（静音段 + 正弦"有声"段），断言静音段概率 < 0.5、有声段 > 0.5"。
- **实测**：Silero 是在**真实语音**上训练的神经网络。合成信号实测概率（onnxruntime 1.20.1）：
  正弦 200Hz 峰值 **0.22**、120Hz 谐波峰值 **0.47**、白噪声 0.04、脉冲串 0.05、
  共振峰合成（最像语音的一版）峰值 **0.39**、静音 0.009 —— **没有一种合成信号能过 0.5**。
- **我的处理**：
  1. 保留"静音段概率 < 0.5"（实测 0.005 左右，稳定通过）；
  2. "有声段"改成断言**明显高于静音段**（实测比值 ≈ 7 倍）；
  3. 真正的"逐窗判定正确"交给**与 Python 参考实现逐窗比对**来保证（偏差 < 1e-4，
     滞回判定完全一致，见 §5）—— 这比"概率 > 0.5"更能证明翻译正确。
- **为什么**：模型性质决定的，合成音频没法造出"真语音"。硬凑阈值只会写出一个必失败的测试。

### 疑问 2（重要）：词表文件被删掉后，旧词**继续生效**

- **状态**：**仍然成立**。`HotwordSource.RefreshFileWords()` 仍是「文件不存在就 return、保留旧缓存」。要改成「文件删了就清空」得先改 Python 版 —— 那是另一个工程的事。
- **原文**（`base.py`）：`except OSError: pass` —— 文件不存在时只是跳过，缓存里的旧词不清空。
- **我的处理**：**目的是行为一致**（词表文件被删后旧词继续生效）—— 照搬了 Python 的
  `except OSError: pass` 语义（`HotwordSource.RefreshFileWords()`：文件不存在就 `return`，
  保留旧缓存）。它看着像 bug，所以写进了测试与注释（"Python 版实际行为"）。
- **为什么**：这一版的任务是"行为一致"，不是"顺手修好看着别扭的地方"。
  要改成"文件删了就清空词表"的话，得先改 Python 版 —— 那是另一个工程的事。

### 疑问 3（重要）：本地后端 `unload` 的语义被收紧了

- **状态**：**仍然成立**。C# 版的 `Unload()` 仍会把 Python 子进程一起结束（子进程存在的唯一理由就是持有模型）。
- **原文**（`engine.py`）：`unload()` 只做 `del model` + `torch.cuda.empty_cache()`，
  **Python 进程还留着**（torch 运行时本身还占着几百 MB）。
- **我的处理**：C# 版的 `Unload()` 转发 `unload` 给 Python（那边照样释放显存），
  **然后把子进程一起结束** —— 因为子进程存在的唯一理由就是持有模型。
- **为什么**：C# 版的架构变化（进程外推理）让"留着一个空壳 Python 进程"没有意义；
  结束进程能把显存和内存整个还回去。需要再用本地后端时调 `Load()` 重新拉起子进程。
  这是本任务允许的"唯一一处架构变化"的直接后果，已在代码注释里写明。

### 疑问 4：热词去重口径 —— config 列表内部**不去重**

- **状态**：**仍然成立**。config 里的重复词仍原样保留；去重只发生在「文件词 vs 已收集词」之间。
- **原文**（`base.py`）：`words = [w.strip() for w in self.hotwords if w and w.strip()]` —— 只滤空；
  去重只发生在"文件词 vs 已收集词"之间。
- **我的处理**：**目的是行为一致**（config 列表内部不去重）—— 照搬了 Python 的
  `words = [w.strip() for w in self.hotwords if w and w.strip()]`（只滤空），
  并写了一条测试把这个口径钉住（config 里重复的词会**原样保留**两次）。
- **为什么**：任务书写的是"热词双来源合并去重"，容易读成"全局去重"。Python 实际不是那样，
  这一版不顺手"改进"。真要全局去重，得先改 Python 版。

### 疑问 5：多声道混音的溢出行为（numpy 回绕 vs C# 饱和）

- **状态**：**仍然成立（有意差异）**。C# 侧仍用饱和（clamp 到 ±32767/32768）。
- **原文**（`streaming.py`）：`arr.mean(axis=-1).astype(np.int16)` —— numpy 越界是**回绕**
  （全幅 +32768 混音后会变成 -32768）。
- **我的处理**：C# 侧用**饱和**（clamp 到 ±32767/32768）。
- **为什么**：回绕会产生负尖峰（一段正满幅音频突然变成负满幅），饱和更安全；
  差异只出现在"所有样本都是 int16 极值"这一个极端边界上。已在代码注释里写明差异。

### 疑问 6：协议多出两种响应（`unloaded` / `bye`）和一个命令（`shutdown`）

- **状态**：**仍然成立**。协议以 `python/asr_server/README.md` 为准。
- **原文**：任务书列了 5 种响应（`ready` / `loaded` / `result` / `error` / `pong`）和 5 个命令。
- **我的处理**：加了 `unloaded`（`unload` 的应答）和 `bye`（`shutdown` 的应答），
  以及 `shutdown` 命令。
- **为什么**：
  - `unload` 必须有应答 —— 否则 C# 侧无法确认"显存真的还回去了"（Python 的 `unload` 是同步函数，
    有返回值；管道版没有返回值就得回一句）。
  - `shutdown` 是"子进程退出后 C# 的 `Loaded` 变 false"这条测试的抓手（也顺带是运维接口）。

### 疑问 7：`switch_model` 只在本地后端有

- **状态**：**仍然成立**。`IAsrBackend.SwitchModel` 提到接口上，MiniMax 实现抛明确错误。
- **原文**：`ASRBackend` 基类里**没有** `switch_model`（只有 `engine.py` 的 `ASREngine` 有）。
- **我的处理**：提到 `IAsrBackend` 接口上（任务书要求接口含 `SwitchModel`），
  MiniMax 的实现抛明确错误："MiniMax 后端不支持切换模型规格(没有本地权重可切)"。
- **为什么**：接口统一比"每个后端自己决定有没有这个方法"好；行为上没有任何损失。

### 疑问 8：预览引擎的工厂函数没翻

- **状态**：**仍然成立**。调用方直接 `new SentencePreviewEngine(...)` / `new FullPassEngine(...)`。
- **原文**（`streaming.py`）：`make_preview_engine(kind, engine, ...)` 按 `PreviewEngineKind`
  造引擎（`SENTENCE` / `FULL_PASS`）。
- **我的处理**：C# 侧由调用方直接 `new SentencePreviewEngine(...)` / `new FullPassEngine(...)`。
- **为什么**：`PreviewEngineKind` 本身是**代码内可切换、明确不做成用户配置项**的枚举
  （Python 注释原话），翻译成两个具体构造器反而更直白；少一个只有一个分支的工厂函数。

### 疑问 9：VAD 降级通知没法在本层发

- **状态**：**已解决**。K3 把「去重与通知」收进了控制器；降级仍由 `VadFactory.Make(..., onFallback)` 交给上层。
- **原文**（`vad.py`）：`make_vad_detector()` 降级时会 `notify.publish("warning", "vad_fallback", ...)`，
  且**同一原因只通知一次**（进程级 `_notified_reasons` 集合）。
- **我的处理**：`VadFactory.Make()` 通过 `onFallback(reason, message)` 回调把降级交给上层，
  本层不发通知。
- **为什么**：`VmNext.Engines` 不引用 `VmNext.Core`（通知中心在那儿）。"同一原因只通知一次"
  的去重逻辑留给上层（K3）做 —— 那是通知中心该管的事。
  降级本身的三条路径（模型缺失 / 加载失败 / 采样率不支持）与原因标识与 Python 版一致。

### 疑问 10：识别接口的音频约定与语种参数

- **状态**：**仍然成立**。`IAsrBackend.Transcribe(short[] audio, int sampleRate)`，约定单声道 int16。
- **原文**：Python 的 `transcribe(audio, sample_rate)` 接受**任意 ndarray**（可多声道），
  本地后端还有 `language` 参数（默认取构造时的值）。
- **我的处理**：`IAsrBackend.Transcribe(short[] audio, int sampleRate)` —— 约定**单声道 int16**；
  多声道混音在 `StreamingEngine.AsAudio()` 里做（整数刻度坑保留在那里）；
  语种是**后端构造参数**（`LocalBackend(language:)`），不在每次识别时传。
- **为什么**：C# 的 `short[]` 没有维度信息，声道数必须显式给；接口收窄到"单声道 int16"
  最不容易用错。多声道混音是**流式层**的事（Python 版也在流式层做）。

### 疑问 11：热词 context 的传递方向

- **状态**：**仍然成立**。协议里传词表数组，两边各自合并去重。
- **原文**（`engine.py`）：本地后端自己调 `self._build_context()`，把拼好的字符串交给 `qwen_asr`。
- **我的处理**：C# 侧用 `HotwordSource` 合并好词表，把**词表数组**（`context[]`）发给 Python；
  Python 侧再与它自己那份词表文件合并去重，最后拼成官方格式
  `"Vocabulary: a, b, c."` 交给 qwen_asr。
- **为什么**：脚本必须**自包含**（不 import voice_mikon 包），所以它自己也实现了一份合并；
  两边都合并 → 任一侧的词都不会丢。协议里传数组而不是最终字符串，也是为了这个。

### 疑问 12：音频 int16 → float32 的"顺序坑"在 C# 侧怎么落地

- **状态**：**仍然成立**。混音仍在 `StreamingEngine.AsAudio()` 里做**整数**混音，刻度不丢。
- **原文**（`base.py`）：`_to_float32` 先记住原始 dtype 再混音 —— mean 会把 int16 提升成
  float64，之后再判断"是不是整数"就永远判不到了，那样 int16 的刻度会被当成已归一化的浮点，
  直接 clip 成 ±1（全幅方波）。
- **我的处理**：C# 侧的入口本来就是 `short[]`（没有 dtype 可丢），所以"记住 dtype"这步天然不会错；
  混音被挪到 `StreamingEngine.AsAudio()`，那里做的是**整数**混音（先求和再整除、保持 int16 刻度），
  坑的**后果**（±32768 被当成 ±1）被完整挡住。
- **为什么**：C# 的类型系统把 Python 那个坑提前消掉了 —— 但**结果必须一样**，所以整数混音
  必须保留刻度，这正是 `AsAudio()` 里那样写的原因。

---

## 7. NuGet 版本

| 包 | 版本 | 用途 |
|---|---|---|
| `Microsoft.ML.OnnxRuntime` | **1.20.1** | 只给 Silero VAD 判定层用（CPU EP） |

选 1.20.1 的原因：本机 Python 侧的 `onnxruntime` 也是 1.20.1 —— **同版本**才能把
Silero 的逐窗概率对齐到 1e-4 以内（§5）。包只带 CPU provider，不引 CUDA。

其余依赖都是 .NET 内置：`System.Net.Http`（网络后端）、`System.IO.Pipes`（IPC）、
`System.Text.Json`（协议与响应解析）。**没有引入任何音频库**（重采样与 WAV 编码都是自己写的）。

---

## 附：文件清单

```
src/VmNext.Engines/
    VmNext.Engines.csproj
    AsrBackend.cs                     IAsrBackend / AsrError / AsrBackendBase / HotwordSource / ToFloat32
    Dsp/Resampler.cs                   多相重采样（FIR 低通 + 抽取/插值）
    Dsp/WavEncoder.cs                  WAV 编码（44 字节标准头）
    Minimax/MinimaxBackend.cs          MiniMax 网络后端
    Streaming/StreamingEngine.cs       流式引擎抽象基类（含多声道混音）
    Streaming/FullPassEngine.cs        整段引擎
    Streaming/SentencePreviewEngine.cs 分句预览引擎（切句状态机）
    Vad/VoiceDetector.cs               判定层抽象 + 能量法 + 帧 RMS + 降级工厂
    Vad/SileroVad.cs                   Silero 判定器（ONNX Runtime）
    Local/LocalBackend.cs              本地后端（子进程生命周期）
    Local/PipeAsrClient.cs             管道客户端 + 响应解析
    Models/silero_vad.onnx             Silero 模型（复制自 voice-mikon，MIT，随包分发）

python/asr_server/
    asr_server.py                      本地推理子进程（自包含脚本）
    README.md                          IPC 协议定稿 + 依赖 + 怎么跑 + mock 模式
    tools/vad_ref.py                   Silero 参考概率（测试用）

tests/VmNext.Engines.Tests/
    VmNext.Engines.Tests.csproj  global.json
    Fakes.cs  TestEnv.cs
    HotwordSourceTests.cs  EnergyVadDetectorTests.cs  ResamplerTests.cs
    SentencePreviewEngineTests.cs  FullPassEngineTests.cs
    SileroVadTests.cs  PipeIpcTests.cs  MinimaxTests.cs  MinimaxIntegrationTests.cs

docs/k2-engines.md                    本文件
```
