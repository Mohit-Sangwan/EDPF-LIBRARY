using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Edpf.Abstractions.Configuration;
using Edpf.Abstractions.Diagnostics;
using Edpf.Abstractions.Primitives;

namespace Edpf.Diagnostics.Redaction;

/// <summary>
/// The ADR-015 redactor, driven by the <see cref="DataClassificationAttribute"/>
/// tags introduced in Phase 01. This is the mechanism that makes "no PHI in
/// logs" a property of the build rather than a policy people are asked to
/// remember.
/// </summary>
/// <remarks>
/// <para>
/// **Redaction is opt-out.** A member is emitted only when its classification
/// is <see cref="DataClassificationLevel.Public"/> or
/// <see cref="DataClassificationLevel.Internal"/>, or when its type is on the
/// known-safe primitive list. Anything unrecognised — an unmapped complex
/// type, an untagged member of a type that carries classified data, a
/// <see cref="SecretValue"/> — is replaced with a marker.
/// </para>
/// <para>
/// The adversarial suite attempts to leak a PHI-bearing object by ten
/// different routes; all ten must be blocked (Phase 05 §⑤).
/// </para>
/// </remarks>
public sealed class SensitiveDataRedactor : ISensitiveDataRedactor
{
    /// <summary>Replaces any classified value.</summary>
    public const string RedactionMarker = "[REDACTED]";

    /// <summary>Replaces a value the redactor cannot prove is safe.</summary>
    public const string UnknownMarker = "[REDACTED:unclassified]";

    /// <summary>Guards against cycles and pathological nesting.</summary>
    public const int MaxDepth = 6;

    private static readonly ConcurrentDictionary<Type, bool> ClassifiedTypeCache = new();
    private static readonly ConcurrentDictionary<Type, MemberPlan[]> PlanCache = new();

    private readonly HashSet<Type> _messageSafeExceptionTypes;

    /// <summary>
    /// Initializes the redactor.
    /// </summary>
    /// <param name="messageSafeExceptionTypes">
    /// Exception types whose <see cref="Exception.Message"/> is contractually
    /// safe to emit. **Empty by default**: an arbitrary exception message is
    /// unclassified free text, and domain code routinely interpolates the very
    /// value the caller was forbidden from logging
    /// (<c>throw new(...$"patient {mrn} not found")</c>). Redaction is
    /// opt-out, so an unrecognised exception surrenders its message and keeps
    /// only its type — the correlation id is how the incident is investigated.
    /// The Phase 18 exception taxonomy registers its own types here, since
    /// those carry codes rather than payloads.
    /// </param>
    public SensitiveDataRedactor(IEnumerable<Type>? messageSafeExceptionTypes = null)
        => _messageSafeExceptionTypes = messageSafeExceptionTypes is null
            ? []
            : [.. messageSafeExceptionTypes];

    /// <inheritdoc />
    public object? Redact(object? value) => RedactCore(value, depth: 0, new HashSet<object>(ReferenceComparer.Instance));

    /// <inheritdoc />
    public string RedactText(string? value) => Sanitize(value ?? string.Empty);

    /// <inheritdoc />
    public bool CarriesClassifiedData(Type type)
    {
        if (type is null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        return ClassifiedTypeCache.GetOrAdd(type, static t => ComputeCarriesClassifiedData(t, depth: 0));
    }

    private object? RedactCore(object? value, int depth, HashSet<object> visited)
    {
        if (value is null)
        {
            return null;
        }

        // A secret is never rendered, at any depth, by any route.
        if (value is SecretValue)
        {
            return SecretValue.Redacted;
        }

        Type type = value.GetType();

        if (IsSafeScalar(type))
        {
            return value is string text ? Sanitize(text) : value;
        }

        if (depth >= MaxDepth)
        {
            return UnknownMarker;
        }

        // Reference cycles: an object graph that points at itself must not
        // hang the logging pipeline.
        if (!type.IsValueType && !visited.Add(value))
        {
            return "[REDACTED:cycle]";
        }

        try
        {
            if (value is Exception exception)
            {
                return RedactException(exception, depth, visited);
            }

            if (value is IDictionary dictionary)
            {
                return RedactDictionary(dictionary, depth, visited);
            }

            if (value is IEnumerable enumerable)
            {
                return RedactEnumerable(enumerable, depth, visited);
            }

            return RedactObject(value, type, depth, visited);
        }
        finally
        {
            if (!type.IsValueType)
            {
                visited.Remove(value);
            }
        }
    }

    private Dictionary<string, object?> RedactObject(object value, Type type, int depth, HashSet<object> visited)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$type"] = type.Name,
        };

        foreach (MemberPlan plan in GetPlan(type))
        {
            if (plan.IsRedacted)
            {
                result[plan.Name] = RedactionMarker;
                continue;
            }

            object? memberValue;
            try
            {
                memberValue = plan.Read(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A property getter that throws must not take the logging
                // pipeline down with it.
                result[plan.Name] = "[REDACTED:unreadable]";
                continue;
            }

            result[plan.Name] = RedactCore(memberValue, depth + 1, visited);
        }

        return result;
    }

