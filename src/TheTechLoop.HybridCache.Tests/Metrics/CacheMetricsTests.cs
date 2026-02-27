using System.Diagnostics.Metrics;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using TheTechLoop.HybridCache.Metrics;

namespace TheTechLoop.HybridCache.Tests.Metrics;

public class CacheMetricsTests : IDisposable
{
    private readonly IMeterFactory _meterFactory;
    private readonly CacheMetrics _sut;
    private readonly MetricCollector<long> _hitsCollector;
    private readonly MetricCollector<long> _missesCollector;
    private readonly MetricCollector<long> _errorsCollector;
    private readonly MetricCollector<long> _evictionsCollector;
    private readonly MetricCollector<long> _circuitBreakerCollector;
    private readonly MetricCollector<long> _circuitBreakerTransitionsCollector;
    private readonly MetricCollector<double> _durationCollector;
    private readonly MetricCollector<double> _lockWaitCollector;
    private readonly MetricCollector<long> _batchSizeCollector;
    private readonly MetricCollector<double> _scanDurationCollector;
    private readonly MetricCollector<long> _scanDeletedCollector;

    public CacheMetricsTests()
    {
        _meterFactory = new TestMeterFactory();
        _sut = new CacheMetrics(_meterFactory);

        _hitsCollector = new MetricCollector<long>(_meterFactory, CacheMetrics.MeterName, "cache.hits");
        _missesCollector = new MetricCollector<long>(_meterFactory, CacheMetrics.MeterName, "cache.misses");
        _errorsCollector = new MetricCollector<long>(_meterFactory, CacheMetrics.MeterName, "cache.errors");
        _evictionsCollector = new MetricCollector<long>(_meterFactory, CacheMetrics.MeterName, "cache.evictions");
        _circuitBreakerCollector = new MetricCollector<long>(_meterFactory, CacheMetrics.MeterName, "cache.circuit_breaker.bypasses");
        _circuitBreakerTransitionsCollector = new MetricCollector<long>(_meterFactory, CacheMetrics.MeterName, "cache.circuit_breaker.transitions");
        _durationCollector = new MetricCollector<double>(_meterFactory, CacheMetrics.MeterName, "cache.duration");
        _lockWaitCollector = new MetricCollector<double>(_meterFactory, CacheMetrics.MeterName, "cache.lock.wait_duration");
        _batchSizeCollector = new MetricCollector<long>(_meterFactory, CacheMetrics.MeterName, "cache.batch.size");
        _scanDurationCollector = new MetricCollector<double>(_meterFactory, CacheMetrics.MeterName, "cache.scan.duration");
        _scanDeletedCollector = new MetricCollector<long>(_meterFactory, CacheMetrics.MeterName, "cache.scan.deleted_keys");
    }

    public void Dispose()
    {
        _meterFactory.Dispose();
    }

    [Fact]
    public void RecordHit_IncrementsCounter()
    {
        _sut.RecordHit("svc:v1:User:1", 2.5);

        _hitsCollector.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Value.Should().Be(1);
    }

    [Fact]
    public void RecordHit_RecordsDuration()
    {
        _sut.RecordHit("svc:v1:User:1", 3.7, "L1");

        _durationCollector.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Value.Should().Be(3.7);
    }

    [Fact]
    public void RecordHit_IncludesLevelTag()
    {
        _sut.RecordHit("svc:v1:User:1", 1.0, "L1");

        var measurement = _hitsCollector.GetMeasurementSnapshot().Single();
        measurement.Tags["cache.level"].Should().Be("L1");
    }

    [Fact]
    public void RecordHit_ExtractsPrefixTag()
    {
        _sut.RecordHit("my-svc:v1:Entity:42", 1.0);

        var measurement = _hitsCollector.GetMeasurementSnapshot().Single();
        measurement.Tags["cache.key_prefix"].Should().Be("my-svc");
    }

    [Fact]
    public void RecordMiss_IncrementsCounter()
    {
        _sut.RecordMiss("svc:v1:User:1", 5.0);

        _missesCollector.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Value.Should().Be(1);
    }

    [Fact]
    public void RecordMiss_RecordsDuration()
    {
        _sut.RecordMiss("svc:v1:User:1", 8.2);

        _durationCollector.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Value.Should().Be(8.2);
    }

    [Fact]
    public void RecordMiss_IncludesLevelTag()
    {
        _sut.RecordMiss("svc:v1:User:1", 1.0);

        var measurement = _missesCollector.GetMeasurementSnapshot().Single();
        measurement.Tags["cache.level"].Should().Be("L2");
    }

