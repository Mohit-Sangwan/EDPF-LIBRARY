using Edpf.Abstractions.Configuration;
using Edpf.Configuration.Reload;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Edpf.UnitTests.Configuration;

/// <summary>
/// Phase 03 §④: reload is transactional — validate fully before swapping; a
/// failed reload keeps the last-known-good and raises an alert.
/// </summary>
public sealed class HotReloadTests
{
    private sealed class DatabaseOptions
    {
        public string Server { get; set; } = string.Empty;
        public int PoolSize { get; set; } = 10;
    }

    private sealed class DatabaseOptionsValidator : IConfigurationValidator<DatabaseOptions>
    {
        public IReadOnlyList<string> Validate(DatabaseOptions options)
        {
            var failures = new List<string>();

            if (string.IsNullOrWhiteSpace(options.Server))
            {
                failures.Add("Server: required");
            }

            if (options.PoolSize is < 1 or > 1000)
            {
                failures.Add("PoolSize: must be between 1 and 1000");
            }

            return failures;
        }
    }

    /// <summary>A monitor whose value a test can replace, simulating a file change.</summary>
    private sealed class MutableMonitor<T>(T initial) : IOptionsMonitor<T>
    {
        private readonly List<Action<T, string?>> _listeners = [];

        public T CurrentValue { get; private set; } = initial;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return null;
        }

        public void Publish(T value)
        {
            CurrentValue = value;
            foreach (Action<T, string?> listener in _listeners)
            {
                listener(value, null);
            }
        }
    }

    private static ValidatedOptionsMonitor<DatabaseOptions> Create(MutableMonitor<DatabaseOptions> inner)
        => new(inner, [new DatabaseOptionsValidator()], NullLogger<ValidatedOptionsMonitor<DatabaseOptions>>.Instance);

    [Fact]
    public void Constructor_InvalidInitialConfiguration_FailsAtStartup()
    {
        // Fail fast at boot, not at 3 a.m. on first use of a rare path.
        var inner = new MutableMonitor<DatabaseOptions>(new DatabaseOptions { Server = "", PoolSize = 0 });

        var exception = Assert.Throws<OptionsValidationException>(() => Create(inner));

        Assert.Contains("EDPF-CFG-8001", string.Join(" ", exception.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_ValidInitialConfiguration_Succeeds()
    {
        var inner = new MutableMonitor<DatabaseOptions>(new DatabaseOptions { Server = "db1", PoolSize = 20 });

        using var monitor = Create(inner);

        Assert.Equal("db1", monitor.CurrentValue.Server);
        Assert.False(monitor.IsServingStaleConfiguration);
    }

    [Fact]
    public void OnChange_ValidReload_IsAdopted()
    {
        var inner = new MutableMonitor<DatabaseOptions>(new DatabaseOptions { Server = "db1", PoolSize = 20 });
        using var monitor = Create(inner);

        inner.Publish(new DatabaseOptions { Server = "db2", PoolSize = 50 });

        Assert.Equal("db2", monitor.CurrentValue.Server);
        Assert.Equal(50, monitor.CurrentValue.PoolSize);
        Assert.False(monitor.IsServingStaleConfiguration);
    }

    [Fact]
    public void OnChange_InvalidReload_KeepsLastKnownGood()
    {
        var inner = new MutableMonitor<DatabaseOptions>(new DatabaseOptions { Server = "db1", PoolSize = 20 });
        using var monitor = Create(inner);

        inner.Publish(new DatabaseOptions { Server = "", PoolSize = 99999 });

        // Not half-applied, not adopted — the previous good snapshot stands.
        Assert.Equal("db1", monitor.CurrentValue.Server);
        Assert.Equal(20, monitor.CurrentValue.PoolSize);
        Assert.True(monitor.IsServingStaleConfiguration);
    }

    [Fact]
    public void OnChange_InvalidReload_DoesNotNotifyListeners()
    {
        var inner = new MutableMonitor<DatabaseOptions>(new DatabaseOptions { Server = "db1", PoolSize = 20 });
        using var monitor = Create(inner);
        int notifications = 0;
        monitor.OnChange((_, _) => notifications++);

        inner.Publish(new DatabaseOptions { Server = "", PoolSize = 0 });

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void OnChange_ValidReloadAfterInvalidOne_RecoversAndClearsStaleFlag()
    {
        var inner = new MutableMonitor<DatabaseOptions>(new DatabaseOptions { Server = "db1", PoolSize = 20 });
        using var monitor = Create(inner);

        inner.Publish(new DatabaseOptions { Server = "", PoolSize = 0 });
        Assert.True(monitor.IsServingStaleConfiguration);

        inner.Publish(new DatabaseOptions { Server = "db3", PoolSize = 30 });

        Assert.Equal("db3", monitor.CurrentValue.Server);
        Assert.False(monitor.IsServingStaleConfiguration);
    }

    [Fact]
    public void OnChange_Unsubscribed_StopsReceivingNotifications()
    {
        var inner = new MutableMonitor<DatabaseOptions>(new DatabaseOptions { Server = "db1", PoolSize = 20 });
        using var monitor = Create(inner);
        int notifications = 0;
        IDisposable? token = monitor.OnChange((_, _) => notifications++);

        inner.Publish(new DatabaseOptions { Server = "db2", PoolSize = 20 });
        token!.Dispose();
        inner.Publish(new DatabaseOptions { Server = "db3", PoolSize = 20 });

        Assert.Equal(1, notifications);
    }
}
