# coding=utf-8
"""无头回放工具:拿**真实录音**复现"预览零出字"。

用法::

    python scripts/replay_preview.py data/recordings/20261029-164630_31s.wav
    python scripts/replay_preview.py <wav> --fast        # 默认:不按真实节奏 sleep
    python scripts/replay_preview.py <wav> --realtime    # 按真实时间推进(复现节奏)
    python scripts/replay_preview.py <wav> --verbose     # 逐帧打印 RMS 和静音判定

**不加载 UI、不占麦克风** —— 只读 WAV 文件,喂给真实的分句引擎。

为什么要有这个:真机实测 31.3 秒说话录音**预览零出字**
(可最终整段识别正常)。日志分不清是哪一环坏了 ——
下面四种情况在日志上都可能长得一样:

    1) 帧没进来        —— 音频根本没喂进引擎
    2) 全判有声        —— VAD 断不出句,一句都不会切
    3) 断句了识别为空  —— 切出来了,但模型没认出东西
    4) partial 没出    —— 认出字了,但没推给界面

这个工具把每一环都量化打印出来,对着真实录音一眼就能定位。
帧的切法跟引擎**完全一致**(直接钩在引擎的 ``_process_frame`` 上),
所以统计出来的"有声帧/静音帧"就是引擎真正看到的。
"""

from __future__ import annotations

import argparse
import logging
import sys
import time
import wave
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

# 让脚本能直接跑(不依赖包被装进 site-packages):把 src/ 加进 sys.path
_SCRIPT_DIR = Path(__file__).resolve().parent
_PROJECT_DIR = _SCRIPT_DIR.parent
_SRC_DIR = _PROJECT_DIR / "src"
if str(_SRC_DIR) not in sys.path:
    sys.path.insert(0, str(_SRC_DIR))

from qwen3_voice.asr.engine import ASREngine
from qwen3_voice.asr.streaming import (
    SENTENCE_MAX_SECONDS,
    SENTENCE_MIN_SECONDS,
    SENTENCE_SILENCE_SECONDS,
    VAD_FRAME_MS,
    VAD_SILENCE_RMS,
    SentencePreviewEngine,
)
from qwen3_voice.audio.recorder import RECORDER_BLOCK_SIZE
from qwen3_voice.config import load_config
from qwen3_voice.logging_setup import get_logger

log = get_logger("replay")


# ---------------------------------------------------------------------------
# WAV 读写(只用标准库 wave,不新增依赖)


def read_wav(path: Path | str) -> tuple[np.ndarray, int]:
    """读 WAV → (int16 单声道 numpy, 采样率)。

    只用标准库 ``wave``:这个工具的意义在于"任何人都能跑起来复现",
    没必要为它拖一个 soundfile/scipy 进来。
    """
    path = Path(path)
    with wave.open(str(path), "rb") as wf:
        channels = wf.getnchannels()
        width = wf.getsampwidth()
        rate = wf.getframerate()
        raw = wf.readframes(wf.getnframes())

    if width == 2:
        data = np.frombuffer(raw, dtype="<i2")
    elif width == 1:
        # 8-bit WAV 是无符号的(静默中点 128),转成 int16 要先减中点
        data = (np.frombuffer(raw, dtype=np.uint8).astype(np.int32) - 128) * 256
    elif width == 4:
        data = np.frombuffer(raw, dtype="<i4") >> 16
    elif width == 3:
        b = np.frombuffer(raw, dtype=np.uint8).reshape(-1, 3)
        val = (b[:, 0].astype(np.int32)
               | (b[:, 1].astype(np.int32) << 8)
               | (b[:, 2].astype(np.int32) << 16))
        # 24-bit 是有符号的:最高位为 1 时要补符号位
        val = np.where(val & 0x800000, val - (1 << 24), val)
        data = (val >> 8).astype(np.int16)
    else:
        raise ValueError(f"不支持的 WAV 采样宽度: {width * 8} bit")

    if channels > 1:
        # 多声道混成单声道:先转 float 再平均,整数直接相加会溢出
        data = data.reshape(-1, channels).astype(np.float32).mean(axis=1).astype(np.int16)

    return np.ascontiguousarray(data, dtype=np.int16), int(rate)


# ---------------------------------------------------------------------------
# 诊断用的数据结构


@dataclass
class FrameInfo:
    """一个分析帧:引擎真正看到的一帧。"""

    index: int
    samples: int
    rms: float
    silent: bool


