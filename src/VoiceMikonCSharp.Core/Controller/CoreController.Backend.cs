using System;
using System.Collections.Generic;
using System.Threading;
using VoiceMikonCSharp.Engines;

namespace VoiceMikonCSharp.Core.Controller;

/// <summary>后端切换与模型切换。</summary>
public sealed partial class CoreController
{
    /// <summary>
    /// 后台切换模型规格（只对**本地**后端有意义）。
    /// </summary>
    public void SwitchModel(string modelTag)
    {
        if (BackendKind != BackendLocal)
        {
            Notify("warning", "model_switch_failed", "网络后端不加载本地模型,切回本地后端才能换模型");
            return;
        }

        if (modelTag == Config.Model && Engine.Loaded)
        {
            return;
        }

        var worker = new Thread(() =>
        {
            Notify("info", "model_switching", $"正在切换到 {modelTag} ...");
            try
            {
                Engine.SwitchModel(modelTag);
                Config.Model = modelTag;
                try
                {
                    Config.Save();
                }
                catch (Exception ex)
                {
                    _log.Debug($"保存模型设置失败: {ex.Message}");
                }

                UpdateState(s => s with { Model = modelTag });
                Notify("info", "model_switched", $"已切换到 {modelTag}");
            }
            catch (AsrError ex)
            {
                Notify("error", "model_switch_failed", ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error($"模型切换异常: {ex}");
                Notify("error", "model_switch_failed", $"模型切换失败: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "model-switch",
        };

        worker.Start();
    }

    /// <summary>
    /// 后台切换识别后端（本地 ↔ 网络）。
    ///
    /// **互斥**：同一时刻只留一个后端。切换的动作是：
    /// 造新的 → 加载新的 → 卸掉旧的 → 换引用。
    ///
    /// 顺序是有讲究的：**先把新的加载好**，失败了才敢卸旧的 ——
    /// 网络不可用（没配 API Key / 端点不通）时用户还能留在本地后端，
    /// 不至于两头都空。
    ///
    /// 旧后端被卸掉时，本地后端会把模型从显存里放掉 ——
    /// 这正是"切到网络省显存"落地的地方。
    ///
    /// **录音 / 识别中拒绝切换**：这一轮的最终位引擎持有旧后端的引用，
    /// 中途换掉会让识别出错或直接丢结果。拒绝时给用户一句提示。
    /// </summary>
    public void SwitchBackend(string kind)
    {
        kind = NormalizeBackend(kind);
        if (kind == BackendKind)
        {
            return;
        }

        if (State is ControllerState.Recording or ControllerState.Processing)
        {
            Notify("warning", "backend_switch_failed", "录音中不能切换识别后端,请先结束这一轮");
            return;
        }

        var worker = new Thread(() => SwitchBackendJob(kind))
        {
            IsBackground = true,
            Name = "backend-switch",
        };

        worker.Start();
    }

    /// <summary>切换后端的实际动作（跑在后台线程里，不卡 UI）。</summary>
    private void SwitchBackendJob(string kind)
    {
        string label = BackendLabels.TryGetValue(kind, out string? text) ? text : BackendLocal;
        Notify("info", "backend_switching", $"正在切换到{label}后端 ...");

        lock (_backendLock)
        {
            IAsrBackend old = Engine;
            IAsrBackend next;
            try
            {
                next = MakeBackend(kind);

                // 先把新后端加载好 —— 失败就留在旧后端
                next.Load();
            }
            catch (AsrError ex)
            {
                _log.Warning($"切换后端失败: {ex.Message}");
                UpdateState(s => s with { BackendHealth = new BackendHealth(HealthStatuses.Failed, ex.Message) });
                Notify("error", "backend_switch_failed", $"切换失败:{ex.Message}");
                return;
            }
            catch (Exception ex)
            {
                _log.Error($"切换后端异常: {ex}");
                UpdateState(s => s with { BackendHealth = new BackendHealth(HealthStatuses.Failed, ex.Message) });
                Notify("error", "backend_switch_failed", $"切换失败:{ex.Message}");
                return;
            }

            // 新后端就绪了，再把旧的卸掉（本地后端在这里释放显存）
            UnloadBackend(old);
            Engine = next;
            BackendKind = kind;
        }

        try
        {
            Config.Backend = kind;
            Config.Save();
        }
        catch (Exception ex)
        {
            _log.Debug($"保存后端设置失败: {ex.Message}");
        }

        UpdateState(s => s with
        {
            Backend = kind,
            BackendHealth = new BackendHealth(HealthStatuses.Ready),
            Model = CurrentModelTag(),
        });

        // MiniMax 没有 context biasing 通道 —— **明说**，
        // 不静默装支持（用户配了词表却发现不生效，那才叫坑）。
        string message = $"已切换到{label}后端";
        if (kind == BackendMinimax)
        {
            message += ":MiniMax 暂不支持热词(本地后端的词表在这一路不生效)";
        }

        Notify("info", "backend_switched", message);

        if (State is ControllerState.Starting or ControllerState.Error)
        {
            SetState(ControllerState.Idle);
        }
    }
}
