using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Abstractions.Tenancy;
using Edpf.Data.Dialects;
using Edpf.Data.Query;
using Edpf.Metadata;

namespace Edpf.UnitTests.Metadata;

/// <summary>
/// Phase 05b — the metadata platform, and the ordering defect it closes.
/// </summary>
/// <remarks>
/// Appendix I.0: the dynamic-query safety model resolves caller-supplied
/// fields against entity metadata, but no metadata repository existed — the
/// query layer was implicitly assuming reflection over compile-time types,
/// which cannot describe a field a customer adds at runtime.
/// </remarks>
public sealed class MetadataPlatformTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    private static MetadataRepository RepositoryWithBaseEntity()
    {
        var repository = new MetadataRepository();
        repository.RegisterCompiled(new EntityMetadata(
            "SubjectRecord",
            "SUBJECT_RECORD",
            [
                new FieldMetadata("Id", "Id", typeof(Guid), DataClassificationLevel.Internal,
                    isFilterable: true, isSortable: true),
                new FieldMetadata("TenantId", "TenantId", typeof(Guid), DataClassificationLevel.Internal,
                    isFilterable: true, isSortable: true),
                new FieldMetadata("DisplayLabel", "DisplayLabel", typeof(string),
                    DataClassificationLevel.Internal, isFilterable: true, isSortable: true),
            ]));
        return repository;
    }

    /// <summary>
    /// Defines a field the way a customer would: at runtime, after the binary
    /// shipped, with nothing but a classification to say what it is.
    /// </summary>
    private static MetadataOverlay CustomPhiField(Guid tenantId, string name = "CustomIdentifier")
        => new(
            "SubjectRecord",
            tenantId,
            [
                new FieldMetadata(
                    name,
                    $"cf_{name}",
                    typeof(string),
                    DataClassificationLevel.Phi,
                    isRuntimeDefined: true,
                    storageStrategy: FieldStorageStrategy.JsonColumn),
            ],
            Now.AddDays(-1));

    // ── the central claim ──────────────────────────────────────────────────

    [Fact]
    public void RuntimeDefinedClassifiedField_ReceivesEveryProtection_WithNoCodeWritten()
    {
        // Phase 05b's stated verification, and the single test that decides
        // whether the classification-driven architecture is real: a field that
        // exists only because someone declared it at runtime must arrive
        // already encrypted, redacted, audited and subject-access-included.
        // Not one line below configures any of those four.
        MetadataRepository repository = RepositoryWithBaseEntity();
        Assert.True(repository.AddOverlay(CustomPhiField(TenantA)).IsSuccess);

        var resolver = new MetadataProtectionResolver(repository);

        DataProtectionRequirements required =
            resolver.ForField("SubjectRecord", "CustomIdentifier", TenantA, Now).Value;

        Assert.True(required.HasFlagSet(DataProtectionRequirements.EncryptAtRest));
        Assert.True(required.HasFlagSet(DataProtectionRequirements.RedactInDiagnostics));
        Assert.True(required.HasFlagSet(DataProtectionRequirements.AuditAccess));
        Assert.True(required.HasFlagSet(DataProtectionRequirements.IncludeInSubjectAccess));
        Assert.True(required.HasFlagSet(DataProtectionRequirements.ErasableByKeyDestruction));
    }

    [Fact]
    public void RuntimeDefinedField_AppearsInEverySubsystemsFieldList()
    {
        // The same question, asked the way each subsystem asks it. If any one
        // of the three answered differently, the gap between them would be a
        // disclosure that no single subsystem's tests would find.
        MetadataRepository repository = RepositoryWithBaseEntity();
        repository.AddOverlay(CustomPhiField(TenantA));
        var resolver = new MetadataProtectionResolver(repository);

        IReadOnlyList<string> encrypt = resolver.FieldsRequiring(
            "SubjectRecord", DataProtectionRequirements.EncryptAtRest, TenantA, Now).Value;
        IReadOnlyList<string> export = resolver.FieldsRequiring(
            "SubjectRecord", DataProtectionRequirements.IncludeInSubjectAccess, TenantA, Now).Value;
        IReadOnlyList<string> audit = resolver.FieldsRequiring(
            "SubjectRecord", DataProtectionRequirements.AuditAccess, TenantA, Now).Value;

        Assert.Contains("CustomIdentifier", encrypt);
        Assert.Contains("CustomIdentifier", export);
        Assert.Contains("CustomIdentifier", audit);
    }

    [Fact]
    public void RuntimeDefinedField_IsRedactedFromDiagnostics_LikeAnyOtherClassifiedField()
    {
        MetadataRepository repository = RepositoryWithBaseEntity();
        repository.AddOverlay(CustomPhiField(TenantA));
        var resolver = new MetadataProtectionResolver(repository);

        IEntityMetadata metadata = repository.GetEntity("SubjectRecord", TenantA, Now).Value;
        var entity = new DynamicEntity(metadata, TenantA);
        entity.SetValue("DisplayLabel", "record 4471");
        entity.SetValue("CustomIdentifier", "NHS-943-476-5919");

        IReadOnlyDictionary<string, object?> view = resolver.RedactForDiagnostics(entity, Now);

        Assert.Equal(MetadataProtectionResolver.RedactionMarker, view["CustomIdentifier"]);
        Assert.DoesNotContain(
            "943", string.Join('|', view.Values), StringComparison.Ordinal);

        // Unclassified data still comes through, or the diagnostic would be
        // useless and someone would go around it.
        Assert.Equal("record 4471", view["DisplayLabel"]);
    }

    [Fact]
    public void RuntimeDefinedField_IsQueryable_WhichReflectionCouldNotHaveAuthorized()
    {
        // The ordering defect made concrete. The query compiler authorizes a
        // filter on a field that did not exist when it was compiled, because
        // it resolves through the repository rather than over a CLR type.
        MetadataRepository repository = RepositoryWithBaseEntity();
        repository.AddOverlay(new MetadataOverlay(
            "SubjectRecord",
            TenantA,
            [
                new FieldMetadata(
                    "Department", "cf_Department", typeof(string), DataClassificationLevel.Internal,
                    isFilterable: true, isSortable: true, isRuntimeDefined: true,
                    storageStrategy: FieldStorageStrategy.SparseColumn),
            ],
            Now.AddDays(-1)));

        IEntityMetadata metadata = repository.GetEntity("SubjectRecord", TenantA, Now).Value;
        var compiler = new QueryCompiler(new SqlServerDialect(), metadata);

        Result<CompiledQuery> compiled = compiler.CompilePaged(
            Specification<object>.Create().Where("Department", FilterOperator.Equal, "Cardiology"),
            new TenantDescriptor(TenantA, "tenant-a", "eu-west", TenantIsolationMode.SharedSchema, Guid.NewGuid()),
            new PageRequest(1, 10));

        Assert.True(compiled.IsSuccess);

        // Resolved to its physical column, and the value is a parameter — the
        // custom field travels the same safe path as a compiled one.
        Assert.Contains("cf_Department", compiled.Value.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("Cardiology", compiled.Value.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDefinedEncryptedField_CannotBeDeclaredFilterable()
    {
        // Filtering ciphertext either fails or, under deterministic
        // encryption, leaks frequency information. A tenant defining a custom
        // PHI field must not be able to opt into that by ticking a box.
        ArgumentException error = Assert.Throws<ArgumentException>(() => new FieldMetadata(
            "CustomIdentifier", "cf_CustomIdentifier", typeof(string), DataClassificationLevel.Phi,
            isFilterable: true, isRuntimeDefined: true));

        Assert.Contains("blind index", error.Message, StringComparison.Ordinal);
    }

    // ── cross-tenant metadata isolation ────────────────────────────────────

    [Fact]
    public void OneTenantsCustomField_IsInvisibleToAnother()
    {
        // Metadata is tenant data. A field named "ClinicalTrialArm" tells a
        // competitor what that hospital is running, with no value attached.
        MetadataRepository repository = RepositoryWithBaseEntity();
        repository.AddOverlay(CustomPhiField(TenantA, "ClinicalTrialArm"));

        IEntityMetadata asTenantB = repository.GetEntity("SubjectRecord", TenantB, Now).Value;

        Assert.DoesNotContain("ClinicalTrialArm", asTenantB.Fields.Keys);
        Assert.True(asTenantB.ResolveField("ClinicalTrialArm").IsFailure);
    }

    [Fact]
    public void AnotherTenantsField_IsRefusedAsUnknown_NotAsForbidden()
    {
        // A "forbidden" would confirm the field exists somewhere, which is the
        // disclosure the scoping exists to prevent.
        MetadataRepository repository = RepositoryWithBaseEntity();
        repository.AddOverlay(CustomPhiField(TenantA, "ClinicalTrialArm"));

        IEntityMetadata asTenantB = repository.GetEntity("SubjectRecord", TenantB, Now).Value;
        Error error = asTenantB.ResolveField("ClinicalTrialArm").Error!;

        Assert.Equal(ErrorCategory.Validation, error.Category);
        Assert.DoesNotContain("tenant", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectedFieldName_DoesNotEnumerateTheTenantsOtherFields()
    {
        MetadataRepository repository = RepositoryWithBaseEntity();
        repository.AddOverlay(CustomPhiField(TenantA, "ClinicalTrialArm"));

        IEntityMetadata asTenantA = repository.GetEntity("SubjectRecord", TenantA, Now).Value;
        string message = asTenantA.ResolveField("Nonexistent").Error!.Message;

        Assert.Contains("Nonexistent", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ClinicalTrialArm", message, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayLabel", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomField_CannotShadowABuiltInField()
    {
        // Shadowing is how a tenant would redefine a classified field as
        // unclassified and strip its protections.
        MetadataRepository repository = RepositoryWithBaseEntity();

        Result result = repository.AddOverlay(new MetadataOverlay(
            "SubjectRecord",
            TenantA,
            [
                new FieldMetadata("DisplayLabel", "cf_DisplayLabel", typeof(string),
                    DataClassificationLevel.Public, isRuntimeDefined: true),
            ],
            Now.AddDays(-1)));

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Duplicate, result.Error!.Code);
    }

    // ── effective dating / reproducibility ─────────────────────────────────

    [Fact]
    public void MetadataAsOfAPastInstant_ReproducesTheDefinitionThatApplied()
    {
        // A form rendered in 2024 must reproduce exactly in an audit five
        // years later. "The field meant something different then" is not an
        // answer an auditor accepts.
        MetadataRepository repository = RepositoryWithBaseEntity();
        DateTimeOffset introduced = Now.AddYears(-2);

        repository.AddOverlay(new MetadataOverlay(
            "SubjectRecord",
            TenantA,
            [
                new FieldMetadata("LegacyCode", "cf_LegacyCode", typeof(string),
                    DataClassificationLevel.Internal, isFilterable: true, isRuntimeDefined: true),
            ],
            introduced,
            effectiveTo: Now.AddYears(-1)));

        IEntityMetadata whenItApplied = repository.GetEntity(
            "SubjectRecord", TenantA, introduced.AddDays(30)).Value;
        IEntityMetadata today = repository.GetEntity("SubjectRecord", TenantA, Now).Value;

        Assert.Contains("LegacyCode", whenItApplied.Fields.Keys);
        Assert.DoesNotContain("LegacyCode", today.Fields.Keys);
    }

    [Fact]
    public void MetadataBeforeAFieldExisted_DoesNotIncludeIt()
    {
        MetadataRepository repository = RepositoryWithBaseEntity();
        repository.AddOverlay(CustomPhiField(TenantA));

        IEntityMetadata before = repository.GetEntity(
            "SubjectRecord", TenantA, Now.AddYears(-1)).Value;

        Assert.DoesNotContain("CustomIdentifier", before.Fields.Keys);
    }

    [Fact]
    public void SameFieldDefinedTwiceOverOverlappingDates_IsRefused()
    {
        // Which definition applied would depend on ordering, and one of the
        // two may be the classified one.
        MetadataRepository repository = RepositoryWithBaseEntity();
        repository.AddOverlay(CustomPhiField(TenantA));

        Result second = repository.AddOverlay(CustomPhiField(TenantA));

        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCodes.Duplicate, second.Error!.Code);
    }

    [Fact]
    public void SameFieldRedefinedAfterTheFirstClosed_IsAllowed()
    {
        // Effective dating must permit succession, or a field could never be
        // corrected — only abandoned.
        MetadataRepository repository = RepositoryWithBaseEntity();

        repository.AddOverlay(new MetadataOverlay(
            "SubjectRecord", TenantA,
            [new FieldMetadata("Code", "cf_Code", typeof(string), DataClassificationLevel.Internal,
                isRuntimeDefined: true)],
            Now.AddYears(-2), effectiveTo: Now.AddYears(-1)));

        Result second = repository.AddOverlay(new MetadataOverlay(
            "SubjectRecord", TenantA,
            [new FieldMetadata("Code", "cf_Code", typeof(string), DataClassificationLevel.Phi,
                isRuntimeDefined: true)],
            Now.AddYears(-1)));

        Assert.True(second.IsSuccess);

        // And the reclassification is what each instant reports.
        var resolver = new MetadataProtectionResolver(repository);
        Assert.False(resolver.ForField("SubjectRecord", "Code", TenantA, Now.AddMonths(-18))
            .Value.HasFlagSet(DataProtectionRequirements.EncryptAtRest));
        Assert.True(resolver.ForField("SubjectRecord", "Code", TenantA, Now)
            .Value.HasFlagSet(DataProtectionRequirements.EncryptAtRest));
    }

    // ── dynamic entities ───────────────────────────────────────────────────

    [Fact]
    public void DynamicEntity_UndeclaredField_CannotBeWritten()
    {
        // Closes the property-bag failure mode: entity["ssn"] = value storing
        // a national identifier no classification covers, in a column no
        // encryption touches, in a write no audit records.
        MetadataRepository repository = RepositoryWithBaseEntity();
        IEntityMetadata metadata = repository.GetEntity("SubjectRecord", TenantA, Now).Value;
        var entity = new DynamicEntity(metadata, TenantA);

        Result result = entity.SetValue("ssn", "078-05-1120");

        Assert.True(result.IsFailure);
        Assert.Empty(entity.PopulatedFields);
    }

    [Fact]
    public void DynamicEntity_MistypedValue_IsRefused()
    {
        MetadataRepository repository = RepositoryWithBaseEntity();
        IEntityMetadata metadata = repository.GetEntity("SubjectRecord", TenantA, Now).Value;
        var entity = new DynamicEntity(metadata, TenantA);

        Assert.True(entity.SetValue("Id", "not-a-guid").IsFailure);
        Assert.True(entity.SetValue("Id", Guid.NewGuid()).IsSuccess);
    }

    [Fact]
    public void DynamicEntity_FieldNameCasing_ResolvesConsistently()
    {
        // Ordinal-ignore-case, not culture-sensitive: under a Turkish culture
        // "I".ToLower() is "ı", and a field would resolve differently
        // depending on the server's locale (Phase 27).
        MetadataRepository repository = RepositoryWithBaseEntity();
        IEntityMetadata metadata = repository.GetEntity("SubjectRecord", TenantA, Now).Value;
        var entity = new DynamicEntity(metadata, TenantA);

        entity.SetValue("displaylabel", "value");

        Assert.Equal("value", entity.GetValue("DisplayLabel").Value);
        Assert.Single(entity.PopulatedFields);
    }

    // ── the compiled path lands in the same model ──────────────────────────

    [Fact]
    public void CompiledEntity_AndRuntimeOverlay_ProduceIndistinguishableFields()
    {
        // Consumers must not be able to tell which produced a given field. If
        // they could, they would eventually branch on it, and the runtime path
        // — carrying fields nobody reviewed at compile time — would be the
        // weaker branch.
        EntityMetadata compiled = CompiledEntityScanner.Scan(typeof(ScannedRecord));

        IFieldMetadata scanned = compiled.Fields["SensitiveValue"];
        var declared = new FieldMetadata(
            "Custom", "cf_Custom", typeof(string), DataClassificationLevel.Phi, isRuntimeDefined: true);

        Assert.Equal(
            ProtectionPolicy.Default.For(scanned.Classification),
            ProtectionPolicy.Default.For(declared.Classification));
        Assert.Equal(scanned.IsFilterable, declared.IsFilterable);
    }

    [Fact]
    public void ScannedProperty_WithNoClassification_DefaultsToInternal_NotPublic()
    {
        // Forgetting to classify is the common mistake. It must not be the
        // mistake that publishes data.
        EntityMetadata compiled = CompiledEntityScanner.Scan(typeof(ScannedRecord));

        Assert.Equal(DataClassificationLevel.Internal, compiled.Fields["Untagged"].Classification);
    }

    [Fact]
    public void ScannedClassifiedProperty_IsNotFilterable()
    {
        // Derived, not asked for: a developer cannot opt a PHI column into a
        // WHERE clause by adding one attribute and forgetting another.
        EntityMetadata compiled = CompiledEntityScanner.Scan(typeof(ScannedRecord));

        Assert.False(compiled.Fields["SensitiveValue"].IsFilterable);
        Assert.True(compiled.Fields["Untagged"].IsFilterable);
    }

    [Fact]
    public void UnknownEntity_IsNotFound()
    {
        Result<IEntityMetadata> result = RepositoryWithBaseEntity()
            .GetEntity("NoSuchEntity", TenantA, Now);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCategory.NotFound, result.Error!.Category);
    }

    [Fact]
    public void OverlayFieldNotMarkedRuntimeDefined_IsRefused()
    {
        MetadataRepository repository = RepositoryWithBaseEntity();

        Result result = repository.AddOverlay(new MetadataOverlay(
            "SubjectRecord", TenantA,
            [new FieldMetadata("Custom", "cf_Custom", typeof(string), DataClassificationLevel.Internal)],
            Now.AddDays(-1)));

        Assert.True(result.IsFailure);
    }

    private sealed class ScannedRecord
    {
        public Guid Id { get; set; }

        public string Untagged { get; set; } = string.Empty;

        [DataClassification(DataClassificationLevel.Phi)]
        public string SensitiveValue { get; set; } = string.Empty;
    }
}

/// <summary>
/// Every classification level's protections, asserted rather than assumed.
/// </summary>
/// <remarks>
/// This table is what four subsystems consult. A silent change to any row
/// would change what gets encrypted, logged, audited and exported at once —
/// which is exactly why it should be hard to change without noticing.
/// </remarks>
public sealed class ProtectionPolicyTests
{
    [Theory]
    [InlineData(DataClassificationLevel.Public, false, false)]
    [InlineData(DataClassificationLevel.Internal, false, false)]
    [InlineData(DataClassificationLevel.Confidential, true, true)]
    [InlineData(DataClassificationLevel.Pii, true, true)]
    [InlineData(DataClassificationLevel.Phi, true, true)]
    public void Level_MapsToItsDocumentedHandlingRules(
        DataClassificationLevel level, bool encrypted, bool redacted)
    {
        DataProtectionRequirements required = ProtectionPolicy.Default.For(level);

        Assert.Equal(encrypted, required.HasFlagSet(DataProtectionRequirements.EncryptAtRest));
        Assert.Equal(redacted, required.HasFlagSet(DataProtectionRequirements.RedactInDiagnostics));
    }

    [Fact]
    public void PersonalData_IsBothExportableAndErasable()
    {
        // GDPR Art. 15 and Art. 17 apply to the same data, and ADR-006
        // satisfies erasure by destroying the key rather than the row so the
        // audit trail survives.
        foreach (DataClassificationLevel level in new[]
                 {
                     DataClassificationLevel.Pii, DataClassificationLevel.Phi,
                 })
        {
            DataProtectionRequirements required = ProtectionPolicy.Default.For(level);

            Assert.True(required.HasFlagSet(DataProtectionRequirements.IncludeInSubjectAccess));
            Assert.True(required.HasFlagSet(DataProtectionRequirements.ErasableByKeyDestruction));
        }
    }

    [Fact]
    public void PaymentData_IsTokenized_NotMerelyEncrypted()
    {
        // PCI DSS: the control is not holding the raw pan, not encrypting it.
        DataProtectionRequirements required = ProtectionPolicy.Default.For(DataClassificationLevel.Pci);

        Assert.True(required.HasFlagSet(DataProtectionRequirements.TokenizeNeverStoreRaw));
    }

    [Fact]
    public void UnrecognisedLevel_FailsTowardsTheStrongestTreatment()
    {
        // A future added level must over-protect rather than under-protect:
        // under-protecting is a breach, over-protecting is an inconvenience.
        DataProtectionRequirements required = ProtectionPolicy.Default.For((DataClassificationLevel)99);

        Assert.True(required.HasFlagSet(DataProtectionRequirements.EncryptAtRest));
        Assert.True(required.HasFlagSet(DataProtectionRequirements.RedactInDiagnostics));
        Assert.True(required.HasFlagSet(DataProtectionRequirements.AuditAccess));
    }

    [Fact]
    public void PublicData_RequiresNothing()
    {
        Assert.Equal(
            DataProtectionRequirements.None,
            ProtectionPolicy.Default.For(DataClassificationLevel.Public));
    }

    [Fact]
    public void RedactionThreshold_MatchesTheAdr015Redactor_LevelForLevel()
    {
        // Two redaction policies that disagree is the exact drift the metadata
        // platform exists to eliminate — and it already happened once while
        // this phase was being written: ProtectionPolicy initially redacted
        // Internal while the ADR-015 redactor did not. The reflection-driven
        // redactor and the metadata-driven one must answer identically for
        // every level, or a field's protection would depend on which subsystem
        // happened to look at it.
        var redactor = new Edpf.Diagnostics.Redaction.SensitiveDataRedactor();

        foreach (DataClassificationLevel level in Enum.GetValues<DataClassificationLevel>())
        {
            bool metadataRedacts = ProtectionPolicy.Default.For(level)
                .HasFlagSet(DataProtectionRequirements.RedactInDiagnostics);
            bool reflectionRedacts = redactor.CarriesClassifiedData(TypeTaggedWith(level));

            Assert.Equal(reflectionRedacts, metadataRedacts);
        }
    }

    private static Type TypeTaggedWith(DataClassificationLevel level) => level switch
    {
        DataClassificationLevel.Public => typeof(PublicHolder),
        DataClassificationLevel.Internal => typeof(InternalHolder),
        DataClassificationLevel.Confidential => typeof(ConfidentialHolder),
        DataClassificationLevel.Pii => typeof(PiiHolder),
        DataClassificationLevel.Phi => typeof(PhiHolder),
        DataClassificationLevel.Pci => typeof(PciHolder),
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private sealed class PublicHolder
    {
        [DataClassification(DataClassificationLevel.Public)]
        public string Value { get; set; } = string.Empty;
    }

    private sealed class InternalHolder
    {
        [DataClassification(DataClassificationLevel.Internal)]
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ConfidentialHolder
    {
        [DataClassification(DataClassificationLevel.Confidential)]
        public string Value { get; set; } = string.Empty;
    }

    private sealed class PiiHolder
    {
        [DataClassification(DataClassificationLevel.Pii)]
        public string Value { get; set; } = string.Empty;
    }

    private sealed class PhiHolder
    {
        [DataClassification(DataClassificationLevel.Phi)]
        public string Value { get; set; } = string.Empty;
    }

    private sealed class PciHolder
    {
        [DataClassification(DataClassificationLevel.Pci)]
        public string Value { get; set; } = string.Empty;
    }
}
