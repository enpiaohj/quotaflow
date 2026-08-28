using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using QuotaFlow.Windows.Core.Models;

namespace QuotaFlow.Windows.App.Services;

/// <summary>
/// 全局"显示/隐藏面板"快捷键（默认 Alt+Z，可在设置页改）。用 Win32 RegisterHotKey 实现——
/// 不引入第三方热键库，跟项目里其它原生互操作（托盘图标、Windows 凭据管理器）风格一致。
///
/// 全局热键需要一个真实的窗口句柄来接收 WM_HOTKEY 消息；<see cref="AttachTo"/> 会在必要时
/// 强制创建面板窗口的句柄（<see cref="WindowInteropHelper.EnsureHandle"/>），不需要面板已经
/// 显示过。注册失败（多半是组合键被其它程序占用）不会抛异常、不会崩进程——返回 false，
/// 调用方（设置页保存）据此给用户明确反馈，绝不能让用户以为"设置了就一定生效"。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;

    // 自定义热键 id，只要在本进程内唯一即可（不同进程的 id 空间互不影响）。
    private const int HotkeyId = 0xA001;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;

    private HwndSource? _source;
    private bool _registered;

    /// <summary>热键触发时引发（已经在 UI 线程上，WM_HOTKEY 是发给窗口过程的消息）。</summary>
    public event Action? HotkeyPressed;

    /// <summary>
    /// 绑定到面板窗口。必须在真正调用 <see cref="Register"/> 之前调用一次；就算窗口从未显示过
    /// 也没关系，这里会强制创建它的原生句柄。
    /// </summary>
    public void AttachTo(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 注册（或更新）热键：先反注册旧的，再注册新的。<paramref name="key"/> 解析失败
    /// （理论上只有手改配置文件才会出现）时直接返回 false，不注册任何东西。
    /// </summary>
    /// <returns>是否注册成功；false 通常意味着这个组合键被其它程序占用。</returns>
    public bool Register(HotkeyModifiers modifiers, string keyName)
    {
        Unregister();

        if (_source is null || modifiers == HotkeyModifiers.None || !Enum.TryParse<Key>(keyName, out var key))
        {
            return false;
        }

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            return false;
        }

        _registered = RegisterHotKey(_source.Handle, HotkeyId, ToWin32Modifiers(modifiers), vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
            _registered = false;
        }
    }

    private static uint ToWin32Modifiers(HotkeyModifiers modifiers)
    {
        uint result = 0;
        if (modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            result |= ModAlt;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Control))
        {
            result |= ModControl;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            result |= ModShift;
        }

        if (modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            result |= ModWindows;
        }

        return result;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
