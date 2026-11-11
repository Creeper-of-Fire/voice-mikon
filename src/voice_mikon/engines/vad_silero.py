"""Silero VAD 判定器:神经网络版的"这一帧是不是人声"。

**模型文件随包走,离线优先**:``silero_vad.onnx`` 放在包内
(``engines/silero_vad.onnx``),随 pip 安装一起分发,运行时**不联网**
下载。上游仓库 snakers4/silero-vad,MIT 许可,允许再分发。

**为什么换成 ONNX(而不是 torch.jit)**:Silero 只是**判定层**,它在整条
链路上最轻,却会把 torch 拖进来 —— 网络后端(MiniMax)模式下本来可以
零 torch,结果光一个 VAD 就把 torch + 几百 MB 内存拖上桌。实测同一段
录音,只造判定器跑 1000 窗:torch 版进程 RSS 222MB、ONNX 版 70MB
(其中 numpy 基线 25MB);载入 1.36s → 0.23s,单窗 0.39ms → 0.13ms。
判定结果几乎逐帧一致(实测最大概率偏差 1.7e-06,滞回后的逐帧判定
完全相同)。

**为什么是神经网络**:能量法只看 RMS 高低,分不清"人在小声说话"和
"环境突然吵了"。真机实测里换环境后底噪抬升,能量门限整轮失效,
预览基本不出字。Silero 是训练过的模型,能把人声和噪声分开。

**采样率**:官方 README 只写了 8000 Hz 和 16000 Hz,但模型自己还认
16000 的整数倍(内部按 ``step = sr // 16000`` 抽取到 16k 再算)。实测
能跑的组合:

    8000 Hz  → 256 样本/窗
    16000 Hz → 512 样本/窗
    32000 Hz → 1024 样本/窗
    48000 Hz → 1536 样本/窗

全部是 **32ms** 的窗(256/8000 = 512/16000 = 1024/32000 = 1536/48000)。
44100 Hz 这类"不是 16k 整数倍"的率跑不了,那种设备自动降级到能量法
并发通知。

**本项目按设备原生率采集**(常见 48k),支持的率直接按窗长喂,
不做重采样 —— 重采样会引入混叠失真,而原生率喂进去模型自己会抽。

**ONNX 图的调用协议**(照上游 ``src/silero_vad/utils_vad.py`` 里的
``OnnxWrapper`` 来,不是自己猜的):

    输入  input : float32[batch, N]   N = 上一窗尾巴 + 当前窗
                  state: float32[2, batch, 128]  LSTM 的 h/c
                  sr   : int64           16k 或 8k
    输出  output: float32[batch, 1]    语音概率
                  stateN: float32[2, batch, 128] 新的 LSTM 状态

关键两点:

1. **模型只认 16k / 8k 两档**,48k/32k 必须先抽稀(``step = sr//16000``)
   再喂 —— 直接喂 1536 样本的原生窗,ONNX 图会在 LSTM 上直接报形状错。
2. **必须拼上"上一窗尾巴"(context prepending)**。上游 ``OnnxWrapper``
   在每次调用前把上一次的输入尾部(context_size 样本)接在当前窗前面
   一起喂:16k 拼 64 样本(64+512=576),8k 拼 32 样本(32+256=288)。
   模型是在带 context 的形态下训练的 —— 实测不拼 context,概率会直接
   塌到 0.0006 上下(全是静音),判定彻底失灵。
"""

from __future__ import annotations

import threading
from pathlib import Path

import numpy as np

from .vad import SileroUnavailable, VoiceDetector


# 模型文件(包内资产,随 pip 安装分发)
MODEL_PATH = Path(__file__).resolve().parent / "silero_vad.onnx"

# 采样率 → 每次推理需要的样本数(每档都是 32ms 的窗)
#
# 这是**判定层喂进去的原生率窗长**:8k/16k 是模型原生支持的;32k/48k
# 是 16k 的整数倍,推理时按 ``step = sr // 16000`` 抽取到 16k 再算 ——
# 所以喂进去的窗长要按原生率乘上去(48k → 1536 样本,抽完正好 512)。
#
# 实测:48000/1536、32000/1024、16000/512、8000/256 全部 OK;
# 44100/24000 直接不支持(降级能量法)。
SILERO_WINDOW_SAMPLES: dict[int, int] = {
    8000: 256,
    16000: 512,
    32000: 1024,
    48000: 1536,
}

