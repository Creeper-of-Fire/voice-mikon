using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VmNext.Core;

namespace VmNext.Audio;

/// <summary>
/// 一个输入设备。
///
/// ⚠ **Index 的口径与 Python 版不同**：Python 用的是 PortAudio 的**全局设备索引**
/// （输出设备也占号），这里用的是 NAudio **采集端点列表内**的序号。
/// 原因：NAudio 没有"全局设备索引"这个概念，只有采集/渲染两个端点列表。
/// 后果：旧 config.json 里的 <c>input_device</c> 数值不能直接沿用（见交付文档的
/// 疑问清单）。设备名、可用性判定这些**行为**保持一致。
/// </summary>
public sealed record InputDevice(int Index, string Name, int MaxChannels, double DefaultRate, bool IsVirtual)
{
    /// <summary>展示用文字（对齐 Python 的 display 属性）。</summary>
    public string Display => $"[{Index}] {Name}";
}

/// <summary>一次设备实测的结果（原生采样率, 实际采集时长, rms）。</summary>
public readonly record struct DeviceProbe(int Rate, double Duration, double Rms);

/// <summary>设备挑选的结果（设备号 + 说明文字）。</summary>
public readonly record struct DeviceChoice(int? Index, string Description);

/// <summary>
/// 音频设备枚举与自动选择。
///
/// 翻译自 voice-mikon/src/voice_mikon/audio/devices.py。
///
/// 为什么要实测而不是看名字：
/// 蓝牙耳机（EDIFIER W800BT Plus 等）在 WASAPI 共享模式下会被 Steam / 网易云
/// 等程序占住音频端点。设备声称支持某个采样率，实际打开后却只拿到零星数据
/// —— 表现为「录音被截断」或「完全采不到声音」。
///
/// 所以策略是：逐个候选实测，选采集速度正常的那个；都不正常就退而求其次。
/// </summary>
public static class DevicePicker
{
    // 明显是虚拟/回环设备，除非没有别的选择否则跳过
    private static readonly string[] VirtualHints =
    {
        "声音映射器", "sound mapper", "streaming", "virtual", "虚拟",
        "回环", "loopback", "cable output", "primary sound capture",
        "立体声混音", "stereo mix", "wave",
    };

    /// <summary>实测时长（秒）。</summary>
    public const double ProbeDuration = 2.0;

    /// <summary>采集速度正常的判定阈值（实测 2 秒，得到 >=1.2 秒算正常）。</summary>
    public const double ProbeOkRatio = 0.6;

    private static readonly Logger _log = VmNext.Core.Log.GetLogger("audio.devices");

    /// <summary>列出所有可用的输入设备。</summary>
    public static IReadOnlyList<InputDevice> ListInputs()
    {
        var list = new List<InputDevice>();

        MMDeviceEnumerator enumerator;
        try
        {
            enumerator = new MMDeviceEnumerator();
        }
        catch (Exception ex)
        {
            _log.Error($"枚举音频设备失败: {ex.Message}");
            return list;
        }

        int index = 0;
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            string name = device.FriendlyName;
            WaveFormat? mix = MixFormat(device);

            list.Add(new InputDevice(
                index,
                name,
                mix?.Channels ?? 1,
                mix?.SampleRate ?? 48000.0,
                IsVirtual(name)));

            index++;
        }

