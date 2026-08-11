using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace RumpSharp.Interop;

/// <summary>
/// Creates Objective-C blocks that call back into managed code, and invokes blocks handed to us by
/// the system.
/// </summary>
/// <remarks>
/// <para>
/// This implements the documented Block ABI (<c>Block_literal_1</c> / <c>Block_descriptor_1</c>)
/// including copy/dispose helpers. The helpers matter: asynchronous APIs such as
/// <c>-[UNUserNotificationCenter requestAuthorizationWithOptions:completionHandler:]</c> call
/// <c>Block_copy</c> to keep the handler alive and <c>Block_release</c> afterwards, so a block
/// without correct helpers gets released with a bogus reference count and crashes the process on a
/// dispatch worker thread.
/// </para>
/// <para>
/// <c>Block_copy</c> only copies the <em>literal</em>: the copy keeps our <c>descriptor</c> pointer
/// verbatim, and <c>Block_release</c> later reads the dispose helper back out of it. Descriptors
/// therefore have to outlive every copy the system might be holding, which is why a compiler emits
/// them into constant data and why <see cref="DescriptorFor"/> shares one per signature and never
/// frees it.
/// </para>
/// </remarks>
internal static unsafe class Block
{
    private const int BlockHasCopyDispose = 1 << 25;
    private const int BlockHasSignature = 1 << 30;

    /// <summary>Offset of the <c>invoke</c> function pointer inside a block literal.</summary>
    private const int InvokeOffset = 16;

    private static readonly IntPtr StackBlockIsa = Native.DlSym(Native.RtldDefault, "_NSConcreteStackBlock");

    /// <summary>
    /// One immortal descriptor per block signature, keyed by that signature. There are only a
    /// handful, and they must never be freed - see the remarks on <see cref="Block"/>.
    /// </summary>
    private static readonly Dictionary<string, IntPtr> Descriptors = new(StringComparer.Ordinal);