@dataclass
class Segment:
    """一次断句(切出去的一句话)。"""

    reason: str = ""            # 静音断句 / 超长强切 / 收尾
    total_samples: int = 0      # 这一句累积的样本数(含句尾静音)
    speech_samples: int = 0     # 其中"有声"的样本数
    samples: int = 0            # 真正送去识别的样本数(裁掉句尾静音后)
    text: str = ""              # 识别文本(空 = 没认出东西)
    recognized: bool = False    # 有没有真的调用过识别
    error: str = ""             # 识别抛的异常
    cost: float = 0.0           # 整个断句流程的耗时
    recognize_cost: float = 0.0  # 识别本身的耗时


@dataclass
class ReplayReport:
    """回放的完整结果。"""

    path: str = ""
    sample_rate: int = 16000
    samples: int = 0
    duration: float = 0.0
    blocks: int = 0
    block_size: int = RECORDER_BLOCK_SIZE
    realtime: bool = False
    frames: list[FrameInfo] = field(default_factory=list)
    segments: list[Segment] = field(default_factory=list)
    partials: list[tuple[float, str]] = field(default_factory=list)
    sentences: list[str] = field(default_factory=list)
    final_text: str = ""
    elapsed: float = 0.0

    @property
    def voiced_frames(self) -> int:
        return sum(1 for f in self.frames if not f.silent)

    @property
    def silent_frames(self) -> int:
        return sum(1 for f in self.frames if f.silent)

    @property
    def speech_segments(self) -> list[Segment]:
        """含有语音的断句(纯静音的段落不算 —— 它们本来就不是一句话)。"""
        return [s for s in self.segments if s.speech_samples > 0]


# ---------------------------------------------------------------------------
# 带诊断钩子的分句引擎


class DiagnosticPreviewEngine(SentencePreviewEngine):
    """给分句引擎加**只读**的诊断钩子。

    只观察,**不改变**断句逻辑 —— 判定仍然完全由父类做:
      - ``_process_frame`` 钩子:记录每一帧的 RMS 和静音判定
        (父类算完判定后我们自己再算一遍同样的判定,只为了记录)
      - ``_flush`` 钩子:记录每次断句的句长、原因、耗时、文本
      - ``_recognize`` 钩子:记录识别耗时和错误信息

    为什么钩在引擎上而不是在脚本里重算:
    引擎是**按每次 feed 的块**切帧的(块大小和 20ms 帧长不一定整除),
    脚本自己另算一套帧边界,统计出来的"静音帧"就和引擎看到的对不上了。
    """

    def __init__(self, *args, **kwargs):
        super().__init__(*args, **kwargs)
        self.frames: list[FrameInfo] = []
        self.segments: list[Segment] = []
        self._pending: Segment | None = None

    def _process_frame(self, frame: np.ndarray) -> None:
        rms = self._rms(frame)
        self.frames.append(FrameInfo(
            index=len(self.frames),
            samples=len(frame),
            rms=rms,
            silent=rms < self._silence_rms,
        ))
        super()._process_frame(frame)

    def _flush(self, notify: bool = True) -> None:
        # 读的是父类的私有状态 —— 断句原因必须和父类的判断顺序一致:
        # 父类先看"超长强切",再看"静音断句"
        total = self._frame_count
        speech = self._speech_samples
        if total >= self._max_samples:
            reason = "超长强切"
        elif self._silence_run >= self._silence_samples:
            reason = "静音断句"
        else:
            reason = "收尾"

        seg = Segment(reason=reason, total_samples=total,
                      speech_samples=speech)
        self._pending = seg
        t0 = time.monotonic()
        try:
            super()._flush(notify=notify)
        finally:
            seg.cost = time.monotonic() - t0
            self._pending = None
            if total > 0:
                self.segments.append(seg)

    def _recognize(self, audio: np.ndarray) -> str:
        # 这里**内联**了父类的 _recognize:父类把异常吞成空字符串,
        # 而诊断时"识别失败"和"识别为空"必须分开看 —— 前者是模型坏了,
        # 后者是模型觉得这段没内容。行为与父类一致(失败返回空串)。
        seg = self._pending
        t0 = time.monotonic()
        try:
            text = (self._engine.transcribe(audio, self._sample_rate) or "").strip()
        except Exception as e:
            if seg is not None:
                seg.error = str(e)
            log.debug("分句识别失败: %s", e)
            text = ""
        if seg is not None:
            seg.recognized = True
            seg.text = text
            seg.samples = len(audio)
            seg.recognize_cost = time.monotonic() - t0
        return text


# ---------------------------------------------------------------------------
# 回放主体


