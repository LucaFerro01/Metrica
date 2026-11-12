using System.Security.Claims;
using Xunit;

namespace TriMetric.ServiceDefaults.Tests;

public sealed class GetUserIdTests
{
    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void ReturnsSubClaim()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, Principal(new Claim("sub", id.ToString())).GetUserId());
    }

    [Fact]
    public void MissingSub_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Principal(new Claim("name", "x")).GetUserId());

    [Fact]
    public void NoIdentity_Throws() =>
        Assert.Throws<InvalidOperationException>(() => new ClaimsPrincipal().GetUserId());

    [Fact]
    public void EmptyGuidSub_Throws() =>
        Assert.Throws<InvalidOperationException>(() => Principal(new Claim("sub", Guid.Empty.ToString())).GetUserId());

    [Fact]
    public void MultipleSubClaims_Throws() =>
        Assert.Throws<InvalidOperationException>(() =>
            Principal(new Claim("sub", Guid.NewGuid().ToString()), new Claim("sub", Guid.NewGuid().ToString())).GetUserId());

    [Fact]
    public void MultipleIdenticalSubClaims_Throws()
    {
        var id = Guid.NewGuid().ToString();

        Assert.Throws<InvalidOperationException>(() => Principal(new Claim("sub", id), new Claim("sub", id)).GetUserId());
    }

    [Fact]
    public void SubInNonStandardFormat_Throws()
    {
        var id = Guid.NewGuid();

        Assert.Throws<InvalidOperationException>(() => Principal(new Claim("sub", id.ToString("N"))).GetUserId());
        Assert.Throws<InvalidOperationException>(() => Principal(new Claim("sub", id.ToString("B"))).GetUserId());
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    public void InvalidSub_Throws(string value) =>
        Assert.Throws<InvalidOperationException>(() => Principal(new Claim("sub", value)).GetUserId());
}
