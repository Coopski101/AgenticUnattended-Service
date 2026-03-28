using System.Runtime.InteropServices;

namespace AgenticUnattended.Platform.macOS;

internal static partial class MacNative
{
    private const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(ObjCLib, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint objc_getClass(string className);

    [LibraryImport(ObjCLib, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint sel_registerName(string selectorName);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    public static partial nint objc_msgSend(nint receiver, nint selector);

    [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
    public static partial int objc_msgSend_int(nint receiver, nint selector);

    [LibraryImport(CoreGraphicsLib)]
    public static partial double CGEventSourceSecondsSinceLastEventType(int stateId, ulong eventTypeMask);

    public const int kCGEventSourceStateCombinedSessionState = 0;
    public const ulong kCGAnyInputEventType = unchecked((ulong)~0);

    public static nint GetSharedWorkspace()
    {
        var cls = objc_getClass("NSWorkspace");
        var sel = sel_registerName("sharedWorkspace");
        return objc_msgSend(cls, sel);
    }

    public static nint GetFrontmostApplication(nint workspace)
    {
        var sel = sel_registerName("frontmostApplication");
        return objc_msgSend(workspace, sel);
    }

    public static int GetProcessIdentifier(nint runningApp)
    {
        var sel = sel_registerName("processIdentifier");
        return objc_msgSend_int(runningApp, sel);
    }

    public static string? GetLocalizedName(nint runningApp)
    {
        var sel = sel_registerName("localizedName");
        var nsString = objc_msgSend(runningApp, sel);
        if (nsString == nint.Zero) return null;
        var utf8Sel = sel_registerName("UTF8String");
        var ptr = objc_msgSend(nsString, utf8Sel);
        return ptr == nint.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }
}
