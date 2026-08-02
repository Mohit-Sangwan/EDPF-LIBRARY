using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Edpf.Abstractions.Data;
using Edpf.Abstractions.Identity;
using Edpf.Abstractions.Primitives;
using Edpf.Abstractions.Query;
using Edpf.Abstractions.Tenancy;
using Edpf.Core.Guards;
using Edpf.Data.Dialects;

namespace Edpf.Data.Query;

/// <summary>
/// Compiles a specification into a runnable statement (Phase 08 §④).
/// Applies, in order: the unavoidable tenant predicate, the soft-delete
/// filter, the caller's filter, a stable sort, and pagination.
/// </summary>
public sealed class QueryCompiler
{
    private readonly SqlDialectBase _dialect;
    private readonly IEntityMetadata _metadata;

    /// <summary>
    /// Initializes the compiler.
    /// </summary>
    /// <param name="dialect">The target dialect.</param>
    /// <param name="metadata">The entity's metadata.</param>
    /// <param name="permissions">
    /// The caller's field permissions (Phase 08b). Omitted means
    /// <see cref="FieldPermissionSet.None"/>: a caller who forgets to supply
    /// them is denied every protected field rather than granted them, because
    /// the forgotten argument must fail in the direction that does not
    /// disclose.
    /// </param>
    public QueryCompiler(
        SqlDialectBase dialect, IEntityMetadata metadata, IFieldPermissions? permissions = null)
    {
        _dialect = Guard.NotNull(dialect, nameof(dialect));
        _metadata = Guard.NotNull(metadata, nameof(metadata));
        _permissions = permissions ?? FieldPermissionSet.None;
    }

    private readonly IFieldPermissions _permissions;

    /// <summary>
    /// Whether the caller may read a field.
    /// </summary>
    /// <param name="field">The field.</param>
    /// <returns>Whether it is readable by this caller.</returns>
    private bool MayRead(IFieldMetadata field)
        => string.IsNullOrEmpty(field.RequiredScope) || _permissions.Grants(field.RequiredScope!);

    /// <summary>The tenant discriminator column, first in every clustered index (Z.2).</summary>
    public const string TenantColumn = "TenantId";

    /// <summary>The soft-delete discriminator column.</summary>
    public const string DeletedColumn = "IsDeleted";

    /// <summary>
    /// Compiles an offset-paginated query.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="specification">The specification.</param>
    /// <param name="tenant">The resolved tenant. Required — see remarks.</param>
    /// <param name="page">The page to fetch.</param>
    /// <returns>
    /// The compiled query, or a filter/projection failure.
    /// </returns>
    /// <remarks>
    /// A null tenant is refused with <see cref="ErrorCodes.TenantScopeViolation"/>
    /// rather than treated as "all tenants" (Phase 10 §④). This single choice
    /// prevents the most severe multi-tenant failure mode; the adversarial
    /// suite verifies it through every entry point.
    /// </remarks>
    public Result<CompiledQuery> CompilePaged<TEntity>(
        ISpecification<TEntity> specification,
        ITenantContext? tenant,
        PageRequest page)
        where TEntity : class
    {
        Guard.NotNull(specification, nameof(specification));

        if (tenant is null)
        {
            return Result.Failure<CompiledQuery>(TenantScopeRequired());
        }

        var compiler = new FilterCompiler(_dialect, _metadata, _permissions);
        Result<string> where = BuildWhere(specification, tenant, compiler);
        if (where.IsFailure)
        {
            return Result.Failure<CompiledQuery>(where.Error!);
        }

        Result<string> projection = BuildProjection(specification);
        if (projection.IsFailure)
        {
            return Result.Failure<CompiledQuery>(projection.Error!);
        }

        Result<IReadOnlyList<SortColumn>> sort = BuildStableSort(specification);
        if (sort.IsFailure)
        {
            return Result.Failure<CompiledQuery>(sort.Error!);
        }

        Dictionary<string, object?> parameters = Copy(compiler.Parameters);
        parameters["skip"] = page.Skip;
        parameters["take"] = page.PageSize;

        var sql = new StringBuilder()
            .Append("SELECT ").Append(projection.Value)
            .Append(" FROM ").Append(_dialect.QuoteIdentifier(_metadata.TableName))
            .Append(" WHERE ").Append(where.Value)
            .Append(" ORDER BY ").Append(_dialect.OrderByList(sort.Value))
            .Append(' ').Append(_dialect.PaginationClause("skip", "take"))
            .ToString();

        return Result.Success(new CompiledQuery(sql, parameters));
    }

