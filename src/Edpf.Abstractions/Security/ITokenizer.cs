using System;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Primitives;

namespace Edpf.Abstractions.Security;

/// <summary>
/// Turns raw subject identifiers into stable pseudonymous tokens
/// (§10.1 Security; C4 §12.3 <c>SubjectTokeniser</c>). Audit records and
/// domain events carry tokens, never identifiers, so they remain valid after
/// crypto-shredding (ADR-006). The token is HMAC-SHA256 under a tenant-scoped
/// salt held under a separately destroyable key — the token→identity mapping
/// is itself erasable.
/// </summary>
public interface ITokenizer
{
    /// <summary>
    /// Tokenizes a raw identifier for a tenant.
    /// </summary>
    /// <param name="rawIdentifier">The raw subject identifier. Never persisted by callers.</param>
    /// <param name="tenantId">The tenant whose salt scopes the token.</param>
    /// <param name="cancellationToken">Cancels salt resolution.</param>
    /// <returns>A stable base64 token safe for audit records and events.</returns>
    Task<Result<string>> TokenizeAsync(string rawIdentifier, Guid tenantId, CancellationToken cancellationToken);
}