    private static readonly Lock DescriptorGate = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct Literal
    {
        public IntPtr Isa;
        public int Flags;
        public int Reserved;
        public IntPtr Invoke;
        public IntPtr Descriptor;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Descriptor
    {
        public nuint Reserved;
        public nuint Size;
        public IntPtr Copy;
        public IntPtr Dispose;
        public IntPtr Signature;
    }

    /// <summary>
    /// A block literal owned by managed code. Pass <see cref="Handle"/> to Objective-C and dispose
    /// the wrapper once the callback has arrived: anything that outlives the call has been copied by
    /// the callee, and the copy owns its own <see cref="GCHandle"/>.
    /// </summary>
    /// <remarks>
    /// Do <em>not</em> dispose while a callback may still be outstanding - if the callee happened not
    /// to copy the block, invoking it would touch freed memory. Callers that give up waiting should
    /// simply leak the scope; it is 40 bytes and one <see cref="GCHandle"/>.
    /// </remarks>
    internal readonly struct Scope : IDisposable
    {
        internal IntPtr Handle { get; init; }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero)
            {
                return;
            }

            var literal = (Literal*)Handle;
            if (literal->Context != IntPtr.Zero)
            {
                GCHandle.FromIntPtr(literal->Context).Free();
            }

            // Only the literal is ours: the descriptor is shared and immortal, because copies the
            // system made still point at it and will read its dispose helper on release.
            NativeMemory.Free((void*)Handle);
        }
    }

    /// <summary>Returns the shared, never-freed descriptor for a block signature.</summary>
    internal static IntPtr DescriptorFor(string signature)
    {
        lock (DescriptorGate)
        {
            if (Descriptors.TryGetValue(signature, out var existing))
            {
                return existing;
            }

            var descriptor = (Descriptor*)NativeMemory.AllocZeroed((nuint)sizeof(Descriptor));
            descriptor->Size = (nuint)sizeof(Literal);
            descriptor->Copy = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&CopyHelper;
            descriptor->Dispose = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&DisposeHelper;
            descriptor->Signature = Marshal.StringToHGlobalAnsi(signature);

            Descriptors[signature] = (IntPtr)descriptor;
            return (IntPtr)descriptor;
        }
    }

    private static Scope Allocate(IntPtr invoke, string signature, Delegate callback)
    {
        var descriptor = DescriptorFor(signature);
        var literal = (Literal*)NativeMemory.AllocZeroed((nuint)sizeof(Literal));

        literal->Isa = StackBlockIsa;
        literal->Flags = BlockHasCopyDispose | BlockHasSignature;
        literal->Invoke = invoke;
        literal->Descriptor = descriptor;
        literal->Context = GCHandle.ToIntPtr(GCHandle.Alloc(callback));

        return new Scope { Handle = (IntPtr)literal };
    }

    /// <summary>Called by <c>Block_copy</c>: the copy needs its own handle to the delegate.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void CopyHelper(IntPtr destination, IntPtr source)
    {
        var context = ((Literal*)source)->Context;
        ((Literal*)destination)->Context = context == IntPtr.Zero
            ? IntPtr.Zero
            : GCHandle.ToIntPtr(GCHandle.Alloc(GCHandle.FromIntPtr(context).Target));
    }

    /// <summary>Called by <c>Block_release</c> when a copy is destroyed.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DisposeHelper(IntPtr block)
    {
        var literal = (Literal*)block;
        if (literal->Context != IntPtr.Zero)
        {
            GCHandle.FromIntPtr(literal->Context).Free();
            literal->Context = IntPtr.Zero;
        }
    }

    private static T? Target<T>(IntPtr block) where T : Delegate
    {
        var context = ((Literal*)block)->Context;
        return context == IntPtr.Zero ? null : GCHandle.FromIntPtr(context).Target as T;
    }

    // ------------------------------------------------------------------ layout accessors (tests)

    /// <summary>Size of a block literal, which is what the descriptor advertises to the runtime.</summary>
    internal static int LiteralSize => sizeof(Literal);

    /// <summary>The descriptor a block literal points at.</summary>
    internal static IntPtr DescriptorOf(IntPtr block) => ((Literal*)block)->Descriptor;

    /// <summary>The <see cref="GCHandle"/> a block literal captured, as an <see cref="IntPtr"/>.</summary>
    internal static IntPtr ContextOf(IntPtr block) => ((Literal*)block)->Context;

    /// <summary>The offset at which <see cref="Invoke(IntPtr)"/> expects the invoke pointer.</summary>
    internal static int InvokePointerOffset => InvokeOffset;

    // ------------------------------------------------------------------ managed -> ObjC blocks

    /// <summary>Creates a <c>void (^)(BOOL, NSError *)</c> block (authorization requests).</summary>
    internal static Scope BoolError(Action<bool, string?> callback) =>
        Allocate((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, byte, IntPtr, void>)&InvokeBoolError, "v24@?0B8@16", callback);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void InvokeBoolError(IntPtr block, byte granted, IntPtr error)
    {
        try
        {
            Target<Action<bool, string?>>(block)?.Invoke(granted != 0, ErrorMessage(error));
        }
        catch
        {
            // Never let a managed exception unwind into Objective-C.
        }
    }

    /// <summary>Creates a <c>void (^)(id)</c> block (e.g. notification settings queries).</summary>
    internal static Scope Object(Action<IntPtr> callback) =>
        Allocate((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&InvokeObject, "v16@?0@8", callback);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void InvokeObject(IntPtr block, IntPtr argument)
    {
        try
        {
            Target<Action<IntPtr>>(block)?.Invoke(argument);
        }
        catch
        {
        }
    }

    /// <summary>Creates a <c>void (^)(NSError *)</c> block (delivery completion handlers).</summary>
    internal static Scope Error(Action<string?> callback) =>
        Allocate((IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&InvokeError, "v16@?0@8", callback);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void InvokeError(IntPtr block, IntPtr error)
    {
        try
        {
            Target<Action<string?>>(block)?.Invoke(ErrorMessage(error));
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------------ ObjC blocks -> invoke

    /// <summary>Invokes a <c>void (^)(void)</c> block owned by the caller.</summary>
    internal static void Invoke(IntPtr block)
    {
        if (block == IntPtr.Zero)
        {
            return;
        }

        ((delegate* unmanaged[Cdecl]<IntPtr, void>)Marshal.ReadIntPtr(block, InvokeOffset))(block);
    }

    /// <summary>Invokes a <c>void (^)(NSUInteger)</c> block owned by the caller.</summary>
    internal static void Invoke(IntPtr block, ulong argument)
    {
        if (block == IntPtr.Zero)
        {
            return;
        }

        ((delegate* unmanaged[Cdecl]<IntPtr, ulong, void>)Marshal.ReadIntPtr(block, InvokeOffset))(block, argument);
    }

    private static string? ErrorMessage(IntPtr error) =>
        error == IntPtr.Zero ? null : ObjC.FromNSString(ObjC.Send(error, Sel.LocalizedDescription));
}