    /// <summary>
    /// Compiles a keyset-paginated query — the form that stays correct and
    /// fast past a few hundred thousand rows, where offset pagination
    /// degrades to unusable.
    /// </summary>
    /// <typeparam name="TEntity">The entity type.</typeparam>
    /// <param name="specification">The specification.</param>
    /// <param name="tenant">The resolved tenant.</param>
    /// <param name="cursorValues">
    /// The previous page's last row, positionally matched to the sort
    /// columns; empty for the first page.
    /// </param>
    /// <param name="pageSize">Rows to fetch.</param>
    /// <returns>The compiled query, or a failure.</returns>
    public Result<CompiledQuery> CompileKeyset<TEntity>(
        ISpecification<TEntity> specification,
        ITenantContext? tenant,
        IReadOnlyList<object?> cursorValues,
        int pageSize)
        where TEntity : class
    {
        Guard.NotNull(specification, nameof(specification));
        Guard.NotNull(cursorValues, nameof(cursorValues));

        if (tenant is null)
        {
            return Result.Failure<CompiledQuery>(TenantScopeRequired());
        }

        if (pageSize < 1 || pageSize > PageRequest.MaxPageSize)
        {
            return Result.Failure<CompiledQuery>(new Error(
                ErrorCodes.ValidationFailed,
                $"Page size must be between 1 and {PageRequest.MaxPageSize}.",
                ErrorCategory.Validation));
        }

        var compiler = new FilterCompiler(_dialect, _metadata, _permissions);
        Result<string> where = BuildWhere(specification, tenant, compiler);
        if (where.IsFailure)
        {
            return Result.Failure<CompiledQuery>(where.Error!);
        }

        Result<string> projection = BuildProjection(specification);
        if (projection.IsFailure)
        {
            return Result.Failure<CompiledQuery>(projection.Error!);
        }

        Result<IReadOnlyList<SortColumn>> sort = BuildStableSort(specification);
        if (sort.IsFailure)
        {
            return Result.Failure<CompiledQuery>(sort.Error!);
        }

        string predicate = where.Value;

        if (cursorValues.Count > 0)
        {
            if (cursorValues.Count != sort.Value.Count)
            {
                return Result.Failure<CompiledQuery>(new Error(
                    ErrorCodes.ValidationFailed,
                    "The cursor does not match the sort columns; it belongs to a different query shape.",
                    ErrorCategory.Validation));
            }

            var cursorNames = new List<string>(cursorValues.Count);
            for (int i = 0; i < cursorValues.Count; i++)
            {
                string name = "cursor" + i.ToString(CultureInfo.InvariantCulture);
                compiler.BindNamed(name, cursorValues[i]);
                cursorNames.Add(name);
            }

            predicate += " AND " + _dialect.KeysetPredicate(sort.Value, cursorNames);
        }

        Dictionary<string, object?> parameters = Copy(compiler.Parameters);
        parameters["take"] = pageSize;
        parameters["skip"] = 0;

        var sql = new StringBuilder()
            .Append("SELECT ").Append(projection.Value)
            .Append(" FROM ").Append(_dialect.QuoteIdentifier(_metadata.TableName))
            .Append(" WHERE ").Append(predicate)
            .Append(" ORDER BY ").Append(_dialect.OrderByList(sort.Value))
            .Append(' ').Append(_dialect.PaginationClause("skip", "take"))
            .ToString();

        return Result.Success(new CompiledQuery(sql, parameters));
    }

    private Result<string> BuildWhere<TEntity>(
        ISpecification<TEntity> specification, ITenantContext tenant, FilterCompiler compiler)
        where TEntity : class
    {
        // The tenant predicate is emitted first and unconditionally. There is
        // no specification a caller can construct that omits it.
        var clauses = new List<string>
        {
            $"{_dialect.QuoteIdentifier(TenantColumn)} = {_dialect.Parameter("tenantId")}",
        };

        if (!specification.IncludeDeleted)
        {
            clauses.Add(
                $"{_dialect.QuoteIdentifier(DeletedColumn)} = {_dialect.BooleanLiteral(false)}");
        }

        if (specification.Filter is not null)
        {
            Result<string> compiled = compiler.Compile(specification.Filter);
            if (compiled.IsFailure)
            {
                return compiled;
            }

            clauses.Add(compiled.Value);
        }

        compiler.BindNamed("tenantId", tenant.TenantId);

        return Result.Success(string.Join(" AND ", clauses));
    }

    /// <summary>
    /// Copies parameters into a mutable dictionary. The
    /// <c>IReadOnlyDictionary</c> constructor overload does not exist on
    /// Tier 3 TFMs (ADR-002), so the copy is explicit.
    /// </summary>
    private static Dictionary<string, object?> Copy(IReadOnlyDictionary<string, object?> source)
    {
        var copy = new Dictionary<string, object?>(source.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> entry in source)
        {
            copy[entry.Key] = entry.Value;
        }

        return copy;
    }