    private Dictionary<string, object?> RedactException(Exception exception, int depth, HashSet<object> visited)
    {
        Type exceptionType = exception.GetType();

        // The message is surrendered unless the exception type is registered
        // as message-safe. A domain exception frequently interpolates the very
        // value the caller was forbidden from logging, and no amount of
        // sanitising a free-text string makes it classification-clean.
        bool messageIsSafe = _messageSafeExceptionTypes.Contains(exceptionType);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$type"] = exceptionType.Name,
            ["message"] = messageIsSafe ? Sanitize(exception.Message) : RedactionMarker,
        };

        if (exception.InnerException is not null && depth < MaxDepth)
        {
            result["inner"] = RedactCore(exception.InnerException, depth + 1, visited);
        }

        foreach (DictionaryEntry entry in exception.Data)
        {
            string key = "data." + Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            result[key] = RedactCore(entry.Value, depth + 1, visited);
        }

        return result;
    }

    private Dictionary<string, object?> RedactDictionary(IDictionary dictionary, int depth, HashSet<object> visited)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            string key = Sanitize(Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? "?");
            result[key] = RedactCore(entry.Value, depth + 1, visited);
        }

        return result;
    }

    private List<object?> RedactEnumerable(IEnumerable enumerable, int depth, HashSet<object> visited)
    {
        var items = new List<object?>();
        foreach (object? item in enumerable)
        {
            items.Add(RedactCore(item, depth + 1, visited));

            // Bound the work: a log line is not a data export.
            if (items.Count >= 50)
            {
                items.Add("[REDACTED:truncated]");
                break;
            }
        }

        return items;
    }

    private static MemberPlan[] GetPlan(Type type) => PlanCache.GetOrAdd(type, static t =>
    {
        var plans = new List<MemberPlan>();

        foreach (PropertyInfo property in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
            {
                continue;
            }

            plans.Add(new MemberPlan(
                property.Name,
                IsRedactedMember(property, property.PropertyType),
                property.GetValue));
        }

        foreach (FieldInfo field in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            plans.Add(new MemberPlan(
                field.Name,
                IsRedactedMember(field, field.FieldType),
                field.GetValue));
        }

        return [.. plans];
    });

    private static bool IsRedactedMember(MemberInfo member, Type memberType)
    {
        if (memberType == typeof(SecretValue))
        {
            return true;
        }

        DataClassificationAttribute? attribute =
            member.GetCustomAttribute<DataClassificationAttribute>(inherit: true)
            ?? memberType.GetCustomAttribute<DataClassificationAttribute>(inherit: true);

        // Opt-out: an untagged member is emitted only when its type is a
        // known-safe scalar or a container of them; anything else is treated
        // as potentially classified.
        if (attribute is null)
        {
            return !IsSafeScalar(memberType) && !IsInspectableComplexType(memberType);
        }

        return attribute.Level >= DataClassificationLevel.Confidential;
    }

    private static bool ComputeCarriesClassifiedData(Type type, int depth)
    {
        if (depth > MaxDepth || IsSafeScalar(type))
        {
            return false;
        }

        foreach (MemberInfo member in type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Public | BindingFlags.Instance)))
        {
            Type memberType = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

            if (memberType == typeof(SecretValue))
            {
                return true;
            }

            DataClassificationAttribute? attribute =
                member.GetCustomAttribute<DataClassificationAttribute>(inherit: true)
                ?? memberType.GetCustomAttribute<DataClassificationAttribute>(inherit: true);

            if (attribute is not null && attribute.Level >= DataClassificationLevel.Confidential)
            {
                return true;
            }

            if (!IsSafeScalar(memberType)
                && memberType != type
                && ComputeCarriesClassifiedData(memberType, depth + 1))
            {
                return true;
            }
        }

        return type.GetCustomAttribute<DataClassificationAttribute>(inherit: true) is
            { Level: >= DataClassificationLevel.Confidential };
    }

    private static bool IsInspectableComplexType(Type type)
        => type.IsClass && type != typeof(object) && !typeof(Delegate).IsAssignableFrom(type);

    private static bool IsSafeScalar(Type type)
    {
        Type actual = Nullable.GetUnderlyingType(type) ?? type;

        return actual.IsPrimitive
            || actual.IsEnum
            || actual == typeof(string)
            || actual == typeof(decimal)
            || actual == typeof(Guid)
            || actual == typeof(DateTime)
            || actual == typeof(DateTimeOffset)
            || actual == typeof(TimeSpan)
            || actual == typeof(Uri);
    }

    /// <summary>
    /// Neutralises newline and control characters so a logged value cannot
    /// forge additional log entries (Phase 05 §⑥).
    /// </summary>
    private static string Sanitize(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                _ when char.IsControl(c) => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }

        return builder.ToString();
    }

    private sealed class MemberPlan(string name, bool isRedacted, Func<object?, object?> read)
    {
        internal string Name { get; } = name;
        internal bool IsRedacted { get; } = isRedacted;
        internal Func<object?, object?> Read { get; } = read;
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static ReferenceComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
