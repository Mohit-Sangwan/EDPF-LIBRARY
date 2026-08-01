using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Edpf.Extensions.DependencyInjection;

/// <summary>
/// The fluent registration surface of ADR-014. Feature modules register
/// independently and composably —
/// <c>AddEdpfCore().AddSqlServer().AddRedisCache().AddAudit()</c> — rather
/// than through one monolithic <c>AddEdpf()</c>, so a consumer takes only
/// what it needs and its dependency graph says what it uses.
/// </summary>
/// <remarks>
/// Third-party providers register through this same public surface the
/// built-in ones use (Phase 04 §⑧) — there is no privileged internal path.
/// </remarks>
public interface IEdpfBuilder
{
    /// <summary>The service collection being populated.</summary>
    IServiceCollection Services { get; }

    /// <summary>Application configuration, for modules that bind options.</summary>
    IConfiguration Configuration { get; }

    /// <summary>
    /// Names of the modules registered so far. Emitted in startup diagnostics
    /// and used to reject double registration.
    /// </summary>
    IReadOnlyCollection<string> RegisteredModules { get; }

    /// <summary>
    /// Records a module as registered.
    /// </summary>
    /// <param name="moduleName">Module name, e.g. <c>Core</c>, <c>SqlServer</c>.</param>
    /// <returns>
    /// True when newly registered; false when it was already present, so a
    /// module can make its own registration idempotent.
    /// </returns>
    bool TryRegisterModule(string moduleName);
}
