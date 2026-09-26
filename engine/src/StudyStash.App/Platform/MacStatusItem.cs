using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;

namespace StudyStash.App.Platform;

/// <summary>
/// The real Mac menu bar icon (an NSStatusItem, made straight from AppKit): Avalonia's own <c>TrayIcon</c> never
/// raises <c>Clicked</c> on macOS, so left-clicking it did nothing. Left click (or <see cref="PerformClick"/>, for
/// the self-test) opens the dropdown under the icon; right click (or Control-click) shows the fallback menu.
/// Windows keeps Avalonia's <c>TrayIcon</c>, which works there.
/// </summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacStatusItem
{
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(ObjC)] static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(ObjC)] static extern bool class_addMethod(IntPtr cls, IntPtr sel, delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> imp, string types);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr r, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendId(IntPtr r, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void SendVoidId(IntPtr r, IntPtr sel, IntPtr a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void SendVoidBool(IntPtr r, IntPtr sel, [MarshalAs(UnmanagedType.I1)] bool a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void SendVoidLong(IntPtr r, IntPtr sel, nint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void SendVoidULong(IntPtr r, IntPtr sel, nuint a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nint SendGetLong(IntPtr r, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern nuint SendGetULong(IntPtr r, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendDouble(IntPtr r, IntPtr sel, double a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern void SendSize(IntPtr r, IntPtr sel, CGSize a);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern bool SendPopUp(IntPtr r, IntPtr sel, IntPtr item, CGPoint at, IntPtr view);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendData(IntPtr r, IntPtr sel, byte* bytes, nuint len);
    [DllImport(ObjC, EntryPoint = "objc_msgSend", CharSet = CharSet.Ansi)]
    static extern IntPtr SendString(IntPtr r, IntPtr sel, [MarshalAs(UnmanagedType.LPUTF8Str)] string s);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr SendIdSelStr(IntPtr r, IntPtr sel, IntPtr title, IntPtr action, IntPtr key);
    // A CGRect (32 bytes) comes back through a hidden pointer on Intel, but plainly through objc_msgSend on Apple silicon.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern CGRect SendRectArm(IntPtr r, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] static extern void SendRectIntel(CGRect* ret, IntPtr r, IntPtr sel);

    [StructLayout(LayoutKind.Sequential)]
    struct CGPoint
    {
        public double X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CGSize
    {
        public double Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
    }

    static CGRect Frame(IntPtr view)
    {
        IntPtr window = Send(view, sel_registerName("window"));
        if (window == IntPtr.Zero) return default;
        IntPtr sel = sel_registerName("frame");
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            CGRect r;
            SendRectIntel(&r, window, sel);
            return r;
        }
        return SendRectArm(window, sel);
    }

    // --- what's kept alive for the app's life ----------------------------------------------------------------------

    static IntPtr statusItem, button, menu, targetClass, target;
    static readonly Action?[] menuActions = new Action?[5];
    static Action<double>? onLeftClick;

    /// <summary>Puts the icon in the menu bar, wired to what the tray's menu items do. Call once, at startup.</summary>
    public static void Create(Action<double> leftClick, Action record, Action search, Action open, Action settings, Action quit)
    {
        onLeftClick = leftClick;
        menuActions[0] = record;
        menuActions[1] = search;
        menuActions[2] = open;
        menuActions[3] = settings;
        menuActions[4] = quit;

        targetClass = objc_allocateClassPair(objc_getClass("NSObject"), "StudyStashStatusTarget", 0);
        class_addMethod(targetClass, sel_registerName("clicked:"), &OnAction, "v@:@");
        objc_registerClassPair(targetClass);
        target = Send(Send(targetClass, sel_registerName("alloc")), sel_registerName("init"));

        var statusBar = Send(objc_getClass("NSStatusBar"), sel_registerName("systemStatusBar"));
        statusItem = SendDouble(statusBar, sel_registerName("statusItemWithLength:"), -1 /* NSVariableStatusItemLength */);
        button = Send(statusItem, sel_registerName("button"));
        SendVoidLong(button, sel_registerName("setTag:"), -1);
        SendVoidId(button, sel_registerName("setTarget:"), target);
        SendVoidId(button, sel_registerName("setAction:"), sel_registerName("clicked:"));
        SendVoidULong(button, sel_registerName("sendActionOn:"), (1u << 2) | (1u << 4)); // NSEventMaskLeftMouseUp | RightMouseUp

        menu = Send(Send(objc_getClass("NSMenu"), sel_registerName("alloc")), sel_registerName("init"));
        MenuItem("Record", 0);
        MenuItem("Search notes and lectures", 1);
        MenuItem("Open Study Stash", 2);
        MenuItem("Settings…", 3);
        SendVoidId(menu, sel_registerName("addItem:"), Send(objc_getClass("NSMenuItem"), sel_registerName("separatorItem")));
        MenuItem("Quit Study Stash", 4);
    }

    static IntPtr NSString(string s) => SendString(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), s);

    static void MenuItem(string title, nint tag)
    {
        IntPtr item = SendIdSelStr(menu, sel_registerName("addItemWithTitle:action:keyEquivalent:"),
            NSString(title), sel_registerName("clicked:"), NSString(""));
        SendVoidId(item, sel_registerName("setTarget:"), target);
        SendVoidLong(item, sel_registerName("setTag:"), tag);
    }

    /// <summary>The icon: a template image (the menu bar tints it) at 18×18pt, with a red dot while recording.</summary>
    public static void SetIcon(byte[] png)
    {
        if (button == IntPtr.Zero) return;
        fixed (byte* p = png)
        {
            IntPtr data = SendData(objc_getClass("NSData"), sel_registerName("dataWithBytes:length:"), p, (nuint)png.Length);
            IntPtr image = SendId(Send(objc_getClass("NSImage"), sel_registerName("alloc")), sel_registerName("initWithData:"), data);
            if (image == IntPtr.Zero) return;
            SendVoidBool(image, sel_registerName("setTemplate:"), true);
            SendSize(image, sel_registerName("setSize:"), new CGSize { Width = 18, Height = 18 });
            SendVoidId(button, sel_registerName("setImage:"), image);
        }
    }

    /// <summary>The icon's window frame, in Cocoa points (origin bottom left, like <c>NSEvent.mouseLocation</c>).
    /// Null before the icon exists.</summary>
    public static (double X, double Y, double Width, double Height)? ButtonFrame()
    {
        if (button == IntPtr.Zero) return null;
        var f = Frame(button);
        return (f.Origin.X, f.Origin.Y, f.Size.Width, f.Size.Height);
    }

    /// <summary>The self-test: opens the dropdown the way a real left click would, from the icon's own position (no
    /// real mouse event to read, so this calls straight through rather than simulating one).</summary>
    public static void PerformClick()
    {
        if (ButtonFrame() is not { } f) return;
        onLeftClick?.Invoke(f.X + f.Width / 2);
    }

    /// <summary>Takes the icon out of the menu bar (quitting).</summary>
    public static void Destroy()
    {
        if (statusItem == IntPtr.Zero) return;
        var statusBar = Send(objc_getClass("NSStatusBar"), sel_registerName("systemStatusBar"));
        SendVoidId(statusBar, sel_registerName("removeStatusItem:"), statusItem);
        statusItem = button = IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    static void OnAction(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        nint tag = SendGetLong(sender, sel_registerName("tag"));
        if (tag != -1)
        {
            var act = tag is >= 0 and < 5 ? menuActions[(int)tag] : null;
            if (act is not null) Dispatcher.UIThread.Post(act);
            return;
        }
        var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
        var current = Send(app, sel_registerName("currentEvent"));
        nuint type = current == IntPtr.Zero ? 0 : SendGetULong(current, sel_registerName("type"));
        if (type == 3 || type == 4) // NSEventTypeRightMouseDown / RightMouseUp: the fallback menu
            SendPopUp(menu, sel_registerName("popUpMenuPositioningItem:atLocation:inView:"), IntPtr.Zero, default, button);
        else if (ButtonFrame() is { } f)
            Dispatcher.UIThread.Post(() => onLeftClick?.Invoke(f.X + f.Width / 2));
    }
}
