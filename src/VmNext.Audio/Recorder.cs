using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using VmNext.Core;
using VmNext.Core.Levels;

namespace VmNext.Audio;

/// <summary>设备访问锁：探测与录音互斥（进程级）。</summary>
internal static class DeviceGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>尝试拿锁（超时返回 false）。</summary>
    public static bool TryAcquire(TimeSpan timeout) => Gate.Wait(timeout);

    /// <summary>放锁。</summary>
    public static void Release() => Gate.Release();
}

/// <summary>一次录音的结果：int16 单声道样本 + 设备原生采样率。</summary>
public readonly record struct RecordingResult(short[] Samples, int SampleRate);

/// <summary>
/// 后台录音采集。
///
/// 翻译自 voice-mikon/src/voice_mikon/audio/recorder.py。
///
/// 两个关键设计（都来自实际踩坑）：
/// 1. **用设备原生采样率打开**。蓝牙耳机声称支持 16 kHz，但 WASAPI 共享模式下会
///    悄悄降速（实测 2 秒只采到 0.38 秒），按 16 kHz 直接开会得到残缺数据。
///    这里不重采样：设备原生率通常是 48 kHz，降到 16 kHz 要做低通+抽取；自己用
///    线性插值实现没有抗混叠滤波，8~24 kHz 的成分会折叠回低频叠在人声基频上
///    （实测失真能量占比高达 98%）。重采样交给识别引擎内部做（librosa，与官方
///    评测路径一致）。
/// 2. **设备访问互斥**。探测流和录音流不能同时占用同一个音频端点，否则其中一方
///    只拿到部分数据。用一把进程级锁串行化，并让后台探测在用户录音时主动让路。
/// </summary>
public sealed class Recorder : ILevelSource, IDisposable
{
    /// <summary>
    /// 采集块大小（每次读到的样本数）。
    ///
    /// 回放工具要按**同样的粒度**切音频喂引擎，否则复现不出真实的喂入节奏 ——
    /// 粒度不同，分句引擎内部按帧切分的边界也跟着变。
    /// </summary>
    public const int RecorderBlockSize = 1024;

    private static readonly Logger _log = VmNext.Core.Log.GetLogger("audio.recorder");

    private readonly object _lock = new();
    private readonly List<short[]> _frames = new();
    private readonly Queue<double> _levels = new();
    private readonly int? _deviceIndex;
    private readonly int _targetRate;
    private readonly int _levelWindow;

    // 环境底噪估计（滑动窗口 p10）：显示门和 VAD 切句门的共同数据源。
    //
    // ⚠️ **跨轮保留**：故意不在 Start()/Stop() 里重置。底噪是房间和设备的属性，
    // 不会一轮一变；留着上一轮的窗口，第二轮录音一开始就有先验，不用再等一秒
    // 冷启动。换设备才需要重新收敛，而 Recorder 本身就是换设备就重建的。
    private readonly NoiseFloorTracker _noise;

    private Int16CaptureSource? _source;
    private Thread? _thread;
    private volatile bool _recording;
    private bool _deviceLockHeld;
    private int _chunkCursor;
    private DateTime _startTime;

    // 进程级"正在录音"标志（对齐 Python 的模块级 _recording_state）：
    // 后台探测看到它就主动让路。
    private static volatile bool s_isRecording;

    /// <summary>
    /// 造一个录音器。
    ///
    /// device：设备序号（null = 自动挑选；口径见 <see cref="DevicePicker"/> 的说明）。
    /// targetRate：识别侧的目标采样率（**仅作记录**，采集本身用设备原生率）。
    /// levelWindow：电平窗口帧数（同时也是底噪估计的窗口帧数）。
    /// </summary>
    public Recorder(int? device = null, int targetRate = 16000, int levelWindow = 48)
    {
        _deviceIndex = device;
        _targetRate = targetRate;
        _levelWindow = levelWindow;

        // 块长 1024/原生采样率 ≈ 64ms(16k)，48 帧 ≈ 3 秒窗口；
        // 攒够 16 帧（约 1 秒）才开始给估计 —— 之前返回 null，调用方兜底。
        _noise = new NoiseFloorTracker(window: levelWindow, minSamples: Math.Min(16, levelWindow));
    }