        return list;
    }

    /// <summary>名字里带虚拟/回环特征的设备？</summary>
    public static bool IsVirtual(string name)
    {
        string low = (name ?? string.Empty).ToLowerInvariant();
        return Array.Exists(VirtualHints, h => low.Contains(h.ToLowerInvariant()));
    }

    /// <summary>
    /// 取设备**原生采样率**（共享模式的 mix format）。
    ///
    /// ⚠ 与 Python 版的差别：Python 是 PortAudio 的 supported_rate()——从高到低
    /// 试候选采样率（48000/44100/…/16000）看设备认哪个。WASAPI 共享模式没有
    /// "选采样率"这回事：流格式由系统的共享格式决定，取到的就是设备当前的
    /// 原生率（通常 48 kHz）。这正是 Python 版注释里说的"用设备原生采样率打开"，
    /// 只是 NAudio 直接给了答案。
    /// </summary>
    public static int SupportedRate(int index)
    {
        MMDevice? device = GetDevice(index);
        if (device is null)
        {
            return 48000;
        }

        WaveFormat? mix = MixFormat(device);
        return mix?.SampleRate ?? 48000;
    }

    /// <summary>
    /// 实测设备：返回（原生采样率, 实际采集时长, rms）。
    ///
    /// rms 是判断「有没有真的采到声音」的关键指标。
    /// </summary>
    public static DeviceProbe Probe(int index, double duration = ProbeDuration)
    {
        // 录音进行中 → 后台探测主动让路（不抢音频端点）
        if (Recorder.IsRecording)
        {
            return new DeviceProbe(0, 0, 0);
        }

        MMDevice? device = GetDevice(index);
        if (device is null)
        {
            return new DeviceProbe(0, 0, 0);
        }

        int rate = SupportedRate(index);

        // 探测流与录音流互斥：同一端点同时被两个流占着，其中一方只拿到部分数据。
        if (!DeviceGate.TryAcquire(TimeSpan.FromSeconds(2)))
        {
            return new DeviceProbe(rate, 0, 0);
        }

        try
        {
            using var source = new Int16CaptureSource(device);
            source.Start();

            var buffer = new short[Int16CaptureSource.BlockSize];
            var collected = new List<short>(capacity: (int)(rate * duration) + Int16CaptureSource.BlockSize);

            long deadline = Stopwatch.GetTimestamp() + (long)(duration * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline)
            {
                int n = source.ReadBlock(buffer);
                if (n <= 0)
                {
                    break;
                }

                for (int i = 0; i < n; i++)
                {
                    collected.Add(buffer[i]);
                }
            }

            double dur = collected.Count / (double)Math.Max(1, rate);
            double rms = RmsOf(collected);
            return new DeviceProbe(rate, dur, rms);
        }
        catch (Exception ex)
        {
            _log.Debug($"探测设备 {index} 失败: {ex.Message}");
            return new DeviceProbe(rate, 0, 0);
        }
        finally
        {
            DeviceGate.Release();
        }
    }

    /// <summary>
    /// 挑选一个可用的输入设备，返回（设备号, 说明文字）。
    ///
    /// 优先级：
    /// 1. 显式指定
    /// 2. 名称关键词匹配（且实测能采到数据）
    /// 3. 所有非虚拟设备里实测最好的
    /// </summary>
    public static DeviceChoice ChooseDevice(int? explicitIndex = null, IReadOnlyList<string>? nameHints = null)
    {
        IReadOnlyList<InputDevice> devices = ListInputs();
        if (devices.Count == 0)
        {
            return new DeviceChoice(null, "没有找到任何输入设备");
        }

        if (explicitIndex is not null)
        {
            foreach (InputDevice d in devices)
            {
                if (d.Index == explicitIndex)
                {
                    return new DeviceChoice(d.Index, $"使用指定设备 {d.Display}");
                }
            }

            _log.Warning($"指定的设备 [{explicitIndex}] 不存在,改为自动选择");
        }

        // 名称关键词
        foreach (string hint in nameHints ?? Array.Empty<string>())
        {
            string h = (hint ?? string.Empty).ToLowerInvariant();
            foreach (InputDevice d in devices)
            {
                if (!d.Name.ToLowerInvariant().Contains(h))
                {
                    continue;
                }

                DeviceProbe probe = Probe(d.Index);
                if (probe.Duration >= ProbeDuration * ProbeOkRatio)
                {
                    return new DeviceChoice(d.Index, $"按名称匹配 {d.Display}(原生 {probe.Rate} Hz)");
                }

                _log.Info($"名称匹配 {d.Display} 但采集不到数据(可能被其它程序占用),继续找");
            }
        }

        // 全量实测
        var good = new List<(double Rms, InputDevice Device, int Rate, double Duration)>();
        var fallback = new List<(double Rms, InputDevice Device, int Rate, double Duration)>();

        foreach (InputDevice d in devices)
        {
            if (d.IsVirtual)
            {
                continue;
            }

            DeviceProbe probe = Probe(d.Index);
            if (probe.Duration >= ProbeDuration * ProbeOkRatio)
            {
                good.Add((probe.Rms, d, probe.Rate, probe.Duration));
            }
            else if (probe.Duration > 0)
            {
                fallback.Add((probe.Rms, d, probe.Rate, probe.Duration));
            }
        }

        if (good.Count > 0)
        {
            // 信号强的优先
            good.Sort((a, b) => b.Rms.CompareTo(a.Rms));
            (double rms, InputDevice dev, int rate, _) = good[0];
            return new DeviceChoice(dev.Index, $"自动选择 {dev.Display}(原生 {rate} Hz, rms={rms:F4})");
        }

        if (fallback.Count > 0)
        {
            fallback.Sort((a, b) => b.Rms.CompareTo(a.Rms));
            (double rms, InputDevice dev, int rate, double dur) = fallback[0];
            return new DeviceChoice(
                dev.Index,
                $"仅 {dev.Display} 有信号但采集偏慢({dur:F1}s/{ProbeDuration:F0}s),可能有其它程序占用音频设备");
        }

        return new DeviceChoice(null, "找不到可用麦克风");
    }

    /// <summary>音频的均方根电平（对齐 Python 的 rms_of）。</summary>
    public static double RmsOf(IReadOnlyList<short> audio)
    {
        if (audio.Count == 0)
        {
            return 0.0;
        }

        double sum = 0.0;
        foreach (short s in audio)
        {
            double v = s / 32768.0;
            sum += v * v;
        }

        return Math.Sqrt(sum / audio.Count);
    }

    /// <summary>取设备（按采集端点列表序号）。</summary>
    private static MMDevice? GetDevice(int index)
    {
        if (index < 0)
        {
            return null;
        }

        try
        {
            var enumerator = new MMDeviceEnumerator();
            int i = 0;
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (i++ == index)
                {
                    return device;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"取设备 {index} 失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>取设备的 mix format（共享模式的流格式 = 设备当前原生率）。</summary>
    private static WaveFormat? MixFormat(MMDevice device)
    {
        try
        {
            var client = device.AudioClient;
            try
            {
                return client.MixFormat;
            }
            finally
            {
                (client as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"取设备 mix format 失败: {ex.Message}");
            return null;
        }
    }
}
