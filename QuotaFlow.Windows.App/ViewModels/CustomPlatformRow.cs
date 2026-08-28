using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using QuotaFlow.Windows.App.Services;
using QuotaFlow.Windows.Core.Models;
using QuotaFlow.Windows.Core.Services;

namespace QuotaFlow.Windows.App.ViewModels;

/// <summary>
/// 设置页"自定义平台"的一个可编辑行。自包含：平台级字段 + 密钥三件套（掩码/眼睛/保存清除）+
/// 一组可折叠的额度窗口子行（<see cref="QuotaWindowRows"/>，v1.1.0 起一个平台可以有多个窗口，
/// 同一次接口响应里分别取值）。密钥处理完全对齐 MiniMax/DeepSeek 的统一模式——明文绝不常驻界面，
/// 只在验证身份后短暂出现。
///
/// <see cref="Id"/>（<c>custom-{n}</c>）在新增时分配后**永不改变**，凭据键
/// <c>custom:{Id}:ApiKey</c> 因此永远稳定；用户编辑的是 <see cref="Name"/> 等显示字段。
/// </summary>
public sealed partial class CustomPlatformRow : ObservableObject
{
    private const string KeyMask = "••••••••••••";

    /// <summary>建议最多支持的窗口数（文档 §配置界面改造）：超过这个数量的自定义指标场景很少见，
    /// 卡片高度和设置页可维护性都会明显变差，先用一个硬上限兜住。</summary>
    public const int MaxQuotaWindows = 5;

    private readonly SecureCredentialStore _credentialStore;
    private readonly Func<string, Task<IdentityVerificationResult>> _verifyIdentity;
    private readonly Action<CustomPlatformRow> _onDelete;
    private readonly Action<string>? _statusSink;
    private int _nextWindowSeq = 1;

    /// <summary>稳定标识 custom-{n}，新增时分配，之后永不改变。</summary>
    public string Id { get; }

    public string ProviderId => Id;

    /// <summary>Windows 凭据管理器键名，与 <see cref="CustomPlatformSettings.CredentialKeyName"/> 同源。</summary>
    public string CredentialKeyName => $"custom:{Id}:ApiKey";

