using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.Services;

/// <summary>
/// 集中管理面板窗口三种显示模式之间的切换（文档 §5）。所有模式相关的窗口属性都只能通过这个
/// 协调器改，不允许在事件处理器里到处堆布尔判断——每个 Enter*Async 都完整走一遍：
/// 保存上一模式状态 → 设置目标模式的窗口样式/置顶/任务栏可见性 → 恢复目标模式的位置/尺寸/
/// 透明度/布局 → 更新状态 → 持久化。
///
/// 注意：Enter*Async 只重新配置窗口（位置/样式/置顶等），不负责 Show()/Hide()——窗口当前是否
/// 可见是一个独立关注点，由既有的托盘点击/快捷键/首次启动逻辑决定，不受这里影响。这样模式切换
/// 可以在窗口隐藏时静默发生（例如启动时恢复上次模式），也可以在窗口正显示时发生（点按钮切模式，
/// 窗口不会因此意外消失或重新弹出）。
/// </summary>
public interface IWindowPresentationCoordinator
{
    WindowPresentationMode CurrentMode { get; }
    bool IsAlwaysOnTop { get; }
    bool IsPositionLocked { get; }
    bool IsCompactLayout { get; }
    double Opacity { get; }
    WindowMaterial Material { get; }
    bool SnapToEdges { get; }
    bool RestoreLastModeOnStartup { get; }
    bool EnhanceReadabilityOnHover { get; }

    /// <summary>模式或任一独立属性变化后触发，供 ViewModel 刷新绑定（含"图标选中状态"）。</summary>
    event EventHandler? StateChanged;

    Task EnterTrayPopupAsync();
    Task EnterFloatingAsync();
    Task EnterDesktopPanelAsync();

    /// <summary>按设置里"启动后恢复上次模式"决定：开启则恢复上次模式，关闭则保持 TrayPopup。
    /// 只重新配置窗口，不改变可见性。</summary>
    Task RestoreLastModeAsync();

    void SetAlwaysOnTop(bool value);
    void SetPositionLocked(bool value);
    void SetCompactLayout(bool value);
    void SetOpacity(double value);
    void SetMaterial(WindowMaterial value);
    void SetSnapToEdges(bool value);
    void SetRestoreLastModeOnStartup(bool value);
    void SetEnhanceReadabilityOnHover(bool value);

    /// <summary>把当前模式的位置恢复成"从未保存过"时的推荐默认位置（对应设置页"恢复默认窗口位置"）。</summary>
    void RestoreDefaultPosition();

    /// <summary>拖动结束后调用：把窗口当前的 Left/Top/Width/Height/所在显示器保存进当前模式的
    /// SavedWindowPlacement 并持久化。只在拖动/调整结束时调用一次，不是每个像素都存盘。</summary>
    void PersistCurrentPlacement();
}