def replay(audio: np.ndarray, sample_rate: int, asr_engine,
           *, realtime: bool = False,
           block_size: int = RECORDER_BLOCK_SIZE,
           verbose: bool = False) -> ReplayReport:
    """把音频按录音器的真实粒度喂给分句引擎,顺便收集诊断数据。

    ``block_size`` 默认取录音器的采集块大小(``RECORDER_BLOCK_SIZE``):
    引擎是**按 feed 的块**切帧的,粒度不同、断句边界就不同。

    ``realtime`` 按真实时间推进(每块睡到它该到的时间点),
    用来复现真实节奏;``fast`` 不睡,只按块切分。
    VAD 是按帧能量算的,节奏不影响判定,但 partial 的触发时机更快。
    """
    report = ReplayReport(
        sample_rate=int(sample_rate),
        samples=len(audio),
        duration=len(audio) / max(1, sample_rate),
        block_size=block_size,
        realtime=realtime,
    )

    partials: list[tuple[float, str]] = report.partials

    def on_partial(text: str) -> None:
        partials.append((time.monotonic() - t_start, text))

    engine = DiagnosticPreviewEngine(asr_engine, sample_rate, partial=on_partial)

    block_duration = block_size / max(1, sample_rate)
    t_start = time.monotonic()
    index = 0
    for start in range(0, len(audio), block_size):
        chunk = audio[start:start + block_size]
        engine.feed(chunk)
        index += 1
        if realtime:
            # 用**绝对时间表**而不是"睡一个块长":
            # 识别耗时会把循环拖慢,按绝对表睡才能让整轮墙钟时间≈音频时长
            target = t_start + index * block_duration
            delay = target - time.monotonic()
            if delay > 0:
                time.sleep(delay)

    report.blocks = index
    report.final_text = engine.close()
    report.elapsed = time.monotonic() - t_start
    report.frames = engine.frames
    report.segments = engine.segments
    # 已认出的句子 = 识别成功且有文本的断句(顺序和引擎内部一致)
    report.sentences = [s.text for s in report.segments if s.recognized and s.text]

    if verbose:
        for f in report.frames:
            flag = "静音" if f.silent else "有声"
            print(f"  [帧 {f.index:5d}] {f.samples:6d} 样本  rms={f.rms:.5f}  {flag}")

    return report


# ---------------------------------------------------------------------------
# 报告输出


def diagnose(report: ReplayReport) -> list[str]:
    """根据诊断数据给出结论提示。

    对应四种可能的故障:帧没进来 / 全判有声 / 断句了识别空 / partial 没出。
    """
    hints: list[str] = []
    if not report.frames:
        hints.append("✗ 没有任何帧进来 —— 检查喂入链路(音频喂进引擎了吗)")
        return hints

    if report.voiced_frames == 0:
        hints.append("✗ 全部判成静音 —— 音频里没有有效人声(麦克风?增益?)")
        return hints

    if report.voiced_frames == len(report.frames):
        hints.append("✗ 全部判成有声 —— VAD 断不出句,"
                     f"检查底噪(阈值 {VAD_SILENCE_RMS})")
    elif not report.speech_segments:
        hints.append("✗ 有有声帧但一次都没断句 —— 静音段太短或阈值太高,"
                     f"当前要求连续 {SENTENCE_SILENCE_SECONDS}s 静音")

    recognized = [s for s in report.speech_segments if s.recognized and s.text]
    if report.speech_segments and not recognized:
        failed = [s for s in report.speech_segments if s.error]
        if failed:
            hints.append("✗ 断句了但识别全部失败 —— 看下面的错误信息")
        else:
            hints.append("✗ 断句了但识别结果为空 —— 模型觉得这段没内容")

    if recognized and not report.partials:
        hints.append("✗ 认出字了但没有 partial 回调 —— 问题在回调链路,不在识别")

    if recognized and report.partials:
        hints.append(f"✓ 一切正常:{len(recognized)} 句,"
                     f"{len(report.partials)} 次 partial")
    return hints


