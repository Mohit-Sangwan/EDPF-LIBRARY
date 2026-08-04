using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Tenancy;

namespace Edpf.WorkerHost;

/// <summary>A message waiting to be handled, as it arrives off a queue.</summary>
public sealed class QueuedMessage
{
    public QueuedMessage(Guid messageId, Guid tenantId, string messageType, string payload)
    {
        MessageId = messageId;
        TenantId = tenantId;
        MessageType = messageType;
        Payload = payload;
    }

    public Guid MessageId { get; }

    /// <summary>
    /// The tenant this message belongs to.
    /// </summary>
    /// <remarks>
    /// <see cref="Guid.Empty"/> is a real possibility here and is the whole
    /// reason this type is not just a payload. A message can arrive from an
    /// older producer, a replayed dead-letter, or a hand-crafted test fixture,
    /// and any of those can lack a tenant.
    /// </remarks>
    public Guid TenantId { get; }

    public string MessageType { get; }

    public string Payload { get; }
}

/// <summary>Handles one kind of message, under an already-established tenant.</summary>
public interface IMessageHandler
{
    string MessageType { get; }

    Task<Result> HandleAsync(QueuedMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Establishes tenancy from the message, then hands off to a handler.
/// </summary>
/// <remarks>
/// <para>
/// **This is the part of the sample worth reading.** In the Web API host the
/// tenant arrives on the request and a middleware stage resolves it before
/// anything touches data (ADR-012 stage 2). A worker has no request. The
/// pipeline stage that made tenancy unavoidable does not exist here, and the
/// framework's guarantees are only as good as whatever replaces it.
/// </para>
/// <para>
/// What replaces it is this: the tenant comes off the message, and a message
/// without one is dead-lettered rather than handled. The failure mode being
/// closed is specific and severe — a worker that treats a missing tenant as
/// "process it anyway" runs a handler with no tenant in scope, and every
/// repository call below it either refuses (best case, and the queue jams) or,
/// in a framework that defaulted differently, reads across every tenant at
/// once.
/// </para>
/// <para>
/// The scope is disposed after each message. Ambient state that outlives the
/// message it belongs to is how a batch's second message gets processed as the
/// first message's tenant.
/// </para>
/// </remarks>
public sealed class TenantScopedMessagePump
{
    private readonly ITenantContextAccessor _tenantAccessor;
    private readonly ITenantStore _tenantStore;
    private readonly Dictionary<string, IMessageHandler> _handlers;

    public TenantScopedMessagePump(
        ITenantContextAccessor tenantAccessor,
        ITenantStore tenantStore,
        IReadOnlyList<IMessageHandler> handlers)
    {
        ArgumentNullException.ThrowIfNull(tenantAccessor);
        ArgumentNullException.ThrowIfNull(tenantStore);
        ArgumentNullException.ThrowIfNull(handlers);

        _tenantAccessor = tenantAccessor;
        _tenantStore = tenantStore;
        _handlers = handlers.ToDictionary(h => h.MessageType, StringComparer.Ordinal);
    }

    public async Task<Result> ProcessAsync(QueuedMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.TenantId == Guid.Empty)
        {
            // Refused before a handler is selected, let alone run. There is no
            // "process it anyway" branch, because the only thing it could mean
            // is "process it as nobody".
            return Result.Failure(new Error(
                ErrorCodes.TenantScopeViolation,
                "The message carries no tenant and cannot be processed.",
                ErrorCategory.NotFound));
        }

        Result<TenantDescriptor> tenant = await _tenantStore.GetAsync(message.TenantId, cancellationToken);
        if (tenant.IsFailure)
        {
            // The tenant on the message no longer exists, or is suspended. A
            // queue outlives a tenant: messages sit in it across an offboarding,
            // and processing them afterwards writes data for somebody who has
            // left.
            return Result.Failure(tenant.Error!);
        }

        if (!_handlers.TryGetValue(message.MessageType, out IMessageHandler? handler))
        {
            return Result.Failure(new Error(
                ErrorCodes.SchemaMismatch,
                "No handler is registered for this message type.",
                ErrorCategory.Validation));
        }

        using (_tenantAccessor.Push(tenant.Value))
        {
            return await handler.HandleAsync(message, cancellationToken);
        }
    }
}
