using System.Runtime.InteropServices;

namespace RumpSharp.Interop;

/// <summary>
/// Minimal hand-rolled bindings to the Objective-C runtime. This keeps RumpSharp free of the
/// .NET "macos" workload: it is a plain <c>net10.0</c> library that talks to
/// <c>libobjc</c> directly, exactly like PyObjC does for the Python <c>rumps</c> package.
/// </summary>
internal static unsafe partial class ObjC
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    // ---------------------------------------------------------------- classes / selectors

    [LibraryImport(LibObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GetClass(string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_lookUpClass", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr LookUpClass(string name);

    [LibraryImport(LibObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GetSelector(string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr AllocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [LibraryImport(LibObjC, EntryPoint = "objc_registerClassPair")]
    internal static partial void RegisterClassPair(IntPtr cls);

    [LibraryImport(LibObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool AddMethod(IntPtr cls, IntPtr selector, IntPtr imp, string types);

    [LibraryImport(LibObjC, EntryPoint = "class_addProtocol")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool AddProtocol(IntPtr cls, IntPtr protocol);

    [LibraryImport(LibObjC, EntryPoint = "objc_getProtocol", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr GetProtocol(string name);

    // ---------------------------------------------------------------- objc_msgSend variants
    // objc_msgSend is variadic in C; every distinct call signature therefore needs its own
    // declaration so the managed marshaller lays the arguments out the way the ABI expects.

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr Send(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a1, IntPtr a2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a1, IntPtr a2, IntPtr a3);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(IntPtr receiver, IntPtr selector, IntPtr a1);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(IntPtr receiver, IntPtr selector, IntPtr a1, IntPtr a2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial void SendVoid(IntPtr receiver, IntPtr selector, ulong a1, IntPtr a2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool SendBool(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial long SendLong(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    internal static partial IntPtr SendDoubleBool(
        IntPtr receiver,
        IntPtr selector,
        double a1,
        [MarshalAs(UnmanagedType.U1)] bool a2);

    /// <summary>
    /// Raw address of <c>objc_msgSend</c>. Used with function pointers for the less common
    /// signatures (mixed integer/pointer arguments) instead of declaring a P/Invoke for each.
    /// </summary>
    private static readonly IntPtr MsgSend = Native.DlSym(Native.RtldDefault, "objc_msgSend");

    /// <summary>obj.selector(ptr, ptr, ptr, out error)</summary>
    internal static IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr a1, IntPtr a2, IntPtr a3, IntPtr a4) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)MsgSend)(receiver, selector, a1, a2, a3, a4);

    /// <summary>obj.selector(ptr, ptr, uint64)</summary>
    internal static IntPtr SendPtrPtrULong(IntPtr receiver, IntPtr selector, IntPtr a1, IntPtr a2, ulong a3) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, ulong, IntPtr>)MsgSend)(receiver, selector, a1, a2, a3);

    /// <summary>obj.selector(ptr, ptr, uint64, ptr, ptr)</summary>
    internal static IntPtr SendPtrPtrULongPtrPtr(IntPtr receiver, IntPtr selector, IntPtr a1, IntPtr a2, ulong a3, IntPtr a4, IntPtr a5) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, ulong, IntPtr, IntPtr, IntPtr>)MsgSend)(receiver, selector, a1, a2, a3, a4, a5);

    /// <summary>obj.selector(ptr, ptr, ptr, uint64)</summary>
    internal static IntPtr SendPtrPtrPtrULong(IntPtr receiver, IntPtr selector, IntPtr a1, IntPtr a2, IntPtr a3, ulong a4) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, ulong, IntPtr>)MsgSend)(receiver, selector, a1, a2, a3, a4);

    /// <summary>obj.selector(ptr, count)</summary>
    internal static IntPtr SendPtrCount(IntPtr receiver, IntPtr selector, IntPtr a1, nuint a2) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, nuint, IntPtr>)MsgSend)(receiver, selector, a1, a2);

    /// <summary>Whether an object implements a selector.</summary>
    internal static bool RespondsTo(IntPtr receiver, IntPtr selector) =>
        receiver != IntPtr.Zero
        && ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, byte>)MsgSend)(receiver, Sel.RespondsToSelector, selector) != 0;

    /// <summary>obj.selector(uint64) - e.g. <c>objectAtIndex:</c>.</summary>
    internal static IntPtr SendULong(IntPtr receiver, IntPtr selector, ulong a1) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, ulong, IntPtr>)MsgSend)(receiver, selector, a1);

    /// <summary>obj.selector(int32)</summary>
    internal static IntPtr SendInt(IntPtr receiver, IntPtr selector, int a1) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, IntPtr>)MsgSend)(receiver, selector, a1);

    /// <summary>obj.selector(int64)</summary>
    internal static void SendVoidLong(IntPtr receiver, IntPtr selector, long a1) =>
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, long, void>)MsgSend)(receiver, selector, a1);

    // ---------------------------------------------------------------- convenience helpers

    /// <summary>Sends <c>alloc</c> followed by <c>init</c>.</summary>
    internal static IntPtr New(IntPtr cls) => Send(Send(cls, Sel.Alloc), Sel.Init);

    /// <summary>Wraps a managed string in an autoreleased <c>NSString</c>.</summary>
    internal static IntPtr NSString(string? value)
    {
        if (value is null)
        {
            return IntPtr.Zero;
        }

        var utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        if (utf8.Length == 0)
        {
            // 'fixed' over an empty array yields a null pointer, and
            // stringWithBytes:length:encoding: raises NSInvalidArgumentException ("NULL cString") for
            // one - an Objective-C exception, which aborts the process rather than surfacing in .NET.
            // Reachable from ordinary input: an empty body, or the default reply placeholder.
            return Send(Cls.NSString, Sel.EmptyString);
        }

        fixed (byte* p = utf8)
        {
            // stringWithBytes:length:encoding: avoids the NUL-termination requirement and
            // tolerates embedded NULs, unlike stringWithUTF8String:.
            return SendStringWithBytes(Cls.NSString, Sel.StringWithBytesLengthEncoding, (IntPtr)p, (nuint)utf8.Length, 4 /* NSUTF8StringEncoding */);
        }
    }

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendStringWithBytes(IntPtr receiver, IntPtr selector, IntPtr bytes, nuint length, nuint encoding);

    /// <summary>Reads an <c>NSString</c> back into managed memory.</summary>
    internal static string? FromNSString(IntPtr nsString)
    {
        if (nsString == IntPtr.Zero)
        {
            return null;
        }

        var utf8 = Send(nsString, Sel.UTF8String);
        return utf8 == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(utf8);
    }

    /// <summary>Builds an <c>NSDictionary</c> of <c>NSString</c> keys and values.</summary>
    internal static IntPtr NSDictionary(IReadOnlyDictionary<string, string> values)
    {
        var dict = Send(Cls.NSMutableDictionary, Sel.Dictionary);
        foreach (var (key, value) in values)
        {
            SendVoid(dict, Sel.SetObjectForKey, NSString(value), NSString(key));
        }

        return dict;
    }

    /// <summary>Creates a fresh autorelease pool; dispose it to drain the pool.</summary>
    internal static AutoreleasePool Pool() => new(New(Cls.NSAutoreleasePool));

    internal readonly struct AutoreleasePool(IntPtr handle) : IDisposable
    {
        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                SendVoid(handle, Sel.Drain);
            }
        }
    }
}
