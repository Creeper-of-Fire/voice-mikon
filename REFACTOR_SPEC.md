# qwen3-voice 架构重构规格（原地重构，非重写）

> ## ⚠ 最高约束：本 spec 不可被实施者修改
>
> **worker（以及任何实施者）不允许修改本文件的任何一个字。**
>
> - **禁止**：改写、增删、"顺手优化"、补注释、修正笔误、重排章节 —— 一个字都不行。
> - **禁止**：发现 spec 与实现对不上时改 spec 迎合实现；觉得某条约束不合理就绕过它。
> - **做不到就如实说做不到**：报告里写清"这条我实现不了 / 只能做到哪一步"，然后停下。
> - 发现歧义、矛盾、缺失：在交付报告里提出来，由主 agent 与用户协商。桥梁是提出问题，不是自行修改。
> - 修改本 spec 必须先经用户明确同意，把拟改内容与理由摆出来，用户点头才动。

---

## 0. 背景与本 spec 的视角

`../voicebar/`（v2）是一次**脱离 v1 的闭眼重写**，已封存（终版快照 `4a54f54`），
不再维护。事故复盘出的核心教训：**没有行为对照物的重写，实施 agent 一定会自由发挥**，
产出不可审计的代码库。

本 spec 把 v2 的目标架构（`../voicebar/SPEC.md`，下称"v2 spec"）翻译成
**从 qwen3-voice 当前代码出发的重构路线**：

- **行为基准** = 重构前的 qwen3-voice 真机行为。重构期间任何用户可见行为变化
  都是事故，唯一例外见 §4 白名单。
- **实现基准** = qwen3-voice 现有代码。目标架构里每个模块的落地方式都是
  "从 v1 哪个文件搬到哪、做什么程度的改造"，不允许"参考着重新写"。
- v2 仓库仅作**只读参考**（它的 presenter/config/notify 经过 2026-11-11 实测能跑，
  窗口层终版快照 = v1 原样实现）。禁止从 v2 直接拷贝 core/audio/engines 代码
  —— 已确认其中埋雷（例：`dbfs_to_unit` 定义了但全仓零调用，音量条量纲错误）。

### 血泪条款（全部实测，实施者必读，违反任何一条 = 验收不通过）

1. **DPI 感知成败判断**：`SetProcessDpiAwareness` 返回 HRESULT，**0 才是成功**。
   v2 曾把判断写反，进程静默落到 system aware，坐标全漂（裁形错位、拖拽跳变、
   跨屏撕裂的总根源）。v1 的 `win32/dpi.py` 是对的，原样保留。
2. **WebView2 是 STA COM 对象**：所有 Python→JS 推送必须过**单线程串行队列**
   （v1 的 `_EventPump` 模式）。多线程并发 `evaluate_js` 会互相踩掉推送
   （音量条丢帧、收球动画不触发），甚至 COM 对象与 RCW 分离崩溃。
3. **电平管道量纲**：推给前端的电平必须是 `dbfs_to_unit`（对数映射 + 底噪门）
   的输出。v1 的这条链路是正确的；前端 mapHeight 曲线按 0..1 对数值设计。
4. **pywebview 动态建窗**：官方 `create_window` 动态路径会在返回前触发
   before_show/shown，调用方拿不到 window 对象、回调挂不上（v2 调研结论）。
   v1 的"start 前建窗"流程是验证过的，保持。
5. **禁令（继承 v1/v2 实测）**：透明窗口禁 box-shadow/backdrop-filter/
   color-scheme；透明窗口 resize 毁透明；DASHSCOPE_API_KEY 只从环境变量读。
6. **闭眼重写禁令**：v1 已有的实现**照搬**，哪怕"看着不顺眼"。认为某处该改，
   停下来在报告里提出，不许动手。

---

## 1. 目标架构（继承 v2 spec §1，实现全部锚定 v1 现有代码）

```
qwen3-voice/src/qwen3_voice/
├── __main__.py          入口：装配 core/apps，处理 CLI（--headless）
├── config.py            v2 schema（见 §5；参照 ../voicebar/src/voicebar/config.py）
├── paths.py / logging_setup.py        原样
├── notify.py            NotificationHub（参照 v2 同名文件，今晚实测能跑）
├── core/                语音业务：状态机、引擎编排、注入
│   ├── controller.py    ← 现 core/controller.py 平移 + §3 改造点
│   └── inject/          ← 现 win32/inject.py 等注入相关平移 + 校验强化
├── audio/               原样平移（devices/recorder/noise/recording_store/levels）
├── engines/             ← 现 asr/ 改名（base/engine/local/network/streaming）
├── host/                ★ 显示脏活的唯一居所
│   ├── capsule.py       ← 现 ui/capsule.py 原样搬入（v1 窗口实现 = 今晚 v2 终版的来源）
│   ├── placement.py     ← 现 ui/placement.py 原样搬入
│   ├── webview_tweaks.py ← 现 ui/webview_tweaks.py 原样搬入
│   ├── tray.py          ← 现 ui/tray.py 搬入
│   └── hotkey.py        ← 现 win32/hotkey.py 搬入
├── win32/               纯 ctypes 绑定（api/clipboard/monitors/dpi）
└── apps/                presenter + 页面
    ├── voicebar/        presenter.py（参照 v2 apps/voicebar/presenter.py）+ pages/
    └── panel/           ← 现 ui/panel.py + static/panel.* 迁入
```

