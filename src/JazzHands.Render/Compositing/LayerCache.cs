using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace JazzHands.Render.Compositing;

/// <summary>What makes two transformed layers the same picture.</summary>
/// <param name="Source">What the picture is: media, stream and source time, or a colour.</param>
/// <param name="Transform">Where it was placed.</param>
/// <param name="Crop">What of it was kept.</param>
/// <param name="Width">The frame it was placed in.</param>
/// <param name="Height">The frame's height.</param>
/// <param name="Bicubic">How it was filtered.</param>
public readonly record struct LayerKey(string Source, Matrix3x2 Transform, Vector4 Crop, int Width, int Height, bool Bicubic);

/// <summary>
/// Transformed layers kept from one frame to the next.
/// </summary>
/// <remarks>
/// Scrubbing back and forth over a region where a layer does not change (a still, a title, a
/// paused picture in picture) would otherwise redo the source and transform passes every time.
/// The key is structural: the same source frame placed the same way is the same texture, however
/// the project changed around it. Opacity, blend and masks are applied at composite time and are
/// not part of it.
///
/// Small on purpose. A 4K half float layer is 66 MB, so eight of them is half a gigabyte, and
/// while playing every frame is new and the compositor does not ask for the cache at all.
/// </remarks>
public sealed class LayerCache : IDisposable
{
    private readonly RenderTargetPool _pool;
    private readonly Dictionary<LayerKey, LinkedListNode<(LayerKey Key, RenderTarget Target)>> _entries = [];
    private readonly LinkedList<(LayerKey Key, RenderTarget Target)> _order = new();

    /// <summary>Creates a cache that returns what it evicts to a pool.</summary>
    public LayerCache(RenderTargetPool pool, int capacity)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);

        _pool = pool;
        Capacity = capacity;
    }

    /// <summary>How many layers it may hold.</summary>
    public int Capacity { get; }

    /// <summary>How many it holds.</summary>
    public int Count => _entries.Count;

    /// <summary>Lookups that found a layer.</summary>
    public long Hits { get; private set; }

    /// <summary>Lookups that did not.</summary>
    public long Misses { get; private set; }

    /// <summary>A kept layer, still owned by the cache.</summary>
    public bool TryGet(LayerKey key, [NotNullWhen(true)] out RenderTarget? target)
    {
        if (_entries.TryGetValue(key, out LinkedListNode<(LayerKey Key, RenderTarget Target)>? node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            Hits++;
            target = node.Value.Target;
            return true;
        }

        Misses++;
        target = null;
        return false;
    }

    /// <summary>Keeps a layer, taking ownership of it. The least recently used goes back to the pool.</summary>
    public void Put(LayerKey key, RenderTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (Capacity == 0)
        {
            _pool.Return(target);
            return;
        }

        if (_entries.Remove(key, out LinkedListNode<(LayerKey Key, RenderTarget Target)>? existing))
        {
            _order.Remove(existing);
            _pool.Return(existing.Value.Target);
        }

        _entries[key] = _order.AddFirst((key, target));

        while (_entries.Count > Capacity)
        {
            LinkedListNode<(LayerKey Key, RenderTarget Target)> oldest = _order.Last!;
            _order.RemoveLast();
            _entries.Remove(oldest.Value.Key);
            _pool.Return(oldest.Value.Target);
        }
    }

    /// <summary>Gives everything back to the pool.</summary>
    public void Clear()
    {
        foreach ((LayerKey _, RenderTarget target) in _order)
        {
            _pool.Return(target);
        }

        _order.Clear();
        _entries.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => Clear();
}
