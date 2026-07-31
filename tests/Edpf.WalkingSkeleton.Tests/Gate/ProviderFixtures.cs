using Edpf.Abstractions.Tenancy;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Edpf.WalkingSkeleton.Tests.Gate;

/// <summary>
/// A provider-backed running slice: one database container plus one hosted
/// API (Z.8: each run provisions and disposes its own environment).
/// </summary>
public abstract class ProviderFixture : IAsyncLifetime
{
    /// <summary>The Database:Provider value the API is booted with.</summary>
    public abstract string ProviderName { get; }

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public string ConnectionString { get; private set; } = string.Empty;

    protected abstract Task<string> StartContainerAsync();

    protected abstract Task StopContainerAsync();

    public async Task InitializeAsync()
    {
        ConnectionString = await StartContainerAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Database:Provider", ProviderName);
            builder.UseSetting($"ConnectionStrings:{ProviderName}", ConnectionString);
            // Keep the harness self-contained: no Seq, no OTLP endpoints needed.
            builder.UseSetting("Serilog:WriteTo:1:Name", "Console");
        });
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        await StopContainerAsync();
    }

    /// <summary>
    /// Direct database access outside the API (for the raw-ciphertext and
    /// outbox demonstrations). Tenant filters are bypassed explicitly where a
    /// demonstration requires cross-tenant inspection.
    /// </summary>
    public SkeletonDbContext CreateDirectDbContext(ITenantContextAccessor accessor)
    {
        var builder = new DbContextOptionsBuilder<SkeletonDbContext>();
        if (string.Equals(ProviderName, "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            builder.UseNpgsql(ConnectionString);
        }
        else
        {
            builder.UseSqlServer(ConnectionString);
        }

        return new SkeletonDbContext(builder.Options, accessor);
    }
}

/// <summary>Tier A provider 1: SQL Server (ADR-008).</summary>
public sealed class SqlServerFixture : ProviderFixture
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public override string ProviderName => "SqlServer";

    protected override async Task<string> StartContainerAsync()
    {
        await _container.StartAsync();
        return _container.GetConnectionString();
    }

    protected override Task StopContainerAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>Tier A provider 2: PostgreSQL (ADR-008).</summary>
public sealed class PostgreSqlFixture : ProviderFixture
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:16-alpine").Build();

    public override string ProviderName => "PostgreSql";

    protected override async Task<string> StartContainerAsync()
    {
        await _container.StartAsync();
        return _container.GetConnectionString();
    }

    protected override Task StopContainerAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>A stub ambient tenant for direct database inspection.</summary>
public sealed class FixedTenantAccessor(Guid tenantId) : ITenantContextAccessor
{
    public ITenantContext? Current { get; } = new TenantDescriptor(
        tenantId, "inspection", "in-south-1", TenantIsolationMode.SharedSchema, Guid.NewGuid());

    public IDisposable Push(ITenantContext context) => throw new NotSupportedException();
}