    /// <summary>当前是否有录音在进行（供后台探测让路判断）。</summary>
    public static bool IsRecording => s_isRecording;

    /// <summary>本轮录音是否在进行。</summary>
    public bool Recording => _recording;

    /// <summary>设备原生采样率（Hz）—— 停止后仍是最后一次的值。</summary>
    public int NativeRate { get; private set; }

    /// <summary>识别侧目标采样率（仅记录；采集用设备原生率）。</summary>
    public int TargetRate => _targetRate;

    /// <summary>已录音时长（秒）。</summary>
    public double Elapsed => _startTime == default ? 0.0 : (DateTime.Now - _startTime).TotalSeconds;

    /// <summary>最近的音量电平序列（用于实时波形）。</summary>
    public IReadOnlyList<double> Levels
    {
        get
        {
            lock (_lock)
            {
                return new List<double>(_levels);
            }
        }
    }

    /// <summary>
    /// 环境底噪估计（线性 RMS）；样本不足返回 null。
    ///
    /// 显示门（电平条）和切句门（VAD）都从这里取 —— 底噪是房间和设备的属性，
    /// 只能有一个真相。冷启动（窗口没攒够）返回 null，调用方自行兜底，别把
    /// null 当 0 用。
    /// </summary>
    public double? NoiseFloor => _noise.Floor();

