using FluentAssertions;
using TheTechLoop.HybridCache.Metrics;

namespace TheTechLoop.HybridCache.Tests.Metrics;

public class CacheKeyExtensionsTests
{
    [Theory]
    [InlineData("company-svc:v1:Dealership:42", "Dealership")]
    [InlineData("svc:v1:User:123", "User")]
    [InlineData("a:b:Entity:x:y", "Entity")]
    public void ExtractEntityType_ScopedKey_ReturnsThirdSegment(string key, string expected)
    {
        key.ExtractEntityType().Should().Be(expected);
    }

    [Fact]
    public void ExtractEntityType_TwoSegments_ReturnsThirdSegment()
    {
        // With exactly 3 parts, the third (index 2) is the entity
        "User:123:Detail".ExtractEntityType().Should().Be("Detail");
    }

    [Fact]
    public void ExtractEntityType_SimpleKeyWithColon_ReturnsFirstSegment()
    {
        // Only 2 parts: falls through to parts[2] which doesn't exist when length < 3
        // Actually length >= 3 check fails for 2 parts, so it returns parts[0]
        "User:123".ExtractEntityType().Should().Be("User");
    }

    [Fact]
    public void ExtractEntityType_SingleSegment_ReturnsItself()
    {
        "Users".ExtractEntityType().Should().Be("Users");
    }

    [Fact]
    public void ExtractEntityType_EmptyString_ReturnsEmpty()
    {
        "".ExtractEntityType().Should().Be("");
    }
}
