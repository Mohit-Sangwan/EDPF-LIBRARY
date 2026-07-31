using System;

namespace Edpf.Abstractions.Primitives;

/// <summary>
/// The clock abstraction (Phase 01). Direct <see cref="DateTime.Now"/> /
/// <see cref="DateTime.UtcNow"/> is forbidden everywhere in the codebase
/// (Z.3 rule 4, enforced by rule EDPF0003): time is an injected dependency so
/// tests are deterministic and clinical-record timestamps are auditable.
/// </summary>
public interface IClock
{
    /// <summary>The current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }
}
