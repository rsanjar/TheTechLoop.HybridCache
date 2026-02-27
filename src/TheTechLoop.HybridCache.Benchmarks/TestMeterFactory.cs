using System.Diagnostics.Metrics;

namespace TheTechLoop.HybridCache.Benchmarks;

/// <summary>
/// Lightweight <see cref="IMeterFactory"/> for benchmarks.
/// Avoids pulling in Microsoft.Extensions.Diagnostics.Testing.
/// </summary>
internal sealed class TestMeterFactory : IMeterFactory
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
        foreach (var meter in _meters)
            meter.Dispose();

        _meters.Clear();
    }
}
