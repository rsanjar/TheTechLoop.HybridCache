using FluentAssertions;
using TheTechLoop.Cache.Keys;

namespace TheTechLoop.Cache.Tests.Keys;

public class CacheKeyBuilderTests
{
    [Fact]
    public void Key_WithServiceNameAndVersion_BuildsScopedKey()
    {
        var builder = new CacheKeyBuilder("company-svc", "v1");

        var key = builder.Key("Dealership", "42");

        key.Should().Be("company-svc:v1:Dealership:42");
    }

    [Fact]
    public void Key_WithEmptyServiceName_UsesVersionOnly()
    {
        var builder = new CacheKeyBuilder("", "v2");

        var key = builder.Key("User", "123");

        key.Should().Be("v2:User:123");
    }

    [Fact]
    public void Key_FiltersEmptyParts()
    {
        var builder = new CacheKeyBuilder("svc", "v1");

        var key = builder.Key("Entity", "", "Id", "");

        key.Should().Be("svc:v1:Entity:Id");
    }

    [Fact]
    public void Key_WithSinglePart_BuildsCorrectly()
    {
        var builder = new CacheKeyBuilder("svc", "v1");

        var key = builder.Key("Users");

        key.Should().Be("svc:v1:Users");
    }

    [Fact]
    public void Pattern_AppendsWildcard()
    {
        var builder = new CacheKeyBuilder("company-svc", "v1");

        var pattern = builder.Pattern("Dealership", "Search");

        pattern.Should().Be("company-svc:v1:Dealership:Search*");
    }

    [Fact]
    public void For_StaticHelper_BuildsKeyWithoutPrefix()
    {
        var key = CacheKeyBuilder.For("shared", "config", "setting");

        key.Should().Be("shared:config:setting");
    }

    [Fact]
    public void For_FiltersEmptyParts()
    {
        var key = CacheKeyBuilder.For("a", "", "b");

        key.Should().Be("a:b");
    }

    [Fact]
    public void ForEntity_WithIntId_BuildsEntityKey()
    {
        var key = CacheKeyBuilder.ForEntity("User", 42);

        key.Should().Be("User:42");
    }

    [Fact]
    public void ForEntity_WithStringKey_SanitizesAndBuildsKey()
    {
        var key = CacheKeyBuilder.ForEntity("User", "john doe");

        key.Should().Be("User:john_doe");
    }

    [Theory]
    [InlineData("hello world", "hello_world")]
    [InlineData("key:with:colons", "key_with_colons")]
    [InlineData("path/to/file", "path_to_file")]
    [InlineData("back\\slash", "back_slash")]
    [InlineData("  UPPER  ", "__upper__")]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    public void Sanitize_ReplacesProblematicCharacters(string input, string expected)
    {
        var result = CacheKeyBuilder.Sanitize(input);

        result.Should().Be(expected);
    }

    [Fact]
    public void DefaultVersion_IsV1()
    {
        var builder = new CacheKeyBuilder("svc");

        var key = builder.Key("Test");

        key.Should().Be("svc:v1:Test");
    }
}
