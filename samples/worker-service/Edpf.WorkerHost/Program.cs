using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Tenancy;
using Edpf.Core.Time;
using Edpf.WorkerHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ─────────────────────────────────────────────────────────────────────────────
// EDPF outside a Web API (ADR-037 v1.0 addition 4).
//
// Everything the framework guarantees was developed against one host type, and
// an assumption that only holds inside an HTTP request is an assumption nobody
// noticed making. This host has no request: tenancy comes off the message, the
// correlation id is generated per message rather than read from a header, and
// there is no middleware pipeline to hang either on.
//
// What survives the change of host is the interesting part. The tenant
// predicate, the classification-driven protections, the error contract and the
// clock seam all work here unmodified, because none of them was ever reading
// HttpContext.
// ─────────────────────────────────────────────────────────────────────────────

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddSingleton<ITenantStore, InMemoryTenantStore>();
builder.Services.AddSingleton<IMessageHandler, AppointmentReminderHandler>();
builder.Services.AddSingleton<TenantScopedMessagePump>(provider => new TenantScopedMessagePump(
    provider.GetRequiredService<ITenantContextAccessor>(),
    provider.GetRequiredService<ITenantStore>(),
    [.. provider.GetServices<IMessageHandler>()]));

builder.Services.AddHostedService<QueueDrainService>();

await builder.Build().RunAsync();

/// <summary>
/// Drains a queue on an interval.
/// </summary>
/// <remarks>
/// A failed message does not stop the loop and does not silently vanish: it is
/// logged with its id and its error code, and the loop continues. A drain
/// service that dies on the first bad message is a drain service that stops
/// draining the moment it is most needed.
/// </remarks>
internal sealed class QueueDrainService(
    TenantScopedMessagePump pump,
    ILogger<QueueDrainService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (QueuedMessage message in DemoQueue.Drain())
            {
                Result handled = await pump.ProcessAsync(message, stoppingToken);

                if (handled.IsFailure)
                {
                    // The id and the code. Not the payload — it is a message
                    // about a patient, and the log is not the place for it
                    // (ADR-015).
                    logger.LogWarning(
                        "Message {MessageId} was not processed: {ErrorCode}",
                        message.MessageId,
                        handled.Error!.Code);
                }
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>Stands in for a broker. A real deployment swaps this for one.</summary>
internal static class DemoQueue
{
    private static readonly Guid DemoTenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    internal static IEnumerable<QueuedMessage> Drain()
    {
        yield return new QueuedMessage(
            Guid.NewGuid(), DemoTenant, "appointment-reminder", "{\"appointmentId\":\"A-1\"}");

        // Deliberately included: a message with no tenant. It is what a
        // replayed dead-letter or an older producer looks like, and the log
        // line it produces is the demonstration.
        yield return new QueuedMessage(
            Guid.NewGuid(), Guid.Empty, "appointment-reminder", "{\"appointmentId\":\"A-2\"}");
    }
}

internal sealed class AppointmentReminderHandler(
    ITenantContextAccessor tenantAccessor,
    ILogger<AppointmentReminderHandler> logger) : IMessageHandler
{
    public string MessageType => "appointment-reminder";

    public Task<Result> HandleAsync(QueuedMessage message, CancellationToken cancellationToken)
    {
        // By the time a handler runs, the tenant is established. This assertion
        // is the sample's point: the guarantee holds without an HTTP request
        // anywhere in the process.
        ITenantContext tenant = tenantAccessor.Current
            ?? throw new InvalidOperationException("A handler ran without a tenant in scope.");

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Handled {MessageType} for tenant {TenantId}", message.MessageType, tenant.TenantId);
        }

        return Task.FromResult(Result.Success());
    }
}

/// <summary>A tenant store with one tenant. A real deployment reads a database.</summary>
internal sealed class InMemoryTenantStore : ITenantStore
{
    private static readonly Guid DemoTenant = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    public Task<Result<TenantDescriptor>> GetAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult(tenantId == DemoTenant
            ? Result<TenantDescriptor>.FromValue(new TenantDescriptor(
                DemoTenant, "demo", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()))
            : Result.Failure<TenantDescriptor>(new Error(
                ErrorCodes.TenantScopeViolation,
                "The requested resource was not found.",
                ErrorCategory.NotFound)));
}