与 v2 spec §1 的差异（拍板）：
- **host 不搞声明式 WindowSpec 工厂**。v2 的工厂实现是事故现场；host 层以
  v1 capsule.py 的现有函数为起点，公共接口逐步抽取，每步真机可跑。
- `caret.py` 不迁移（白名单 §4）；companions/qr_scanner 后议，不阻塞。

### 硬规则（继承 v2 spec §1.1，违反 = 验收不通过）

1. `apps/` 禁 import host.win32（或任何 win32/ctypes），只走 host 公开服务。
2. `core/`、`engines/` 不知道 GUI 存在：不 import host、不 import apps。
3. `host/` 不认识 core。
4. presenter 是唯一同时知道 core 和 host 的层，只翻译不判断。
5. 焦点/前台/裁形/透明的知识只允许存在于 host/。
6. 依赖单向：`apps → (core, host)`；`core → engines, audio, inject`；`host → win32`。
7. 任何"声称成功"的操作必须有验证（剪贴板读回、SendInput 投递数核对）。

---

## 2. 通信模型（继承 v2 spec §2）

- 状态流：core `subscribe()` + 快照优先（新订阅者立即收全量快照）。
- 通知流：NotificationHub 纯事件 fire-and-forget。
- 命令：点对点方法调用；命令失败的事实必须同时进通知流。
- 电平流：30fps 独立高频流，presenter 直通前端，不进快照。
- Python→JS：**单入口串行**。v1 的 `_EventPump`/`_call_js` 队列模式保持
  （血泪条款 2）；v2 的 `window.__dispatch(type, payload)` 单入口 + type 常量
  机制可采纳，但串行队列是前提，不许退回直调。

---

## 3. core / engines 平移改造点（继承 v2 spec §8）

- `asr/` → `engines/` 改名，契约不变（本地/网络/流式完全一致）。
- controller：状态机骨架、录音编排、预览装配、双后端互斥切换原样平移；改造仅限：
  - 去掉 error 态（错误走通知，见 §4 白名单）；
  - backend_health 进状态快照；
  - 拒绝/失败走"应答 + 通知"双轨；
  - UI 事件推送职责移交 presenter（controller 只发状态，不再直推 ui）。
- **电平链路一行不许动**：recorder → dbfs_to_unit(底噪门) → 30fps 推送。

---

## 4. 行为变更白名单（重构期间唯一允许的行为变化，均为此前用户拍板项）

| 变更 | 依据 |
|---|---|
| 砍掉悬浮球 follow/pinned 档、胶囊位置三档、贴光标摆位；`caret.py` 与 comtypes 依赖退役 | v2 spec §0.1（用户："没啥用，而且经常定位出问题"） |
| 错误不再是状态机状态：error 态退役，改通知 + 终态停留呈现（迁移表见 v2 spec §3.2） | v2 spec §3.1 |
| 模型加载中按热键 → 弹回动画（v1 现状是无反应） | v2 spec §3.2 |
| config 换 v2 schema（§5），数据不自动迁移，用户手动拷 hotwords*/recordings/ | v2 spec §0.2 |
| 热词前台编辑器继续不存在；全局事件总线等 YAGNI 项继续不做 | v2 spec §14 |

白名单之外的一切行为差异 = 事故，回滚重做。

---

## 5. config schema（v2 spec §10 原样）

```json
{
  "hotkey": "ctrl+alt+space",
  "backend": "local",
  "model": "0.6B",
  "model_root": "D:\\Dev\\Environments\\models",
  "dashscope_model": "qwen3-asr-flash",
  "input_device": null, "device_hints": ["USB"],
  "language": null,
  "hotwords": [],
  "theme": "auto",
  "orb": {"enabled": true, "standby": "resident"},
  "show_preview": true,
  "save_recordings": true, "max_duration": 120,
  "restore_clipboard": true, "trailing_space": true
}
```

（`orb.enabled` 默认 true：悬浮球常驻，对齐 v1 现状。）数据布局全进 `data/`（portable，
见根 AGENTS.md）。

---

## 6. 执行方式与最终验收

一次性直接执行，**不分阶段、不设验收门槛**。在 `refactor/v2-arch` 分支上做，
master 随时可跑、随时可退；小步 commit。建议按依赖顺序推进（仅是顺序建议，
不是交付门槛）：骨架（notify / config / CLI）→ core/engines/audio 无头化 →
host 落位（纯搬家，函数体一行不动，只改 import）→ presenter 落地 →
panel/托盘 → 白名单收尾（caret/follow/位置三档退役、错误语义迁移）。

**不写测试**（用户拍板）：预先测试一律不写；出问题时为排查该问题写临时测试，
修完即删，不留常驻测试套件。

做完后验收 = 真机对照清单逐项与重构前一致：
点球录音 / 热键录音 / 拖球（含跨屏来回）/ 松手位置记忆与重启恢复 / 收球动画 /
取消回退 / 音量条高度与节奏 / 主题三档 / 托盘着色 / 透明无灰底无残影。
白名单项按 §4 生效：被砍功能确实消失且无残留代码、错误呈现符合 v2 spec §3.2
迁移表、模型加载中按热键有弹回动画。

---

## 7. 明确不做（YAGNI，继承 v2 spec §14）

全局事件总线；跨进程事件流；热词前台编辑器；原生绘制渲染层；配置热重载；多语言 UI；
host 声明式窗口工厂（本 spec §1 已拍板不搞）；常驻测试套件（排查问题的临时测试除外）。
