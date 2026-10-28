# coding=utf-8
"""从 MiniMax Code 的聊天记录里提取中文语音识别术语表(候选)。

用法:
    python scripts/build_hotwords.py                 # 输出到 stdout
    python scripts/build_hotwords.py -o out.txt      # 输出到文件

只读,不改任何东西。

## 提取逻辑(每一步都可解释)

1. 扫 ``%LOCALAPPDATA%`` 之外的会话目录(见 ``--sessions``),逐行读
   ``messages.jsonl``,只取 ``role == "user"`` 的正文。
   —— 用户**手打**的内容才是术语的真实形态;assistant 的话是模型自己的词汇。
2. 剥掉 harness 注入的块(``<system-reminder>`` 等),它们不是用户说的。
3. 只保留**紧邻汉字**的 token —— 这是最有效的一刀过滤:
   代码标识符、PowerShell cmdlet 通常两边都不是中文。
4. 形态过滤:含大写字母,或含 ``.`` ``_`` ``-``。
   纯小写技术词(如 pywebview)单列出来,交给人工补。
5. 噪声排除:单字母、纯数字、UUID、长 hex、URL、路径、包名、
   已知代码扩展名、m-code 内部机制名。
6. 大小写变体归一:按出现次数投票出规范写法。

输出按频次排序,每行 ``频次\\t词``,便于人工审核。
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

# ---- m-code / agent 内部机制词:用户口述时不会这么说 ----
INTERNAL_NOISE = {
    "worker_session", "update_goal", "user-provided-context", "commit-drafter",
    "role_bot", "modelsDirs", "on_ready", "canonical_name", "tool_call",
    "tool_result", "get_snapshot", "move_window", "close_panel", "quit_app",
    "toggle_recording", "switch_model", "setLevels", "setElapsed", "setStatus",
    "setResult", "setPreview", "setModel", "setHotkey", "setState",
}

# 代码/配置文件的扩展名(不是术语)
CODE_EXTS = {
    ".md", ".py", ".ps1", ".json", ".js", ".ts", ".tsx", ".jsx", ".html",
    ".css", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".txt", ".log", ".bat",
    ".cmd", ".sh", ".rs", ".go", ".java", ".c", ".cpp", ".h", ".csv", ".xml",
    ".lock", ".svg", ".png", ".jpg", ".zip", ".asar", ".dll", ".exe",
}

# 需要剥掉的 harness 注入块
INJECT_BLOCKS = [
    re.compile(r"<system-reminder>.*?</system-reminder>", re.S),
    re.compile(r"<questionnaire-response>.*?</questionnaire-response>", re.S),
    re.compile(r"<background-task-finished>.*?</background-task-finished>", re.S),
    re.compile(r"<background-task-completion-reminder>.*?</background-task-completion-reminder>", re.S),
    re.compile(r"```.*?```", re.S),
]

URL_RE = re.compile(r"https?://\S+")
UUID_RE = re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-"
                     r"[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")
HEX_RE = re.compile(r"\b[0-9a-fA-F]{12,}\b")
WINPATH_RE = re.compile(r"[A-Za-z]:\\[^\s]*")
PKG_RE = re.compile(r"\b[a-zA-Z_][\w]*(?:\.[a-zA-Z_][\w]*){2,}\b")

CJK_RE = re.compile(r"[\u4e00-\u9fff]")
TOKEN_RE = re.compile(r"[A-Za-z][A-Za-z0-9]*(?:[._\-][A-Za-z0-9]+)*")


def clean_text(text: str) -> str:
    """剥掉注入块与明显的机器内容。"""
    for rx in INJECT_BLOCKS:
        text = rx.sub(" ", text)
    text = URL_RE.sub(" ", text)
    text = UUID_RE.sub(" ", text)
    text = HEX_RE.sub(" ", text)
    text = WINPATH_RE.sub(" ", text)
    text = PKG_RE.sub(" ", text)
    return text


def is_noise(token: str) -> bool:
    """这个词值不值得当术语。"""
    if len(token) < 2:
        return True
    if token.lower() in INTERNAL_NOISE:
        return True
    low = token.lower()
    if any(low.endswith(ext) for ext in CODE_EXTS):
        return True
    # 纯数字 / 单字母
    if token.isdigit() or (len(token) == 1 and token.isalpha()):
        return True
    # 全小写且没有分隔符 -> 可能是普通英文单词,单独处理
    return False


def has_marker(token: str) -> bool:
    """形态标记:含大写,或含分隔符。"""
    return any(c.isupper() for c in token) or any(c in token for c in "._-")


def extract_from_text(text: str, strict_counts: Counter, loose_counts: Counter,
                      loose_forms: dict) -> None:
    """从一段用户文本里抽词。

    strict: 必须含大写/分隔符的词
    loose:  额外收录的纯小写长词(紧邻中文时)
    """
    text = clean_text(text)
    for m in TOKEN_RE.finditer(text):
        tok = m.group(0)
        if is_noise(tok):
            continue
        before = text[m.start() - 1] if m.start() > 0 else ""
        after = text[m.end()] if m.end() < len(text) else ""
        near_cjk = bool(before and CJK_RE.match(before)) or bool(after and CJK_RE.match(after))
        if not near_cjk:
            continue
        if has_marker(tok):
            strict_counts[tok] += 1
            loose_counts[tok] += 1
            loose_forms.setdefault(tok.lower(), Counter())[tok] += 1
        else:
            loose_counts[tok] += 1
            loose_forms.setdefault(tok.lower(), Counter())[tok] += 1


def canonicalize(counts: Counter) -> list[tuple[str, int]]:
    """大小写变体归一:同一个小写形式取出现次数最多的写法。"""
    merged: dict[str, Counter] = defaultdict(Counter)
    for tok, n in counts.items():
        merged[tok.lower()][tok] += n
    out = []
    for low, forms in merged.items():
        best, total = forms.most_common(1)[0]
        out.append((best, total))
    return out


def iter_user_texts(sessions_root: Path):
    """逐行流式扫所有 messages.jsonl,产出用户消息正文。"""
    files = list(sessions_root.rglob("messages.jsonl"))
    for f in files:
        try:
            with f.open("r", encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    line = line.strip()
                    if not line or '"role":"user"' not in line:
                        continue
                    try:
                        obj = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    msg = obj.get("message") or {}
                    if msg.get("role") != "user":
                        continue
                    content = msg.get("content")
                    if isinstance(content, str):
                        yield content
                    elif isinstance(content, list):
                        for blk in content:
                            if isinstance(blk, dict) and blk.get("type") == "text":
                                yield blk.get("text") or ""
        except OSError:
            continue


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--sessions", default=None,
                    help="会话根目录(默认自动定位)")
    ap.add_argument("-o", "--output", default=None, help="输出文件(默认 stdout)")
    ap.add_argument("--min-freq", type=int, default=2)
    args = ap.parse_args()

    if args.sessions:
        root = Path(args.sessions)
    else:
        base = Path.home() / ".minimax" / "v2" / "sessions"
        if not base.is_dir():
            base = Path.home() / ".minimax" / "sessions"
        root = base

    if not root.is_dir():
        print(f"找不到会话目录: {root}", file=sys.stderr)
        return 1

    print(f"扫描会话目录: {root}", file=sys.stderr)

    strict: Counter = Counter()
    loose: Counter = Counter()
    loose_forms: dict = defaultdict(Counter)
    n_texts = 0

    for text in iter_user_texts(root):
        n_texts += 1
        if text:
            extract_from_text(text, strict, loose, loose_forms)

    print(f"处理用户消息 {n_texts} 条", file=sys.stderr)

    strict_list = canonicalize(strict)
    loose_list = canonicalize(loose)

    lines = []
    lines.append("# 术语表候选(自动提取,需人工审核)")
    lines.append(f"# 来源: {root}")
    lines.append(f"# 用户消息 {n_texts} 条")
    lines.append("#")
    lines.append("# [严格档] 含大写或分隔符,且紧邻汉字 —— 优先人工看这一档")
    for tok, n in sorted(strict_list, key=lambda x: -x[1]):
        if n >= args.min_freq:
            lines.append(f"{n}\t{tok}")
    lines.append("")
    lines.append("# [宽松档] 额外包含纯小写词 —— 多数是普通英文单词,少量是技术词")
    strict_lower = {t.lower() for t, _ in strict_list}
    for tok, n in sorted(loose_list, key=lambda x: -x[1]):
        if n >= args.min_freq and tok.lower() not in strict_lower:
            lines.append(f"{n}\t{tok}")

    text = "\n".join(lines) + "\n"
    if args.output:
        Path(args.output).write_text(text, encoding="utf-8")
        print(f"已写出: {args.output}", file=sys.stderr)
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
