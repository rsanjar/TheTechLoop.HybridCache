using FluentAssertions;
using System.Diagnostics.Metrics;
using TheTechLoop.Cache.Metrics;

namespace TheTechLoop.Cache.Tests.Metrics;

public class CacheEffectivenessMetricsTests
{
    [Fact]
    public void RecordEntityHit_UpdatesStats()
    {
        var meterFactory = new TestMeterFactory();
        var sut = new CacheEffectivenessMetrics(meterFactory);

        sut.RecordEntityHit("User", 2.5, 1024);
        sut.RecordEntityHit("User", 3.0, 2048);

        var stats = sut.GetEntityStats("User");

        stats.EntityType.Should().Be("User");
        stats.Hits.Should().Be(2);
        stats.Misses.Should().Be(0);
        stats.HitRate.Should().Be(1.0);
    }

    [Fact]
    public void RecordEntityMiss_UpdatesStats()
    {
        var meterFactory = new TestMeterFactory();
        var sut = new CacheEffectivenessMetrics(meterFactory);

        sut.RecordEntityMiss("Dealership", 10.0);

        var stats = sut.GetEntityStats("Dealership");

        stats.Hits.Should().Be(0);
        stats.Misses.Should().Be(1);
        stats.HitRate.Should().Be(0.0);
    }

    [Fact]
    public void GetEntityStats_CalculatesHitRate()
    {
        var meterFactory = new TestMeterFactory();
        var sut = new CacheEffectivenessMetrics(meterFactory);

        sut.RecordEntityHit("Company", 1.0);
        sut.RecordEntityHit("Company", 1.5);
        sut.RecordEntityHit("Company", 2.0);
        sut.RecordEntityMiss("Company", 5.0);

        var stats = sut.GetEntityStats("Company");

        stats.Hits.Should().Be(3);
        stats.Misses.Should().Be(1);
        stats.TotalRequests.Should().Be(4);
        stats.HitRate.Should().Be(0.75);
    }

    [Fact]
    public void GetAllEntityStats_ReturnsAllTrackedEntities()
    {
        var meterFactory = new TestMeterFactory();
        var sut = new CacheEffectivenessMetrics(meterFactory);

        sut.RecordEntityHit("User", 1.0);
        sut.RecordEntityHit("Dealership", 2.0);
        sut.RecordEntityMiss("Company", 3.0);

        var allStats = sut.GetAllEntityStats();

        allStats.Should().HaveCount(3);
        allStats.Should().Contain(s => s.EntityType == "User");
        allStats.Should().Contain(s => s.EntityType == "Dealership");
        allStats.Should().Contain(s => s.EntityType == "Company");
    }

    [Fact]
    public void ExtractEntityType_ParsesScopedKey()
    {
        var key = "company-svc:v1:Dealership:42";

        var entityType = key.ExtractEntityType();

        entityType.Should().Be("Dealership");
    }

    [Fact]
    public void ExtractEntityType_HandlesSimpleKey()
    {
        var key = "User:123";

        var entityType = key.ExtractEntityType();

        entityType.Should().Be("User");
    }
}

// Helper for tests
internal class TestMeterFactory : IMeterFactory
{
    public Meter Create(MeterOptions options) => new(options);
    public void Dispose() { }
}
