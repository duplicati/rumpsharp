namespace RumpSharp.Interop;

/// <summary>
/// A bounded, least-recently-used cache of retained Objective-C handles keyed by a string.
/// </summary>
/// <remarks>
/// Used for notification categories, which are process-wide, have to be re-sent to macOS in full every
/// time one is added, and are keyed by a digest of their action buttons. An application that builds
/// action titles dynamically would register a new one per notification, so the set has to be bounded -
/// and eviction has to hand the handle back, because the caller owns a retain on it and can only
/// release it once the replacement set is in place.
/// </remarks>
/// <param name="capacity">Maximum number of handles to keep. Must be positive.</param>
internal sealed class HandleCache(int capacity)
{
    private readonly Dictionary<string, IntPtr> _handles = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];

    /// <summary>How many handles are currently cached.</summary>
    internal int Count => _handles.Count;

    /// <summary>
    /// Whether <paramref name="key"/> is cached, marking it as the most recently used one if it is.
    /// </summary>
    internal bool TryTouch(string key)
    {
        if (!_handles.ContainsKey(key))
        {
            return false;
        }

        _order.Remove(key);
        _order.Add(key);
        return true;
    }

    /// <summary>Caches a handle, evicting the least recently used one if the cache is full.</summary>
    /// <param name="key">Key to cache under. Must not already be present.</param>
    /// <param name="handle">The retained handle to cache.</param>
    /// <returns>
    /// The evicted handle, which the caller still owns a retain on, or <see cref="IntPtr.Zero"/> if
    /// nothing had to be evicted.
    /// </returns>
    internal IntPtr Add(string key, IntPtr handle)
    {
        var evicted = IntPtr.Zero;
        if (_handles.Count >= capacity)
        {
            var oldest = _order[0];
            _order.RemoveAt(0);
            evicted = _handles[oldest];
            _handles.Remove(oldest);
        }

        _handles[key] = handle;
        _order.Add(key);
        return evicted;
    }

    /// <summary>The cached handles, for handing the complete set back to macOS.</summary>
    internal IntPtr[] ToArray()
    {
        var handles = new IntPtr[_handles.Count];
        _handles.Values.CopyTo(handles, 0);
        return handles;
    }
}
