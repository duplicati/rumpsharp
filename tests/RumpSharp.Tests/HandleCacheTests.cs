using RumpSharp.Interop;
using Xunit;

namespace RumpSharp.Tests;

/// <summary>
/// Covers the bounded cache behind notification categories. Getting eviction wrong either grows the
/// registered set without limit or releases a handle macOS is still referencing.
/// </summary>
public sealed class HandleCacheTests
{
    [Fact]
    public void StartsEmpty()
    {
        var cache = new HandleCache(4);

        Assert.Equal(0, cache.Count);
        Assert.Empty(cache.ToArray());
        Assert.False(cache.TryTouch("missing"));
    }

    [Fact]
    public void RemembersWhatWasAdded()
    {
        var cache = new HandleCache(4);

        Assert.Equal(IntPtr.Zero, cache.Add("a", 10));
        Assert.Equal(IntPtr.Zero, cache.Add("b", 20));

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryTouch("a"));
        Assert.True(cache.TryTouch("b"));
        Assert.False(cache.TryTouch("c"));
        Assert.Equal([(IntPtr)10, (IntPtr)20], cache.ToArray());
    }

    [Fact]
    public void EvictsTheLeastRecentlyAddedWhenFull()
    {
        var cache = new HandleCache(2);
        cache.Add("a", 10);
        cache.Add("b", 20);

        Assert.Equal((IntPtr)10, cache.Add("c", 30));

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryTouch("a"));
        Assert.True(cache.TryTouch("b"));
        Assert.True(cache.TryTouch("c"));
    }

    [Fact]
    public void TouchingProtectsAnEntryFromTheNextEviction()
    {
        var cache = new HandleCache(2);
        cache.Add("a", 10);
        cache.Add("b", 20);

        // "a" becomes the most recently used, so "b" is next out.
        Assert.True(cache.TryTouch("a"));

        Assert.Equal((IntPtr)20, cache.Add("c", 30));
        Assert.True(cache.TryTouch("a"));
        Assert.False(cache.TryTouch("b"));
    }

    [Fact]
    public void NeverExceedsItsCapacity()
    {
        var cache = new HandleCache(8);
        var evicted = new List<IntPtr>();

        for (var i = 1; i <= 100; i++)
        {
            var handle = cache.Add($"key-{i}", i);
            if (handle != IntPtr.Zero)
            {
                evicted.Add(handle);
            }

            Assert.True(cache.Count <= 8);
        }

        Assert.Equal(8, cache.Count);

        // Everything that went in either came back out exactly once or is still cached.
        Assert.Equal(92, evicted.Count);
        Assert.Equal(evicted.Count, evicted.Distinct().Count());
        Assert.Empty(evicted.Intersect(cache.ToArray()));
    }

    [Fact]
    public void ACapacityOfOneKeepsOnlyTheNewest()
    {
        var cache = new HandleCache(1);
        cache.Add("a", 10);

        Assert.Equal((IntPtr)10, cache.Add("b", 20));
        Assert.Equal(1, cache.Count);
        Assert.Equal([(IntPtr)20], cache.ToArray());
    }
}
