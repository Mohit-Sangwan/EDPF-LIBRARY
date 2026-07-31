namespace Edpf.Abstractions.Tenancy;

/// <summary>
/// The three pluggable isolation modes of ADR-004. A deployment chooses per
/// tenant; the framework enforces the boundary identically in all three.
/// </summary>
public enum TenantIsolationMode
{
    /// <summary>
    /// Shared schema with a tenant discriminator column (the default).
    /// <c>TenantId</c> leads every clustered index (Z.2) so isolation is free,
    /// not costly.
    /// </summary>
    SharedSchema = 0,

    /// <summary>A schema per tenant within a shared database.</summary>
    SchemaPerTenant = 1,

    /// <summary>A database per tenant — strongest isolation, highest operational cost.</summary>
    DatabasePerTenant = 2,
}
