using System;
using System.Runtime.InteropServices;

namespace WinDynamicNotch.Interop;

/// <summary>
/// Глобальный низкоуровневый хук клавиатуры: ловит мультимедиа-клавиши
/// (play/pause, next, prev, stop) и при этом не перехватывает их у системы —
/// событие проходит дальше через CallNextHookEx.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    private readonly HookProc _proc;
    private IntPtr _hook;

    public KeyboardHook()
    {
        _proc = Callback;
    }

    public event EventHandler? MediaKeyPressed;

    public bool Install()
    {
        if (_hook != IntPtr.Zero)
        {
            return true;
        }

        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        return _hook != IntPtr.Zero;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var message = wParam.ToInt32();
                if (message is WmKeyDown or WmSysKeyDown)
                {
                    var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                    if (IsMediaKey(info.VkCode))
                    {
                        MediaKeyPressed?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }
        catch
        {
            // В хуке нельзя падать — иначе Windows отключит его целиком.
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>VK_MEDIA_NEXT_TRACK (0xB0) ... VK_MEDIA_PLAY_PAUSE (0xB3).</summary>
    private static bool IsMediaKey(int virtualKey) => virtualKey is >= 0xB0 and <= 0xB3;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
