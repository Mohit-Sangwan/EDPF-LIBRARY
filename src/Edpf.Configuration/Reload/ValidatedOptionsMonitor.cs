using System;
using System.Collections.Generic;
using System.Linq;
using Edpf.Abstractions.Configuration;
using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Edpf.Configuration.Reload;

/// <summary>
/// Transactional hot reload (Phase 03 §④). A configuration change is
/// validated in full **before** it is adopted; a reload that fails validation
/// keeps the last-known-good snapshot and raises an alert instead of
/// half-applying itself.
/// </summary>
/// <remarks>
/// Silent partial reload is a production-incident generator: half the
/// application picks up a new value, half does not, and the symptom appears
/// hours later somewhere unrelated. This type makes a bad reload a loud
/// no-op.
/// </remarks>
/// <typeparam name="TOptions">The reloadable options type.</typeparam>
public sealed class ValidatedOptionsMonitor<TOptions> : IOptionsMonitor<TOptions>, IDisposable
    where TOptions : class
{
    private readonly List<IConfigurationValidator<TOptions>> _validators;
    private readonly ILogger _logger;
    private readonly IDisposable? _subscription;
    private readonly List<Action<TOptions, string?>> _listeners = [];
    private readonly object _gate = new();

    private TOptions _lastKnownGood;
    private bool _disposed;

    /// <summary>
    /// Initializes the monitor and validates the initial snapshot.
    /// </summary>
    /// <param name="inner">The underlying options monitor.</param>
    /// <param name="validators">Validators applied to every snapshot.</param>
    /// <param name="logger">Logger for reload outcomes.</param>
    /// <exception cref="OptionsValidationException">
    /// The initial configuration is invalid — the host fails at boot
    /// (EDPF-CFG-8001), which is the entire point of startup validation.
    /// </exception>
    public ValidatedOptionsMonitor(
        IOptionsMonitor<TOptions> inner,
        IEnumerable<IConfigurationValidator<TOptions>> validators,
        ILogger<ValidatedOptionsMonitor<TOptions>> logger)
    {
        Guard.NotNull(inner, nameof(inner));
        _validators = Guard.NotNull(validators, nameof(validators)).ToList();
        _logger = Guard.NotNull(logger, nameof(logger));

        TOptions initial = inner.CurrentValue;
        List<string> failures = Validate(initial);
        if (failures.Count > 0)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(TOptions),
                failures.Select(f => $"{ErrorCodes.ConfigurationInvalid}: {f}").ToList());
        }

        _lastKnownGood = initial;
        _subscription = inner.OnChange(OnInnerChanged);
    }

    /// <summary>The last snapshot that passed validation.</summary>
    public TOptions CurrentValue
    {
        get
        {
            lock (_gate)
            {
                return _lastKnownGood;
            }
        }
    }

    /// <summary>
    /// True while the underlying configuration is invalid and the monitor is
    /// serving a stale-but-good snapshot. Surfaced as a health-check signal.
    /// </summary>
    public bool IsServingStaleConfiguration { get; private set; }

    /// <summary>Named options are not supported; EDPF options are singletons per section.</summary>
    /// <param name="name">Ignored.</param>
    public TOptions Get(string? name) => CurrentValue;

    /// <summary>
    /// Registers a listener notified only when a **valid** reload is adopted.
    /// </summary>
    /// <param name="listener">Callback receiving the new snapshot.</param>
    /// <returns>A token that unregisters the listener on dispose.</returns>
    public IDisposable? OnChange(Action<TOptions, string?> listener)
    {
        Guard.NotNull(listener, nameof(listener));

        lock (_gate)
        {
            _listeners.Add(listener);
        }

        return new Unsubscriber(this, listener);
    }

    private void OnInnerChanged(TOptions candidate, string? name)
    {
        List<string> failures = Validate(candidate);

        if (failures.Count > 0)
        {
            IsServingStaleConfiguration = true;
            ConfigurationLog.ReloadRejected(
                _logger, typeof(TOptions).Name, failures.Count, string.Join("; ", failures));
            return;
        }

        Action<TOptions, string?>[] listeners;
        lock (_gate)
        {
            _lastKnownGood = candidate;
            listeners = [.. _listeners];
        }

        IsServingStaleConfiguration = false;
        ConfigurationLog.ReloadAccepted(_logger, typeof(TOptions).Name);

        foreach (Action<TOptions, string?> listener in listeners)
        {
            listener(candidate, name);
        }
    }

    private List<string> Validate(TOptions candidate)
    {
        var failures = new List<string>();
        foreach (IConfigurationValidator<TOptions> validator in _validators)
        {
            failures.AddRange(validator.Validate(candidate));
        }

        return failures;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _subscription?.Dispose();
        _disposed = true;
    }

    private sealed class Unsubscriber(ValidatedOptionsMonitor<TOptions> owner, Action<TOptions, string?> listener)
        : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._listeners.Remove(listener);
            }
        }
    }
}

/// <summary>
/// Configuration log messages. Failure text names keys and rules only —
/// validators are contractually forbidden from echoing values, which may be
/// secrets (EDPF-CFG-8001).
/// </summary>
internal static partial class ConfigurationLog
{
    [LoggerMessage(
        EventId = 3010,
        Level = LogLevel.Information,
        Message = "Configuration reload accepted for {OptionsType}")]
    internal static partial void ReloadAccepted(ILogger logger, string optionsType);

    [LoggerMessage(
        EventId = 3011,
        Level = LogLevel.Error,
        Message = "Configuration reload REJECTED for {OptionsType}: {FailureCount} validation failure(s): "
                + "{Failures}. Last-known-good configuration retained.")]
    internal static partial void ReloadRejected(
        ILogger logger, string optionsType, int failureCount, string failures);
}
