#!/usr/bin/env python3
"""Silero VAD 参考实现（Python）—— 给 C# 侧的逐帧概率对比用。

C# 的 ``VoiceMikonCSharp.Engines.Vad.SileroVadDetector`` 跑的是同一个 onnx 图、
同一套调用协议（抽稀 → 拼 context → run → 收 state/context）。
本脚本用 Python 的 onnxruntime 跑一遍同样的流程，把**逐窗概率**吐成 JSON，
C# 侧拿它当参考值比对（预期偏差 < 1e-5）。

用法::

    python vad_ref.py --onnx <模型路径> --rate 16000 --input <wav 文件>

输出（stdout）::

    {"probabilities": [0.0031, 0.0027, ...]}

依赖：**onnxruntime**（没有就报错退出 —— C# 侧会跳过对比测试）。
⚠ 不要用 voice-mikon 的 venv 跑这个脚本（那是另一个工程的依赖环境）。
"""

from __future__ import annotations

import argparse
import json
import sys
import wave

# 与 vad_silero.py 一致：采样率 → (模型内部率, 抽稀步长, 抽稀后窗长, context 长度)
SILERO_PLANS = {
    8000: (8000, 1, 256, 32),
    16000: (16000, 1, 512, 64),
    32000: (16000, 2, 512, 64),
    48000: (16000, 3, 512, 64),
}

SILERO_WINDOW_SAMPLES = {8000: 256, 16000: 512, 32000: 1024, 48000: 1536}


def read_wav_int16(path: str, expect_rate: int) -> list[int]:
    """读 16-bit PCM WAV（标准库 wave，不引 numpy）。"""
    with wave.open(path, "rb") as wf:
        if wf.getsampwidth() != 2:
            raise SystemExit(f"只支持 16-bit PCM WAV: {path}")
        if wf.getnchannels() != 1:
            raise SystemExit(f"只支持单声道 WAV: {path}")
        rate = wf.getframerate()
        frames = wf.readframes(wf.getnframes())
        if rate != expect_rate:
            raise SystemExit(f"WAV 采样率 {rate} 与 --rate {expect_rate} 不一致")

    import array

    samples = array.array("h")
    samples.frombytes(frames)
    if sys.byteorder == "big":
        samples.byteswap()
    return list(samples)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Silero VAD 参考概率（Python）")
    parser.add_argument("--onnx", required=True, help="silero_vad.onnx 路径")
    parser.add_argument("--rate", type=int, default=16000, help="采样率（默认 16000）")
    parser.add_argument("--input", required=True, help="输入 WAV（单声道 16-bit PCM）")
    args = parser.parse_args(argv)

    try:
        import numpy as np
        import onnxruntime as ort
    except ImportError as exc:
        print(f"缺少依赖: {exc}", file=sys.stderr)
        return 3

    if args.rate not in SILERO_PLANS:
        print(f"采样率 {args.rate} 不受支持", file=sys.stderr)
        return 2

    model_rate, step, inner_window, context_samples = SILERO_PLANS[args.rate]
    window = SILERO_WINDOW_SAMPLES[args.rate]

    samples = read_wav_int16(args.input, args.rate)
    audio = np.asarray(samples, dtype=np.float32) / 32768.0

    session = ort.InferenceSession(args.onnx, providers=["CPUExecutionProvider"])
    state = np.zeros((2, 1, 128), dtype=np.float32)
    context = np.zeros((1, context_samples), dtype=np.float32)

    probabilities: list[float] = []
    for start in range(0, max(0, len(audio) - window + 1), window):
        chunk = audio[start:start + window]
        if step > 1:
            chunk = chunk[::step]
        if chunk.size != inner_window:
            break

        win = np.ascontiguousarray(chunk, dtype=np.float32).reshape(1, -1)
        xin = np.concatenate([context, win], axis=1)
        out, new_state = session.run(
            None,
            {
                "input": xin,
                "state": state,
                "sr": np.array(model_rate, dtype=np.int64),
            },
        )
        state = np.ascontiguousarray(new_state, dtype=np.float32)
        context = np.ascontiguousarray(xin[:, -context_samples:], dtype=np.float32)
        probabilities.append(float(out[0][0]))

    print(json.dumps({"probabilities": probabilities}))
    return 0


if __name__ == "__main__":
    sys.exit(main())
