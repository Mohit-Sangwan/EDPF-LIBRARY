using Edpf.Connectors;

namespace Edpf.UnitTests.Connectors;

/// <summary>
/// Phase 26f — retry semantics and the manifest's secrets boundary.
/// </summary>
public sealed class RetryAndManifestTests
{
    private static readonly RetryPolicy Policy = new(
        maximumAttempts: 5,
        baseDelay: TimeSpan.FromSeconds(1),
        maximumDelay: TimeSpan.FromMinutes(2),
        jitterSeed: 7);

    // ── retry ──────────────────────────────────────────────────────────────

    [Fact]
    public void SourcesRetryAfter_OverridesTheComputedBackoff()
    {
        // The source is the only party that knows when its own limit resets.
        // Ignoring it in favour of a local delay is how a rate-limited
        // connector converts a throttle into a ban.
        RetryVerdict verdict = Policy.Decide(
            attempt: 1, RequestOutcome.RateLimited, retryAfter: TimeSpan.FromSeconds(90));

        Assert.Equal(RetryDecision.Retry, verdict.Decision);
        Assert.Equal(TimeSpan.FromSeconds(90), verdict.Delay);
    }

    [Fact]
    public void RetryAfterIsHonoured_EvenAboveOurOwnCeiling()
    {
        // Our ceiling is two minutes; the source says ten. The source wins,
        // because our ceiling is a guess about its recovery and its header is
        // a statement about it.
        RetryVerdict verdict = Policy.Decide(
            attempt: 1, RequestOutcome.RateLimited, retryAfter: TimeSpan.FromMinutes(10));

        Assert.Equal(TimeSpan.FromMinutes(10), verdict.Delay);
    }

    [Theory]
    [InlineData(RequestOutcome.Unauthorized)]
    [InlineData(RequestOutcome.Forbidden)]
    [InlineData(RequestOutcome.BadRequest)]
    public void FailuresRetryingCannotFix_AreFatal(RequestOutcome outcome)
    {
        // Retrying adds load to a source that has already said no, and a
        // retried 401 is how an account gets locked.
        RetryVerdict verdict = Policy.Decide(attempt: 1, outcome);

        Assert.Equal(RetryDecision.Fatal, verdict.Decision);
        Assert.Equal(TimeSpan.Zero, verdict.Delay);
    }

    [Theory]
    [InlineData(RequestOutcome.Transient)]
    [InlineData(RequestOutcome.Timeout)]
    [InlineData(RequestOutcome.RateLimited)]
    public void TransientFailures_AreRetried(RequestOutcome outcome)
    {
        Assert.Equal(RetryDecision.Retry, Policy.Decide(attempt: 1, outcome).Decision);
    }