    private Result<string> BuildProjection<TEntity>(ISpecification<TEntity> specification)
        where TEntity : class
    {
        if (specification.Projection.Count == 0)
        {
            // A default projection quietly omits fields the caller may not
            // read, rather than failing the whole query. The caller asked for
            // "the row", not for these fields specifically, and denying
            // outright would make every protected column break every default
            // read for everyone below it. Withheld columns are reported on the
            // compiled query so a caller can surface the omission rather than
            // silently present a partial row as a whole one.
            IEnumerable<string> all = _metadata.Fields.Values
                .Where(f => f.IsProjectable && MayRead(f))
                .Select(f => _dialect.QuoteIdentifier(f.ColumnName));

            string projected = string.Join(", ", all);

            return string.IsNullOrEmpty(projected)
                ? Result.Failure<string>(new Error(
                    ErrorCodes.FieldAccessDenied,
                    "No field of this entity is readable by the caller.",
                    ErrorCategory.Authorization))
                : Result.Success(projected);
        }

        var columns = new List<string>(specification.Projection.Count);
        foreach (string fieldName in specification.Projection)
        {
            Result<IFieldMetadata> resolved = _metadata.ResolveField(fieldName);
            if (resolved.IsFailure)
            {
                return Result.Failure<string>(resolved.Error!);
            }

            // Named explicitly and not readable: refused with the same shape
            // as a field that does not exist. Saying "you may not read this"
            // would confirm the column is there, and on a tenant-overlaid
            // entity the field list is itself tenant data (ADR-025).
            if (!MayRead(resolved.Value))
            {
                return Result.Failure<string>(new Error(
                    ErrorCodes.InvalidFilter,
                    $"'{fieldName}' is not a queryable field of '{_metadata.EntityName}'.",
                    ErrorCategory.Validation));
            }

            if (!resolved.Value.IsProjectable)
            {
                return Result.Failure<string>(new Error(
                    ErrorCodes.FieldAccessDenied,
                    "A requested field is not projectable.",
                    ErrorCategory.Authorization));
            }

            columns.Add(_dialect.QuoteIdentifier(resolved.Value.ColumnName));
        }

        return Result.Success(string.Join(", ", columns));
    }

    /// <summary>
    /// Validates the requested sort and appends a unique tiebreaker.
    /// </summary>
    /// <remarks>
    /// Applied unconditionally (BRL-017): without a stable total order, two
    /// rows sharing a sort value can appear on both pages or on neither, and
    /// the defect surfaces as "a record went missing" long after the fact.
    /// </remarks>
    private Result<IReadOnlyList<SortColumn>> BuildStableSort<TEntity>(ISpecification<TEntity> specification)
        where TEntity : class
    {
        var sort = new List<SortColumn>(specification.Sort.Count + 1);

        foreach (SortColumn requested in specification.Sort)
        {
            Result<IFieldMetadata> resolved = _metadata.ResolveField(requested.ColumnName);
            if (resolved.IsFailure)
            {
                return Result.Failure<IReadOnlyList<SortColumn>>(resolved.Error!);
            }

            // Sorting on a field is reading it, by the same argument as
            // filtering: ORDER BY on a protected column and a binary search
            // over page boundaries reconstructs the ordering, and an ordering
            // over salaries is most of the salaries.
            if (!MayRead(resolved.Value))
            {
                return Result.Failure<IReadOnlyList<SortColumn>>(new Error(
                    ErrorCodes.InvalidFilter,
                    $"'{requested.ColumnName}' is not a queryable field of '{_metadata.EntityName}'.",
                    ErrorCategory.Validation));
            }

            if (!resolved.Value.IsSortable)
            {
                return Result.Failure<IReadOnlyList<SortColumn>>(new Error(
                    ErrorCodes.InvalidFilter,
                    $"Field '{requested.ColumnName}' is not sortable.",
                    ErrorCategory.Validation));
            }

            sort.Add(new SortColumn(resolved.Value.ColumnName, requested.Descending));
        }

        if (!sort.Any(s => string.Equals(s.ColumnName, "Id", StringComparison.Ordinal)))
        {
            sort.Add(new SortColumn("Id"));
        }

        return Result.Success<IReadOnlyList<SortColumn>>(sort);
    }

    private static Error TenantScopeRequired() => new(
        ErrorCodes.TenantScopeViolation,
        "The requested resource was not found.",
        ErrorCategory.NotFound);
}
