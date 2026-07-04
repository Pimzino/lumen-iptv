using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Lumen.App.Services.Playback;

/// <summary>
/// Paints LibVLCSharp's native video host window black. The library hosts video in a bare
/// Win32 "static" child window and never draws it, so whenever VLC's vout has no frame to
/// show (stream opening, buffering, reconnects, resizes, surface swaps) the pane surfaces
/// white/stale pixels — a glaring glitch in a dark UI, visible in the preview, the full
/// player, and the PiP window alike. Subclassing the window to answer WM_ERASEBKGND and
/// WM_PAINT with a black fill guarantees the pane is black any time VLC isn't rendering.
/// The vout child VLC creates is untouched: the host has WS_CLIPCHILDREN, so parent painting
/// never draws over live video. Subclassing is classic user32 SetWindowLongPtr chaining —
/// the comctl32 subclass API is not name-exported in every comctl32 version a WPF process
/// can end up loading.
/// </summary>
internal static class VideoHostBlackout
{
    private const uint WmPaint = 0x000F;
    private const uint WmEraseBkgnd = 0x0014;
    private const uint WmNcDestroy = 0x0082;
    private const int GwlpWndProc = -4;
    private const int BlackBrush = 4; // GetStockObject index

    // Rooted so the native thunk outlives the subclass; one proc serves every host window.
    private static readonly WndProc Proc = HandleMessage;
    private static readonly IntPtr ProcPtr = Marshal.GetFunctionPointerForDelegate(Proc);

    // hwnd → the window proc we replaced. UI-thread only (wndprocs run on the owning thread).
    private static readonly Dictionary<IntPtr, IntPtr> Originals = [];

    /// <summary>
    /// Subclasses the video host window inside <paramref name="videoView"/>. Idempotent —
    /// safe to call on every (re)attach; WPF parks the HwndHost window between parents, so
    /// in practice the handle is stable for the view's lifetime. Returns false while the
    /// host window hasn't been built yet.
    /// </summary>
    public static bool Apply(DependencyObject videoView)
    {
        if (FindHwndHost(videoView) is not { } host || host.Handle == IntPtr.Zero)
        {
            return false;
        }

        var handle = host.Handle;
        if (GetWindowLongPtr(handle, GwlpWndProc) == ProcPtr)
        {
            return true; // already ours
        }

        var original = SetWindowLongPtr(handle, GwlpWndProc, ProcPtr);
        if (original == IntPtr.Zero)
        {
            return false;
        }

        Originals[handle] = original;

        // Repaint immediately so an already-exposed white pane goes black now, not on the
        // next resize/expose.
        InvalidateRect(handle, IntPtr.Zero, true);
        return true;
    }

    private static HwndHost? FindHwndHost(DependencyObject root)
    {
        if (root is HwndHost host)
        {
            return host;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindHwndHost(VisualTreeHelper.GetChild(root, i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (!Originals.TryGetValue(hwnd, out var original))
        {
            return DefWindowProc(hwnd, msg, wParam, lParam); // unreachable in practice
        }

        switch (msg)
        {
            case WmEraseBkgnd:
                GetClientRect(hwnd, out var eraseRect);
                FillRect(wParam, ref eraseRect, GetStockObject(BlackBrush));
                return 1;

            case WmPaint:
                // The "static" class erases its own background (system window color — white)
                // during WM_PAINT; take over the paint entirely instead.
                var hdc = BeginPaint(hwnd, out var ps);
                if (hdc == IntPtr.Zero)
                {
                    return CallWindowProc(original, hwnd, msg, wParam, lParam);
                }

                GetClientRect(hwnd, out var paintRect);
                FillRect(hdc, ref paintRect, GetStockObject(BlackBrush));
                EndPaint(hwnd, ref ps);
                return IntPtr.Zero;

            case WmNcDestroy:
                var result = CallWindowProc(original, hwnd, msg, wParam, lParam);
                Originals.Remove(hwnd);
                return result;

            default:
                return CallWindowProc(original, hwnd, msg, wParam, lParam);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PaintStruct
    {
        public IntPtr Hdc;
        public int Erase;
        public NativeRect Paint;
        public int Restore;
        public int IncUpdate;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newValue);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref NativeRect rect, IntPtr brush);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PaintStruct ps);

    [DllImport("user32.dll")]
    private static extern IntPtr EndPaint(IntPtr hwnd, ref PaintStruct ps);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);
}