# 采样率 → 推理计划 (模型内部率, 抽稀步长, 抽稀后的窗长, context 长度)
#
# 这几个常量全部来自上游 ``utils_vad.py`` 的 ``OnnxWrapper``:
#   - 抽稀步长 ``step = sr // 16000``(只对 16k 的整数倍生效)
#   - 模型窗长 ``num_samples = 512 if sr == 16000 else 256``
#   - context 长度 ``context_size = 64 if sr == 16000 else 32``
#     (和 TorchScript 版模型里的 ``context_size_samples`` 属性同值,
#      实测 _model=64 / _model_8k=32)
#
# 8k 那一档**不抽稀**:8000 不是 16000 的整数倍,上游是让它走模型里
# 专门的 8k 分支(_model_8k),ONNX 图里对应按 ``sr`` 走的 If 分支。
SILERO_PLANS: dict[int, tuple[int, int, int, int]] = {
    8000: (8000, 1, 256, 32),
    16000: (16000, 1, 512, 64),
    32000: (16000, 2, 512, 64),
    48000: (16000, 3, 512, 64),
}

# LSTM 状态形状:2 = [h; c] 拼在一起(batch=1,hidden=128)。
SILERO_STATE_SHAPE = (2, 1, 128)

# 进入语音的阈值:概率 ≥ 这个值算"开始说话"。
#
# 取官方推荐值 0.5("lazy 0.5 is pretty good for most datasets")。
# 项目原则是"宁可把底噪包进句子,不吞轻声开头" —— 偏向召回,
# 所以开门不往上调。
SILERO_SPEECH_ON_THRESHOLD = 0.5

# 退出语音的阈值:概率 < 这个值算"停止说话"。
#
# 官方 get_speech_timestamps 的负阈值默认是 ``threshold - 0.15``
# (= 0.35)。比开门低,中间留一条滞回带 —— 和能量法的双门同构:
# 只有一个门的话,概率在门附近抖一下就会翻转,连续静音永远攒不满,
# 该切的时候切不出来。
SILERO_SPEECH_OFF_THRESHOLD = 0.35


# 进程级共享的 InferenceSession。
#
# **为什么能共享**:ONNX 图的 LSTM 状态是**显式**的 —— state 从入参进、
# stateN 从出参出,谁调用谁拿着。所以同一个 session 能同时服务多颗
# 判定器,天然不会串味。
#
# **TorchScript 版做不到这点**:torch.jit 的模型把 ``_state``/``_context``
# 存在**模块自身**里,两颗判定器共用一个模型对象就会互相串味(实测
# 交替喂"有声/静音"时,静音那路的概率被从 0.006 抬到 0.115,判定直接
# 失真),所以当年只能靠 deepcopy 隔离 —— 结构上多了一份"模板 + 副本"
# 的复杂度,还带着"旧预览线程可能还活着,新一轮录音的判定器已经建好
# 了"这种串味隐患。换成 ONNX 之后,这个隐患是**结构性消失**的,不用
# 再防。
#
# **线程安全**:onnxruntime 的 ``InferenceSession.run()`` 是线程安全的
# (状态在入参/出参里,不在 session 里),多线程并发调用同一个 session
# 不会串数据。
_session_cache: dict[str, object] = {}
_session_lock = threading.Lock()


def _load_session(model_path: Path):
    """拿一个进程级共享的 InferenceSession。失败抛 SileroUnavailable。"""
    try:
        import onnxruntime
    except ImportError as e:
        raise SileroUnavailable(
            "onnxruntime_missing",
            f"onnxruntime 未安装({e})") from e

    key = str(model_path)
    with _session_lock:
        cached = _session_cache.get(key)
        if cached is not None:
            return cached

        if not model_path.is_file():
            raise SileroUnavailable(
                "model_missing",
                f"模型文件不存在: {model_path}")
        try:
            opts = onnxruntime.SessionOptions()
            # 单线程:一窗才 32ms,线程调度开销比推理本身还大;
            # 上游 OnnxWrapper 也是这么配的。
            opts.inter_op_num_threads = 1
            opts.intra_op_num_threads = 1
            # 显式钉 CPU:VAD 这点算力不值得去抢 GPU,而且网络后端
            # 模式下 GPU 可能压根没启用。缺 CPU provider 的构建极少见,
            # 真没有就退回 onnxruntime 自己的默认选择。
            available = onnxruntime.get_available_providers()
            providers = (["CPUExecutionProvider"]
                         if "CPUExecutionProvider" in available
                         else available[:1])
            session = onnxruntime.InferenceSession(
                str(model_path), sess_options=opts, providers=providers)
        except Exception as e:
            raise SileroUnavailable(
                "model_load_failed",
                f"模型加载失败({model_path}): {e}") from e

        _session_cache[key] = session
        return session


