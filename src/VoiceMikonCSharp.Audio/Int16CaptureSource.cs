using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace VoiceMikonCSharp.Audio;

/// <summary>
/// WASAPI 采集源：把设备的 mix format 统一读成 **int16 单声道**。
///
/// 为什么要有这一层：WASAPI 共享模式下拿到的 mix format 因设备而异
/// （32-bit float / 16-bit / 24-bit、1 或 2 声道都有）。上层（录音、探测）
/// 只想要一件事：**int16 单声道、原始采样率**的样本流。
///
/// 通道混音用整数平均（截断），与 Python 版 frames_to_int16 的
/// "int16 均值后 astype(int16)"一致 —— 那个实现也是截断，不是四舍五入。
///
/// 块大小固定 1024 样本（对齐 Python 的 blocksize=1024）：WASAPI 的事件块大小
/// 由驱动决定，不固定，所以这里自己攒块 —— 攒够 1024 才算一块。
/// </summary>
internal sealed class Int16CaptureSource : IDisposable
{
    /// <summary>每次读的块大小（样本数，单声道口径）。</summary>
    public const int BlockSize = 1024;

    private readonly WasapiCapture _capture;
    private readonly object _lock = new();
    private readonly Queue<short> _pending = new();

    private bool _stopped;
    private bool _disposed;

    /// <summary>打开设备的采集流（共享模式）。</summary>
    public Int16CaptureSource(MMDevice device)
    {
        _capture = new WasapiCapture(device);
        _capture.DataAvailable += OnDataAvailable;

        WaveFormat? format = _capture.WaveFormat;
        SampleRate = format?.SampleRate ?? 48000;
        Channels = Math.Max(1, format?.Channels ?? 1);
    }

    /// <summary>设备原生采样率（Hz）。</summary>
    public int SampleRate { get; private set; }

    /// <summary>设备声道数（读取时混成单声道）。</summary>
    public int Channels { get; private set; }

    /// <summary>开始采集。</summary>
    public void Start()
    {
        _capture.StartRecording();

        // 某些驱动在 StartRecording 之后才给出最终格式 —— 这时刷新一次。
        WaveFormat? format = _capture.WaveFormat;
        if (format is not null)
        {
            SampleRate = format.SampleRate;
            Channels = Math.Max(1, format.Channels);
        }
    }

    /// <summary>
    /// 读一块样本到 outSamples（单声道口径），返回实际读到的帧数。
    ///
    /// 阻塞直到凑满一块（或采集停止）。返回 0 = 采集已结束。
    /// </summary>
    public int ReadBlock(short[] outSamples)
    {
        lock (_lock)
        {
            while (_pending.Count < BlockSize && !_stopped)
            {
                Monitor.Wait(_lock);
            }

            if (_pending.Count == 0)
            {
                return 0;
            }

            int n = Math.Min(BlockSize, _pending.Count);
            for (int i = 0; i < n; i++)
            {
                outSamples[i] = _pending.Dequeue();
            }

            return n;
        }
    }

    /// <summary>停止采集（读循环会随之返回 0）。</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            Monitor.PulseAll(_lock);
        }

        try
        {
            _capture.StopRecording();
        }
        catch (Exception)
        {
            // 停止失败不影响收尾
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Stop();

        _capture.DataAvailable -= OnDataAvailable;

        try
        {
            _capture.Dispose();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>采集事件：把这一包原始字节解成 int16 单声道，排队等待凑块。</summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            byte[] buffer = e.Buffer;
            int bytes = e.BytesRecorded;
            if (bytes <= 0 || buffer.Length < bytes)
            {
                return;
            }

            WaveFormat format = _capture.WaveFormat;
            int channels = Math.Max(1, format.Channels);
            int frames = bytes / format.BlockAlign;
            if (frames <= 0)
            {
                return;
            }

            var mono = new short[frames];
            for (int f = 0; f < frames; f++)
            {
                int sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int offset = (f * channels + c) * format.BlockAlign;
                    sum += ToInt16(buffer, offset, format);
                }

                mono[f] = (short)(sum / channels);
            }

            lock (_lock)
            {
                foreach (short s in mono)
                {
                    _pending.Enqueue(s);
                }

                Monitor.PulseAll(_lock);
            }
        }
        catch (Exception)
        {
            // 采集回调里抛异常会掀翻 NAudio 的线程 —— 吞掉
        }
    }

    /// <summary>把一个采样点解成 int16（支持 IEEE float32 / PCM 16 / 24 / 32 位）。</summary>
    private static short ToInt16(byte[] buffer, int offset, WaveFormat format)
    {
        float value = format switch
        {
            WaveFormat { Encoding: WaveFormatEncoding.IeeeFloat, BitsPerSample: 64 } =>
                (float)BitConverter.ToDouble(buffer, offset),

            WaveFormat { Encoding: WaveFormatEncoding.IeeeFloat } =>
                BitConverter.ToSingle(buffer, offset),

            WaveFormat { Encoding: WaveFormatEncoding.Pcm, BitsPerSample: 16 } =>
                BitConverter.ToInt16(buffer, offset) / 32768f,

            WaveFormat { Encoding: WaveFormatEncoding.Pcm, BitsPerSample: 24 } =>
                ReadInt24(buffer, offset) / 8388608f,

            WaveFormat { Encoding: WaveFormatEncoding.Pcm, BitsPerSample: 32 } =>
                BitConverter.ToInt32(buffer, offset) / 2147483648f,

            WaveFormat { Encoding: WaveFormatEncoding.Pcm, BitsPerSample: 8 } =>
                (buffer[offset] - 128) / 128f,

            _ => 0f,
        };

        int scaled = (int)(value * short.MaxValue);
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    /// <summary>读 24 位小端 PCM 采样（符号扩展）。</summary>
    private static int ReadInt24(byte[] buffer, int offset)
    {
        int value = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
        if ((value & 0x800000) != 0)
        {
            value |= unchecked((int)0xFF000000);
        }

        return value;
    }
}
