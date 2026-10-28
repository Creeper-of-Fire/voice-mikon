# coding=utf-8
"""术语表管理器 —— 独立程序,与主程序解耦。

三个子命令,一条流水线:

    scan    扫描聊天记录 → data/hotwords_candidates.tsv
            **全量候选,零筛选** —— 每个词都带分数与示例,判断权在你。

    build   候选 + 你的手动编辑(data/hotwords_user.tsv) → data/hotwords.txt
            你通过编辑文件加分(``+N``)/拉黑(``!``)/强制收录(``=``),
            分数只是排序建议,不替你做决定。

    show    只看产出的最终词表。

主程序(qwen3-voice)**每次识别时**实时读 data/hotwords.txt,
所以你改完词表立刻生效,不用重启。

## 打分怎么算(全部公开在 candidates 文件里)

拉丁 token:
    base = 出现次数
    +15  紧邻汉字(口述语境 —— 用户用中文夹着说的英文才是真术语)
    +5   含大写/驼峰/分隔符(专有名词形态)
    -10  PowerShell cmdlet 形态(Get-ChildItem 这种 Verb-Noun)
    -15  纯版本号/纯数字
    -20  URL/路径/包名/文件名形态

中文(jieba 分词,以词为单位):
    base = 出现次数
    停用词(的了是在……)不进候选
    其余原样 —— 你反复用的表达(如"固有")频次自然高。

你的编辑叠加在分数上:
    +N   加 N 分
    !    拉黑(build 时排除)
    =    强制收录(无视分数与 --top)
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections import Counter
from pathlib import Path

# ---------------------------------------------------------------------------
# 路径(portable:跟项目走,尊重 QWEN3_VOICE_HOME,不写死绝对路径)

_SCRIPT_DIR = Path(__file__).resolve().parent          # <项目>/scripts
_PROJECT_DIR = _SCRIPT_DIR.parent                       # <项目>

def _data_dir() -> Path:
    override = os.environ.get("QWEN3_VOICE_HOME")
    return Path(override).expanduser() if override else _PROJECT_DIR / "data"

DATA_DIR = _data_dir()
CANDIDATES = DATA_DIR / "hotwords_candidates.tsv"
USER_EDITS = DATA_DIR / "hotwords_user.tsv"
OUTPUT = DATA_DIR / "hotwords.txt"

SESSIONS_DEFAULT = Path.home() / ".minimax" / "v2" / "sessions"

# ---------------------------------------------------------------------------
# 停用词/降权表

# 中文虚词与通用高频字组 —— 它们频次高但绝不是"用语习惯"
HANZI_STOP = set("""
的 了 是 在 我 你 他 她 它 们 这 那 也 都 就 还 和 跟 与 或 但 而 因为 所以 如果 虽然
一个 一些 什么 怎么 为什么 这个 那个 这样 那样 的话 就是 不是 没有 可以 应该 还是
然后 然后 其实 确实 已经 现在 可能 一下 一点 有点 非常 特别 比较 而已 出来 进去
我们 你们 他们 自己 这里 那里 哪里 时候 地方 东西 事情 问题 情况 部分 一下
不 对 好 行 嗯 哦 啊 吧 吗 呢 呀 了 吧
""".split())

# 单字停用(高频字,单独无意义) —— 分词模式下单字本来就不收,此表已废弃
# (保留定义以防回退到 n-gram 方案时复用)

# m-code / agent 内部机制词(要证据在候选里可见,只是降权)
INTERNAL_WORDS = {
    "worker_session", "update_goal", "commit-drafter", "role_bot",
    "modelsDirs", "on_ready", "canonical_name", "get_snapshot",
    "move_window", "close_panel", "quit_app", "toggle_recording",
    "switch_model", "setLevels", "setElapsed", "setStatus", "setResult",
    "setPreview", "setModel", "setHotkey", "setState", "tool_search",
    "mcp_invoke", "self_reminder", "todowrite",
}

CODE_EXTS = (
    ".md", ".py", ".ps1", ".json", ".js", ".ts", ".tsx", ".jsx", ".html",
    ".css", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".txt", ".log", ".bat",
    ".cmd", ".sh", ".rs", ".go", ".java", ".c", ".cpp", ".h", ".csv", ".xml",
    ".lock", ".svg", ".png", ".jpg", ".zip", ".asar", ".dll", ".exe", ".pyc",
)

# ---------------------------------------------------------------------------
# 正则

INJECT_BLOCKS = [
    re.compile(r"<system-reminder>.*?</system-reminder>", re.S),
    re.compile(r"<questionnaire-response>.*?</questionnaire-response>", re.S),
    re.compile(r"<background-task-finished>.*?</background-task-finished>", re.S),
    re.compile(r"<background-task-completion-reminder>.*?</background-task-completion-reminder>", re.S),
    re.compile(r"\[request \d+/\d+\]", re.S),
    re.compile(r"\[System CronTask\].*", re.S),
]
CODE_BLOCK = re.compile(r"```.*?```", re.S)

URL_RE = re.compile(r"https?://\S+")
UUID_RE = re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")
HEX_RE = re.compile(r"\b[0-9a-fA-F]{12,}\b")
WINPATH_RE = re.compile(r"[A-Za-z]:\\[^\s\"]*")
PKG_RE = re.compile(r"\b[a-zA-Z_][\w]*(?:\.[a-zA-Z_][\w]*){2,}\b")

CJK_RE = re.compile(r"[\u4e00-\u9fff]")
TOKEN_RE = re.compile(r"[A-Za-z][A-Za-z0-9]*(?:[._\-+/][A-Za-z0-9]+)*")
HANZI_RUN_RE = re.compile(r"[\u4e00-\u9fff]+")

# PowerShell cmdlet: 大写开头-大写开头 双词
CMDLET_RE = re.compile(r"^[A-Z][a-z]+-[A-Z][a-z]+$")
VERSION_RE = re.compile(r"^v?\d+(\.\d+)*$")


# ---------------------------------------------------------------------------
# scan:聊天记录 → 候选

def iter_user_texts(sessions_root: Path):
    """逐行流式产出用户消息正文(跳过 .history-mutation 备份目录)。"""
    for f in sessions_root.rglob("messages.jsonl"):
        if ".history-mutation" in f.parts:
            continue
        try:
            with f.open("r", encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    if '"user"' not in line:
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


def split_code(text: str) -> tuple[str, str]:
    """把正文拆成(叙述部分, 代码部分) —— 代码里的词降权,叙述里的词才是用语。"""
    code_parts = []
    prose = text
    for rx in INJECT_BLOCKS:
        prose = rx.sub(" ", prose)
    def _stash(m):
        code_parts.append(m.group(0))
        return " \x00CODE%d\x00 " % (len(code_parts) - 1)
    prose = CODE_BLOCK.sub(_stash, prose)
    return prose, "\n".join(code_parts)


def score_latin(tok: str, near_cjk: bool, in_code: bool) -> int:
    s = 0
    if near_cjk:
        s += 15          # 口述语境:中文夹着的英文
    if any(c.isupper() for c in tok) or any(c in tok for c in "._-"):
        s += 5           # 专有名词形态
    if CMDLET_RE.match(tok):
        s -= 10          # PowerShell cmdlet
    if VERSION_RE.match(tok) or tok.isdigit():
        s -= 15          # 版本号/数字
    if tok.lower().endswith(CODE_EXTS):
        s -= 20          # 文件名
    if tok.lower() in INTERNAL_WORDS:
        s -= 25          # agent 内部机制词
    if in_code:
        s -= 12          # 出现在代码块里(叙述里出现过的会再叠加)
    return s


def scan(sessions_root: Path, out: Path) -> int:
    try:
        import jieba
    except ImportError:
        print("scan 需要分词器 jieba(中文部分),装一下:", file=sys.stderr)
        print("  uv pip install --python <项目解释器> jieba", file=sys.stderr)
        return 1

    lat_freq: Counter = Counter()          # (token, near_cjk, in_code) -> n
    han_freq: Counter = Counter()          # 中文词 -> n
    samples: dict[str, str] = {}
    n_msgs = 0

    for raw in iter_user_texts(sessions_root):
        if not raw:
            continue
        n_msgs += 1
        prose, code = split_code(raw)

        # --- 拉丁 token(叙述 + 代码分开记) ---
        for src_text, in_code in ((prose, False), (code, True)):
            t = URL_RE.sub(" ", src_text)
            t = UUID_RE.sub(" ", t)
            t = WINPATH_RE.sub(" ", t)
            for m in TOKEN_RE.finditer(t):
                tok = m.group(0)
                if len(tok) < 2 or len(tok) > 40:
                    continue
                before = t[m.start() - 1] if m.start() > 0 else ""
                after = t[m.end()] if m.end() < len(t) else ""
                near = bool(before and CJK_RE.match(before)) or bool(after and CJK_RE.match(after))
                key = (tok, near, in_code)
                lat_freq[key] += 1
                if tok not in samples and near and not in_code:
                    ctx = t[max(0, m.start() - 12):m.end() + 12].replace("\n", " ")
                    samples[tok] = ctx.strip()

        # --- 中文分词(jieba,只切叙述部分) ---
        # 为什么用分词器而不是 n-gram:n-gram 会产生"我还/不知/道怎"这类
        # 跨词碎片段;分词直接以词为单位,"固有/歧义/采样率"都是完整词。
        # (试过把 Qwen tokenizer 词表喂给 jieba 做增强 —— 负收益:
        #  专业术语在 BPE 里本来就是拆开的多 token,词表里全是
        #  "引起的/就是为了"这种通用组合,反而切得更差,放弃。)
        for w in jieba.cut(prose):
            w = w.strip()
            if len(w) >= 2 and all(CJK_RE.match(ch) for ch in w):
                han_freq[w] += 1

    # --- 汇总拉丁 token:同 token 的不同 (near, in_code) 合并计分 ---
    rows: list[tuple[int, int, str, str, str]] = []   # score, freq, kind, token, sample
    tok_total: Counter = Counter()
    tok_score: dict[str, int] = {}
    for (tok, near, in_code), n in lat_freq.items():
        tok_total[tok] += n
        tok_score[tok] = tok_score.get(tok, 0) + score_latin(tok, near, in_code) * min(n, 20)

    for tok, total in tok_total.items():
        base = tok_score.get(tok, 0) + min(total, 30)
        rows.append((base, total, "latin", tok, samples.get(tok, "")))

    # --- 中文词计分 ---
    # 分词器已经以词为单位,频次即分数(封顶 60,避免超高频通用词压榜)。
    # 通用词("其他/推荐/另外")排前是**正常现象** —— 它们确实是词,
    # 只是模型不会听错;要不要拉黑由你在 user.tsv 里定夺,这里不预筛。
    for g, n in han_freq.items():
        if n < 2 or g in HANZI_STOP:
            continue
        rows.append((min(n, 60), n, "hanzi", g, ""))

    rows.sort(key=lambda r: -r[0])

    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8", newline="\n") as f:
        f.write("# 候选词表(scan 产出) —— 全量,零筛选,判断权在你\n")
        f.write(f"# 来源: {sessions_root}\n")
        f.write(f"# 用户消息: {n_msgs} 条;拉丁 token {len(tok_total)} 个,中文词 {len(han_freq)} 个\n")
        f.write("# 列: 分数 <tab> 频次 <tab> 类型 <tab> 词 <tab> 示例上下文\n")
        f.write("#\n")
        f.write("# 下一步: 编辑 data/hotwords_user.tsv 打分(见 hotwords_manager.py 顶部说明),\n")
        f.write("# 然后 python scripts/hotwords_manager.py build\n")
        f.write("#\n")
        for score, freq, kind, tok, sample in rows:
            if score <= 0 and freq < 2:
                continue   # 只隐藏「单次出现且负分」的纯噪声;其余全保留
            f.write(f"{score}\t{freq}\t{kind}\t{tok}\t{sample}\n")

    print(f"scan 完成: {len(rows)} 条候选 → {out}")
    print(f"  用户消息 {n_msgs} 条")
    return 0


# ---------------------------------------------------------------------------
# 用户编辑解析

def parse_user_edits(path: Path) -> dict[str, tuple[str, int, str]]:
    """读用户编辑文件 → {词: (动作, 分值, 备注)}。

    动作: '+'(加分) / '!'(拉黑) / '='(强制收录)
    """
    edits: dict[str, tuple[str, int, str]] = {}
    if not path.is_file():
        return edits
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.rstrip("\n")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        parts = [p.strip() for p in line.split("\t")]
        if len(parts) < 2:
            # 允许空格分隔的兜底
            parts = line.split(None, 2)
            parts = [p.strip() for p in parts]
        if len(parts) < 2:
            continue
        tok, action = parts[0], parts[1]
        note = parts[2] if len(parts) > 2 else ""
        if action == "!" or action == "=":
            edits[tok] = (action, 0, note)
        elif action.startswith("+") or action.startswith("-"):
            try:
                val = int(action)
            except ValueError:
                continue
            edits[tok] = ("+", val, note)
    return edits


# ---------------------------------------------------------------------------
# build:候选 + 用户编辑 → 最终词表

def build(cand_path: Path, user_path: Path, out: Path, top: int) -> int:
    if not cand_path.is_file():
        print(f"找不到候选文件 {cand_path},先跑 scan", file=sys.stderr)
        return 1

    # 读候选
    rows: list[tuple[int, int, str, str]] = []
    for line in cand_path.read_text(encoding="utf-8").splitlines():
        if line.startswith("#") or not line.strip():
            continue
        parts = line.split("\t")
        if len(parts) < 4:
            continue
        try:
            score = int(parts[0])
            freq = int(parts[1])
        except ValueError:
            continue
        rows.append((score, freq, parts[2], parts[3]))

    edits = parse_user_edits(user_path)

    merged: dict[str, int] = {}
    for score, freq, kind, tok in rows:
        merged[tok] = score

    forced: list[str] = []
    for tok, (action, val, _note) in edits.items():
        if action == "!":
            merged.pop(tok, None)
        elif action == "=":
            forced.append(tok)
        else:
            merged[tok] = merged.get(tok, 0) + val

    ranked = sorted(merged.items(), key=lambda kv: -kv[1])
    positive = [tok for tok, s in ranked if s > 0][: max(0, top)]
    final_list = list(dict.fromkeys(forced + positive))

    out.parent.mkdir(parents=True, exist_ok=True)
    with out.open("w", encoding="utf-8", newline="\n") as f:
        f.write("# 最终术语表(build 产出) —— 每次识别时实时读取,改完立即生效\n")
        f.write(f"# 收录 {len(final_list)} 条(强制 {len(forced)} + 按分 {len(positive)};拉黑 {sum(1 for a,_,_ in edits.values() if a=='!')} 条)\n")
        f.write("# 想改:编辑 data/hotwords_user.tsv 再 build;或直接手改本文件(下次 build 会覆盖)\n")
        f.write("#\n")
        f.write("# 强制收录(=)\n")
        for tok in forced:
            f.write(f"{tok}\n")
        f.write("# 按分数收录(前 %d)\n" % len(positive))
        for tok in positive:
            f.write(f"{tok}\n")

    print(f"build 完成: {len(final_list)} 条 → {out}")
    print(f"  强制收录 {len(forced)} 条,按分数 {len(positive)} 条,"
          f"拉黑 {sum(1 for a, _, _ in edits.values() if a == '!')} 条")
    return 0


# ---------------------------------------------------------------------------
# show

def show(out: Path) -> int:
    if not out.is_file():
        print(f"还没有产出文件 {out},先 scan + build", file=sys.stderr)
        return 1
    print(out.read_text(encoding="utf-8"))
    return 0


# ---------------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description="术语表管理器(scan/build/show)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p_scan = sub.add_parser("scan", help="扫描聊天记录 → 全量候选")
    p_scan.add_argument("--sessions", type=Path, default=SESSIONS_DEFAULT)
    p_scan.add_argument("--out", type=Path, default=CANDIDATES)

    p_build = sub.add_parser("build", help="候选 + 你的编辑 → 最终词表")
    p_build.add_argument("--candidates", type=Path, default=CANDIDATES)
    p_build.add_argument("--user", type=Path, default=USER_EDITS)
    p_build.add_argument("--out", type=Path, default=OUTPUT)
    p_build.add_argument("--top", type=int, default=120,
                         help="按分数收录的上限(强制收录不受限;默认 120)")

    p_show = sub.add_parser("show", help="查看最终词表")
    p_show.add_argument("--out", type=Path, default=OUTPUT)

    args = ap.parse_args()
    if args.cmd == "scan":
        return scan(args.sessions, args.out)
    if args.cmd == "build":
        return build(args.candidates, args.user, args.out, args.top)
    if args.cmd == "show":
        return show(args.out)
    return 2


if __name__ == "__main__":
    sys.exit(main())
