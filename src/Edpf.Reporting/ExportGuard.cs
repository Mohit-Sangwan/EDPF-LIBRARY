using System;
using System.Collections.Generic;
using Edpf.Abstractions.Identity;
using Edpf.Abstractions.Metadata;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Core.Guards;
using Edpf.Metadata;

namespace Edpf.Reporting;

/// <summary>
/// What an export was allowed to contain, and what it cost (Phase 33b).
/// </summary>
/// <remarks>
/// A bulk export of a multi-tenant clinical dataset is the highest-risk read
/// a system performs, and the one least likely to be noticed: it looks like a
/// report. The record exists so that "who took a copy of what, and when" has
/// an answer that does not depend on someone having watched.
/// </remarks>
public sealed class ExportManifest
{
    /// <summary>Initializes a manifest.</summary>
    /// <param name="exportName">What was exported.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="requestedBy">Who asked for it.</param>
    /// <param name="requestedUtc">When.</param>
    /// <param name="columns">The columns included, in order.</param>
    /// <param name="withheldColumns">Columns removed because the requester could not read them.</param>
    /// <param name="highestClassification">The most sensitive classification present.</param>
    /// <param name="rowLimit">The cap applied.</param>
    public ExportManifest(
        string exportName,
        Guid tenantId,
        string requestedBy,
        DateTimeOffset requestedUtc,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> withheldColumns,
        DataClassificationLevel highestClassification,
        int rowLimit)
    {
        ExportName = Guard.NotNullOrWhiteSpace(exportName, nameof(exportName));
        TenantId = Guard.NotDefault(tenantId, nameof(tenantId));
        RequestedBy = Guard.NotNullOrWhiteSpace(requestedBy, nameof(requestedBy));
        RequestedUtc = requestedUtc;
        Columns = Guard.NotNull(columns, nameof(columns));
        WithheldColumns = Guard.NotNull(withheldColumns, nameof(withheldColumns));
        HighestClassification = highestClassification;
        RowLimit = rowLimit;
    }

    /// <summary>What was exported.</summary>
    public string ExportName { get; }

    /// <summary>The owning tenant.</summary>
    public Guid TenantId { get; }

    /// <summary>Who asked for it.</summary>
    public string RequestedBy { get; }

    /// <summary>When.</summary>
    public DateTimeOffset RequestedUtc { get; }

    /// <summary>The columns included, in order.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>
    /// Columns removed because the requester could not read them.
    /// </summary>
    /// <remarks>
    /// Recorded for the same reason `ValuesWithheld` is recorded on a quality
    /// profile (ADR-028): a recipient who does not know a column was removed
    /// will read the export as complete, and act on it.
    /// </remarks>
    public IReadOnlyList<string> WithheldColumns { get; }

    /// <summary>
    /// The most sensitive classification present in the export.
    /// </summary>
    /// <remarks>
    /// **The export artefact inherits this.** A CSV containing one PHI column
    /// is a PHI artefact, and the file's storage, transport and retention must
    /// be governed accordingly. Recording it is what lets that happen without
    /// someone re-deriving it by inspection.
    /// </remarks>
    public DataClassificationLevel HighestClassification { get; }

    /// <summary>The row cap applied.</summary>
    public int RowLimit { get; }

    /// <summary>
    /// A summary safe to log, carrying no exported values.
    /// </summary>
    /// <returns>The summary.</returns>
    public override string ToString()
        => $"{ExportName} by {RequestedBy}: {Columns.Count} column(s), "
            + $"{WithheldColumns.Count} withheld, max {HighestClassification}, cap {RowLimit}";
}

/// <summary>
/// Decides what an export may contain (Phase 33b).
/// </summary>
/// <remarks>
/// <para>
/// **This is the second enforcement point ADR-031 asked for.** That decision
/// put field-level authorization in the query compiler and recorded, as a
/// revisit trigger, that any other path to the data — bulk export, reporting,
/// a direct provider call — needs the same check or an explicit exemption.
/// An export that skipped it would be the obvious way around every control
/// the query layer applies.
/// </para>
/// <para>
/// Unlike the query compiler, an export **withholds rather than refuses** an
/// unreadable column even when named explicitly, and records what it withheld.
/// A report definition is a long-lived artefact edited by one person and run
/// by many; failing the whole run because one recipient lacks one column would
/// mean the report simply stops working for most of the organisation, and the
/// response to that is invariably to grant everyone the permission.
/// </para>
/// </remarks>
public sealed class ExportGuard
{
    private readonly IDataProtectionPolicy _policy;

    /// <summary>Initializes a guard.</summary>
    /// <param name="maximumRows">
    /// The largest export permitted. There is no unlimited option.
    /// </param>
    /// <param name="policy">The classification-to-protection policy.</param>
    /// <exception cref="ArgumentOutOfRangeException">The cap is not positive.</exception>
    public ExportGuard(int maximumRows = DefaultMaximumRows, IDataProtectionPolicy? policy = null)
    {
        MaximumRows = Guard.Positive(maximumRows, nameof(maximumRows));
        _policy = policy ?? ProtectionPolicy.Default;
    }

