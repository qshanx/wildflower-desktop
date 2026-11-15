using System.Runtime.InteropServices;
using System.Text;

namespace WildflowerDesktop;

internal static class Native
{
    internal delegate bool EnumProc(nint window, nint param);
    internal delegate nint MouseProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Rectangle Rectangle => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseData { public POINT Point; public uint MouseDataValue, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct HitTest { public POINT Point; public uint Flags; public int Item, SubItem, Group; }
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint parent, EnumProc callback, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string? className, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string? className, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int count);
    internal static string Class(nint window) { var b = new StringBuilder(256); GetClassName(window, b, b.Capacity); return b.ToString(); }
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll")] internal static extern nint GetParent(nint window);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool GetClientRect(nint window, out RECT rect);
    [DllImport("user32.dll")] internal static extern bool ScreenToClient(nint window, ref POINT point);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(nint window, ref POINT point);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(POINT point);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool EnableWindow(nint window, bool enabled);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] internal static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int id, MouseProc callback, nint module, uint thread);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint VirtualAllocEx(nint process, nint address, nuint size, uint allocation, uint protection);
    [DllImport("kernel32.dll")] internal static extern bool VirtualFreeEx(nint process, nint address, nuint size, uint type);
    [DllImport("kernel32.dll")] internal static extern bool WriteProcessMemory(nint process, nint address, ref HitTest value, nuint size, out nuint written);
    [DllImport("kernel32.dll")] internal static extern bool ReadProcessMemory(nint process, nint address, out HitTest value, nuint size, out nuint read);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int DwmGetWindowRect(nint window, uint attribute, out RECT rect, int size);
}
