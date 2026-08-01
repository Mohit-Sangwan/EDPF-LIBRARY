using System.Text.Json;
using Edpf.Abstractions.Configuration;
using Edpf.Abstractions.Primitives;
using Edpf.Configuration.Secrets;
using Edpf.UnitTests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Edpf.UnitTests.Configuration;

public sealed class SecretValueTests
{
    [Fact]
    public void ToString_Always_ReturnsRedactionMarkerNotValue()
    {
        using var secret = new SecretValue("hunter2");

        Assert.Equal(SecretValue.Redacted, secret.ToString());
        Assert.DoesNotContain("hunter2", secret.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StringInterpolation_Always_RedactsViaToString()
    {
        // The commonest accidental leak: someone interpolates the secret into
        // a message. ToString is the only path interpolation has.
        using var secret = new SecretValue("hunter2");

        string interpolated = $"connecting with {secret}";

        Assert.DoesNotContain("hunter2", interpolated, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonSerialization_Always_ExposesNoValue()
    {
        using var secret = new SecretValue("hunter2");

        string json = JsonSerializer.Serialize(secret);

        Assert.DoesNotContain("hunter2", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Reveal_Always_ReturnsRealValue()
    {
        using var secret = new SecretValue("hunter2");

        Assert.Equal("hunter2", secret.Reveal());
    }

    [Fact]
    public void Dispose_Always_ZeroesAndInvalidates()
    {
        var secret = new SecretValue("hunter2");

        secret.Dispose();

        Assert.Throws<ObjectDisposedException>(() => secret.Reveal());
    }

    [Fact]
    public void Equals_SameValue_IsTrue()
    {
        using var left = new SecretValue("same");
        using var right = new SecretValue("same");

        Assert.Equal(left, right);
    }

    [Fact]
    public void Equals_DifferentValue_IsFalse()
    {
        using var left = new SecretValue("one");
        using var right = new SecretValue("two");

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void GetHashCode_Always_DerivesFromLengthNotContent()
    {
        // Hashing content would leak a secret through any hash-code dump.
        using var left = new SecretValue("aaaa");
        using var right = new SecretValue("bbbb");

        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Constructor_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SecretValue(null!));
    }

    [Fact]
    public void Empty_Always_IsEmptyButNotMissing()
    {
        Assert.True(SecretValue.Empty.IsEmpty);
        Assert.Equal(0, SecretValue.Empty.Length);
    }
}

public sealed class SecretRotationTests
{
    private sealed class RecordingHandler(string key) : ISecretRotationHandler
    {
        public string SecretKey { get; } = key;

        public SecretRotationView? Observed { get; private set; }

        public bool ShouldFail { get; set; }

        public Task<Result> OnRotatedAsync(SecretRotationView rotation, CancellationToken cancellationToken)
        {
            Observed = rotation;
            return Task.FromResult(ShouldFail
                ? Result.Failure(new Error("EDPF-CFG-8001", "refresh failed", ErrorCategory.Configuration))
                : Result.Success());
        }
    }

    private static SecretRotationCoordinator CreateCoordinator(
        InMemorySecretStore store, params ISecretRotationHandler[] handlers)
        => new(store, handlers, new FakeClock(), NullLogger<SecretRotationCoordinator>.Instance);

    [Fact]
    public async Task RotateAsync_Always_NotifiesMatchingHandlerWithBothValues()
    {
        var clock = new FakeClock();
        var store = new InMemorySecretStore(clock);
        await store.SetAsync("Db:Password", new SecretValue("old"), CancellationToken.None);
        var handler = new RecordingHandler("Db:Password");

        Result<SecretRotationEvent> result = await CreateCoordinator(store, handler)
            .RotateAsync("Db:Password", new SecretValue("new"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("new", handler.Observed!.Current.Reveal());
        Assert.Equal("old", handler.Observed.Previous!.Reveal());
    }

    [Fact]
    public async Task RotateAsync_Always_AuditsKeyAndTimingButNotValue()
    {
        var store = new InMemorySecretStore(new FakeClock());
        await store.SetAsync("Db:Password", new SecretValue("old"), CancellationToken.None);

        Result<SecretRotationEvent> result = await CreateCoordinator(store)
            .RotateAsync("Db:Password", new SecretValue("super-secret"), CancellationToken.None);

        SecretRotationEvent audited = result.Value;
        Assert.Equal("Db:Password", audited.SecretKey);
        Assert.Equal("in-memory", audited.StoreName);
        Assert.NotNull(audited.OverlapExpiresUtc);

        // The audit record type has no member that could carry a value.
        string serialized = JsonSerializer.Serialize(audited);
        Assert.DoesNotContain("super-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RotateAsync_HandlerFails_ReturnsFailureAndLeavesOverlapOpen()
    {
        var store = new InMemorySecretStore(new FakeClock());
        await store.SetAsync("Db:Password", new SecretValue("old"), CancellationToken.None);
        var handler = new RecordingHandler("Db:Password") { ShouldFail = true };

        Result<SecretRotationEvent> result = await CreateCoordinator(store, handler)
            .RotateAsync("Db:Password", new SecretValue("new"), CancellationToken.None);

        Assert.True(result.IsFailure);

        // Traffic keeps flowing: the outgoing value is still accepted.
        Result<SecretRotationView> view =
            await store.GetForRotationAsync("Db:Password", CancellationToken.None);
        Assert.True(view.Value.IsRotating);
    }

    [Fact]
    public async Task RotateAsync_UnrelatedHandler_IsNotNotified()
    {
        var store = new InMemorySecretStore(new FakeClock());
        await store.SetAsync("Db:Password", new SecretValue("old"), CancellationToken.None);
        var unrelated = new RecordingHandler("Api:Key");

        await CreateCoordinator(store, unrelated)
            .RotateAsync("Db:Password", new SecretValue("new"), CancellationToken.None);

        Assert.Null(unrelated.Observed);
    }

    [Fact]
    public async Task GetForRotationAsync_AfterOverlapExpires_ReportsNoPreviousValue()
    {
        var clock = new FakeClock();
        var store = new InMemorySecretStore(clock, overlapWindow: TimeSpan.FromMinutes(15));
        await store.SetAsync("Db:Password", new SecretValue("old"), CancellationToken.None);
        await store.SetAsync("Db:Password", new SecretValue("new"), CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(16));
        Result<SecretRotationView> view =
            await store.GetForRotationAsync("Db:Password", CancellationToken.None);

        // A compromised credential is not honoured indefinitely.
        Assert.False(view.Value.IsRotating);
        Assert.Equal("new", view.Value.Current.Reveal());
    }
}

public sealed class ChainedSecretStoreTests
{
    [Fact]
    public async Task GetAsync_Always_FirstStoreWithKeyWins()
    {
        var high = new InMemorySecretStore(new FakeClock());
        var low = new InMemorySecretStore(new FakeClock());
        await high.SetAsync("Key", new SecretValue("from-high"), CancellationToken.None);
        await low.SetAsync("Key", new SecretValue("from-low"), CancellationToken.None);

        var chain = new ChainedSecretStore([high, low]);
        Result<SecretValue> result = await chain.GetAsync("Key", CancellationToken.None);

        Assert.Equal("from-high", result.Value.Reveal());
    }

    [Fact]
    public async Task GetAsync_MissingInFirst_FallsThroughToNext()
    {
        var high = new InMemorySecretStore(new FakeClock());
        var low = new InMemorySecretStore(new FakeClock());
        await low.SetAsync("Key", new SecretValue("from-low"), CancellationToken.None);

        var chain = new ChainedSecretStore([high, low]);
        Result<SecretValue> result = await chain.GetAsync("Key", CancellationToken.None);

        Assert.Equal("from-low", result.Value.Reveal());
    }

    [Fact]
    public async Task SetAsync_ReadOnlyFirstLayer_WritesToFirstWritableStore()
    {
        var readOnly = new EnvironmentSecretStore(reader: _ => null);
        var writable = new InMemorySecretStore(new FakeClock());

        var chain = new ChainedSecretStore([readOnly, writable]);
        Result written = await chain.SetAsync("Key", new SecretValue("v"), CancellationToken.None);

        Assert.True(written.IsSuccess);
        Assert.Equal("v", (await writable.GetAsync("Key", CancellationToken.None)).Value.Reveal());
    }

    [Fact]
    public void Constructor_EmptyChain_Throws()
    {
        Assert.Throws<ArgumentException>(() => new ChainedSecretStore([]));
    }

    [Fact]
    public void Name_Always_DescribesThePrecedenceOrder()
    {
        var chain = new ChainedSecretStore(
            [new EnvironmentSecretStore(reader: _ => null), new InMemorySecretStore(new FakeClock())]);

        Assert.Equal("chained[environment > in-memory]", chain.Name);
    }
}
