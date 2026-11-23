using System;

namespace VmNext.Ui;

/// <summary>
/// 滚动电平条的数据源。
///
/// <para><b>算法全部照搬</b> voice-mikon/src/voice_mikon/host/static/capsule.js 的
/// <c>mapHeight</c> / <c>nextBarHeight</c> / <c>smoothLevel</c> / <c>advanceBar</c>，
/// 常量也照搬（BAR_COUNT / BAR_WIDTH / BAR_GAP / DEAD_ZONE / SILENT_HEIGHT / MAX_HEIGHT …）。
/// 唯一替换的是"电平从哪来"：真机是 presenter 30fps 推电平（<see cref="SetLevel"/>），
/// demo 里用正弦 + 噪声模拟（见 demos/Capsule.Demo 的 SimulatedLevels）。</para>
///
/// <para>条的**时间线**也照搬：电平 30fps 更新（capsule.js 注释："Python 侧 30fps 推一串滑动窗口"），
/// 条每 140ms 推进一根（<c>BAR_TICK_MS</c>）。</para>
/// </summary>
public sealed class WaveformData
{
    /// <summary>同时显示的条数：13 根（capsule.js 的 BAR_COUNT，MiniMax Code 的 asr-volume-bars 规格）。</summary>
    public const int BarCount = 13;

    /// <summary>元素池大小 = 可见条数 + 1（capsule.js 的 POOL_SIZE，多出来的那根已在视口左侧外，用来回收）。</summary>
    public const int PoolSize = BarCount + 1;

    /// <summary>条宽（px）：capsule.js 的 BAR_WIDTH。</summary>
    public const double BarWidth = 3;

    /// <summary>条间隙（px）：capsule.js 的 BAR_GAP。</summary>
    public const double BarGap = 3;

    /// <summary>步进 = 宽 + 间隙 = 6px（capsule.js 的 BAR_STEP）。</summary>
    public const double BarStep = BarWidth + BarGap;

    /// <summary>每推进一根条的周期（ms）：capsule.js 的 BAR_TICK_MS（140ms ≈ 7 根/秒，13 可见 → 1.8s 滚一圈）。</summary>
    public const int BarTickMs = 140;

    /// <summary>静音死区：响度 ≤ 0.05 画成 2px 灰条（capsule.js 的 DEAD_ZONE）。</summary>
    public const double DeadZone = 0.05;

    /// <summary>静音条高度（px）：capsule.js 的 SILENT_HEIGHT。</summary>
    public const double SilentHeight = 2;

    /// <summary>条高上限（px）：capsule.js 的 MAX_HEIGHT。</summary>
    public const double MaxHeight = 20;

    /// <summary>不高于它就算静音条、画灰色（capsule.js 的 SILENT_MAX_PX）。</summary>
    public const double SilentMaxPx = 4;

    /// <summary>电平更新频率（fps）：后端 30fps 推电平（capsule.js 的 setLevels 注释）。</summary>
    public const int LevelFps = 30;

    private readonly int[] _ages = new int[PoolSize];
    private readonly double[] _heights = new double[PoolSize];

    private double _smoother;
    private double _level;
    private int _slotIndex;
    private double _lastBarHeight;

    /// <summary>当前（平滑后）的电平 0..1。</summary>
    public double Level => _level;

    /// <summary>
    /// 进入录音：重来一遍，不保留上一次的残留高度（capsule.js 的 resetLevelBars）。
    /// </summary>
    public void Reset()
    {
        for (int i = 0; i < PoolSize; i++)
        {
            _ages[i] = i;
            _heights[i] = SilentHeight;
        }

        _slotIndex = 0;
        _lastBarHeight = 0;
        _smoother = 0;
        _level = 0;
    }

    /// <summary>
    /// 写入一帧电平（30fps 调用）。对应 capsule.js 的 <c>setLevels</c>：
    /// 取窗口里最新的一个值喂给平滑器，**不做窗口内归一化** ——
    /// 归一化之后条子永远看起来是满的，用户就没法从条的高低看出"我说话声音大不大"。
    /// </summary>
    /// <param name="level">已映射的电平（0..1，<c>LevelPump.DbfsToUnit</c> 的输出）。</param>
    public void SetLevel(double level)
    {
        _level = SmoothLevel(Math.Clamp(level, 0, 1));
    }

    /// <summary>
    /// 推进一根条（140ms 调用一次），返回池中每根条的新年龄与新高度。
    ///
    /// 年龄决定位置（translateX = -age × BAR_STEP），高度决定条长。
    /// 最老的一根被回收成新的最右条（capsule.js 的 advanceBar）。
    /// </summary>
    /// <param name="ages">输出：每根条的年龄（0 = 最新）。</param>
    /// <param name="heights">输出：每根条的像素高度。</param>
    public void Advance(int[] ages, double[] heights)
    {
        // 位置：整体左移一格，最老的回到最右（age = 0）
        for (int i = PoolSize - 1; i > 0; i--)
        {
            _ages[i] = _ages[i - 1] + 1;
        }

        _ages[0] = 0;

        // 高度：只有新条改高度，旧条保持（CSS 里旧条的 height 不动）
        for (int i = PoolSize - 1; i > 0; i--)
        {
            _heights[i] = _heights[i - 1];
        }

        double curPx = MapHeight(_level);
        _heights[0] = NextBarHeight(curPx, _level);

        Array.Copy(_ages, ages, PoolSize);
        Array.Copy(_heights, heights, PoolSize);
    }

    /// <summary>
    /// 响度(0..1) → 条高(px)。4 段非线性，照搬 capsule.js 的 mapHeight：
    /// 0~DEAD_ZONE 固定 2px；DEAD_ZONE~0.5 是 4→12px；0.5~0.75 是 12→18px；0.75~1 是 18→20px。
    /// </summary>
    public static double MapHeight(double level)
    {
        if (level <= DeadZone)
        {
            return SilentHeight;
        }

        if (level <= 0.5)
        {
            return 4 + ((level - DeadZone) / (0.5 - DeadZone) * 8);
        }

        if (level <= 0.75)
        {
            return 12 + ((level - 0.5) / 0.25 * 6);
        }

        return Math.Min(MaxHeight, 18 + ((level - 0.75) / 0.25 * 2));
    }

    /// <summary>
    /// 每次算出三个高度（过渡 / 当前 / 衰减）轮流用，相邻条因此天然错开
    /// —— "流动感"的来源。照搬 capsule.js 的 nextBarHeight。
    /// </summary>
    private double NextBarHeight(double curPx, double level)
    {
        double prev = _lastBarHeight;

        // 过渡条：与上一根做 65% 插值 → 缓升 / 缓降
        double trans = prev + ((curPx - prev) * 0.65);

        // 衰减条：回声感，越响掉得越多
        double decay = curPx <= SilentMaxPx
            ? curPx
            : Math.Max(SilentMaxPx, curPx - (0.75 + (3.75 * level)));

        double[] slots = { trans, curPx, decay };
        double h = slots[_slotIndex % 3];
        _slotIndex++;

        _lastBarHeight = h;

        return Math.Clamp(h, SilentHeight, MaxHeight);
    }

    /// <summary>
    /// 电平滑：升快降慢（上升 0.7、下降 0.5），再叠一层自适应增益。
    /// 照搬 capsule.js 的 smoothLevel。
    /// </summary>
    private double SmoothLevel(double target)
    {
        double t = _smoother;
        double r = t + ((target - t) * (target > t ? 0.7 : 0.5));
        _smoother = r;
        double output = r + ((target - r) * (0.3 + (0.5 * r)));
        return Math.Clamp(output, 0, 1);
    }
}
