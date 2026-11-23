using System;

namespace VmNext.Core.Presentation;

/// <summary>
/// 面板 presenter 需要的 core 能力（**窄接口**）。
///
/// <para>翻译自 voice-mikon 的 <c>apps/panel/presenter.py</c>：那边的 <c>core</c> 形参
/// 写的是 <c>Any</c>（鸭子类型），只用得到"订阅快照 / 切模型 / 切后端"这三样。
/// C# 侧把它收成一个窄接口，好处是<b>presenter 可以被单元测试用假实现驱动</b>，
/// 不必真的去造控制器、加载模型、碰剪贴板。</para>
///
/// <para>⚠ Core 不能引用 UI：本接口只认 core 的类型（AppState），
/// 不认识任何 Avalonia 类型（和 <see cref="VoiceBarPresenter"/> 一样的规矩）。</para>
/// </summary>
public interface IPanelCore
{
    /// <summary>当前状态快照（只读）。</summary>
    AppState StateSnapshot { get; }

    /// <summary>
    /// 订阅状态快照。
    ///
    /// <para>⚠ **订阅即投递**：新订阅者会**立刻**收到当前快照一次 ——
    /// 装配期状态可能已经不是初始值了（引擎甚至已经就绪），
    /// 漏掉这一次的话，订阅者得一直等到下一次变化才知道自己身处什么状态。</para>
    /// </summary>
    void Subscribe(Action<AppState> callback);

    /// <summary>
    /// 切模型规格（只对**本地**后端有意义：0.6B / 1.7B）。
    ///
    /// <para>⚠ **不返回结果**：切换在后台线程里做，拒绝与失败只走**通知流** ——
    /// 面板想给用户反馈必须订阅通知，而不是等一个返回值
    /// （这是 Python 版 <c>presenter.py</c> 模块说明里点名的坑）。</para>
    /// </summary>
    void SwitchModel(string modelTag);

    /// <summary>
    /// 切识别后端（local / minimax）。录音 / 识别中会被拒绝，
    /// 拒绝原因同样只走通知流。
    /// </summary>
    void SwitchBackend(string kind);
}

/// <summary>
/// 面板 presenter 需要的配置能力（**窄接口**）。
///
/// <para>翻译自 Python 版 presenter 的 <c>config: Any</c>：那边用 <c>getattr</c> 兜底
/// 读配置、写完 <c>save()</c>。C# 侧收成窄接口，
/// 单元测试可以用假配置记录"有没有 Save"，
/// 不用真的往 data/config.json 里写。</para>
/// </summary>
public interface IPanelConfig
{
    /// <summary>主题档位（auto / light / dark）。</summary>
    string Theme { get; set; }

    /// <summary>界面形态四档（orb / resident / on_demand / headless）。</summary>
    string UiMode { get; set; }

    /// <summary>录音时要不要显示实时预览文字条。</summary>
    bool ShowPreview { get; set; }

    /// <summary>粘贴后要不要归还原剪贴板内容。</summary>
    bool RestoreClipboard { get; set; }

    /// <summary>热键字符串（原始，不含"热键"前缀）。</summary>
    string Hotkey { get; }

    /// <summary>保存配置。</summary>
    void Save();
}
