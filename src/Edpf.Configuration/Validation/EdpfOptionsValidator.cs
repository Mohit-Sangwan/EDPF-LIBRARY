using System;
using System.Collections.Generic;
using System.Linq;
using Edpf.Abstractions.Configuration;
using Edpf.Core.Guards;
using Microsoft.Extensions.Options;

namespace Edpf.Configuration.Validation;

/// <summary>
/// Bridges <see cref="IConfigurationValidator{TOptions}"/> into the options
/// pipeline so EDPF validators participate in <c>ValidateOnStart</c>
/// (Phase 03 §④): the application fails fast at boot rather than at 3 a.m.
/// on a rarely-hit path.
/// </summary>
/// <typeparam name="TOptions">The options type being validated.</typeparam>
public sealed class EdpfOptionsValidator<TOptions> : IValidateOptions<TOptions>
    where TOptions : class
{
    private readonly List<IConfigurationValidator<TOptions>> _validators;
    private readonly string _sectionName;

    /// <summary>
    /// Initializes the bridge.
    /// </summary>
    /// <param name="validators">The EDPF validators for this options type.</param>
    /// <param name="sectionName">Configuration section name, used in failure messages.</param>
    public EdpfOptionsValidator(
        IEnumerable<IConfigurationValidator<TOptions>> validators,
        string sectionName)
    {
        _validators = Guard.NotNull(validators, nameof(validators)).ToList();
        _sectionName = Guard.NotNullOrWhiteSpace(sectionName, nameof(sectionName));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail(
                $"{Abstractions.Primitives.ErrorCodes.ConfigurationInvalid}: "
                + $"section '{_sectionName}' is missing.");
        }

        var failures = new List<string>();
        foreach (IConfigurationValidator<TOptions> validator in _validators)
        {
            failures.AddRange(validator
                .Validate(options)
                .Select(failure =>
                    $"{Abstractions.Primitives.ErrorCodes.ConfigurationInvalid}: {_sectionName}: {failure}"));
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
