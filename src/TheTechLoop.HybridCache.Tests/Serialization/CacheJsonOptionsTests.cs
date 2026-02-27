using System.Text.Json;
using FluentAssertions;
using TheTechLoop.HybridCache.Serialization;

namespace TheTechLoop.HybridCache.Tests.Serialization;

public class CacheJsonOptionsTests
{
    [Fact]
    public void Default_UsesCamelCase()
    {
        var obj = new TestPayload { UserName = "Alice", Age = 30 };
        var json = JsonSerializer.Serialize(obj, CacheJsonOptions.Default);

        json.Should().Contain("\"userName\"");
        json.Should().Contain("\"age\"");
    }

    [Fact]
    public void Default_IgnoresNullWhenWriting()
    {
        var obj = new TestPayload { UserName = null!, Age = 25 };
        var json = JsonSerializer.Serialize(obj, CacheJsonOptions.Default);

        json.Should().NotContain("userName");
        json.Should().Contain("\"age\":25");
    }

    [Fact]
    public void Default_IsCaseInsensitiveOnRead()
    {
        var json = """{"UserName":"Bob","Age":40}""";

        var obj = JsonSerializer.Deserialize<TestPayload>(json, CacheJsonOptions.Default);

        obj.Should().NotBeNull();
        obj!.UserName.Should().Be("Bob");
        obj.Age.Should().Be(40);
    }

    [Fact]
    public void Default_AllowsTrailingCommas()
    {
        var json = """{"userName":"Eve","age":22,}""";

        var obj = JsonSerializer.Deserialize<TestPayload>(json, CacheJsonOptions.Default);

        obj.Should().NotBeNull();
        obj!.UserName.Should().Be("Eve");
    }

    [Fact]
    public void Default_SerializesEnumsAsStrings()
    {
        var obj = new TestWithEnum { Status = TestStatus.Active };
        var json = JsonSerializer.Serialize(obj, CacheJsonOptions.Default);

        json.Should().Contain("\"Active\"");
    }

    [Fact]
    public void Default_DeserializesEnumsFromStrings()
    {
        var json = """{"status":"Inactive"}""";

        var obj = JsonSerializer.Deserialize<TestWithEnum>(json, CacheJsonOptions.Default);

        obj.Should().NotBeNull();
        obj!.Status.Should().Be(TestStatus.Inactive);
    }

    [Fact]
    public void TryDeserialize_ValidJson_ReturnsObject()
    {
        var json = """{"userName":"Charlie","age":35}""";

        var result = CacheJsonOptions.TryDeserialize<TestPayload>(json);

        result.Should().NotBeNull();
        result!.UserName.Should().Be("Charlie");
        result.Age.Should().Be(35);
    }

    [Fact]
    public void TryDeserialize_InvalidJson_ReturnsDefault()
    {
        var result = CacheJsonOptions.TryDeserialize<TestPayload>("not valid json {{{");

        result.Should().BeNull();
    }

    [Fact]
    public void TryDeserialize_EmptyString_ReturnsDefault()
    {
        var result = CacheJsonOptions.TryDeserialize<TestPayload>("");

        result.Should().BeNull();
    }

    [Fact]
    public void TryDeserialize_WithCustomOptions_UsesProvided()
    {
        var json = """{"UserName":"Dave","Age":28}""";
        var customOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var result = CacheJsonOptions.TryDeserialize<TestPayload>(json, customOptions);

        result.Should().NotBeNull();
        result!.UserName.Should().Be("Dave");
    }

    [Fact]
    public void TryDeserialize_TypeMismatch_ReturnsDefault()
    {
        var json = """{"completely":"different","shape":true}""";

        var result = CacheJsonOptions.TryDeserialize<TestPayload>(json);

        // Doesn't throw, returns object with defaults
        result.Should().NotBeNull();
        result!.Age.Should().Be(0);
    }

    private class TestPayload
    {
        public string UserName { get; set; } = string.Empty;
        public int Age { get; set; }
    }

    private class TestWithEnum
    {
        public TestStatus Status { get; set; }
    }

    private enum TestStatus
    {
        Active,
        Inactive
    }
}
