# qwen3-voice

Windows 全局语音输入:本地 Qwen3-ASR,按热键说话 → 文字注入光标处。
架构与用法见 `README.md`;前后端面板协议见 `src/qwen3_voice/ui/BRIDGE.md`。
portable 约定(数据只进 `data/`)见上级目录 `../AGENTS.md`。

## 约定

- 词表以用户在 `data/hotwords_user.tsv` 的编辑为准(可指定必收/拉黑),
  自动打分只是排序建议 —— 这个分工是用户明确认可的,别改回"自动筛选"。
- 词表形态还在演化(用户对多词表等有想法但需求未定),
  动手前先问用户,不要预设方案。