    [Fact]
    public void RecordMiss_DurationIncludesLevelTag()
    {
        _sut.RecordMiss("svc:v1:User:1", 1.0, "L1");

        var measurement = _durationCollector.GetMeasurementSnapshot().Single();
        measurement.Tags["cache.level"].Should().Be("L1");
    }

    [Fact]
    public void RecordError_IncrementsCounter()
    {
        _sut.RecordError("svc:v1:User:1");

        _errorsCollector.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Value.Should().Be(1);
    }

    [Fact]
    public void RecordError_IncludesLevelTag()
    {
        _sut.RecordError("svc:v1:User:1", "L1");

        var measurement = _errorsCollector.GetMeasurementSnapshot().Single();
        measurement.Tags["cache.level"].Should().Be("L1");
    }

    [Fact]
    public void RecordEviction_IncrementsCounter()
    {
        _sut.RecordEviction("svc:v1:User:1");

        _evictionsCollector.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Value.Should().Be(1);
    }

    [Fact]
    public void RecordEviction_IncludesLevelTag()
    {
        _sut.RecordEviction("svc:v1:User:1");

        var measurement = _evictionsCollector.GetMeasurementSnapshot().Single();
        measurement.Tags["cache.level"].Should().Be("L2");
    }

    [Fact]
    public void RecordCircuitBreakerBypass_IncrementsCounter()
    {
        _sut.RecordCircuitBreakerBypass();
        _sut.RecordCircuitBreakerBypass();

        _circuitBreakerCollector.GetMeasurementSnapshot().Should().HaveCount(2);
    }

    [Fact]
    public void RecordCircuitBreakerTransition_IncrementsWithStateTag()
    {
        _sut.RecordCircuitBreakerTransition("opened");

        var measurement = _circuitBreakerTransitionsCollector.GetMeasurementSnapshot().Single();
        measurement.Value.Should().Be(1);
        measurement.Tags["cache.circuit_breaker.state"].Should().Be("opened");
    }

    [Fact]
    public void RecordCircuitBreakerTransition_TracksMultipleStates()
    {
        _sut.RecordCircuitBreakerTransition("opened");
        _sut.RecordCircuitBreakerTransition("half_open");
        _sut.RecordCircuitBreakerTransition("closed");

        var snapshots = _circuitBreakerTransitionsCollector.GetMeasurementSnapshot();
        snapshots.Should().HaveCount(3);
        snapshots.Select(s => (string)s.Tags["cache.circuit_breaker.state"]!)
            .Should().BeEquivalentTo(["opened", "half_open", "closed"]);
    }

    [Fact]
    public void RecordLockWait_RecordsDuration()
    {
        _sut.RecordLockWait(12.5, true);

        var measurement = _lockWaitCollector.GetMeasurementSnapshot().Single();
        measurement.Value.Should().Be(12.5);
        measurement.Tags["cache.lock.acquired"].Should().Be(true);
    }

    [Fact]
    public void RecordLockWait_NotAcquired_TagsFalse()
    {
        _sut.RecordLockWait(1.0, false);

        var measurement = _lockWaitCollector.GetMeasurementSnapshot().Single();
        measurement.Tags["cache.lock.acquired"].Should().Be(false);
    }

    [Fact]
    public void RecordBatchSize_RecordsCount()
    {
        _sut.RecordBatchSize(500, "get");

        var measurement = _batchSizeCollector.GetMeasurementSnapshot().Single();
        measurement.Value.Should().Be(500);
        measurement.Tags["cache.operation"].Should().Be("get");
    }

    [Fact]
    public void RecordBatchSize_SetOperation()
    {
        _sut.RecordBatchSize(100, "set");

        var measurement = _batchSizeCollector.GetMeasurementSnapshot().Single();
        measurement.Tags["cache.operation"].Should().Be("set");
    }

    [Fact]
    public void RecordScanDeletion_RecordsBoth()
    {
        _sut.RecordScanDeletion(45.2, 150);

        _scanDurationCollector.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Value.Should().Be(45.2);
        _scanDeletedCollector.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Value.Should().Be(150);
    }

    [Fact]
    public void MultipleOperations_AccumulateCorrectly()
    {
        _sut.RecordHit("k1", 1.0);
        _sut.RecordHit("k2", 2.0);
        _sut.RecordMiss("k3", 3.0);
        _sut.RecordError("k4");

        _hitsCollector.GetMeasurementSnapshot().Should().HaveCount(2);
        _missesCollector.GetMeasurementSnapshot().Should().HaveCount(1);
        _errorsCollector.GetMeasurementSnapshot().Should().HaveCount(1);
    }

    private class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters) meter.Dispose();
        }
    }
}
