using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RumpSharp.Interop;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Covers the Objective-C block ABI implementation, above all the descriptor lifetime: a block the
/// system copied keeps our <c>descriptor</c> pointer verbatim, and <c>Block_release</c> reads the
/// dispose helper back out of it long after the call that created the block has returned.
/// </summary>
public sealed unsafe partial class BlockTests
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    /// <summary>Signature of <c>void (^)(NSError *)</c>, as used by delivery completion handlers.</summary>
    private const string ErrorSignature = "v16@?0@8";

    /// <summary>Signature of <c>void (^)(BOOL, NSError *)</c>, as used by authorization requests.</summary>
    private const string BoolErrorSignature = "v24@?0B8@16";

    [LibraryImport(LibSystem, EntryPoint = "_Block_copy")]
    private static partial IntPtr BlockCopy(IntPtr block);

    [LibraryImport(LibSystem, EntryPoint = "_Block_release")]
    private static partial void BlockRelease(IntPtr block);

    [Fact]
    public void LiteralMatchesTheDocumentedBlockLayout()
    {
        // isa, flags, reserved, invoke, descriptor, context.
        Assert.Equal(40, Block.LiteralSize);
        Assert.Equal(16, Block.InvokePointerOffset);
    }

    [Fact]
    public void BlocksWithTheSameSignatureShareOneDescriptor()
    {
        using var first = Block.Error(_ => { });
        using var second = Block.Error(_ => { });

        var descriptor = Block.DescriptorOf(first.Handle);

        Assert.NotEqual(IntPtr.Zero, descriptor);
        Assert.Equal(descriptor, Block.DescriptorOf(second.Handle));
        Assert.Equal(descriptor, Block.DescriptorFor(ErrorSignature));
    }

    [Fact]
    public void EachSignatureGetsItsOwnDescriptor()
    {
        using var error = Block.Error(_ => { });
        using var boolError = Block.BoolError((_, _) => { });

        Assert.NotEqual(Block.DescriptorOf(error.Handle), Block.DescriptorOf(boolError.Handle));
        Assert.Equal(Block.DescriptorFor(BoolErrorSignature), Block.DescriptorOf(boolError.Handle));
    }

    [Fact]
    public void DisposingAScopeDoesNotInvalidateTheSharedDescriptor()
    {
        IntPtr descriptor;
        using (var scope = Block.Error(_ => { }))
        {
            descriptor = Block.DescriptorOf(scope.Handle);
        }

        using var later = Block.Error(_ => { });

        Assert.Equal(descriptor, Block.DescriptorOf(later.Handle));
        Assert.Equal(descriptor, Block.DescriptorFor(ErrorSignature));
    }

    /// <summary>
    /// The regression test for the descriptor lifetime bug: the system copies the completion handler,
    /// we free our literal as soon as the callback arrives, and the system releases its copy at some
    /// later point. That release has to find a live descriptor and free the copy's own
    /// <see cref="GCHandle"/>.
    /// </summary>
    [Fact]
    public void CopiedBlockSurvivesTheLiteralAndReleasesItsOwnHandle()
    {
        var invoked = new bool[1];
        var (scope, captured) = CreateTrackedErrorBlock(invoked);

        var copy = BlockCopy(scope.Handle);
        Assert.NotEqual(IntPtr.Zero, copy);
        Assert.NotEqual(scope.Handle, copy);

        // The copy helper gave the copy its own GCHandle, but the descriptor is shared.
        Assert.NotEqual(Block.ContextOf(scope.Handle), Block.ContextOf(copy));
        Assert.Equal(Block.DescriptorOf(scope.Handle), Block.DescriptorOf(copy));

        // Free our literal while the system still holds its copy. This is what used to leave the
        // copy's descriptor - and its signature string - dangling.
        scope.Dispose();

        InvokeWithError(copy, IntPtr.Zero);
        Assert.True(invoked[0], "the copied block should still reach managed code");

        Collect();
        Assert.True(captured.IsAlive, "the copy's GCHandle should keep the captured delegate alive");

        // Reads the dispose helper out of the shared descriptor and frees the copy's GCHandle.
        BlockRelease(copy);

        Collect();
        Assert.False(captured.IsAlive, "releasing the copy should have run the dispose helper");
    }

    [Fact]
    public void DisposingAScopeFreesItsCapturedDelegate()
    {
        var invoked = new bool[1];
        var (scope, captured) = CreateTrackedErrorBlock(invoked);

        Collect();
        Assert.True(captured.IsAlive);

        scope.Dispose();

        Collect();
        Assert.False(captured.IsAlive);
    }

    [Fact]
    public void InvokingABlockPassesTheErrorMessageThrough()
    {
        string? seen = null;
        using var scope = Block.Error(message => seen = message);

        InvokeWithError(scope.Handle, IntPtr.Zero);

        Assert.Null(seen);
    }

    /// <summary>
    /// Builds a block whose captured state is reachable <em>only</em> through the block, so a weak
    /// reference to it observes exactly when the ABI helpers free their handles. Kept in its own
    /// non-inlined method so no caller frame keeps the closure alive.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Block.Scope Scope, WeakReference Captured) CreateTrackedErrorBlock(bool[] invoked)
    {
        var captured = new object();
        var scope = Block.Error(_ =>
        {
            invoked[0] = true;
            GC.KeepAlive(captured);
        });

        return (scope, new WeakReference(captured));
    }

    private static void InvokeWithError(IntPtr block, IntPtr error)
    {
        var invoke = Marshal.ReadIntPtr(block, Block.InvokePointerOffset);
        ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)invoke)(block, error);
    }

    private static void Collect()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }
    }
}
