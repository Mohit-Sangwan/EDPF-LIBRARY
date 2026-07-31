using Edpf.Abstractions.Primitives;
using Edpf.Core.Guards;

namespace Edpf.UnitTests.Primitives;

public sealed class EntityIdTests
{
    private sealed class PatientBrand;
    private sealed class EncounterBrand;

    [Fact]
    public void New_Always_ProducesNonEmptyId()
    {
        var id = EntityId<PatientBrand>.New();

        Assert.False(id.IsEmpty);
    }

    [Fact]
    public void Equals_SameGuidSameBrand_AreEqual()
    {
        var guid = Guid.NewGuid();

        Assert.Equal(EntityId<PatientBrand>.From(guid), EntityId<PatientBrand>.From(guid));
        Assert.True(EntityId<PatientBrand>.From(guid) == EntityId<PatientBrand>.From(guid));
    }

    [Fact]
    public void Equals_SameGuidDifferentBrand_AreDistinctTypes()
    {
        var guid = Guid.NewGuid();
        object patient = EntityId<PatientBrand>.From(guid);
        object encounter = EntityId<EncounterBrand>.From(guid);

        Assert.False(patient.Equals(encounter));
    }

    [Fact]
    public void Empty_Default_IsEmpty()
    {
        Assert.True(EntityId<PatientBrand>.Empty.IsEmpty);
    }
}

public sealed class PageRequestTests
{
    [Fact]
    public void Constructor_FirstPage_SkipsZero()
    {
        var page = new PageRequest(1, 50);

        Assert.Equal(0, page.Skip);
    }

    [Fact]
    public void Constructor_ThirdPageOfTwenty_SkipsForty()
    {
        var page = new PageRequest(3, 20);

        Assert.Equal(40, page.Skip);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_PageBelowOne_Throws(int pageNumber)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(pageNumber));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PageRequest.MaxPageSize + 1)]
    public void Constructor_PageSizeOutOfBounds_Throws(int pageSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageRequest(1, pageSize));
    }

    [Fact]
    public void Default_Instance_IsFirstPageAtDefaultSize()
    {
        PageRequest page = default;

        Assert.Equal(1, page.PageNumber);
        Assert.Equal(PageRequest.DefaultPageSize, page.PageSize);
    }
}

public sealed class PagedResultTests
{
    [Fact]
    public void TotalPages_ExactMultiple_ComputesCorrectly()
    {
        var paged = new PagedResult<int>([1, 2], new PageRequest(1, 2), totalCount: 4);

        Assert.Equal(2, paged.TotalPages);
        Assert.True(paged.HasNextPage);
        Assert.False(paged.HasPreviousPage);
    }

    [Fact]
    public void TotalPages_ZeroItems_IsZero()
    {
        var paged = new PagedResult<int>([], new PageRequest(1, 10), totalCount: 0);

        Assert.Equal(0, paged.TotalPages);
        Assert.False(paged.HasNextPage);
    }

    [Fact]
    public void Constructor_NegativeTotal_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PagedResult<int>([], new PageRequest(1, 10), totalCount: -1));
    }

    [Fact]
    public void Constructor_NullItems_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new PagedResult<int>(null!, new PageRequest(1, 10), totalCount: 0));
    }
}

public sealed class GuardTests
{
    [Fact]
    public void NotNull_WithValue_ReturnsValue()
    {
        var value = new object();

        Assert.Same(value, Guard.NotNull(value, nameof(value)));
    }

    [Fact]
    public void NotNull_WithNull_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => Guard.NotNull<object>(null, "p"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NotNullOrWhiteSpace_Blank_Throws(string value)
    {
        Assert.Throws<ArgumentException>(() => Guard.NotNullOrWhiteSpace(value, "p"));
    }

    [Fact]
    public void NotNullOrWhiteSpace_Null_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => Guard.NotNullOrWhiteSpace(null, "p"));
    }

    [Fact]
    public void NotDefault_EmptyGuid_Throws()
    {
        Assert.Throws<ArgumentException>(() => Guard.NotDefault(Guid.Empty, "p"));
    }

    [Fact]
    public void NotDefault_RealGuid_ReturnsValue()
    {
        var guid = Guid.NewGuid();

        Assert.Equal(guid, Guard.NotDefault(guid, "p"));
    }
}

public sealed class ErrorTests
{
    [Fact]
    public void Equals_SameCodeMessageCategory_AreEqual()
    {
        var left = new Error(ErrorCodes.NotFound, "m", ErrorCategory.NotFound);
        var right = new Error(ErrorCodes.NotFound, "m", ErrorCategory.NotFound);

        Assert.Equal(left, right);
    }

    [Fact]
    public void WithCorrelationId_Always_PreservesEverythingElse()
    {
        var error = new Error(ErrorCodes.Duplicate, "m", ErrorCategory.Conflict);

        Error enriched = error.WithCorrelationId("abc");

        Assert.Equal(error.Code, enriched.Code);
        Assert.Equal("abc", enriched.CorrelationId);
    }

    [Fact]
    public void ToString_Always_ContainsCodeAndMessage()
    {
        var error = new Error(ErrorCodes.ValidationFailed, "field: required", ErrorCategory.Validation);

        Assert.Equal("EDPF-VAL-1001: field: required", error.ToString());
    }
}
