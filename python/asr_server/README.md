# asr_server —— 本地推理子进程（named pipe IPC）

C# 版把"本地推理"从"进程内 `import torch`"改成"**Python 子进程 + 命名管道**"：
本脚本被 `VoiceMikonCSharp.Engines.Local.LocalBackend` 拉起，双方用一套行式 JSON 协议对话。

这样 C# 主进程不再背着几百 MB 的 torch 常驻 —— 只有真正要用本地模型时才起这个子进程。

**脚本自包含**：不 import `voice_mikon` 包（那是另一个工程、另一套依赖）。
需要的东西都翻成了独立实现：

| Python（`voice_mikon/src/voice_mikon/engines/`） | 本脚本 |
|---|---|
| `engine.py` 的 `Qwen3ASRModel.from_pretrained(...)` | `ModelEngine.load()`（dtype / device_map / max_inference_batch_size / max_new_tokens 逐项一致） |
| `engine.py` 的 `ASREngine.transcribe()` | `ModelEngine.transcribe()` |
| `engine.py` 的 `ASREngine.unload()`（`del model` + `empty_cache()`） | `ModelEngine.unload()` |
| `base.py` 的 `_effective_hotwords` / `_build_context` | `Hotwords.merge()` / `build_context()` |
| `config.py` 的 `load_hotwords_file()` | `load_hotwords_file()` |

---

## 1. 怎么跑

### 真模型模式

```powershell
python python/asr_server/asr_server.py --pipe voice-mikon-csharp-asr-1234-abcdefgh
```

需要（本机装好即可，脚本本身不负责装）：

- `torch`
- `qwen-asr`（官方包 `qwen-asr`，内含 `Qwen3ASRModel`）
- `numpy`（torch 的依赖，脚本用它在识别时把 int16 转成 float32）

> ⚠ **不要用 `voice-mikon` 的 venv 跑本脚本** —— 那是另一个工程的依赖环境。
> 本脚本用**系统 python**（`C# 侧默认就是系统 python`，可用环境变量 `VOICE_MIKON_CSHARP_PYTHON` 覆盖）。

### mock 模式（测试用）

```powershell
python python/asr_server/asr_server.py --pipe voice-mikon-csharp-asr-1234-abcdefgh --mock
```

- **一个第三方包都不 import**（只用标准库）—— 系统 python 干干净净也能跑。
- `load` 立即 `ready`；`transcribe` 回 `[mock:{n_samples}:{crc32:08x}]`
  （`crc32` 是**收到的音频字节**的校验和，C# 侧据此验证"音频原样到达"）。
- 故障注入：请求里带 `"mock_fail": true` → 脚本抛异常并回 `error`；
  再带 `"mock_fatal": true` → 那条 `error` 的 `fatal` 为 `true`。

### 参数

| 参数 | 说明 |
|---|---|
| `--pipe <名字>` | **必填**。管道名（不含 `\\.\pipe\` 前缀）。C# 侧生成后传进来。 |
| `--mock` | mock 模式：不加载真模型，不 import torch / qwen_asr。 |
| `--hotwords-file <路径>` | 可选。词表文件（脚本会与 C# 送来的 `context` 合并去重）。 |

---

## 2. 管道

- Windows 命名管道：`\\.\pipe\{名字}`
- 方向：双向（`PipeDirection.InOut`）
- 管道名由 **C# 生成**：`voice-mikon-csharp-asr-{进程ID}-{8 位随机}` —— 带 PID 和随机后缀，多实例不打架。
- 服务端 `open()` 会**阻塞直到客户端连上**；C# 侧的 `Connect()` 与之会合。

---

## 3. 协议

### 3.1 帧格式

- **控制消息**：UTF-8 JSON，**一行一个**（`\n` 结尾）。
- **音频**：`transcribe` 请求行之后，**紧接着**是 `n_samples × 2` 字节的
  **int16 小端**裸帧（没有额外的长度头 —— 长度在 JSON 里给了）。

读侧要注意行/块混排：按行切 JSON，剩下的字节留给裸帧。

### 3.2 请求（C# → Python）

| `cmd` | 字段 | 说明 |
|---|---|---|
| `ping` | — | 连通性探测 |
| `load` | `model_root`, `tag` | 加载本地权重（`<model_root>/Qwen3-ASR-<tag>`）。**只认本地目录，绝不联网下载** |
| `transcribe` | `req_id`, `sample_rate`, `language`, `context[]`, `n_samples` | 识别；请求行之后紧跟裸音频帧 |
| `switch` | `tag` | 切换模型规格（`0.6B` / `1.7B`），重新加载 |
| `unload` | — | 卸载模型（`del model` + `torch.cuda.empty_cache()`），进程留着 |
| `shutdown` | — | 子进程优雅退出（运维/诊断用；正常卸载用 `unload`） |

示例：

```json
{"cmd":"load","model_root":"D:\\models","tag":"0.6B"}
{"cmd":"transcribe","req_id":"12","sample_rate":48000,"language":null,"context":["词A","词B"],"n_samples":16000}
{"cmd":"switch","tag":"1.7B"}
{"cmd":"unload"}
{"cmd":"ping"}
```

- `language` 可以是 `null`（自动检测语种）。
- `context` 是**词表数组**（不是最终提示串）—— 脚本会把它与自己那份词表文件合并去重，
  再拼成官方格式 `"Vocabulary: a, b, c."` 交给 qwen_asr 的 `context` 参数。
  **两边都合并**，所以任一侧的词都不会丢。

### 3.3 响应（Python → C#）

| `type` | 字段 | 说明 |
|---|---|---|
| `pong` | — | `ping` 的应答 |
| `ready` | `elapsed` | 模型就绪（`elapsed` = 加载耗时秒数） |
| `loaded` | `tag`, `elapsed` | 切换完成 |
| `unloaded` | — | 卸载完成 |
| `result` | `req_id`, `text` | 识别结果（`req_id` 与请求一一对应） |
| `error` | `req_id`, `message`, `fatal` | 出错 |
| `bye` | — | `shutdown` 的应答 |

示例：

```json
{"type":"ready","elapsed":3.21}
{"type":"result","req_id":"12","text":"识别结果"}
{"type":"error","req_id":"12","message":"识别失败: ...","fatal":false}
```

### 3.4 错误语义

- **Python 侧任何异常都回 `error`，绝不挂死**（主循环整体包在 try/except 里）。
- `fatal: true` = 后端**整体不可用**（C# 侧会把本地后端标记为不健康，`Loaded` 变 false）。
  典型场景：`load` 失败（权重缺失 / torch 没装 / 加载炸了）。
- `fatal: false` = 单次请求失败，后端还活着（典型场景：某一句识别失败）。
- `req_id` 原样带回；`load` / `unload` 这类没有 `req_id` 的请求，`req_id` 为空串。

---

## 4. 参考工具（测试用）

### `tools/vad_ref.py`

用 Python 的 onnxruntime 跑同一个 `silero_vad.onnx`，输出**逐窗概率**（JSON），
给 C# 侧比对（预期偏差 < 1e-5）：

```powershell
python python/asr_server/tools/vad_ref.py --onnx src/VoiceMikonCSharp.Engines/Models/silero_vad.onnx --rate 16000 --input 某段.wav
```

输出：

```json
{"probabilities": [0.0031, 0.0027, ...]}
```

依赖 `onnxruntime` + `numpy`（没有就报错退出，C# 侧会跳过对比测试）。
