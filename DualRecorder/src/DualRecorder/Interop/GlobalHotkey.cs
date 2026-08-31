using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DualRecorder.Interop
{
    /// <summary>System wide hotkey (works while the window is minimised or in the tray).</summary>
    public sealed class GlobalHotkey : IDisposable
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;

        private const int WM_HOTKEY = 0x0312;
        private const int HotkeyId = 0x4452; // "DR"

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private HwndSource _source;
        private IntPtr _handle;
        private bool _registered;

        public event Action Pressed;

        /// <summary>Returns false if another program already owns the combination.</summary>
        public bool Register(Window window, uint modifiers, uint virtualKey)
        {
            _handle = new WindowInteropHelper(window).EnsureHandle();
            _source = HwndSource.FromHwnd(_handle);
            if (_source == null) return false;
            _source.AddHook(HwndHook);
            _registered = RegisterHotKey(_handle, HotkeyId, modifiers | MOD_NOREPEAT, virtualKey);
            return _registered;
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                var h = Pressed;
                if (h != null) h();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_source != null) { try { _source.RemoveHook(HwndHook); } catch { } _source = null; }
            if (_registered) { try { UnregisterHotKey(_handle, HotkeyId); } catch { } _registered = false; }
        }
    }
}
