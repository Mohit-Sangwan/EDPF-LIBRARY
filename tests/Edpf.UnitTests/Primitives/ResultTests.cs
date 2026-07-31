using Edpf.Abstractions.Primitives;

namespace Edpf.UnitTests.Primitives;

public sealed class ResultTests
{
    private static Error SomeError => new(ErrorCodes.NotFound, "missing", ErrorCategory.NotFound);

    [Fact]
    public void Success_NoValue_IsSuccessAndHasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_WithError_ExposesError()
    {
        var result = Result.Failure(SomeError);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.NotFound, result.Error!.Code);
    }

    [Fact]
    public void Failure_NullError_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Fact]
    public void Value_OnSuccess_ReturnsValue()
    {
        Result<int> result = Result.Success(42);

        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Value_OnFailure_Throws()
    {
        Result<int> result = Result.Failure<int>(SomeError);

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void TryGetValue_OnSuccess_ReturnsTrueAndValue()
    {
        Result<string> result = Result.Success("x");

        bool found = result.TryGetValue(out string? value);

        Assert.True(found);
        Assert.Equal("x", value);
    }

    [Fact]
    public void TryGetValue_OnFailure_ReturnsFalse()
    {
        Result<string> result = Result.Failure<string>(SomeError);

        bool found = result.TryGetValue(out _);

        Assert.False(found);
    }

    [Fact]
    public void Map_OnSuccess_TransformsValue()
    {
        Result<int> result = Result.Success(2);

        Result<int> mapped = result.Map(v => v * 10);

        Assert.Equal(20, mapped.Value);
    }

    [Fact]
    public void Map_OnFailure_PropagatesError()
    {
        Result<int> result = Result.Failure<int>(SomeError);

        Result<int> mapped = result.Map(v => v * 10);

        Assert.True(mapped.IsFailure);
        Assert.Same(result.Error, mapped.Error);
    }

    [Fact]
    public void Bind_OnSuccess_ChainsResult()
    {
        Result<int> result = Result.Success(2);

        Result<string> bound = result.Bind(
            v => Result.Success(v.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal("2", bound.Value);
    }

    [Fact]
    public void Bind_OnFailure_ShortCircuits()
    {
        Result<int> result = Result.Failure<int>(SomeError);
        bool invoked = false;

        Result<string> bound = result.Bind(v =>
        {
            invoked = true;
            return Result.Success(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });

        Assert.False(invoked);
        Assert.True(bound.IsFailure);
    }

    [Fact]
    public void Match_OnSuccess_TakesSuccessBranch()
    {
        Result<int> result = Result.Success(7);

        string outcome = result.Match(v => "value:" + v, e => "error:" + e.Code);

        Assert.Equal("value:7", outcome);
    }

    [Fact]
    public void Match_OnFailure_TakesFailureBranch()
    {
        Result<int> result = Result.Failure<int>(SomeError);

        string outcome = result.Match(v => "value:" + v, e => "error:" + e.Code);

        Assert.Equal("error:" + ErrorCodes.NotFound, outcome);
    }

    [Fact]
    public void ImplicitConversion_FromValue_IsSuccess()
    {
        Result<int> result = 5;

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.Value);
    }

    [Fact]
    public void ImplicitConversion_FromError_IsFailure()
    {
        Result<int> result = SomeError;

        Assert.True(result.IsFailure);
    }
}
