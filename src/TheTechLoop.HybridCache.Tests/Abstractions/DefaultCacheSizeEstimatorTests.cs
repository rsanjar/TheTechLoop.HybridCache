using FluentAssertions;
using TheTechLoop.HybridCache.Abstractions;

namespace TheTechLoop.HybridCache.Tests.Abstractions;

public class DefaultCacheSizeEstimatorTests
{
    private readonly DefaultCacheSizeEstimator _sut = new();

    [Fact]
    public void EstimateSize_Null_ReturnsOne()
    {
        _sut.EstimateSize<string?>(null).Should().Be(1);
    }

    [Fact]
    public void EstimateSize_SmallString_ReturnsMinimumOne()
    {
        _sut.EstimateSize("hello").Should().Be(1);
    }

    [Fact]
    public void EstimateSize_LargeString_ScalesByLength()
    {
        // 2000 chars × 2 bytes/char = 4000 bytes / 1024 = 3
        var large = new string('A', 2000);
        _sut.EstimateSize(large).Should().Be(3);
    }

    [Fact]
    public void EstimateSize_ByteArray_ScalesByLength()
    {
        var data = new byte[5000];
        _sut.EstimateSize(data).Should().Be(4); // 5000 / 1024 = 4
    }

    [Fact]
    public void EstimateSize_SmallByteArray_ReturnsMinimumOne()
    {
        _sut.EstimateSize(new byte[100]).Should().Be(1);
    }

    [Fact]
    public void EstimateSize_List_ScalesByCount()
    {
        var list = Enumerable.Range(0, 50).ToList();
        _sut.EstimateSize(list).Should().Be(50);
    }

    [Fact]
    public void EstimateSize_EmptyList_ReturnsMinimumOne()
    {
        _sut.EstimateSize(new List<int>()).Should().Be(1);
    }

    [Fact]
    public void EstimateSize_Dictionary_CountsAtDoubleWeight()
    {
        var dict = Enumerable.Range(0, 20).ToDictionary(i => $"key{i}", i => i);
        _sut.EstimateSize(dict).Should().Be(40); // 20 × 2
    }

    [Fact]
    public void EstimateSize_PlainObject_ReturnsOne()
    {
        _sut.EstimateSize(new { Id = 1, Name = "test" }).Should().Be(1);
    }

    [Fact]
    public void EstimateSize_Int_ReturnsOne()
    {
        _sut.EstimateSize(42).Should().Be(1);
    }
}
