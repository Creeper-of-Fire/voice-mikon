#!/usr/bin/env python3
"""本地推理子进程服务（named pipe IPC）。

C# 版把"本地推理"从"进程内 import torch"改成"Python 子进程 + 命名管道"：
本脚本被 C# 的 ``LocalBackend`` 拉起，双方用一套行式 JSON 协议对话。

**脚本自包含**：不 import voice_mikon 包（那边是另一个工程、另一套依赖）。
需要的东西都翻成了独立实现：

* 模型加载：复用 ``voice_mikon/src/voice_mikon/engines/engine.py`` 的
  ``Qwen3ASRModel.from_pretrained(...)`` 调用方式（dtype / device_map /
  max_inference_batch_size / max_new_tokens 逐项一致）。
* 热词合并：翻自 ``engines/base.py`` 的 ``_effective_hotwords`` /
  ``_build_context``（config 列表 + ``data/hotwords.txt`` 双来源、mtime 缓存、
  官方 ``"Vocabulary: a, b, c."`` 格式）。

协议细节见同目录的 ``README.md``。

依赖：
* 真模型模式：``torch`` + ``qwen_asr``（外加它们自带的 numpy）
* mock 模式：**一个第三方包都不需要**（只用标准库）
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import zlib

PIPE_PREFIX = "\\\\.\\pipe\\"

# 模型规格白名单（与 engine.py 的 switch_model 一致）
SUPPORTED_TAGS = ("0.6B", "1.7B")


def log(msg: str) -> None:
    """日志走 stderr（stdout 留给协议；实际上协议走管道，这里只是习惯）。"""
    print(msg, file=sys.stderr, flush=True)


# --------------------------------------------------------------------------
# 命名管道（Windows）
# --------------------------------------------------------------------------
#
# ⚠ **不能直接用 open(path, "r+b")** —— 那条路走的是普通文件的 CreateFile 语义，
# 管道实例还不存在时会**立刻**报 "No such file or directory"（而不是等待）。
# 而"服务端实例"只能由服务端自己创建（CreateNamedPipe），客户端的 Connect
# 才有东西可连 —— 双方互相等是死锁。
#
# 所以这里走 Windows 命名管道的正规三步：
#   CreateNamedPipe  → 建实例（并等待第一个客户端）
#   ConnectNamedPipe → 与客户端会合
#   open_osfhandle   → 把 HANDLE 交给 Python 的文件对象
#
# 这段只在 Windows 上有意义；别的平台直接报错退出（C# 侧也是 Windows 工具）。

_PIPE_ACCESS_DUPLEX = 0x00000003
_PIPE_TYPE_BYTE = 0x00000000
_PIPE_READMODE_BYTE = 0x00000000
_PIPE_WAIT = 0x00000000
_ERROR_PIPE_CONNECTED = 535
_INVALID_HANDLE_VALUE = -1


def open_named_pipe(path: str):
    """建好命名管道实例并等客户端连上，返回二进制文件对象。"""
    if os.name != "nt":
        raise RuntimeError("命名管道 IPC 只支持 Windows")

    import ctypes
    import msvcrt
    from ctypes import wintypes

    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

    kernel32.CreateNamedPipeW.argtypes = [
        wintypes.LPCWSTR,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.DWORD,
        wintypes.DWORD,
        ctypes.c_void_p,
    ]
    kernel32.CreateNamedPipeW.restype = wintypes.HANDLE
    kernel32.ConnectNamedPipe.argtypes = [wintypes.HANDLE, ctypes.c_void_p]
    kernel32.ConnectNamedPipe.restype = wintypes.BOOL

    handle = kernel32.CreateNamedPipeW(
        path,
        _PIPE_ACCESS_DUPLEX,
        _PIPE_TYPE_BYTE | _PIPE_READMODE_BYTE | _PIPE_WAIT,
        1,          # 最大实例数：只服务一个客户端
        65536,      # 输出缓冲
        65536,      # 输入缓冲
        0,          # 默认超时
        None,
    )
    if handle == _INVALID_HANDLE_VALUE or handle is None:
        raise ctypes.WinError(ctypes.get_last_error())

    connected = kernel32.ConnectNamedPipe(handle, None)
    if not connected:
        err = ctypes.get_last_error()
        # ERROR_PIPE_CONNECTED：客户端在我们调 ConnectNamedPipe 之前就连上了
        if err != _ERROR_PIPE_CONNECTED:
            kernel32.CloseHandle(handle)
            raise ctypes.WinError(err)

    fd = msvcrt.open_osfhandle(handle, os.O_RDWR | os.O_BINARY)
    return os.fdopen(fd, "r+b")



# --------------------------------------------------------------------------
# 热词（翻自 engines/base.py）
# --------------------------------------------------------------------------


def load_hotwords_file(path: str) -> list[str]:
    """读术语表文件（一行一词，``#`` 开头是注释，空行忽略，文件内去重）。

    翻自 voice-mikon/src/voice_mikon/config.py 的 ``load_hotwords_file``。
    文件不存在是正常情况 —— 没有术语而已，不算错误。
    """
    if not os.path.isfile(path):
        return []
    try:
        with open(path, "r", encoding="utf-8") as fh:
            text = fh.read()
    except OSError as exc:
        log(f"术语表 {path} 读取失败: {exc}")
        return []

    words: list[str] = []
    seen: set[str] = set()
    for line in text.splitlines():
        w = line.strip()
        if not w or w.startswith("#"):
            continue
        if w not in seen:
            seen.add(w)
            words.append(w)
    return words


class Hotwords:
    """热词合并器：config 列表（由 C# 送来）+ 词表文件（可选，mtime 缓存）。

    与 Python 版 ``ASRBackend._effective_hotwords`` 同口径：
    - config 列表内部**不去重**（只滤空）；
    - 词表文件内部的重复在读文件时去掉；
    - 词表文件的词相对**已收集到的全部词**去重后再追加。
    """

    def __init__(self, path: str | None = None) -> None:
        self.path = path
        self._mtime: float | None = None
        self._words: list[str] = []

    def _file_words(self) -> list[str]:
        if not self.path:
            return self._words
        try:
            mtime = os.stat(self.path).st_mtime
        except OSError:
            # 文件不存在或不可访问 —— 不是错误（与 Python 版一致：保留旧缓存）
            return self._words
        if mtime != self._mtime:
            self._words = load_hotwords_file(self.path)
            self._mtime = mtime
        return self._words

    def merge(self, config_words) -> list[str]:
        words: list[str] = []
        for w in config_words or []:
            if w is None:
                continue
            text = str(w)
            if not text or not text.strip():
                continue
            words.append(text.strip())

        seen = set(words)
        for w in self._file_words():
            if w not in seen:
                seen.add(w)
                words.append(w)
        return words


def build_context(words: list[str]) -> str:
    """拼成官方那套 ``"Vocabulary: a, b, c."`` 格式（翻自 base.py）。"""
    if not words:
        return ""
    return "Vocabulary: " + ", ".join(words) + "."


# --------------------------------------------------------------------------
# 引擎（真模型 / mock）
# --------------------------------------------------------------------------


class ModelEngine:
    """真模型后端（torch + qwen_asr）。

    翻自 voice-mikon/src/voice_mikon/engines/engine.py 的 ``ASREngine``。
    """

    def __init__(self) -> None:
        self._model = None
        self._tag = "0.6B"
        self._root = ""

    def load(self, model_root: str, tag: str) -> float:
        self._root = model_root
        local = os.path.join(model_root, f"Qwen3-ASR-{tag}")
        # ⚠ 先查权重目录再 import torch —— 权重缺失是最常见的原因，
        # 不该被"torch 没装"这种环境问题盖住（错误文案要指向真正的原因）。
        if not os.path.isdir(local):
            raise RuntimeError(
                f"本地权重缺失:{local} 不存在\n"
                "本地后端不会联网下载 —— 请二选一:\n"
                "  1) 下载权重后,在 data/config.json 的 model_root 里配好"
                "权重根目录(其下应有 Qwen3-ASR-" + tag + "/ 子目录),国内可用 ModelScope:\n"
                '     modelscope download --model Qwen/Qwen3-ASR-' + tag + ' --local_dir "' + local + '"\n'
                "  2) 或把后端切成 minimax 网络接口(不需要本地权重,音频会出本机)"
            )

        import torch  # 延迟导入：mock 模式根本用不到
        from qwen_asr import Qwen3ASRModel

        log(f"加载模型 {local} ...")
        t0 = time.time()
        try:
            model = Qwen3ASRModel.from_pretrained(
                local,
                dtype=torch.bfloat16 if torch.cuda.is_available() else torch.float32,
                device_map="cuda:0" if torch.cuda.is_available() else "cpu",
                max_inference_batch_size=4,
                max_new_tokens=1024,
            )
        except Exception as exc:
            raise RuntimeError(f"模型加载失败: {exc}") from exc

        self._model = model
        self._tag = tag
        elapsed = time.time() - t0
        log(f"模型加载完成,耗时 {elapsed:.1f}s")
        return elapsed

    def switch(self, tag: str) -> float:
        if tag not in SUPPORTED_TAGS:
            raise RuntimeError(f"不支持的模型规格: {tag}")
        return self.load(self._root, tag)

    def unload(self) -> None:
        model, self._model = self._model, None
        del model
        try:
            import torch

            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except Exception as exc:
            log(f"释放显存缓存失败: {exc}")

    def transcribe(self, pcm: bytes, sample_rate: int, language, context: str) -> str:
        if self._model is None:
            raise RuntimeError("模型尚未加载")
        import numpy as np

        # int16 → [-1, 1] float32（qwen_asr 内部用 librosa 重采样，librosa 只吃浮点）
        arr = np.frombuffer(pcm, dtype="<i2").astype(np.float32) / 32768.0
        t0 = time.time()
        try:
            results = self._model.transcribe(
                audio=(arr, sample_rate), language=language, context=context
            )
        except Exception as exc:
            raise RuntimeError(f"识别失败: {exc}") from exc

        text = results[0].text if results else ""
        log(f"识别完成 {len(pcm) / 2 / max(sample_rate, 1):.1f}s 音频 / {time.time() - t0:.1f}s 耗时: {text}")
        return text


class MockEngine:
    """mock 后端：不 import torch / qwen_asr / numpy，一个第三方包都不用。

    给 C# 侧单元测试用：
    - ``load``    → 立即 ready
    - ``transcribe`` → 回 ``[mock:{n_samples}:{crc32:08x}]``（crc32 是**音频字节**的校验和，
      C# 侧可以据此验证"音频原样到达"）
    """

    def __init__(self) -> None:
        self._tag = "0.6B"
        self._root = ""

    def load(self, model_root: str, tag: str) -> float:
        self._root = model_root
        self._tag = tag
        return 0.0

    def switch(self, tag: str) -> float:
        if tag not in SUPPORTED_TAGS:
            raise RuntimeError(f"不支持的模型规格: {tag}")
        self._tag = tag
        return 0.0

    def unload(self) -> None:
        pass

    def transcribe(self, pcm: bytes, sample_rate: int, language, context: str) -> str:
        n = len(pcm) // 2
        crc = zlib.crc32(pcm) & 0xFFFFFFFF
        return f"[mock:{n}:{crc:08x}]"


# --------------------------------------------------------------------------
# 协议
# --------------------------------------------------------------------------


def read_exact(handle, count: int) -> bytes:
    """读满 count 字节（管道上单次 read 可能不满，循环读）。"""
    chunks: list[bytes] = []
    remaining = count
    while remaining > 0:
        chunk = handle.read(remaining)
        if not chunk:
            break
        chunks.append(chunk)
        remaining -= len(chunk)
    return b"".join(chunks)


def respond(handle, payload: dict) -> None:
    handle.write((json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8"))
    handle.flush()


def serve(pipe_name: str, mock: bool, hotwords_file: str | None) -> int:
    """主循环：读一行命令 → 处理 → 写一行响应。"""
    path = pipe_name if pipe_name.startswith(PIPE_PREFIX) else PIPE_PREFIX + pipe_name

    engine = MockEngine() if mock else ModelEngine()
    hotwords = Hotwords(hotwords_file)

    # 建好管道实例并等客户端连上（C# 侧的 Connect 与之会合）
    try:
        handle = open_named_pipe(path)
    except Exception as exc:
        log(f"打开命名管道失败({path}): {exc}")
        return 2

    log(f"asr_server 已就绪 pipe={pipe_name} mock={mock}")

    while True:
        try:
            line = handle.readline()
        except OSError as exc:
            log(f"读命令失败: {exc}")
            break
        if not line:
            # 对端关闭
            break

        text = line.decode("utf-8", errors="replace").strip()
        if not text:
            continue

        try:
            request = json.loads(text)
        except Exception as exc:
            respond(handle, {
                "type": "error",
                "req_id": "",
                "message": f"请求不是合法 JSON: {exc}",
                "fatal": True,
            })
            continue

        cmd = str(request.get("cmd", ""))
        req_id = str(request.get("req_id", "") or "")

        try:
            if cmd == "ping":
                respond(handle, {"type": "pong"})

            elif cmd == "load":
                elapsed = engine.load(
                    str(request.get("model_root", "") or ""),
                    str(request.get("tag", "0.6B") or "0.6B"),
                )
                respond(handle, {"type": "ready", "elapsed": elapsed})

            elif cmd == "switch":
                tag = str(request.get("tag", "0.6B") or "0.6B")
                elapsed = engine.switch(tag)
                respond(handle, {"type": "loaded", "tag": tag, "elapsed": elapsed})

            elif cmd == "unload":
                engine.unload()
                respond(handle, {"type": "unloaded"})

            elif cmd == "shutdown":
                # 优雅退出：先回一句再断（对端知道是正常关闭，不是崩溃）
                respond(handle, {"type": "bye"})
                break

            elif cmd == "transcribe":
                n_samples = int(request.get("n_samples", 0) or 0)
                pcm = read_exact(handle, n_samples * 2)
                if len(pcm) != n_samples * 2:
                    raise RuntimeError(
                        f"音频数据不完整: 期望 {n_samples * 2} 字节, 实际 {len(pcm)}"
                    )
                # mock 故障注入（给 C# 测试用）：故意抛异常，验证对端收到 error 而不是挂死
                if request.get("mock_fail"):
                    raise RuntimeError("mock 故障注入: 故意抛异常")

                words = hotwords.merge(request.get("context") or [])
                context = build_context(words)
                out = engine.transcribe(
                    pcm,
                    int(request.get("sample_rate", 16000) or 16000),
                    request.get("language"),
                    context,
                )
                respond(handle, {"type": "result", "req_id": req_id, "text": out})

            else:
                respond(handle, {
                    "type": "error",
                    "req_id": req_id,
                    "message": f"未知命令: {cmd}",
                    "fatal": False,
                })

        except Exception as exc:  # 任何异常都回 error，绝不挂死
            fatal = bool(request.get("mock_fatal")) or cmd == "load"
            respond(handle, {
                "type": "error",
                "req_id": req_id,
                "message": str(exc),
                "fatal": fatal,
            })

    try:
        handle.close()
    except OSError:
        pass

    if not mock:
        try:
            engine.unload()
        except Exception:
            pass

    log("asr_server 退出")
    return 0


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="本地推理子进程（named pipe IPC）")
    parser.add_argument("--pipe", required=True, help="命名管道名（不含 \\\\.\\pipe\\ 前缀）")
    parser.add_argument("--mock", action="store_true", help="mock 模式：不加载真模型")
    parser.add_argument("--hotwords-file", default=None, help="词表文件（可选）")
    args = parser.parse_args(argv)

    return serve(args.pipe, args.mock, args.hotwords_file)


if __name__ == "__main__":
    sys.exit(main())
