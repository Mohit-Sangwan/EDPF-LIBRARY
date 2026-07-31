using Edpf.Abstractions.Consistency;
using Edpf.Abstractions.Primitives;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edpf.WalkingSkeleton.Api.Infrastructure.Consistency;

/// <summary>
/// Smallest-scale outbox dispatch (ADR-003; Phase 02 §④ "one outbox message
/// published on create"). Delivery here is a structured log entry standing in
/// for a broker publish — Phase 26 supplies real transports. Claim-then-mark
/// gives at-least-once with attempt accounting.
/// </summary>
public sealed class EfOutboxDispatcher(
    SkeletonDbContext db,
    IClock clock,
    ILogger<EfOutboxDispatcher> logger) : IOutboxDispatcher
{
    public async Task<Result<int>> DispatchPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        List<OutboxRow> pending = await db.Outbox
            .Where(o => o.DispatchedUtc == null)
            .OrderBy(o => o.OccurredUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (OutboxRow message in pending)
        {
            message.Attempts++;

            // The "transport": a structured, correlated log entry (Phase 26
            // replaces this with IMessagePublisher). Payloads carry tokens
            // only, so this log line is classification-clean by construction.
            OutboxLog.Dispatched(
                logger, message.MessageType, message.Id, message.TenantId, message.Attempts, message.CorrelationId);

            message.DispatchedUtc = clock.UtcNow;
        }

        if (pending.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success(pending.Count);
    }
}

/// <summary>Source-generated log messages for the outbox path (CA1848/CA1873).</summary>
internal static partial class OutboxLog
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Outbox dispatched {MessageType} {MessageId} for tenant {TenantId} (attempt {Attempt}) {CorrelationId}")]
    internal static partial void Dispatched(
        ILogger logger, string messageType, Guid messageId, Guid tenantId, int attempt, string correlationId);
}

/// <summary>Polls the outbox on a short interval (skeleton scale).</summary>
public sealed class OutboxDispatcherService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
                await dispatcher.DispatchPendingAsync(batchSize: 50, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The dispatcher must outlive transient store failures; the
                // next sweep retries. Messages are never lost — they are rows.
                logger.LogError(ex, "Outbox sweep failed; will retry");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