    /// <summary>启动录音。失败返回 false（原因写进日志）。</summary>
    public bool Start()
    {
        lock (_lock)
        {
            _frames.Clear();
            _chunkCursor = 0;
        }

        MMDevice? device = ResolveDevice();
        if (device is null)
        {
            _log.Error("录音启动失败: 找不到可用的输入设备");
            return false;
        }

        // 设备访问互斥：探测与录音不能同时占着同一个音频端点。
        if (!DeviceGate.TryAcquire(TimeSpan.FromSeconds(10)))
        {
            _log.Error("录音启动失败: 音频设备被其它流占用(等了 10 秒)");
            return false;
        }

        _deviceLockHeld = true;

        try
        {
            _source = new Int16CaptureSource(device);
            _source.Start();
            NativeRate = _source.SampleRate;

            _recording = true;
            s_isRecording = true;
            _startTime = DateTime.Now;

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "audio-recorder",
            };
            _thread.Start();

            _log.Info($"录音中(设备 {_deviceIndex?.ToString() ?? "自动"},原生采样率 {NativeRate} Hz)");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"录音启动失败: {ex.Message}");
            _recording = false;
            s_isRecording = false;
            _source?.Dispose();
            _source = null;
            ReleaseDeviceLock();
            return false;
        }
    }

    /// <summary>
    /// 停止录音并返回**原生采样率**的单声道 int16 音频。
    ///
    /// 这里**不做**重采样 —— 由识别引擎内部完成（见类注释里的理由）。
    /// </summary>
    public RecordingResult Stop()
    {
        _recording = false;
        s_isRecording = false;

        try
        {
            _source?.Stop();
        }
        catch (Exception)
        {
            // 停止失败不影响收尾
        }

        try
        {
            _thread?.Join(TimeSpan.FromSeconds(1.0));
        }
        catch (Exception)
        {
        }

        _thread = null;

        _source?.Dispose();
        _source = null;

        ReleaseDeviceLock();

        lock (_lock)
        {
            short[] samples = Concat(_frames);
            _frames.Clear();
            return new RecordingResult(samples, NativeRate);
        }
    }

    /// <summary>
    /// 当前已录到的音频（原生采样率，int16 单声道）。
    ///
    /// 给实时预览用：**不消费、不打断录音**，录音继续，Stop() 仍然能拿到完整音频。
    ///
    /// 必须**在锁内拷贝帧列表**：采集线程随时在往 _frames 里塞数据，直接把列表
    /// 给出去会被并发修改。
    /// </summary>
    public short[] Snapshot()
    {
        lock (_lock)
        {
            return Concat(_frames);
        }
    }

    /// <summary>
    /// 取走"上次取走之后新录到的音频"（原生采样率，int16 单声道）。
    ///
    /// 给流式引擎用：预览位只需要**增量**，不必每次都把全部音频重新拼一遍 ——
    /// 那正是旧预览"整段重识别"越说越慢的根源之一。
    ///
    /// 与 Snapshot() 的分工：
    ///     Snapshot()     给**全量**且不消费（整段引擎 / 调试用）
    ///     DrainChunks()  给**增量**且推进游标（流式引擎用）
    ///
    /// 两者都不打断录音 —— Stop() 仍然拿到完整音频。
    /// 录音停止后（帧列表已清空）再调用只会拿到空数组。
    ///
    /// 必须**在锁内取游标**：采集线程随时在往 _frames 里塞数据，游标和帧列表
    /// 要一起取，否则会漏帧或者重复。
    /// </summary>
    public short[] DrainChunks()
    {
        lock (_lock)
        {
            int start = _chunkCursor;
            if (start >= _frames.Count)
            {
                return Array.Empty<short>();
            }

            _chunkCursor = _frames.Count;
            return Concat(_frames.GetRange(start, _frames.Count - start));
        }
    }

    /// <summary>释放资源（等价于 Stop）。</summary>
    public void Dispose()
    {
        if (_recording)
        {
            Stop();
        }
    }

    /// <summary>采集线程主体：按块读、算 RMS、喂电平与底噪。</summary>
    private void Loop()
    {
        Int16CaptureSource? source = _source;
        if (source is null)
        {
            return;
        }

        var buffer = new short[RecorderBlockSize];

        while (_recording)
        {
            int n;
            try
            {
                n = source.ReadBlock(buffer);
            }
            catch (Exception ex)
            {
                _log.Debug($"录音读取异常: {ex.Message}");
                break;
            }

            if (n <= 0)
            {
                break;
            }

            var copy = new short[n];
            Array.Copy(buffer, copy, n);

            // 同一个 RMS 顺手喂给底噪估计器 —— 它和电平条看的是同一串帧，
            // 不该有第二份数据源。
            double rms = DevicePicker.RmsOf(copy);

            lock (_lock)
            {
                _frames.Add(copy);

                _levels.Enqueue(rms);
                while (_levels.Count > _levelWindow)
                {
                    _levels.Dequeue();
                }
            }

            _noise.Update(rms);
        }
    }

    /// <summary>把采集到的帧拼成 int16 单声道数组（各帧本来就是单声道 int16）。</summary>
    private static short[] Concat(List<short[]> frames)
    {
        if (frames.Count == 0)
        {
            return Array.Empty<short>();
        }

        int total = 0;
        foreach (short[] frame in frames)
        {
            total += frame.Length;
        }

        var result = new short[total];
        int offset = 0;
        foreach (short[] frame in frames)
        {
            Array.Copy(frame, 0, result, offset, frame.Length);
            offset += frame.Length;
        }

        return result;
    }

    /// <summary>取设备（null = 第一个非虚拟设备）。</summary>
    private MMDevice? ResolveDevice()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();

            if (_deviceIndex is int index)
            {
                int i = 0;
                foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                {
                    if (i++ == index)
                    {
                        return device;
                    }
                }

                return null;
            }

            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (!DevicePicker.IsVirtual(device.FriendlyName))
                {
                    return device;
                }
            }

            // 全是虚拟设备：退而求其次，用第一个
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                return device;
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"解析输入设备失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>释放设备访问锁。</summary>
    private void ReleaseDeviceLock()
    {
        if (_deviceLockHeld)
        {
            try
            {
                DeviceGate.Release();
            }
            catch (Exception)
            {
            }

            _deviceLockHeld = false;
        }
    }
}
