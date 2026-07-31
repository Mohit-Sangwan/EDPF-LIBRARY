using Edpf.Abstractions.Audit;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Security;
using Edpf.WalkingSkeleton.Api.Infrastructure.Audit;
using Edpf.WalkingSkeleton.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Edpf.WalkingSkeleton.Tests.Component;

/// <summary>
/// C4 §12.3 proofs: chain continuity, tamper evidence, and the ADR-006
/// invariant that erasure never breaks verification.
/// </summary>
public sealed class AuditChainTests : IDisposable
{
    private readonly SkeletonHarness _harness = new();
    private readonly AuditWriter _writer;
    private readonly AuditChainVerifier _verifier;

    public AuditChainTests()
    {
        _writer = new AuditWriter(
            _harness.Db, _harness.Tokenizer, _harness.Hashing, _harness.TenantAccessor, _harness.Clock);
        _verifier = new AuditChainVerifier(_harness.Db, _harness.Hashing);
    }

    public void Dispose() => _harness.Dispose();

    private Task<Result> WriteAsync(string eventType, Guid? subject = null) => _writer.WriteAsync(
        new AuditEventDescriptor(eventType, subject ?? Guid.NewGuid(), "corr-1"),
        CancellationToken.None);

    [Fact]
    public async Task Write_ThreeEvents_ChainsSequencesAndVerifies()
    {
        await WriteAsync("PatientCreated");
        await WriteAsync("PatientViewed");
        await WriteAsync("PatientViewed");

        Result<AuditChainVerification> verification =
            await _verifier.VerifyAsync(_harness.TenantId, CancellationToken.None);

        Assert.True(verification.Value.IsValid);
        Assert.Equal(3, verification.Value.RecordCount);
    }

    [Fact]
    public async Task Verify_TamperedEventType_ReportsBrokenLink()
    {
        await WriteAsync("PatientCreated");
        await WriteAsync("PatientViewed");

        AuditRow victim = await _harness.Db.AuditEvents.OrderBy(a => a.Sequence).FirstAsync();
        victim.EventType = "PatientDeleted"; // rewrite history
        await _harness.Db.SaveChangesAsync();

        Result<AuditChainVerification> verification =
            await _verifier.VerifyAsync(_harness.TenantId, CancellationToken.None);

        Assert.False(verification.Value.IsValid);
        Assert.Equal(1, verification.Value.FirstBrokenSequence);
    }

    [Fact]
    public async Task Verify_DeletedMiddleRecord_ReportsBrokenLink()
    {
        await WriteAsync("PatientCreated");
        await WriteAsync("PatientViewed");
        await WriteAsync("PatientViewed");

        AuditRow middle = await _harness.Db.AuditEvents.SingleAsync(a => a.Sequence == 2);
        _harness.Db.AuditEvents.Remove(middle);
        await _harness.Db.SaveChangesAsync();

        Result<AuditChainVerification> verification =
            await _verifier.VerifyAsync(_harness.TenantId, CancellationToken.None);

        Assert.False(verification.Value.IsValid);
    }

    [Fact]
    public async Task Verify_AfterSubjectErasure_ChainStillValid()
    {
        // The ADR-006 three-way reconciliation, in running code: audit is
        // tamper-evident AND the subject is erasable, simultaneously.
        var subject = Guid.NewGuid();
        await _harness.Crypto.EncryptAsync(
            [1, 2, 3], KeyScope.ForSubject(_harness.TenantId, subject), CancellationToken.None);
        await WriteAsync("PatientCreated", subject);
        await WriteAsync("PatientViewed", subject);

        Result destroyed = await _harness.Kms.DestroyAsync(
            KeyScope.ForSubject(_harness.TenantId, subject), CancellationToken.None);

        Result<AuditChainVerification> verification =
            await _verifier.VerifyAsync(_harness.TenantId, CancellationToken.None);

        Assert.True(destroyed.IsSuccess);
        Assert.True(verification.Value.IsValid);
    }

    [Fact]
    public async Task Write_Subject_NeverStoresRawIdentifier()
    {
        var subject = Guid.NewGuid();
        await WriteAsync("PatientCreated", subject);

        AuditRow row = await _harness.Db.AuditEvents.SingleAsync();

        Assert.DoesNotContain(subject.ToString("D"), row.SubjectToken, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(row.SubjectToken);
    }
}