def format_report(report: ReplayReport) -> str:
    """把诊断结果拼成一段可读文本。"""
    lines: list[str] = []
    lines.append("=" * 60)
    lines.append(f"回放诊断:{report.path}")
    lines.append("=" * 60)
    lines.append(f"音频      : {report.duration:.1f}s @ {report.sample_rate} Hz"
                 f"({report.samples} 样本)")
    lines.append(f"喂入粒度  : {report.block_size} 样本/块"
                 f"({report.block_size / max(1, report.sample_rate) * 1000:.1f}ms),"
                 f"共 {report.blocks} 块")
    lines.append(f"节奏      : {'真实时间(realtime)' if report.realtime else '快速(fast,不 sleep)'}")
    lines.append(f"VAD       : rms < {VAD_SILENCE_RMS} 判静音,"
                 f"连续 {SENTENCE_SILENCE_SECONDS}s 断句,"
                 f"单句 {SENTENCE_MIN_SECONDS}~{SENTENCE_MAX_SECONDS}s,"
                 f"帧长 {VAD_FRAME_MS}ms")

    lines.append("")
    lines.append("--- 帧级 VAD(引擎实际看到的帧)---")
    dropped = len(report.segments) - len(report.speech_segments)
    lines.append(f"总帧数 {len(report.frames)} | 有声帧 {report.voiced_frames} "
                 f"| 静音帧 {report.silent_frames} | 断句 "
                 f"{len(report.speech_segments)} 次(丢弃纯静音段 {dropped} 个)")

    lines.append("")
    lines.append("--- 断句 ---")
    if not report.segments:
        lines.append("(一次都没断句)")
    for i, seg in enumerate(report.segments, 1):
        dur = seg.total_samples / max(1, report.sample_rate)
        speech = seg.speech_samples / max(1, report.sample_rate)
        if seg.speech_samples == 0:
            lines.append(f"[{i}] {seg.reason} | 全是静音,不是一句话(引擎丢掉)")
            continue
        if seg.recognized:
            text = seg.text if seg.text else "(空)"
            lines.append(f"[{i}] {seg.reason} | 句长 {dur:.2f}s(有声 {speech:.2f}s)"
                         f" | 识别 {seg.recognize_cost:.2f}s | 文本: {text}")
        else:
            lines.append(f"[{i}] {seg.reason} | 句长 {dur:.2f}s(有声 {speech:.2f}s)"
                         f" | 未识别(片段太短,被引擎丢掉)")
        if seg.error:
            lines.append(f"      识别错误: {seg.error}")

    lines.append("")
    lines.append("--- partial(累计拼接文本)---")
    if not report.partials:
        lines.append("(没有 partial 回调)")
    for i, (t, text) in enumerate(report.partials, 1):
        lines.append(f"[{i}] {t:.1f}s {text}")

    lines.append("")
    lines.append("--- 汇总 ---")
    lines.append(f"句子数    : {len(report.sentences)}")
    durations = []
    for seg in report.segments:
        if seg.recognized and seg.text:
            durations.append(seg.total_samples / max(1, report.sample_rate))
    lines.append("各句时长  : " + (" / ".join(f"{d:.2f}s" for d in durations) or "-"))
    lines.append(f"整轮耗时  : {report.elapsed:.1f}s")
    lines.append(f"最终文本  : {report.final_text or '(空)'}")

    lines.append("")
    lines.append("--- 诊断 ---")
    for hint in diagnose(report):
        lines.append(hint)

    return "\n".join(lines)


# ---------------------------------------------------------------------------
# 命令行


def build_asr_engine(config) -> ASREngine:
    """按主程序的方式造识别引擎。

    模型参数读 ``data/config.json``,和主程序**完全一致**
    (同一套模型、同一套热词、同一套语种设置)——
    不一致的话,回放出来的结果没法代表真机。
    """
    return ASREngine(
        model_root=config.model_root,
        model_tag=config.model,
        language=config.language,
        hotwords=config.hotwords,
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="无头回放:拿真实录音复现预览零出字")
    parser.add_argument("wav", type=Path, help="要回放的 WAV 文件")
    pace = parser.add_mutually_exclusive_group()
    pace.add_argument("--realtime", action="store_true",
                      help="按真实时间推进(复现真实节奏,会等)")
    pace.add_argument("--fast", action="store_true",
                      help="不 sleep,只按块切分(默认)")
    parser.add_argument("--verbose", action="store_true",
                        help="逐帧打印 RMS 和静音判定")
    args = parser.parse_args(argv)

    logging.basicConfig(level=logging.INFO,
                        format="[%(asctime)s] %(levelname)s %(message)s",
                        datefmt="%H:%M:%S")
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass

    if not args.wav.is_file():
        print(f"找不到文件: {args.wav}", file=sys.stderr)
        return 2

    audio, rate = read_wav(args.wav)
    if len(audio) == 0:
        print("WAV 里没有音频数据", file=sys.stderr)
        return 2

    config = load_config()
    asr_engine = build_asr_engine(config)
    print(f"加载模型 {asr_engine.model_id} ...")
    t0 = time.monotonic()
    try:
        asr_engine.load(config.model)
    except Exception as e:
        print(f"模型加载失败: {e}", file=sys.stderr)
        return 1
    # 注意:这里量到的时间比日志里的"模型加载完成"长 ——
    # 首次 import torch/qwen_asr 也要几十秒,那一段在日志的计时之外。
    print(f"模型就绪,总耗时 {time.monotonic() - t0:.1f}s(含 torch 导入)\n")

    report = replay(
        audio, rate, asr_engine,
        realtime=args.realtime,
        verbose=args.verbose,
    )
    report.path = args.wav.name
    print(format_report(report))
    return 0


if __name__ == "__main__":
    sys.exit(main())