    [Fact]
    public void AttemptBudget_IsSpentEventually()
    {
        RetryVerdict verdict = Policy.Decide(attempt: 5, RequestOutcome.Transient);

        Assert.Equal(RetryDecision.GiveUp, verdict.Decision);
        Assert.Contains("5 attempts are spent", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Backoff_GrowsWithEachAttempt()
    {
        TimeSpan first = Policy.DelayFor(1);
        TimeSpan third = Policy.DelayFor(3);

        Assert.True(third > first);
    }

    [Fact]
    public void Backoff_NeverExceedsTheCeiling()
    {
        // A high attempt count must not shift past the range of a long and
        // wrap to a negative delay.
        foreach (int attempt in new[] { 1, 10, 30, 60, 1_000 })
        {
            TimeSpan delay = Policy.DelayFor(attempt);

            Assert.True(delay > TimeSpan.Zero, $"Attempt {attempt} produced {delay}.");
            Assert.True(delay <= Policy.MaximumDelay, $"Attempt {attempt} produced {delay}.");
        }
    }

    [Fact]
    public void Jitter_IsDeterministic_SoBackoffIsReproducibleInAnIncidentReview()
    {
        // Derived from the attempt and seed rather than drawn from a random
        // source, so the same connector retries the same way every run.
        Assert.Equal(Policy.DelayFor(3), Policy.DelayFor(3));
    }

    [Fact]
    public void DifferentConnectors_DoNotRetryInLockstep()
    {
        // Without jitter, every connector that hit the same outage retries at
        // the same instant and the recovering source is hit by a synchronised
        // herd — often the thing that keeps it down.
        var a = new RetryPolicy(jitterSeed: 1);
        var b = new RetryPolicy(jitterSeed: 2);

        Assert.NotEqual(a.DelayFor(3), b.DelayFor(3));
    }

    [Fact]
    public void CeilingBelowTheBaseDelay_IsRefused()
    {
        // It would silently cancel the backoff.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(
            baseDelay: TimeSpan.FromSeconds(10), maximumDelay: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Success_NeedsNoRetry()
    {
        Assert.Equal(RetryDecision.GiveUp, Policy.Decide(1, RequestOutcome.Success).Decision);
    }

    // ── manifest ───────────────────────────────────────────────────────────

    private static ConnectorManifest Manifest(
        ConnectorAuthKind auth = ConnectorAuthKind.ApiKey, string? secretName = "labs-api-key")
        => new(
            "labs",
            "Acme LIS",
            auth,
            secretName,
            PaginationPlan.Keyset(200).Value,
            Policy,
            TimeSpan.FromSeconds(30));

    [Fact]
    public void Manifest_NamesTheSecret_AndCarriesNoValue()
    {
        // The manifest travels in source control, gets reviewed, diffed, and
        // copied between environments. A credential in it is a credential in
        // every one of those places.
        ConnectorManifest manifest = Manifest();

        Assert.Equal("labs-api-key", manifest.CredentialSecretName);

        // Asserted structurally. The rule is not "no credential-ish property
        // names" — `CredentialSecretName` is exactly the right name for a
        // pointer to a credential. The rule is that any such property must be
        // a REFERENCE: its name ends in SecretName or Reference, so a property
        // called `Password` or `ApiKey` that holds the value itself cannot be
        // added without failing here.
        string[] credentialWords = ["Password", "Token", "ApiKey", "Credential", "Secret", "Key"];

        foreach (System.Reflection.PropertyInfo property in typeof(ConnectorManifest).GetProperties())
        {
            bool mentionsCredential = false;
            foreach (string word in credentialWords)
            {
                if (property.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    mentionsCredential = true;
                    break;
                }
            }

            if (!mentionsCredential)
            {
                continue;
            }

            Assert.True(
                property.Name.EndsWith("SecretName", StringComparison.Ordinal)
                || property.Name.EndsWith("Reference", StringComparison.Ordinal),
                $"'{property.Name}' looks like it could hold a credential. A manifest travels in source "
                + "control and between environments, so it may name a secret but never carry one — "
                + "rename it to end in 'SecretName' or 'Reference', and fetch the value from ISecretStore.");
        }
    }

    [Fact]
    public void AuthenticatedConnectorWithNoNamedSecret_IsRefused()
    {
        // Leaving it unset is how a credential ends up inline in the manifest
        // instead.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => Manifest(ConnectorAuthKind.OAuthClientCredentials, secretName: null));

        Assert.Contains("must name the secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnauthenticatedConnector_NeedsNoSecret()
    {
        Assert.Null(Manifest(ConnectorAuthKind.None, secretName: null).CredentialSecretName);
    }

    [Fact]
    public void ManifestWithoutASafetyLag_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ConnectorManifest(
            "labs", "Acme LIS", ConnectorAuthKind.None, null,
            PaginationPlan.Keyset(200).Value, Policy, TimeSpan.Zero));
    }

    // ── audit ──────────────────────────────────────────────────────────────

    [Fact]
    public void RunRecord_CarriesCountsAndPositions_ButNoRecordContent()
    {
        // A connector log that echoed record content would put the source's
        // data into a log sink that was never assessed to hold it.
        var planner = new WatermarkPlanner(TimeSpan.FromSeconds(30));
        DateTimeOffset noon = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        var record = new ConnectorRunRecord(
            "labs",
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            noon,
            planner.PlanNext(SyncCursor.Beginning, noon),
            new SyncCursor(noon.AddMinutes(-1), "record-42"),
            recordsRead: 120,
            recordsRejected: 0,
            attempts: 2);

        string summary = record.ToString();

        Assert.Contains("read 120", summary, StringComparison.Ordinal);
        Assert.Contains("rejected 0", summary, StringComparison.Ordinal);
        Assert.Contains("record-42", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void RunRecord_SurfacesRejections_BecauseTheyMeanTheSourceIgnoredItsBounds()
    {
        // A non-zero count means the source is not honouring the bounds it was
        // given — worth an alert, since the completeness argument assumes it
        // filters as asked.
        var planner = new WatermarkPlanner(TimeSpan.FromSeconds(30));
        DateTimeOffset noon = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

        var record = new ConnectorRunRecord(
            "labs", Guid.NewGuid(), noon,
            planner.PlanNext(SyncCursor.Beginning, noon),
            SyncCursor.Beginning, recordsRead: 100, recordsRejected: 7, attempts: 1);

        Assert.Equal(7, record.RecordsRejected);
        Assert.Contains("rejected 7", record.ToString(), StringComparison.Ordinal);
    }
}
