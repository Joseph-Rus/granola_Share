using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace StudyStash.App.Platform;

/// <summary>What a global shortcut does.</summary>
public enum Shortcut
{
    /// <summary>⌥Space on a Mac, Alt+Shift+Space on Windows: the quick panel.</summary>
    Quick = 1,
    /// <summary>⌥⇧R on a Mac, Ctrl+Alt+R on Windows: start or stop recording.</summary>
    Record = 2,
}

/// <summary>Whether each shortcut is registered, so Settings can say which one (if any) is taken by another app.</summary>
public readonly record struct HotkeyResult(bool Quick, bool Record)
{
    public bool All => Quick && Record;
}

/// <summary>
/// The app's shortcuts, working from any app: Carbon's hot keys on a Mac (no permission needed), RegisterHotKey on
/// Windows. Pressed, they call back on the UI thread. <see cref="Register"/> can be called again after
/// <see cref="Unregister"/> (the Shortcuts toggle in Settings turns them on and off without restarting).
/// </summary>
public static class Hotkeys
{
    static Action<Shortcut>? pressed;

    public static HotkeyResult Register(Action<Shortcut> onPressed)
    {
        pressed = onPressed;
        try
        {
            if (OperatingSystem.IsMacOS()) return Mac.Register();
            if (OperatingSystem.IsWindows()) return Win.Register();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return new HotkeyResult(false, false);
    }

    /// <summary>Let both shortcuts go: another app (or nobody) can have them until <see cref="Register"/> again.</summary>
    public static void Unregister()
    {
        try
        {
            if (OperatingSystem.IsMacOS()) Mac.Unregister();
            else if (OperatingSystem.IsWindows()) Win.Unregister();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    static void Fire(Shortcut s) => Avalonia.Threading.Dispatcher.UIThread.Post(() => pressed?.Invoke(s));

    [SupportedOSPlatform("macos")]
    static unsafe class Mac
    {
        const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
        const uint Cmd = 0x0100, Shift = 0x0200, Option = 0x0800;
        const uint KeySpace = 0x31, KeyR = 0x0F;
        const uint KeyboardClass = 0x6B657962; // 'keyb'
        const uint HotKeyPressed = 5;
        const uint Signature = 0x53747348; // 'StsH'

        [StructLayout(LayoutKind.Sequential)]
        struct EventTypeSpec
        {
            public uint Class, Kind;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HotKeyId
        {
            public uint Signature, Id;
        }

        [DllImport(Carbon)]
        static extern IntPtr GetApplicationEventTarget();

        [DllImport(Carbon)]
        static extern int InstallEventHandler(IntPtr target, delegate* unmanaged<IntPtr, IntPtr, IntPtr, int> handler, nuint count, EventTypeSpec* types,
            IntPtr userData, IntPtr* outRef);

        [DllImport(Carbon)]
        static extern int RegisterEventHotKey(uint keyCode, uint modifiers, HotKeyId id, IntPtr target, uint options, IntPtr* outRef);

        [DllImport(Carbon)]
        static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

        [DllImport(Carbon)]
        static extern int GetEventParameter(IntPtr evt, uint name, uint type, IntPtr outType, nuint size, IntPtr outSize, void* data);

        static bool installed;
        static IntPtr quickRef, recordRef;

        public static HotkeyResult Register()
        {
            IntPtr target = GetApplicationEventTarget();
            if (!installed)
            {
                var spec = new EventTypeSpec { Class = KeyboardClass, Kind = HotKeyPressed };
                IntPtr handler;
                if (InstallEventHandler(target, &OnHotKey, 1, &spec, IntPtr.Zero, &handler) != 0) return new HotkeyResult(false, false);
                installed = true;
            }
            IntPtr a, b;
            bool quick = RegisterEventHotKey(KeySpace, Option, new HotKeyId { Signature = Signature, Id = (uint)Shortcut.Quick }, target, 0, &a) == 0;
            bool record = RegisterEventHotKey(KeyR, Option | Shift, new HotKeyId { Signature = Signature, Id = (uint)Shortcut.Record }, target, 0, &b) == 0;
            quickRef = quick ? a : IntPtr.Zero;
            recordRef = record ? b : IntPtr.Zero;
            return new HotkeyResult(quick, record);
        }

        public static void Unregister()
        {
            if (quickRef != IntPtr.Zero) UnregisterEventHotKey(quickRef);
            if (recordRef != IntPtr.Zero) UnregisterEventHotKey(recordRef);
            quickRef = recordRef = IntPtr.Zero;
        }

        [UnmanagedCallersOnly]
        static int OnHotKey(IntPtr call, IntPtr evt, IntPtr user)
        {
            HotKeyId id;
            if (GetEventParameter(evt, 0x2D2D2D2D /* '----' */, 0x686B6964 /* 'hkid' */, IntPtr.Zero, (nuint)sizeof(HotKeyId), IntPtr.Zero, &id) == 0
                && id.Signature == Signature)
                Fire((Shortcut)id.Id);
            return 0;
        }
    }

    [SupportedOSPlatform("windows")]
    static unsafe class Win
    {
        const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;
        const uint VkSpace = 0x20, VkR = 0x52, WmHotKey = 0x0312;
        static readonly IntPtr MessageOnly = new(-3);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WndClass
        {
            public uint Size, Style;
            public delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> Proc;
            public int ClsExtra, WndExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public IntPtr MenuName, ClassName;
            public IntPtr IconSmall;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern ushort RegisterClassExW(WndClass* cls);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

        [DllImport("user32.dll")]
        static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

        [DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string? name);

        static IntPtr window;

        public static HotkeyResult Register()
        {
            if (window == IntPtr.Zero)
            {
                IntPtr name = Marshal.StringToHGlobalUni("StudyStashHotkeys");
                var cls = new WndClass { Size = (uint)sizeof(WndClass), Proc = &Proc, Instance = GetModuleHandleW(null), ClassName = name };
                RegisterClassExW(&cls);
                window = CreateWindowExW(0, "StudyStashHotkeys", "Study Stash", 0, 0, 0, 0, 0, MessageOnly, IntPtr.Zero, cls.Instance, IntPtr.Zero);
                if (window == IntPtr.Zero) return new HotkeyResult(false, false);
            }
            bool quick = RegisterHotKey(window, (int)Shortcut.Quick, ModAlt | ModShift | ModNoRepeat, VkSpace);
            bool record = RegisterHotKey(window, (int)Shortcut.Record, ModControl | ModAlt | ModNoRepeat, VkR);
            return new HotkeyResult(quick, record);
        }

        public static void Unregister()
        {
            if (window == IntPtr.Zero) return;
            UnregisterHotKey(window, (int)Shortcut.Quick);
            UnregisterHotKey(window, (int)Shortcut.Record);
        }

        [UnmanagedCallersOnly]
        static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        {
            if (msg == WmHotKey)
            {
                Fire((Shortcut)(int)w);
                return IntPtr.Zero;
            }
            return DefWindowProcW(hwnd, msg, w, l);
        }
    }
}
