using System;
using System.Collections.Generic;
using Edpf.Core.Guards;
using Microsoft.Extensions.Configuration;

namespace Edpf.Configuration;

/// <summary>
/// The ADR-013 configuration precedence, as code. Sources are applied lowest
/// to highest, so a later source overrides an earlier one. Encoding the order
/// here — rather than leaving each host to assemble its own — is what makes
/// "why is this value not taking effect?" answerable.
/// </summary>
public static class EdpfConfigurationPrecedence
{
    /// <summary>
    /// The precedence order of ADR-013, lowest priority first. Asserted
    /// against the ADR by an architecture test.
    /// </summary>
    public static IReadOnlyList<ConfigurationSourceKind> Order { get; } =
    [
        ConfigurationSourceKind.BuiltInDefaults,
        ConfigurationSourceKind.AppSettings,
        ConfigurationSourceKind.AppSettingsEnvironment,
        ConfigurationSourceKind.LegacyXml,
        ConfigurationSourceKind.UserSecrets,
        ConfigurationSourceKind.EnvironmentVariables,
        ConfigurationSourceKind.CommandLine,
        ConfigurationSourceKind.SecretStore,
    ];

    /// <summary>
    /// Applies the standard EDPF precedence to a configuration builder.
    /// </summary>
    /// <param name="builder">The builder to configure.</param>
    /// <param name="environmentName">Environment name, e.g. <c>Production</c>.</param>
    /// <param name="defaults">Built-in defaults — the lowest-priority layer.</param>
    /// <param name="basePath">Directory holding the JSON files. Defaults to the current directory.</param>
    /// <param name="commandLineArgs">Process arguments, when the host has them.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="environmentName"/> is null.</exception>
    public static IConfigurationBuilder AddEdpfPrecedence(
        this IConfigurationBuilder builder,
        string environmentName,
        IReadOnlyDictionary<string, string?>? defaults = null,
        string? basePath = null,
        string[]? commandLineArgs = null)
    {
        Guard.NotNull(builder, nameof(builder));
        Guard.NotNullOrWhiteSpace(environmentName, nameof(environmentName));

        if (defaults is { Count: > 0 })
        {
            builder.AddInMemoryCollection(defaults);
        }

        if (basePath is { Length: > 0 })
        {
            builder.SetBasePath(basePath);
        }

        builder.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
        builder.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true);
        builder.AddEnvironmentVariables();

        if (commandLineArgs is { Length: > 0 })
        {
            builder.AddCommandLine(commandLineArgs);
        }

        // The secret store is applied last — highest precedence — by the host
        // through AddEdpfSecrets, after the store itself has been resolved.
        return builder;
    }
}

/// <summary>The configuration source kinds ADR-013 orders.</summary>
public enum ConfigurationSourceKind
{
    /// <summary>Framework defaults compiled into the assembly.</summary>
    BuiltInDefaults = 0,

    /// <summary><c>appsettings.json</c>.</summary>
    AppSettings = 1,

    /// <summary><c>appsettings.{Environment}.json</c>.</summary>
    AppSettingsEnvironment = 2,

    /// <summary><c>web.config</c> / <c>app.config</c> for Tier 3 legacy hosts.</summary>
    LegacyXml = 3,

    /// <summary>User secrets — development only, never present in production.</summary>
    UserSecrets = 4,

    /// <summary>Environment variables.</summary>
    EnvironmentVariables = 5,

    /// <summary>Command-line arguments.</summary>
    CommandLine = 6,

    /// <summary>The secret store — highest precedence, and the only source trusted with credentials.</summary>
    SecretStore = 7,
}