    /// <summary>
    /// The default cap.
    /// </summary>
    /// <remarks>
    /// A number rather than "unlimited", because an unbounded export over a
    /// multi-tenant clinical dataset is an exfiltration channel that looks
    /// exactly like a report. The specification makes the same argument about
    /// unbounded GraphQL. A deployment needing more should raise it
    /// deliberately and know it did.
    /// </remarks>
    public const int DefaultMaximumRows = 100_000;

    /// <summary>The largest export permitted.</summary>
    public int MaximumRows { get; }

    /// <summary>
    /// Plans an export, removing columns the requester may not read.
    /// </summary>
    /// <param name="exportName">What is being exported.</param>
    /// <param name="metadata">The entity's metadata.</param>
    /// <param name="requestedColumns">The columns asked for; empty means every projectable column.</param>
    /// <param name="permissions">The requester's field permissions.</param>
    /// <param name="tenantId">The owning tenant.</param>
    /// <param name="requestedBy">Who asked.</param>
    /// <param name="requestedUtc">When.</param>
    /// <param name="requestedRowLimit">The cap asked for; clamped to <see cref="MaximumRows"/>.</param>
    /// <returns>The manifest, or a failure when nothing is exportable.</returns>
    public Result<ExportManifest> Plan(
        string exportName,
        IEntityMetadata metadata,
        IReadOnlyList<string> requestedColumns,
        IFieldPermissions? permissions,
        Guid tenantId,
        string requestedBy,
        DateTimeOffset requestedUtc,
        int requestedRowLimit)
    {
        Guard.NotNull(metadata, nameof(metadata));
        Guard.NotNull(requestedColumns, nameof(requestedColumns));

        IFieldPermissions granted = permissions ?? FieldPermissionSet.None;

        var included = new List<string>();
        var withheld = new List<string>();
        DataClassificationLevel highest = DataClassificationLevel.Public;

        IEnumerable<string> candidates = requestedColumns.Count > 0
            ? requestedColumns
            : AllProjectable(metadata);

        foreach (string columnName in candidates)
        {
            Result<IFieldMetadata> resolved = metadata.ResolveField(columnName);
            if (resolved.IsFailure)
            {
                // An unknown column is the requester's mistake and is worth
                // failing on: silently dropping it would produce a report
                // missing a column nobody notices is missing.
                return Result.Failure<ExportManifest>(resolved.Error!);
            }

            IFieldMetadata field = resolved.Value;

            if (!field.IsProjectable)
            {
                withheld.Add(field.Name);
                continue;
            }

            if (!string.IsNullOrEmpty(field.RequiredScope) && !granted.Grants(field.RequiredScope!))
            {
                withheld.Add(field.Name);
                continue;
            }

            included.Add(field.Name);

            if (field.Classification > highest)
            {
                highest = field.Classification;
            }
        }

        if (included.Count == 0)
        {
            return Result.Failure<ExportManifest>(new Error(
                ErrorCodes.FieldAccessDenied,
                "No column of this export is readable by the requester.",
                ErrorCategory.Authorization));
        }

        // Clamped, never honoured as requested. A cap the caller can raise is
        // not a cap (BRL-018 makes the same choice for page size).
        int limit = requestedRowLimit <= 0
            ? MaximumRows
            : Math.Min(requestedRowLimit, MaximumRows);

        return Result.Success(new ExportManifest(
            exportName, tenantId, requestedBy, requestedUtc, included, withheld, highest, limit));
    }

    /// <summary>
    /// Whether an export's artefact must itself be protected.
    /// </summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The protections the file inherits from its most sensitive column.</returns>
    /// <remarks>
    /// A CSV containing one PHI column is a PHI artefact. Answering this from
    /// the same <see cref="IDataProtectionPolicy"/> every other subsystem uses
    /// is what stops the file's handling being decided separately from the
    /// data's (ADR-025).
    /// </remarks>
    public DataProtectionRequirements ArtefactProtection(ExportManifest manifest)
    {
        Guard.NotNull(manifest, nameof(manifest));
        return _policy.For(manifest.HighestClassification);
    }

    private static List<string> AllProjectable(IEntityMetadata metadata)
    {
        var names = new List<string>();

        foreach (KeyValuePair<string, IFieldMetadata> pair in metadata.Fields)
        {
            if (pair.Value.IsProjectable)
            {
                names.Add(pair.Value.Name);
            }
        }

        // Sorted, so the same export definition produces the same column order
        // on every run and a downstream consumer parsing by position does not
        // break when the metadata dictionary rehashes.
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
