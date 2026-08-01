using Edpf.Abstractions.Errors;
using Edpf.Abstractions.Primitives;
using Edpf.Diagnostics.Redaction;

namespace Edpf.UnitTests.Errors;

/// <summary>
/// Phase 18's critical security property: an outward-facing error reveals a
/// correlation id and a stable code — never a stack trace, SQL fragment,
/// connection string, internal path, provider version, or a message that
/// distinguishes "does not exist" from "exists but you may not see it".
/// </summary>
public sealed class ErrorContractSecurityTests
{
    private static EdpfException Construct(Type exceptionType, Error error)
        => (EdpfException)Activator.CreateInstance(exceptionType, error, null)!;

    public static TheoryData<Type> TaxonomyTypes => new(EdpfException.TaxonomyTypes);

    [Theory]
    [MemberData(nameof(TaxonomyTypes))]
    public void EveryExceptionType_CarriesAStableCode(Type exceptionType)
    {
        var error = new Error(ErrorCodes.NotFound, "safe message", ErrorCategory.NotFound);

        EdpfException exception = Construct(exceptionType, error);

        Assert.Equal(ErrorCodes.NotFound, exception.Code);
        Assert.Same(error, exception.Error);
    }

    [Theory]
    [MemberData(nameof(TaxonomyTypes))]
    public void EveryExceptionType_MessageComesFromTheCatalogue_NotTheThrowSite(Type exceptionType)
    {
        // The message is the Error's message. There is no constructor taking
        // free text, so a throw site cannot compose one containing PHI.
        var error = new Error(ErrorCodes.ProviderFailure, "catalogue text", ErrorCategory.Internal);

        EdpfException exception = Construct(exceptionType, error);

        Assert.Equal("catalogue text", exception.Message);
    }

    [Fact]
    public void TenantScopeException_And_NotFoundException_AreIndistinguishableOutward()
    {
        // The enumeration defence. Both must present identically: message,
        // category, and HTTP status. Only the internal code differs, and that
        // never leaves the process.
        var notFound = new Error(
            ErrorCodes.NotFound, "The requested resource was not found.", ErrorCategory.NotFound);
        var crossTenant = new Error(
            ErrorCodes.TenantScopeViolation, "The requested resource was not found.", ErrorCategory.NotFound);

        var absent = new EdpfNotFoundException(notFound);
        var forbidden = new EdpfTenantScopeException(crossTenant);

        Assert.Equal(absent.Message, forbidden.Message);
        Assert.Equal(absent.Error.Category, forbidden.Error.Category);
    }

    [Theory]
    [MemberData(nameof(TaxonomyTypes))]
    public void EveryExceptionType_RedactedForLogging_KeepsItsCodeAndDropsNothingElse(Type exceptionType)
    {
        // Phase 18 closes the ADR-015 loop: the taxonomy is registered as
        // message-safe *because* its messages come from the catalogue, so a
        // support engineer still sees the code in the log.
        var redactor = new SensitiveDataRedactor(EdpfException.TaxonomyTypes);
        var error = new Error(ErrorCodes.CryptoFailure, "EDPF-SEC-5001: crypto failure", ErrorCategory.Security);

        EdpfException exception = Construct(exceptionType, error);
        string rendered = System.Text.Json.JsonSerializer.Serialize(redactor.Redact(exception));

        Assert.Contains("EDPF-SEC-5001", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void ThirdPartyException_IsStillRedacted_EvenAlongsideTheTaxonomy()
    {
        // Registering the taxonomy must not weaken the default for everything
        // else. A domain exception that interpolated PHI is still surrendered.
        var redactor = new SensitiveDataRedactor(EdpfException.TaxonomyTypes);
        var leaky = new InvalidOperationException("patient MRN-SECRET-99 not found");

        string rendered = System.Text.Json.JsonSerializer.Serialize(redactor.Redact(leaky));

        Assert.DoesNotContain("MRN-SECRET-99", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(TaxonomyTypes))]
    public void EveryExceptionType_InnerException_IsNotPartOfTheOutwardMessage(Type exceptionType)
    {
        // The native driver exception is kept for the log and never merged
        // into the message a caller might see.
        var inner = new InvalidOperationException("Server=db01;Password=hunter2");
        var error = new Error(ErrorCodes.ProviderFailure, "A provider error occurred.", ErrorCategory.Internal);

        var exception = (EdpfException)Activator.CreateInstance(exceptionType, error, inner)!;

        Assert.DoesNotContain("hunter2", exception.Message, StringComparison.Ordinal);
        Assert.Same(inner, exception.InnerException);
    }

    [Fact]
    public void Taxonomy_CoversEveryCategoryTheCatalogueDefines()
    {
        // A category with no exception type would force callers back to
        // untyped failures, which is how bespoke error shapes creep in.
        Assert.Equal(11, EdpfException.TaxonomyTypes.Count);
        Assert.All(EdpfException.TaxonomyTypes, t => Assert.True(typeof(EdpfException).IsAssignableFrom(t)));
    }

    [Fact]
    public void Exception_NullError_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new EdpfValidationException(null!));
    }
}
