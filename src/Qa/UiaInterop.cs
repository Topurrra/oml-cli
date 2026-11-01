using System.IO.Compression;
using System.Runtime.InteropServices;

namespace OmlCli.Qa;

// Windows UI Automation through its COM interfaces (UIAutomationClient.h), declared here instead of a package.
// A COM interface is a vtable: every method up to the last one used is declared in header order; the ones oml does
// not call are placeholders (_n) that only hold their slot. Methods after the last used one are left out.
static class Uia
{
    public const int TreeScopeDescendants = 4, TreeScopeSubtree = 7;

    // Property ids (UIAutomationClient.h, UIA_*PropertyId).
    public const int BoundingRectangle = 30001, ControlType = 30003, LocalizedControlType = 30004, Name = 30005,
        HasKeyboardFocus = 30008, IsEnabled = 30010, AutomationId = 30011, ClassName = 30012, HelpText = 30013,
        IsPassword = 30019, IsOffscreen = 30022, IsRequiredForForm = 30025,
        IsExpandCollapsePatternAvailable = 30028, IsInvokePatternAvailable = 30031, IsScrollItemPatternAvailable = 30035,
        IsSelectionItemPatternAvailable = 30036, IsTogglePatternAvailable = 30041, IsValuePatternAvailable = 30043,
        ValueValue = 30045, ValueIsReadOnly = 30046, SelectionItemIsSelected = 30079, LegacyState = 30096, AriaRole = 30101;

    // Control type ids (UIA_*ControlTypeId).
    public const int Button = 50000, CheckBox = 50002, ComboBox = 50003, Edit = 50004, Hyperlink = 50005, Image = 50006,
        ListItem = 50007, MenuItem = 50011, RadioButton = 50013, TabItem = 50019, Text = 50020, Custom = 50025, Group = 50026,
        DataGrid = 50028, DataItem = 50029, Document = 50030, SplitButton = 50031, Pane = 50033, HeaderItem = 50035, Table = 50036;

    // Pattern ids (UIA_*PatternId).
    public const int InvokePattern = 10000, ValuePattern = 10002, ExpandCollapsePattern = 10005, SelectionItemPattern = 10010,
        TogglePattern = 10015, ScrollItemPattern = 10017, LegacyIAccessiblePattern = 10018;

    public const int StateBusy = 0x800; // STATE_SYSTEM_BUSY in LegacyIAccessible.State

    static readonly Guid CUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");

    public static IUIAutomation Create() =>
        (IUIAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(CUIAutomation8, throwOnError: true)!)!;

    // A cached property, or null when the provider does not support it (UIA answers with a "not supported" object).
    public static object? Cached(IUIAutomationElement e, int property)
    {
        var v = e.GetCachedPropertyValue(property);
        return v is string or bool or int or double or double[] ? v : null;
    }

    public static object? Current(IUIAutomationElement e, int property)
    {
        var v = e.GetCurrentPropertyValue(property);
        return v is string or bool or int or double or double[] ? v : null;
    }

    // What a UI Automation call throws when the element or the action is not available: COM errors, and the .NET
    // exceptions that some UIA error codes map to (an element that went away, an action the state does not allow).
    public static bool Failed(Exception e) => e is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or TimeoutException;

    public static IEnumerable<IUIAutomationElement> Items(IUIAutomationElementArray? array)
    {
        if (array is null) yield break;
        for (var i = 0; i < array.Length; i++) yield return array.GetElement(i);
    }
}

[ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomation
{
    void _CompareElements();
    void _CompareRuntimeIds();
    void _GetRootElement();
    IUIAutomationElement ElementFromHandle(IntPtr hwnd);
    void _ElementFromPoint();
    void _GetFocusedElement();
    void _GetRootElementBuildCache();
    void _ElementFromHandleBuildCache();
    void _ElementFromPointBuildCache();
    void _GetFocusedElementBuildCache();
    void _CreateTreeWalker();
    void _ControlViewWalker();
    void _ContentViewWalker();
    void _RawViewWalker();
    void _RawViewCondition();
    void _ControlViewCondition();
    void _ContentViewCondition();
    IUIAutomationCacheRequest CreateCacheRequest();
    IUIAutomationCondition CreateTrueCondition();
    void _CreateFalseCondition();
    IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    void _CreatePropertyConditionEx();
    void _CreateAndCondition();
    void _CreateAndConditionFromArray();
    void _CreateAndConditionFromNativeArray();
    void _CreateOrCondition();
    void _CreateOrConditionFromArray();
    void _CreateOrConditionFromNativeArray();
    void _CreateNotCondition();
    void _AddAutomationEventHandler();
    void _RemoveAutomationEventHandler();
    void AddPropertyChangedEventHandlerNativeArray(IUIAutomationElement element, int scope, IUIAutomationCacheRequest? request,
        IUIAutomationPropertyChangedEventHandler handler, [MarshalAs(UnmanagedType.LPArray)] int[] properties, int count);
    void _AddPropertyChangedEventHandler();
    void _RemovePropertyChangedEventHandler();
    void AddStructureChangedEventHandler(IUIAutomationElement element, int scope, IUIAutomationCacheRequest? request, IUIAutomationStructureChangedEventHandler handler);
    void _RemoveStructureChangedEventHandler();
    void _AddFocusChangedEventHandler();
    void _RemoveFocusChangedEventHandler();
    void RemoveAllEventHandlers();
}

// Event callbacks: UI Automation calls them on its own threads. oml's handlers only note the time.
[ComImport, Guid("e81d1b4e-11c5-42f8-9754-e7036c79f054"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationStructureChangedEventHandler
{
    void HandleStructureChangedEvent(IUIAutomationElement sender, int changeType, [MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[] runtimeId);
}

[ComImport, Guid("40cd37d4-c756-4b0c-8c6f-bddfeeb13b50"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationPropertyChangedEventHandler
{
    void HandlePropertyChangedEvent(IUIAutomationElement sender, int propertyId, [MarshalAs(UnmanagedType.Struct)] object newValue);
}

[ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationCondition { }

[ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationCacheRequest
{
    void AddProperty(int propertyId);
    void AddPattern(int patternId);
    void _Clone();
    void _GetTreeScope();
    void SetTreeScope(int scope);
    void _GetTreeFilter();
    void SetTreeFilter(IUIAutomationCondition filter);
}

[ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationElementArray
{
    int Length { get; }
    IUIAutomationElement GetElement(int index);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationElement
{
    void SetFocus();
    void _GetRuntimeId();
    IUIAutomationElement? FindFirst(int scope, IUIAutomationCondition condition);
    IUIAutomationElementArray? FindAll(int scope, IUIAutomationCondition condition);
    void _FindFirstBuildCache();
    void _FindAllBuildCache();
    IUIAutomationElement BuildUpdatedCache(IUIAutomationCacheRequest request);
    [return: MarshalAs(UnmanagedType.Struct)] object? GetCurrentPropertyValue(int propertyId);
    void _GetCurrentPropertyValueEx();
    [return: MarshalAs(UnmanagedType.Struct)] object? GetCachedPropertyValue(int propertyId);
    void _GetCachedPropertyValueEx();
    void _GetCurrentPatternAs();
    void _GetCachedPatternAs();
    [return: MarshalAs(UnmanagedType.IUnknown)] object? GetCurrentPattern(int patternId);
    void _GetCachedPattern();
    void _GetCachedParent();
    IUIAutomationElementArray? GetCachedChildren();
}

[ComImport, Guid("fb377fbe-8ea6-46d5-9c73-6499642d3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationInvokePattern { void Invoke(); }

[ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationValuePattern { void SetValue([MarshalAs(UnmanagedType.BStr)] string value); }

[ComImport, Guid("94cf8058-9b8d-4ab9-8bfd-4cd0a33c8c70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationTogglePattern { void Toggle(); }

[ComImport, Guid("a8efa66a-0fda-421a-9194-38021f3578ea"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationSelectionItemPattern { void Select(); }

[ComImport, Guid("619be086-1f4e-4ee4-bafa-210128738730"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationExpandCollapsePattern { void Expand(); void Collapse(); }

[ComImport, Guid("b488300f-d015-4f19-9c29-bb595e3645ef"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationScrollItemPattern { void ScrollIntoView(); }

[ComImport, Guid("828055ad-355b-4435-86d5-3b51c14a9b1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IUIAutomationLegacyIAccessiblePattern { void Select(int flags); void DoDefaultAction(); }

// The Win32 calls the UI Automation engine needs: find the browser's windows, close one, give the foreground back,
// and copy a window to a PNG (PrintWindow with PW_RENDERFULLCONTENT also renders an off-screen Chromium window).
static class Win32
{
    public const int WmClose = 0x0010, SmXVirtualScreen = 76, SmYVirtualScreen = 77;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, char[] name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, int flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, int usage, out IntPtr bits, IntPtr section, int offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfo { public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant; }

    // The visible, titled Chromium top-level windows of a process.
    public static List<IntPtr> BrowserWindows(int pid)
    {
        var found = new List<IntPtr>();
        var name = new char[64];
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out var owner) != 0 && owner == pid && IsWindowVisible(hwnd) && GetWindowTextLength(hwnd) > 0
                && new string(name, 0, GetClassName(hwnd, name, name.Length)) == "Chrome_WidgetWin_1")
                found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static byte[]? CapturePng(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return null;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0) return null;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BitmapInfo { Size = Marshal.SizeOf<BitmapInfo>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        var old = SelectObject(dc, bitmap);
        try
        {
            if (!PrintWindow(hwnd, dc, 2)) return null; // PW_RENDERFULLCONTENT
            var bgra = new byte[width * height * 4];
            Marshal.Copy(bits, bgra, 0, bgra.Length);
            return Png(bgra, width, height);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // A minimal PNG writer: 8-bit RGB, filter 0 on every row, zlib from System.IO.Compression.
    static byte[] Png(byte[] bgra, int width, int height)
    {
        var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                for (int x = 0, s = y * width * 4, d = 1; x < width; x++, s += 4, d += 3)
                    (row[d], row[d + 1], row[d + 2]) = (bgra[s + 2], bgra[s + 1], bgra[s]);
                z.Write(row);
            }
        }
        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        (header[8], header[9]) = (8, 2); // 8 bits, RGB
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, head.AsSpan(4));
        s.Write(head);
        s.Write(data);
        var crc = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(head.AsSpan(4), data));
        s.Write(crc);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
