using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Drageon.App;

internal sealed class NativeTrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8001;
    private const int WmContextMenu = 0x007B;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NotifyIconVersion4 = 4;
    private const uint MfString = 0x00000000;
    private const uint MfDisabled = 0x00000002;
    private const uint MfPopup = 0x00000010;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private static readonly IntPtr MessageOnlyWindow = new(-3);

    private readonly HwndSource _window;
    private readonly IntPtr _icon;
    private readonly uint _id = 1;
    private bool _menuOpen;
    private bool _disposed;

    public NativeTrayIcon(string tooltip)
    {
        var parameters = new HwndSourceParameters("Drageon.NativeTrayIcon")
        {
            ParentWindow = MessageOnlyWindow,
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WindowProc);

        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            var large = new IntPtr[1];
            _ = ExtractIconEx(executable, 0, large, null, 1);
            _icon = large[0];
        }

        var data = CreateData(NifMessage | NifIcon | NifTip);
        data.CallbackMessage = CallbackMessage;
        data.Icon = _icon;
        data.Tooltip = tooltip;
        if (!ShellNotifyIcon(NimAdd, ref data))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Das Tray-Symbol konnte nicht erstellt werden.");
        data.TimeoutOrVersion = NotifyIconVersion4;
        _ = ShellNotifyIcon(NimSetVersion, ref data);
    }

    public Func<Task<IReadOnlyList<NativeTrayMenuItem>>>? MenuProvider { get; init; }
    public Action? DoubleClickAction { get; init; }

    public void ShowBalloon(int timeoutMilliseconds, string title, string message, NativeTrayBalloonIcon icon)
    {
        if (_disposed) return;
        var data = CreateData(NifInfo);
        data.InfoTitle = title;
        data.Info = message;
        data.TimeoutOrVersion = (uint)Math.Clamp(timeoutMilliseconds, 10_000, 30_000);
        data.InfoFlags = (uint)icon;
        _ = ShellNotifyIcon(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = CreateData(0);
        _ = ShellNotifyIcon(NimDelete, ref data);
        _window.RemoveHook(WindowProc);
        _window.Dispose();
        if (_icon != IntPtr.Zero) _ = DestroyIcon(_icon);
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != CallbackMessage) return IntPtr.Zero;
        var mouseMessage = unchecked((int)(long)lParam) & 0xffff;
        if (mouseMessage == WmLButtonDoubleClick)
        {
            DoubleClickAction?.Invoke();
            handled = true;
        }
        else if (mouseMessage is WmContextMenu or WmRButtonUp)
        {
            _ = ShowMenuAsync();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async Task ShowMenuAsync()
    {
        if (_menuOpen || MenuProvider is null || _disposed) return;
        _menuOpen = true;
        try
        {
            var items = await MenuProvider();
            var actions = new Dictionary<uint, Func<Task>>();
            uint nextId = 100;
            var menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            try
            {
                AppendItems(menu, items, actions, ref nextId);
                _ = SetForegroundWindow(_window.Handle);
                _ = GetCursorPos(out var position);
                var selected = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCommand,
                    position.X, position.Y, _window.Handle, IntPtr.Zero);
                if (selected != 0 && actions.TryGetValue(selected, out var action)) await action();
            }
            finally
            {
                _ = DestroyMenu(menu);
            }
        }
        finally
        {
            _menuOpen = false;
        }
    }

    private static void AppendItems(IntPtr menu, IReadOnlyList<NativeTrayMenuItem> items,
        IDictionary<uint, Func<Task>> actions, ref uint nextId)
    {
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                _ = AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
                continue;
            }

            if (item.Children is { Count: > 0 })
            {
                var childMenu = CreatePopupMenu();
                AppendItems(childMenu, item.Children, actions, ref nextId);
                var flags = MfPopup | (item.IsEnabled ? 0 : MfDisabled);
                _ = AppendMenu(menu, flags, unchecked((UIntPtr)(ulong)childMenu.ToInt64()), item.Text);
                continue;
            }

            var id = nextId++;
            var itemFlags = MfString | (item.IsEnabled ? 0 : MfDisabled);
            _ = AppendMenu(menu, itemFlags, new UIntPtr(id), item.Text);
            if (item.Action is not null) actions[id] = item.Action;
        }
    }

    private NotifyIconData CreateData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        Window = _window.Handle,
        Id = _id,
        Flags = flags
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tooltip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    private static bool ShellNotifyIcon(uint message, ref NotifyIconData data) => Shell_NotifyIcon(message, ref data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int iconIndex, IntPtr[]? largeIcons, IntPtr[]? smallIcons, uint iconCount);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr item, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}

internal sealed record NativeTrayMenuItem(
    string Text,
    Func<Task>? Action = null,
    IReadOnlyList<NativeTrayMenuItem>? Children = null,
    bool IsEnabled = true,
    bool IsSeparator = false)
{
    public static NativeTrayMenuItem Separator { get; } = new("", IsSeparator: true);
}

internal enum NativeTrayBalloonIcon : uint
{
    None,
    Info,
    Warning,
    Error
}
