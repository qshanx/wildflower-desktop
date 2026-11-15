using System.Runtime.InteropServices;

namespace WildflowerDesktop;

internal sealed class DesktopShell : IDisposable
{
    internal nint Parent { get; private set; }
    internal nint IconView { get; private set; }
    private nint explorer, hitMemory;
    private static readonly nuint HitSize = (nuint)Marshal.SizeOf<Native.HitTest>();

    internal void Connect()
    {
        Dispose();
        var progman = Native.FindWindow("Progman", null);
        if (progman == 0) throw new InvalidOperationException("找不到 Windows 桌面，请重启资源管理器后再应用。");
        Native.SendMessageTimeout(progman, 0x052C, 0, 0, 2, 1000, out _);
        nint defViewOwner = 0;
        Native.EnumWindows((window, _) =>
        {
            var def = Native.FindWindowEx(window, 0, "SHELLDLL_DefView", null);
            if (def != 0)
            {
                defViewOwner = window;
                IconView = Native.FindWindowEx(def, 0, "SysListView32", null);
            }
            return true;
        }, 0);
        // Windows 11's raised desktop keeps the wallpaper WorkerW inside Progman.
        if (((long)Native.GetWindowLongPtr(progman, -20) & 0x00200000) != 0)
        {
            var child = Native.FindWindowEx(progman, 0, "WorkerW", null);
            if (child != 0 && Native.FindWindowEx(child, 0, "SHELLDLL_DefView", null) == 0) Parent = child;
        }
        if (Parent == 0 && defViewOwner != 0)
            Parent = Native.FindWindowEx(0, defViewOwner, "WorkerW", null);
        if (Parent == 0 || IconView == 0 || Native.FindWindowEx(Parent, 0, "SHELLDLL_DefView", null) != 0)
            throw new InvalidOperationException("当前 Windows 桌面结构尚不支持，系统壁纸保持不变。");
        Native.GetWindowThreadProcessId(IconView, out var pid);
        explorer = Native.OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid);
        if (explorer != 0) hitMemory = Native.VirtualAllocEx(explorer, 0, HitSize, 0x3000, 0x04);
        if (hitMemory == 0) throw new InvalidOperationException("无法安全检测桌面图标，已取消应用。");
    }

    internal void Attach(Form form)
    {
        var style = (long)Native.GetWindowLongPtr(form.Handle, -16);
        Native.SetWindowLongPtr(form.Handle, -16, (nint)((style & ~0x80000000L) | 0x40000000L));
        Native.SetParent(form.Handle, Parent);
        if (Native.GetParent(form.Handle) != Parent) throw new InvalidOperationException("无法挂接 Windows 桌面。");
        var screen = Screen.PrimaryScreen!.Bounds;
        var origin = new Native.POINT(screen.Left, screen.Top);
        Native.ScreenToClient(Parent, ref origin);
        Native.SetWindowPos(form.Handle, new nint(1), origin.X, origin.Y, screen.Width, screen.Height, 0x0010 | 0x0020 | 0x0040);
    }

    internal bool IsEmptyDesktop(Native.POINT screenPoint)
    {
        if (!Native.IsWindow(IconView) || !Native.IsWindow(Parent)) return false;
        var hit = Native.WindowFromPoint(screenPoint);
        var cls = Native.Class(hit);
        if (hit != IconView && cls != "SHELLDLL_DefView" && cls != "WorkerW" && cls != "Progman") return false;
        // A similarly named window belonging to another process is not the shell.
        Native.GetWindowThreadProcessId(hit, out var hitPid);
        Native.GetWindowThreadProcessId(IconView, out var shellPid);
        if (hitPid != shellPid) return false;
        var local = screenPoint;
        if (!Native.ScreenToClient(IconView, ref local)) return false;
        var test = new Native.HitTest { Point = local, Item = -1 };
        if (!Native.WriteProcessMemory(explorer, hitMemory, ref test, HitSize, out var written) || written != HitSize) return false;
        // LVM_HITTEST requires memory in Explorer, not a pointer into this process.
        if (Native.SendMessageTimeout(IconView, 0x1012, 0, hitMemory, 2, 50, out _) == 0) return false;
        if (!Native.ReadProcessMemory(explorer, hitMemory, out test, HitSize, out var read) || read != HitSize) return false;
        return test.Item < 0 && (test.Flags & (2 | 4 | 8)) == 0;
    }

    internal static bool FullyCovered(Rectangle bounds, nint wallpaper)
    {
        var uncovered = new List<Rectangle> { bounds };
        Native.EnumWindows((window, _) =>
        {
            if (window == wallpaper || !Native.IsWindowVisible(window) || Native.IsIconic(window)) return true;
            var cls = Native.Class(window);
            if (cls is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (Native.DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            // Layered and transparent windows cannot prove an area is fully opaque.
            if (((long)Native.GetWindowLongPtr(window, -20) & (0x00080000 | 0x00000020)) != 0) return true;
            if (Native.DwmGetWindowRect(window, 9, out var rect, Marshal.SizeOf<Native.RECT>()) != 0 && !Native.GetWindowRect(window, out rect)) return true;
            var cover = rect.Rectangle;
            var next = new List<Rectangle>();
            foreach (var area in uncovered) Subtract(area, cover, next);
            uncovered = next;
            return uncovered.Count != 0;
        }, 0);
        return uncovered.Count == 0;
    }

    private static void Subtract(Rectangle area, Rectangle cover, List<Rectangle> result)
    {
        var cut = Rectangle.Intersect(area, cover);
        if (cut.IsEmpty) { result.Add(area); return; }
        if (cut.Top > area.Top) result.Add(Rectangle.FromLTRB(area.Left, area.Top, area.Right, cut.Top));
        if (cut.Bottom < area.Bottom) result.Add(Rectangle.FromLTRB(area.Left, cut.Bottom, area.Right, area.Bottom));
        if (cut.Left > area.Left) result.Add(Rectangle.FromLTRB(area.Left, cut.Top, cut.Left, cut.Bottom));
        if (cut.Right < area.Right) result.Add(Rectangle.FromLTRB(cut.Right, cut.Top, area.Right, cut.Bottom));
    }

    public void Dispose()
    {
        if (hitMemory != 0) Native.VirtualFreeEx(explorer, hitMemory, 0, 0x8000);
        if (explorer != 0) Native.CloseHandle(explorer);
        explorer = hitMemory = Parent = IconView = 0;
    }
}

internal sealed class DesktopMouse : IDisposable
{
    private readonly Native.MouseProc callback;
    private nint hook;
    internal DesktopMouse(Control dispatcher, Action<uint, Native.POINT> receive)
    {
        callback = (code, message, pointer) =>
        {
            if (code >= 0 && ((uint)message is 0x0201 or 0x0202 or 0x0200))
            {
                var data = Marshal.PtrToStructure<Native.MouseData>(pointer);
                if (!dispatcher.IsDisposed && dispatcher.IsHandleCreated)
                {
                    try { dispatcher.BeginInvoke(() => receive((uint)message, data.Point)); }
                    catch (InvalidOperationException) { }
                }
            }
            // Always forward unchanged: no desktop or application input is consumed.
            return Native.CallNextHookEx(hook, code, message, pointer);
        };
        hook = Native.SetWindowsHookEx(14, callback, Native.GetModuleHandle(null), 0);
        if (hook == 0) throw new InvalidOperationException("无法启用桌面鼠标观察，请重新应用主题。");
    }
    public void Dispose() { if (hook != 0) Native.UnhookWindowsHookEx(hook); hook = 0; }
}
