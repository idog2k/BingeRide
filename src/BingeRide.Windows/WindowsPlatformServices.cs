using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using BingeRide.App.Services;
using BingeRide.Core.Ble;
using Windows.Devices.Bluetooth;

namespace BingeRide.Windows;

internal sealed class WindowsPlatformServices(bool bluetooth = true) : IPlatformServices
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExLayered = 0x80000;
    private const uint LwaAlpha = 0x2;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x1, SwpNoMove = 0x2, SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200;

    private readonly HashSet<IntPtr> _wasLayered = [];

    public IBleAdapter? CreateBleAdapter()
    {
        if (!bluetooth) return null;
        try
        {
            var lookup = BluetoothAdapter.GetDefaultAsync().AsTask();
            var radio = lookup.Wait(TimeSpan.FromSeconds(5)) ? lookup.Result : null;
            if (radio is null || !radio.IsLowEnergySupported)
            {
                BleTrace.Log("no Bluetooth LE radio");
                return null;
            }
            return new WinBleAdapter();
        }
        catch (Exception ex)
        {
            BleTrace.Log($"Bluetooth unavailable: {ex.Message}");
            return null;
        }
    }

    public void SetClickThrough(Window window, bool enabled)
    {
        if (window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if (enabled)
        {
            if ((style & WsExLayered) != 0) _wasLayered.Add(hwnd);
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExTransparent | WsExLayered));
            // A layered window without attributes is invisible; keep it fully opaque (transparency comes from Avalonia).
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
        }
        else
        {
            var cleared = style & ~WsExTransparent;
            if (!_wasLayered.Remove(hwnd)) cleared &= ~WsExLayered;
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(cleared));
        }
    }

    public void KeepOnTop(Window window)
    {
        if (window.IsVisible && window.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    public IDisposable? RegisterGlobalHotkeys(Action toggleCompact, Action toggleClickThrough) =>
        new GlobalHotkeys(toggleCompact, toggleClickThrough);

    public IDisposable? ListenForActivation(Action<bool> show) => SingleInstance.Listen(show);

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}

/// <summary>Ctrl+Alt+K (overlay ↔ full) and Ctrl+Alt+L (click-through) on a dedicated message-loop thread.</summary>
internal sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312, WmQuit = 0x0012;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    private const uint VkK = 0x4B, VkL = 0x4C;
    private uint _threadId;

    public GlobalHotkeys(Action toggleCompact, Action toggleClickThrough)
    {
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            var compact = RegisterHotKey(IntPtr.Zero, 1, ModControl | ModAlt | ModNoRepeat, VkK);
            var through = RegisterHotKey(IntPtr.Zero, 2, ModControl | ModAlt | ModNoRepeat, VkL);
            BleTrace.Log($"hotkeys: Ctrl+Alt+K {(compact ? "ok" : "taken")}, Ctrl+Alt+L {(through ? "ok" : "taken")}");
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message != WmHotkey) continue;
                if (msg.wParam == 1) toggleCompact();
                else if (msg.wParam == 2) toggleClickThrough();
            }
            UnregisterHotKey(IntPtr.Zero, 1);
            UnregisterHotKey(IntPtr.Zero, 2);
        }) { IsBackground = true, Name = "Global hotkeys" };
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public nint wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
