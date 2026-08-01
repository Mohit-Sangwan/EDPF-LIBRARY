namespace Edpf.Abstractions.Query;

/// <summary>
/// The complete, closed set of filter operators (ADR-018). Operators come
/// from this enum and nowhere else: there is no API anywhere in EDPF that
/// accepts an operator as a caller-supplied string, because that string would
/// eventually reach a query.
/// </summary>
public enum FilterOperator
{
    /// <summary>Equality.</summary>
    Equal = 0,

    /// <summary>Inequality.</summary>
    NotEqual = 1,

    /// <summary>Strictly greater.</summary>
    GreaterThan = 2,

    /// <summary>Greater or equal.</summary>
    GreaterThanOrEqual = 3,

    /// <summary>Strictly less.</summary>
    LessThan = 4,

    /// <summary>Less or equal.</summary>
    LessThanOrEqual = 5,

    /// <summary>Prefix match. The value is parameterised and its wildcards escaped.</summary>
    StartsWith = 6,

    /// <summary>Suffix match. Cannot use an index; the query-cost estimator accounts for it.</summary>
    EndsWith = 7,

    /// <summary>Substring match. Cannot use an index.</summary>
    Contains = 8,

    /// <summary>Membership in a bounded set. The set size is capped by the provider's parameter limit.</summary>
    In = 9,

    /// <summary>Non-membership in a bounded set.</summary>
    NotIn = 10,

    /// <summary>Inclusive range.</summary>
    Between = 11,

    /// <summary>Is null.</summary>
    IsNull = 12,

    /// <summary>Is not null.</summary>
    IsNotNull = 13,
}

/// <summary>How two predicates combine.</summary>
public enum FilterLogic
{
    /// <summary>Both must hold.</summary>
    And = 0,

    /// <summary>Either may hold.</summary>
    Or = 1,
}
