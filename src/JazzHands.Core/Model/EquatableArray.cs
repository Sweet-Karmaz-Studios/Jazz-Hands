using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace JazzHands.Core.Model;

/// <summary>
/// An immutable array with value equality.
/// </summary>
/// <remarks>
/// The project model is built from records, and records get their equality from their members.
/// <see cref="ImmutableArray{T}"/> compares by reference, so two projects with identical clips
/// would compare unequal and undo would think every command changed everything. This wraps it and
/// compares element by element instead, which is what the rest of the model assumes.
///
/// It is a readonly struct over the array, so wrapping costs nothing.
/// </remarks>
/// <typeparam name="T">The element type, which must itself have value equality.</typeparam>
[CollectionBuilder(typeof(EquatableArray), nameof(EquatableArray.Create))]
[DebuggerDisplay("Count = {Length}")]
public readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly ImmutableArray<T> _items;

    /// <summary>Wraps an immutable array.</summary>
    public EquatableArray(ImmutableArray<T> items) => _items = items;

    /// <summary>Copies a sequence into a new array.</summary>
    public EquatableArray(IEnumerable<T> items) => _items = [.. items];

    /// <summary>The empty array.</summary>
    public static EquatableArray<T> Empty => new(ImmutableArray<T>.Empty);

    /// <summary>Element count. Zero for a default instance.</summary>
    public int Length => _items.IsDefault ? 0 : _items.Length;

    /// <inheritdoc />
    public int Count => Length;

    /// <summary>True when there are no elements.</summary>
    public bool IsEmpty => Length == 0;

    /// <summary>The underlying immutable array, never default.</summary>
    public ImmutableArray<T> Items => _items.IsDefault ? ImmutableArray<T>.Empty : _items;

    /// <inheritdoc />
    public T this[int index] => Items[index];

    /// <summary>Wraps an immutable array.</summary>
    public static implicit operator EquatableArray<T>(ImmutableArray<T> items) => new(items);

    /// <summary>Unwraps to the underlying immutable array.</summary>
    public static implicit operator ImmutableArray<T>(EquatableArray<T> array) => array.Items;

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    /// <summary>Named alternate for the equality operator.</summary>
    public static bool Equals(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    /// <summary>Creates an array from the given elements.</summary>
    public static EquatableArray<T> Create(params ReadOnlySpan<T> items) => new(ImmutableArray.Create(items));

    /// <summary>A copy with one more element at the end.</summary>
    public EquatableArray<T> Add(T item) => new(Items.Add(item));

    /// <summary>A copy with the element inserted at an index.</summary>
    public EquatableArray<T> Insert(int index, T item) => new(Items.Insert(index, item));

    /// <summary>A copy with the element at an index removed.</summary>
    public EquatableArray<T> RemoveAt(int index) => new(Items.RemoveAt(index));

    /// <summary>A copy with the element at an index replaced.</summary>
    public EquatableArray<T> SetItem(int index, T item) => new(Items.SetItem(index, item));

    /// <summary>The index of the first matching element, or -1.</summary>
    public int IndexOf(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        ImmutableArray<T> items = Items;
        for (int index = 0; index < items.Length; index++)
        {
            if (predicate(items[index]))
            {
                return index;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public bool Equals(EquatableArray<T> other)
    {
        ImmutableArray<T> mine = Items;
        ImmutableArray<T> theirs = other.Items;

        if (mine.Length != theirs.Length)
        {
            return false;
        }

        for (int index = 0; index < mine.Length; index++)
        {
            if (!EqualityComparer<T>.Default.Equals(mine[index], theirs[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (T item in Items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Helpers for building <see cref="EquatableArray{T}"/> without naming the element type.</summary>
public static class EquatableArray
{
    /// <summary>Creates an array from the given elements.</summary>
    public static EquatableArray<T> Create<T>(params ReadOnlySpan<T> items)
        where T : IEquatable<T> =>
        new(ImmutableArray.Create(items));

    /// <summary>Copies a sequence into a new array.</summary>
    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> items)
        where T : IEquatable<T> =>
        new(items);
}
