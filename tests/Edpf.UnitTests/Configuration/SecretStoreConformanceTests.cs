using Edpf.Abstractions.Configuration;
using Edpf.Abstractions.Primitives;
using Edpf.Configuration.Secrets;
using Edpf.UnitTests.TestDoubles;

namespace Edpf.UnitTests.Configuration;

/// <summary>
/// The secret-store conformance suite (Phase 03 exit criteria: "all secret
/// stores pass an identical conformance suite"). Every backend — in-memory,
/// environment, and the cloud vaults that follow — runs these same cases.
/// A store that cannot pass them is not a supported store.
/// </summary>
public abstract class SecretStoreConformanceTests
{
    /// <summary>Creates the store under test, seeded with the given secrets.</summary>
    protected abstract ISecretStore CreateStore(IDictionary<string, string> seed);

    /// <summary>True when the store accepts writes; read-only stores skip the rotation cases.</summary>
    protected abstract bool SupportsWrites { get; }

    [Fact]
    public async Task GetAsync_ExistingSecret_ReturnsValue()
    {
        ISecretStore store = CreateStore(new Dictionary<string, string> { ["Db:Password"] = "s3cret" });

        Result<SecretValue> result = await store.GetAsync("Db:Password", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("s3cret", result.Value.Reveal());
    }

    [Fact]
    public async Task GetAsync_MissingSecret_FailsWithConfigurationCode()
    {
        ISecretStore store = CreateStore(new Dictionary<string, string>());

        Result<SecretValue> result = await store.GetAsync("Absent:Key", CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ConfigurationInvalid, result.Error!.Code);
    }

    [Fact]
    public async Task GetAsync_MissingSecret_ErrorNamesKeyButNoValue()
    {
        ISecretStore store = CreateStore(new Dictionary<string, string> { ["Other"] = "value-should-not-appear" });

        Result<SecretValue> result = await store.GetAsync("Absent:Key", CancellationToken.None);

        Assert.Contains("Absent:Key", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("value-should-not-appear", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetForRotationAsync_NotRotating_ReportsNoOverlap()
    {
        ISecretStore store = CreateStore(new Dictionary<string, string> { ["Db:Password"] = "v1" });

        Result<SecretRotationView> result =
            await store.GetForRotationAsync("Db:Password", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsRotating);
        Assert.Equal("v1", result.Value.Current.Reveal());
    }

    [Fact]
    public async Task GetAsync_NullOrBlankKey_Throws()
    {
        ISecretStore store = CreateStore(new Dictionary<string, string>());

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.GetAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.GetAsync("  ", CancellationToken.None));
    }

    [Fact]
    public async Task SetAsync_WhenSupported_MakesValueReadable()
    {
        if (!SupportsWrites)
        {
            return;
        }

        ISecretStore store = CreateStore(new Dictionary<string, string>());
        using var value = new SecretValue("new-value");

        Result written = await store.SetAsync("Db:Password", value, CancellationToken.None);
        Result<SecretValue> read = await store.GetAsync("Db:Password", CancellationToken.None);

        Assert.True(written.IsSuccess);
        Assert.Equal("new-value", read.Value.Reveal());
    }

    [Fact]
    public async Task SetAsync_WhenReadOnly_FailsExplicitly()
    {
        if (SupportsWrites)
        {
            return;
        }

        ISecretStore store = CreateStore(new Dictionary<string, string>());
        using var value = new SecretValue("attempted");

        Result written = await store.SetAsync("Db:Password", value, CancellationToken.None);

        // A read-only store must refuse loudly, never silently discard.
        Assert.True(written.IsFailure);
    }

    [Fact]
    public async Task SetAsync_OverExistingSecret_OpensRotationOverlap()
    {
        if (!SupportsWrites)
        {
            return;
        }

        ISecretStore store = CreateStore(new Dictionary<string, string> { ["Db:Password"] = "old" });
        using var incoming = new SecretValue("new");

        await store.SetAsync("Db:Password", incoming, CancellationToken.None);
        Result<SecretRotationView> view =
            await store.GetForRotationAsync("Db:Password", CancellationToken.None);

        Assert.True(view.Value.IsRotating);
        Assert.Equal("new", view.Value.Current.Reveal());
        Assert.Equal("old", view.Value.Previous!.Reveal());
    }

    [Fact]
    public void Name_Always_IsNonEmptyAndCredentialFree()
    {
        ISecretStore store = CreateStore(new Dictionary<string, string> { ["Db:Password"] = "s3cret" });

        Assert.False(string.IsNullOrWhiteSpace(store.Name));
        Assert.DoesNotContain("s3cret", store.Name, StringComparison.Ordinal);
    }
}

/// <summary>The in-memory store against the shared suite.</summary>
public sealed class InMemorySecretStoreConformanceTests : SecretStoreConformanceTests
{
    protected override bool SupportsWrites => true;

    protected override ISecretStore CreateStore(IDictionary<string, string> seed)
    {
        var store = new InMemorySecretStore(new FakeClock());
        foreach (KeyValuePair<string, string> entry in seed)
        {
            store.SetAsync(entry.Key, new SecretValue(entry.Value), CancellationToken.None)
                 .GetAwaiter().GetResult();
        }

        return store;
    }
}

/// <summary>The environment store against the shared suite.</summary>
public sealed class EnvironmentSecretStoreConformanceTests : SecretStoreConformanceTests
{
    protected override bool SupportsWrites => false;

    protected override ISecretStore CreateStore(IDictionary<string, string> seed)
    {
        // The reader is injected so conformance never mutates machine state.
        var variables = seed.ToDictionary(
            e => "EDPF_" + e.Key.Replace(':', '_').Replace('.', '_').ToUpperInvariant(),
            e => e.Value,
            StringComparer.Ordinal);

        return new EnvironmentSecretStore(
            reader: name => variables.TryGetValue(name, out string? value) ? value : null);
    }
}