    // ---- 平台级编辑字段（保存时由 ToSettings() 打包成定义）----

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _endpoint = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyVisibility))]
    [NotifyPropertyChangedFor(nameof(HeaderNameVisibility))]
    private CustomAuthKind _authKind = CustomAuthKind.BearerKey;

    [ObservableProperty] private string? _headerName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderVisibility))]
    [NotifyPropertyChangedFor(nameof(EditorVisibility))]
    private bool _isExpanded;

    /// <summary>该平台的额度窗口子行，按列表顺序即展示顺序；至少保留一个（UI 层面阻止删到 0 个）。</summary>
    public ObservableCollection<CustomQuotaWindowRow> QuotaWindowRows { get; } = [];

    public bool CanAddWindow => QuotaWindowRows.Count < MaxQuotaWindows;

    // ---- 密钥三件套（对齐 MiniMax/DeepSeek 统一模式）----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplayText))]
    [NotifyPropertyChangedFor(nameof(KeyIsReadOnly))]
    [NotifyPropertyChangedFor(nameof(KeyEyeVisibility))]
    private bool _isConfigured;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyDisplayText))]
    [NotifyPropertyChangedFor(nameof(KeyIsReadOnly))]
    private bool _isKeyRevealed;

    [ObservableProperty] private string _keyDisplayText = string.Empty;

    public bool KeyIsReadOnly => IsConfigured && !IsKeyRevealed;

    public Visibility KeyEyeVisibility => IsConfigured ? Visibility.Visible : Visibility.Collapsed;

    // ---- 可见性（返回 Visibility，免转换器）----

    /// <summary>未展开时只显示名称输入框 + 展开/删除。</summary>
    public Visibility HeaderVisibility => IsExpanded ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>展开后显示完整编辑区。</summary>
    public Visibility EditorVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>无需鉴权时隐藏密钥块。</summary>
    public Visibility KeyVisibility => AuthKind == CustomAuthKind.None ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>只有自定义请求头方式才需要填请求头名称。</summary>
    public Visibility HeaderNameVisibility => AuthKind == CustomAuthKind.CustomHeader ? Visibility.Visible : Visibility.Collapsed;

    public IRelayCommand SaveKeyCommand { get; }
    public IRelayCommand ClearKeyCommand { get; }
    public IAsyncRelayCommand ToggleRevealKeyCommand { get; }
    public IRelayCommand ToggleExpandCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand AddWindowCommand { get; }

    /// <param name="id">固定标识 custom-{n}，新增时分配，之后不改变。</param>
    /// <param name="existing">已保存的定义；null 表示新建。</param>
    /// <param name="onDelete">点击删除时回调（负责从两个集合移除并删除凭据）。</param>
    /// <param name="statusSink">把状态文案送回设置页底部。</param>
    public CustomPlatformRow(
        string id,
        SecureCredentialStore credentialStore,
        Func<string, Task<IdentityVerificationResult>> verifyIdentity,
        Action<CustomPlatformRow> onDelete,
        Action<string>? statusSink,
        CustomPlatformSettings? existing = null)
    {
        Id = id;
        _credentialStore = credentialStore;
        _verifyIdentity = verifyIdentity;
        _onDelete = onDelete;
        _statusSink = statusSink;

        _name = existing?.Name ?? string.Empty;
        _endpoint = existing?.Endpoint ?? string.Empty;
        _authKind = existing?.AuthKind ?? CustomAuthKind.BearerKey;
        _headerName = existing?.HeaderName;

        IsConfigured = !string.IsNullOrEmpty(_credentialStore.TryRead(CredentialKeyName));
        KeyDisplayText = IsConfigured ? KeyMask : string.Empty;

        SaveKeyCommand = new RelayCommand(SaveKey);
        ClearKeyCommand = new RelayCommand(ClearKey);
        ToggleRevealKeyCommand = new AsyncRelayCommand(ToggleRevealKeyAsync);
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        DeleteCommand = new RelayCommand(() => onDelete(this));
        AddWindowCommand = new RelayCommand(AddWindow, () => CanAddWindow);

        var windowDefs = existing?.QuotaWindows ?? [];
        if (windowDefs.Count == 0)
        {
            // 新建平台（或迁移前的空配置，理论上不会出现——AppSettingsStore 已经迁移过了）：
            // 至少给一个默认展开的空窗口，方便直接填。
            AddWindow();
        }
        else
        {
            foreach (var def in windowDefs.OrderBy(w => w.SortOrder))
            {
                var row = new CustomQuotaWindowRow(
                    string.IsNullOrWhiteSpace(def.Id) ? $"window-{_nextWindowSeq}" : def.Id,
                    DeleteWindow, def);
                QuotaWindowRows.Add(row);

                if (TryParseWindowIndex(row.Id, out var n) && n >= _nextWindowSeq)
                {
                    _nextWindowSeq = n + 1;
                }
            }
        }
    }

    /// <summary>
    /// OpenCode GO 内置模板：预填显示名称/接口地址/鉴权方式，并创建文档指定的三个额度窗口
    /// （5 小时/每周/每月，均为"已使用百分比" + 绝对重置时间）。用户只需要填并保存 API Key。
    /// </summary>
    public static CustomPlatformRow CreateOpenCodeGoTemplate(
        string id,
        SecureCredentialStore credentialStore,
        Func<string, Task<IdentityVerificationResult>> verifyIdentity,
        Action<CustomPlatformRow> onDelete,
        Action<string>? statusSink)
    {
        var definition = new CustomPlatformSettings
        {
            Id = id,
            Name = "OpenCode GO",
            Endpoint = "https://opencode.ai/zen/go/v1/usage",
            AuthKind = CustomAuthKind.BearerKey,
            QuotaWindows =
            [
                new CustomQuotaWindowSettings
                {
                    Id = "window-1",
                    Name = "5 小时",
                    DataKind = CustomDataKind.UtilizationPercent,
                    ValuePath = "usage.rolling.percent",
                    ResetsAtPath = "usage.rolling.resetsAt",
                    ResetTimeKind = CustomResetTimeKind.Absolute,
                    SortOrder = 0,
                },
                new CustomQuotaWindowSettings
                {
                    Id = "window-2",
                    Name = "每周",
                    DataKind = CustomDataKind.UtilizationPercent,
                    ValuePath = "usage.weekly.percent",
                    ResetsAtPath = "usage.weekly.resetsAt",
                    ResetTimeKind = CustomResetTimeKind.Absolute,
                    SortOrder = 1,
                },
                new CustomQuotaWindowSettings
                {
                    Id = "window-3",
                    Name = "每月",
                    DataKind = CustomDataKind.UtilizationPercent,
                    ValuePath = "usage.monthly.percent",
                    ResetsAtPath = "usage.monthly.resetsAt",
                    ResetTimeKind = CustomResetTimeKind.Absolute,
                    SortOrder = 2,
                },
            ],
        };

        var row = new CustomPlatformRow(id, credentialStore, verifyIdentity, onDelete, statusSink, definition);
        row.IsExpanded = true; // 模板创建后直接展开，方便用户立刻看到并填写 API Key
        return row;
    }

    /// <summary>把当前编辑态打包成平台定义（保存设置时使用）；窗口按当前列表顺序赋 SortOrder。</summary>
    public CustomPlatformSettings ToSettings() => new()
    {
        Id = Id,
        Name = Name.Trim(),
        Endpoint = Endpoint.Trim(),
        AuthKind = AuthKind,
        HeaderName = string.IsNullOrWhiteSpace(HeaderName) ? null : HeaderName.Trim(),
        QuotaWindows = QuotaWindowRows.Select((row, index) => row.ToSettings(index)).ToList(),
    };

    private void AddWindow()
    {
        if (!CanAddWindow)
        {
            _statusSink?.Invoke($"「{Name}」最多支持 {MaxQuotaWindows} 个额度窗口");
            return;
        }

        var row = new CustomQuotaWindowRow($"window-{_nextWindowSeq++}", DeleteWindow) { IsExpanded = true };
        QuotaWindowRows.Add(row);
        OnPropertyChanged(nameof(CanAddWindow));
        AddWindowCommand.NotifyCanExecuteChanged();
    }

    private void DeleteWindow(CustomQuotaWindowRow row)
    {
        if (QuotaWindowRows.Count <= 1)
        {
            _statusSink?.Invoke($"「{Name}」至少要保留一个额度窗口");
            return;
        }

        QuotaWindowRows.Remove(row);
        OnPropertyChanged(nameof(CanAddWindow));
        AddWindowCommand.NotifyCanExecuteChanged();
    }

    private static bool TryParseWindowIndex(string id, out int index)
    {
        index = 0;
        const string prefix = "window-";
        return id.StartsWith(prefix, StringComparison.Ordinal) &&
               int.TryParse(id[prefix.Length..], out index) && index >= 0;
    }

    private async Task ToggleRevealKeyAsync()
    {
        if (IsKeyRevealed)
        {
            HideKey();
            return;
        }

        var result = await _verifyIdentity($"验证身份以查看自定义平台「{Name}」的 API Key 明文");
        if (result != IdentityVerificationResult.Verified)
        {
            _statusSink?.Invoke(result == IdentityVerificationResult.Cancelled ? "已取消验证" : "验证失败，无法显示明文");
            return;
        }

        KeyDisplayText = _credentialStore.TryRead(CredentialKeyName) ?? string.Empty;
        IsKeyRevealed = true;
    }

    /// <summary>窗口失焦/关闭时调用，避免明文 Key 在界面上停留超出必要时间。</summary>
    public void HideKey()
    {
        IsKeyRevealed = false;
        KeyDisplayText = IsConfigured ? KeyMask : string.Empty;
    }

    private void SaveKey()
    {
        // 掩码状态下输入框只读，不会产生新值；直接保存会误把掩码写进凭据管理器。
        if (KeyIsReadOnly)
        {
            _statusSink?.Invoke("当前为掩码状态，点开眼睛并验证身份后即可编辑保存");
            return;
        }

        var value = KeyDisplayText?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            _statusSink?.Invoke("请输入该平台的 API Key");
            return;
        }

        _credentialStore.Save(CredentialKeyName, value);
        IsConfigured = true;
        KeyDisplayText = KeyMask;
        IsKeyRevealed = false;
        _statusSink?.Invoke($"「{Name}」的 API Key 已保存");
    }

    private void ClearKey()
    {
        _credentialStore.Delete(CredentialKeyName);
        IsConfigured = false;
        IsKeyRevealed = false;
        KeyDisplayText = string.Empty;
        _statusSink?.Invoke($"已清除「{Name}」的凭据");
    }
}
