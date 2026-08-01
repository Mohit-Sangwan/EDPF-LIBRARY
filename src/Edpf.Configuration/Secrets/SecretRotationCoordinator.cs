using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Edpf.Abstractions.Configuration;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;
using Microsoft.Extensions.Logging;

namespace Edpf.Configuration.Secrets;

/// <summary>
/// Rotates a secret without a restart (Phase 03 §④): write the new value,
/// open the dual-secret overlap, notify every handler, audit the event.
/// In-flight work finishes on the outgoing value; only new work takes the
/// incoming one (Phase 03 §⑧).
/// </summary>
public sealed class SecretRotationCoordinator
{
    private readonly ISecretStore _store;
    private readonly List<ISecretRotationHandler> _handlers;
    private readonly IClock _clock;
    private readonly ILogger<SecretRotationCoordinator> _logger;

    /// <summary>
    /// Initializes the coordinator.
    /// </summary>
    /// <param name="store">The store holding the secret.</param>
    /// <param name="handlers">Handlers that refresh derived state.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="logger">Logger. Rotation is logged by key, never by value.</param>
    public SecretRotationCoordinator(
        ISecretStore store,
        IEnumerable<ISecretRotationHandler> handlers,
        IClock clock,
        ILogger<SecretRotationCoordinator> logger)
    {
        _store = Guard.NotNull(store, nameof(store));
        _handlers = Guard.NotNull(handlers, nameof(handlers)).ToList();
        _clock = Guard.NotNull(clock, nameof(clock));
        _logger = Guard.NotNull(logger, nameof(logger));
    }

    /// <summary>
    /// Rotates <paramref name="key"/> to <paramref name="newValue"/>.
    /// </summary>
    /// <param name="key">The secret key.</param>
    /// <param name="newValue">The incoming value.</param>
    /// <param name="cancellationToken">Cancels the rotation before handlers run.</param>
    /// <returns>
    /// The audited rotation event on success. If any handler fails, the
    /// overlap stays open and the failure is returned — traffic keeps
    /// flowing on the outgoing value rather than breaking.
    /// </returns>
    public async Task<Result<SecretRotationEvent>> RotateAsync(
        string key, SecretValue newValue, CancellationToken cancellationToken)
    {
        Guard.NotNullOrWhiteSpace(key, nameof(key));
        Guard.NotNull(newValue, nameof(newValue));

        Result written = await _store.SetAsync(key, newValue, cancellationToken).ConfigureAwait(false);
        if (written.IsFailure)
        {
            return Result.Failure<SecretRotationEvent>(written.Error!);
        }

        Result<SecretRotationView> view =
            await _store.GetForRotationAsync(key, cancellationToken).ConfigureAwait(false);
        if (view.IsFailure)
        {
            return Result.Failure<SecretRotationEvent>(view.Error!);
        }

        var rotationEvent = new SecretRotationEvent(
            key, _store.Name, _clock.UtcNow, view.Value.OverlapExpiresUtc);

        foreach (ISecretRotationHandler handler in _handlers.Where(h =>
            string.Equals(h.SecretKey, key, StringComparison.Ordinal)))
        {
            Result handled = await handler
                .OnRotatedAsync(view.Value, cancellationToken)
                .ConfigureAwait(false);

            if (handled.IsFailure)
            {
                SecretLog.RotationHandlerFailed(_logger, key, _store.Name, handled.Error!.Code);
                return Result.Failure<SecretRotationEvent>(handled.Error!);
            }
        }

        SecretLog.Rotated(_logger, key, _store.Name, rotationEvent.OverlapExpiresUtc);
        return Result.Success(rotationEvent);
    }
}

/// <summary>
/// Source-generated rotation log messages. Every parameter is a key, a store
/// name, an error code or a timestamp — a secret value can never reach a sink
/// through this type (Phase 03 §⑥).
/// </summary>
internal static partial class SecretLog
{
    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Secret {SecretKey} rotated in store {StoreName}; overlap closes {OverlapExpiresUtc}")]
    internal static partial void Rotated(
        ILogger logger, string secretKey, string storeName, DateTimeOffset? overlapExpiresUtc);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Error,
        Message = "Rotation handler failed for secret {SecretKey} in store {StoreName}: {ErrorCode}. "
                + "Overlap left open; outgoing value still accepted.")]
    internal static partial void RotationHandlerFailed(
        ILogger logger, string secretKey, string storeName, string errorCode);
}
