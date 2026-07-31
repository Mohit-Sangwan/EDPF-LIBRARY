using System;

namespace Edpf.Abstractions.Primitives;

/// <summary>
/// A strongly-typed identifier (Phase 01 shared kernel): a <c>Patient</c> id
/// cannot be passed where an <c>Encounter</c> id is expected, at compile time.
/// </summary>
/// <typeparam name="TEntity">The entity type this id identifies. Used only as a compile-time brand.</typeparam>
public readonly struct EntityId<TEntity> : IEquatable<EntityId<TEntity>>
{
    /// <summary>Initializes an id wrapping <paramref name="value"/>.</summary>
    /// <param name="value">The underlying GUID.</param>
    public EntityId(Guid value) => Value = value;

    /// <summary>The underlying GUID.</summary>
    public Guid Value { get; }

    /// <summary>The empty (default) id — never valid for persistence.</summary>
    public static EntityId<TEntity> Empty => default;

    /// <summary>True when this id is the empty id.</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>Creates a new random id.</summary>
    public static EntityId<TEntity> New() => new(Guid.NewGuid());

    /// <summary>Wraps an existing GUID.</summary>
    /// <param name="value">The GUID to wrap.</param>
    public static EntityId<TEntity> From(Guid value) => new(value);

    /// <inheritdoc />
    public bool Equals(EntityId<TEntity> other) => Value.Equals(other.Value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EntityId<TEntity> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary>Value equality.</summary>
    public static bool operator ==(EntityId<TEntity> left, EntityId<TEntity> right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(EntityId<TEntity> left, EntityId<TEntity> right) => !left.Equals(right);

    /// <summary>The GUID in "D" format. Contains no classified data; safe to log.</summary>
    public override string ToString() => Value.ToString("D");
}
