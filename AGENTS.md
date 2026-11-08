# qwen3-voice

Windows 全局语音输入:本地 Qwen3-ASR,按热键说话 → 文字注入光标处。
架构与用法见 `README.md`;前后端面板协议见 `src/qwen3_voice/ui/BRIDGE.md`。
portable 约定(数据只进 `data/`)见上级目录 `../AGENTS.md`。

## 约定

- **识别前不加本地预判**:录到的音频**整段**丢给模型,不做静音门、时长门、
  音量门这类本地阈值判定 —— 该不该听、听成什么,由模型说。用户识别错了
  自己会删,不需要程序替他决定"这段算不算说话"。
  (踩过的坑:曾用"整段平均 RMS < silence_rms"判静音报「没有检测到声音」,
  静音占比越高越容易被误杀 —— 说得越久反而越容易中招;同一处还有
  "录音太短"那道门。两道门都已删除,别再加回来。)
- 预览层的 VAD(`asr/streaming.py`)是**断句**用的,不是判定用 ——
  它只影响实时预览的出字节奏,最终结果永远来自整段识别。它可以留着调。
- **测试只为"确实要实现的功能"保留**(格式转换、算法、协议编码这类
  有明确对错规范的,现存的 `tests/test_audio|config|engine|engines|hotwords|network_engine.py`),
  **不用测试锚定行为** —— 状态机流转、UI 表现、"注入失败带原文"这类
  从代码一看就懂的行为不写测试。新功能默认**不加测试**,快速失败,
  真实使用就是端到端验证(用户是唯一用户,天天在用)。
  (不应该在改对行为后的第一件事反而是改测试 —— 测试守的不是偶然实现,是用户需求。)
- **剪贴板写入必须带 owner**(`clipboard.py` 的 message-only 窗口):
  `OpenClipboard(NULL)` 写入的话,第三方剪贴板管理器(Ditto 一类)查不到
  来源,只能把识别文本标注成"用户正聚焦的那个应用" —— 带上我们自己的
  owner 窗口,来源标注才归位。别改回 `OpenClipboard(None)`。
  **owner 窗口必须在主线程创建**(`__main__.main` 里 `ensure_owner_window()`):
  它属于创建它的线程,线程退出窗口就没了,而 Windows 在 owner 窗口
  销毁时**连带清空剪贴板** —— 建在录音线程上会让"识别结果留在剪贴板"
  这个兜底直接失效,还会让后续 `OpenClipboard(死句柄)` 全线失败。
  进程退出前要 `release_ownership()`(把所有权转给 NULL,内容留住)。
- **真透明与裁形(2026-11-07 重定型)**:
  - 灰底的根因不在裁形:pywebview 建 hidden 窗口的透明 hack
    (`Opacity=0 → Show() → Hide() → Opacity=1`,winforms 后端的
    ``on_navigation_start``)会**摘掉 WS_EX_LAYERED**,窗口退回
    WinForms 默认灰底 `#F0F0F0`。修法见 ``capsule.py`` 的
    `_ensure_transparent_surface`:shown 事件(正好落在 hack 之后)
    补挂 layered + force 重传透明 surface —— 顺序必须先挂 layered
    再传 surface,反了 UpdateLayeredWindow 必然失败。修完之后
    WebView2 透明的地方透出的是桌面,不再是灰底。
  - 裁形:胶囊与球**统一按矩形**裁,不再按"控制器状态猜形态"分圆/
    椭圆 —— 状态切换和形状变化不是同一拍,收回动画期间状态先切、
    形状后变,会把长条胶囊裁成怪椭圆(实测)。矩形多出的四角是
    "看不见但能点"的边,有鼠标穿透兜着,不影响观感。文字条/浮层
    仍按圆角矩形裁(14px/12px)。`api.py` 的 `ellipses` 形参已无
    调用方(退役中的死代码)。
  - 浮层(右键菜单/提示)必须**单独上报一块**(`overlay`),不能并进球
    的包围盒 —— 并进去就是"球到菜单"的大矩形,中间全变成穿透盲区,
    菜单还会被裁断。
  - 别再去"重设 WebView2 透明底色"治灰底 —— 根因是 pywebview 摘了
    layered,不是 WebView2 丢透明。
- **HTML 的 `hidden` 属性会被作者样式的 `display` 架空**:UA 样式表的
  `[hidden]{display:none}` 优先级低于作者的 `.x{display:flex}` —— 元素
  照样占布局(只是 `opacity:0` 看不见),实测把命中区包围盒撑大一大块,
  留下一大片能点但看不见的空边。给弹层加 `display` 时必须同时写
  `.x[hidden]{display:none}`。
- **附属工具放 `companions/`,架构彻底独立**(用户拍板,现有
  `companions/qr_scanner/` 二维码扫描):与主包**零 import**(双向都不
  认识对方);主程序只经 `__main__.make_qr_scanner_launcher` 用
  subprocess 拉起
  独立进程 —— 不等它、不接管它、它崩了不相干。工具 UI 用自己的技术栈
  (tkinter),**不碰 pywebview**。依赖进主项目 pyproject 的可选组
  (`[project.optional-dependencies]`),共用主程序同一个 venv。
  往球菜单加新功能时按这个模式走:菜单按钮 + `JsApi` 桥接方法 +
  `set_xxx` 注入回调,工具本体进 companions。

## 词表
- 词表以用户在 `data/hotwords_user.tsv` 的编辑为准(可指定必收/拉黑),
  自动打分只是排序建议 —— 这个分工是用户明确认可的,别改回"自动筛选"。
- 词表形态还在演化(用户对多词表等有想法但需求未定),
  动手前先问用户,不要预设方案。
