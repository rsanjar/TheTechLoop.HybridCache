using FluentAssertions;
using TheTechLoop.Cache.Abstractions;

namespace TheTechLoop.Cache.Tests.Abstractions;

public class CacheEntryOptionsTests
{
    [Fact]
    public void Absolute_CreatesAbsoluteExpirationOptions()
    {
        var options = CacheEntryOptions.Absolute(TimeSpan.FromMinutes(30), "User", "Session");

        options.Expiration.Should().Be(TimeSpan.FromMinutes(30));
        options.ExpirationType.Should().Be(CacheExpirationType.Absolute);
        options.Tags.Should().Contain("User");
        options.Tags.Should().Contain("Session");
    }

    [Fact]
    public void Sliding_CreatesSlidingExpirationOptions()
    {
        var options = CacheEntryOptions.Sliding(TimeSpan.FromMinutes(5), "UserSession");

        options.Expiration.Should().Be(TimeSpan.FromMinutes(5));
        options.ExpirationType.Should().Be(CacheExpirationType.Sliding);
        options.Tags.Should().Contain("UserSession");
    }

    [Fact]
    public void DefaultExpirationType_IsAbsolute()
    {
        var options = new CacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(10)
        };

        options.ExpirationType.Should().Be(CacheExpirationType.Absolute);
    }
}