class SileroVadDetector(VoiceDetector):
    """Silero VAD 判定器:逐窗推理,带滞回。

    一次推理是亚毫秒级(实测约 0.1ms/窗),预览线程里同步跑完全够用,
    不需要另开线程。
    """

    def __init__(self, sample_rate: int = 16000, model_path: Path | None = None):
        self._sample_rate = int(sample_rate)
        window = SILERO_WINDOW_SAMPLES.get(self._sample_rate)
        if window is None:
            raise SileroUnavailable(
                "unsupported_sample_rate",
                f"采样率 {self._sample_rate} Hz 不受支持"
                f"(支持 {'/'.join(str(r) for r in SILERO_WINDOW_SAMPLES)} Hz)")
        self._window_samples = window
        (self._model_sr, self._step,
         self._inner_window, self._context_samples) = SILERO_PLANS[self._sample_rate]
        # session 是进程级共享的(见 _session_cache 的说明);
        # 状态(state / context)是每颗判定器自己的 —— 这就是"不串味"的结构保证。
        self._session = _load_session(model_path or MODEL_PATH)
        self._in_speech = False
        # 模型是 LSTM,带着上一轮的状态会判歪 —— 每轮先重置。
        self.reset()

    @property
    def frame_samples(self) -> int:
        return self._window_samples

    @property
    def requires_exact_frames(self) -> bool:
        # Silero 只吃**精确**窗长(窗长不对,ONNX 图会直接报形状错),
        # 所以引擎必须跨块拼帧,不足一窗的留到下一块。
        return True

    def reset(self) -> None:
        """重置滞回状态 + 模型状态(LSTM 的 h/c + context)。

        ONNX 的状态是显式的两份:``state``(LSTM 的 h/c)由 ``run()``
        返回、调用方自己留,``context``(上一窗尾巴)也是调用方自己留的。
        两份一起清零 = 每轮录音从干净状态开始,等价于 TorchScript 版的
        ``reset_states()``。
        """
        self._in_speech = False
        self._state = np.zeros(SILERO_STATE_SHAPE, dtype=np.float32)
        self._context = np.zeros((1, self._context_samples), dtype=np.float32)

    def is_speech(self, frame: np.ndarray) -> bool:
        """判定这一帧是不是人声(带滞回)。

        迟滞方向和能量法一致:不在说话态时用开门阈值,说话态中用关门阈值。
        """
        prob = self._probability(frame)
        if self._in_speech:
            if prob < SILERO_SPEECH_OFF_THRESHOLD:
                self._in_speech = False
        elif prob >= SILERO_SPEECH_ON_THRESHOLD:
            self._in_speech = True
        return self._in_speech

    @property
    def in_speech(self) -> bool:
        return self._in_speech

    def _probability(self, frame: np.ndarray) -> float:
        """跑一次推理,返回语音概率。

        归一化到 [-1, 1] 再喂 —— 模型是这么训练的。

        三步走(和上游 OnnxWrapper 同序):抽稀到模型内部率 → 拼上
        上一窗尾巴(context)→ run(),把新 state 和新 context 收下。
        """
        arr = frame.astype(np.float32)
        if np.issubdtype(frame.dtype, np.integer):
            arr = arr / 32768.0
        if arr.size != self._window_samples:
            # TorchScript 版是模型自己抛 ValueError;ONNX 图的输入长度
            # 是动态的,喂错了不一定报错,只会算出一个看着像回事但其实
            # 不对的值 —— 所以这里自己把住这道门,错要错得明白。
            raise ValueError(
                f"窗口样本数不对: 期望 {self._window_samples},"
                f"实际 {arr.size}(采样率 {self._sample_rate} Hz)")

        # 抽稀到模型内部率(48k → 16k 每 3 个取 1 个)。
        if self._step > 1:
            arr = arr[::self._step]
        if arr.size != self._inner_window:
            # ONNX 图只吃 512(16k)/256(8k)两种窗长,喂别的长度会在
            # LSTM 上报一串看不懂的形状错 —— 自己先把话说明白。
            raise ValueError(
                f"抽稀后窗口样本数不对: 期望 {self._inner_window},"
                f"实际 {arr.size}(采样率 {self._sample_rate} Hz)")
        win = np.ascontiguousarray(arr, dtype=np.float32).reshape(1, -1)

        # 拼上"上一窗尾巴"。模型是在带 context 的形态下训练的 ——
        # 少这一段,概率会塌到 0.0006 上下,判定彻底失灵。
        xin = np.concatenate([self._context, win], axis=1)

        out, state = self._session.run(
            None,
            {
                "input": xin,
                "state": self._state,
                "sr": np.array(self._model_sr, dtype=np.int64),
            },
        )
        self._state = np.ascontiguousarray(state, dtype=np.float32)
        self._context = np.ascontiguousarray(
            xin[:, -self._context_samples:], dtype=np.float32)
        return float(out[0][0])
