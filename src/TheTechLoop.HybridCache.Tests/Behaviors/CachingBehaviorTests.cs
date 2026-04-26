using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.MediatR.Abstractions;
using TheTechLoop.HybridCache.MediatR.Behaviors;
using TheTechLoop.HybridCache.Keys;

namespace TheTechLoop.HybridCache.Tests.Behaviors;

public class CachingBehaviorTests
{
    private readonly Mock<ICacheService> _cacheMock;
    private readonly CacheKeyBuilder _keyBuilder;
    private readonly CachingBehavior<TestCacheableQuery, string> _sut;
    private readonly CachingBehavior<TestNonCacheableQuery, string> _nonCacheableSut;

    public CachingBehaviorTests()
    {
        _cacheMock = new Mock<ICacheService>();
        _keyBuilder = new CacheKeyBuilder("test-svc", "v1");

        _sut = new CachingBehavior<TestCacheableQuery, string>(
            _cacheMock.Object,
            _keyBuilder,
            NullLogger<CachingBehavior<TestCacheableQuery, string>>.Instance);

        _nonCacheableSut = new CachingBehavior<TestNonCacheableQuery, string>(
            _cacheMock.Object,
            _keyBuilder,
            NullLogger<CachingBehavior<TestNonCacheableQuery, string>>.Instance);
    }

    [Fact]
    public async Task Handle_NonCacheableRequest_SkipsCache()
    {
        var request = new TestNonCacheableQuery();
        var handlerCalled = false;

        var result = await _nonCacheableSut.Handle(
            request,
            ct => { handlerCalled = true; return Task.FromResult("handler-result"); },
            CancellationToken.None);

        result.Should().Be("handler-result");
        handlerCalled.Should().BeTrue();
        _cacheMock.Verify(c => c.GetOrCreateAsync<string>(
            It.IsAny<string>(), It.IsAny<Func<Task<string>>>(),
            It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_CacheableRequest_DelegatesToCacheService()
    {
        var request = new TestCacheableQuery(42);

        _cacheMock
            .Setup(c => c.GetOrCreateAsync(
                "test-svc:v1:Entity:42",
                It.IsAny<Func<Task<string>>>(),
                TimeSpan.FromMinutes(30),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("cached-value");

        var result = await _sut.Handle(
            request,
            ct => Task.FromResult("should-not-be-called"),
            CancellationToken.None);

        result.Should().Be("cached-value");

        _cacheMock.Verify(c => c.GetOrCreateAsync(
            "test-svc:v1:Entity:42",
            It.IsAny<Func<Task<string>>>(),
            TimeSpan.FromMinutes(30),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_CacheableRequest_UsesCorrectScopedKey()
    {
        var request = new TestCacheableQuery(99);
        string? capturedKey = null;

        _cacheMock
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<string>>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<Task<string>>, TimeSpan, CancellationToken>((key, _, _, _) => capturedKey = key)
            .ReturnsAsync("value");

        await _sut.Handle(request, ct => Task.FromResult("value"), CancellationToken.None);

        capturedKey.Should().Be("test-svc:v1:Entity:99");
    }

    [Fact]
    public async Task Handle_CacheableRequest_UsesDurationFromInterface()
    {
        var request = new TestCacheableQuery(1);
        TimeSpan? capturedDuration = null;

        _cacheMock
            .Setup(c => c.GetOrCreateAsync(
                It.IsAny<string>(),
                It.IsAny<Func<Task<string>>>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<Task<string>>, TimeSpan, CancellationToken>((_, _, dur, _) => capturedDuration = dur)
            .ReturnsAsync("value");

        await _sut.Handle(request, ct => Task.FromResult("value"), CancellationToken.None);

        capturedDuration.Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task Handle_TaggedCacheableRequest_UsesEntryOptionsWithScopedTags()
    {
        var cacheMock = new Mock<ICacheServiceWithEntryOptions>();
        var sut = new CachingBehavior<TestTaggedCacheableQuery, string>(
            cacheMock.Object,
            _keyBuilder,
            NullLogger<CachingBehavior<TestTaggedCacheableQuery, string>>.Instance);

        CacheEntryOptions? capturedOptions = null;
        cacheMock
            .Setup(c => c.GetOrCreateAsync(
                "test-svc:v1:Entity:42",
                It.IsAny<Func<Task<string>>>(),
                It.IsAny<CacheEntryOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Func<Task<string>>, CacheEntryOptions, CancellationToken>((_, _, options, _) =>
                capturedOptions = options)
            .ReturnsAsync("tagged-value");

        var result = await sut.Handle(
            new TestTaggedCacheableQuery(42),
            ct => Task.FromResult("handler-value"),
            CancellationToken.None);

        result.Should().Be("tagged-value");
        capturedOptions.Should().NotBeNull();
        capturedOptions!.Expiration.Should().Be(TimeSpan.FromMinutes(30));
        capturedOptions.ExpirationType.Should().Be(CacheExpirationType.Absolute);
        capturedOptions.Tags.Should().BeEquivalentTo(["test-svc:v1:Entity", "test-svc:v1:Entity:42"]);
    }

    [Fact]
    public async Task Handle_TaggedCacheableRequest_ThrowsWhenCacheDoesNotSupportEntryOptions()
    {
        var sut = new CachingBehavior<TestTaggedCacheableQuery, string>(
            _cacheMock.Object,
            _keyBuilder,
            NullLogger<CachingBehavior<TestTaggedCacheableQuery, string>>.Instance);

        var act = () => sut.Handle(
            new TestTaggedCacheableQuery(42),
            ct => Task.FromResult("handler-value"),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*ICacheServiceWithEntryOptions*");
    }

    #region Test Request Types

    public record TestCacheableQuery(int Id) : IRequest<string>, ICacheable
    {
        public string CacheKey => $"Entity:{Id}";
        public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);
    }

    public record TestTaggedCacheableQuery(int Id) : IRequest<string>, ITaggedCacheable
    {
        public string CacheKey => $"Entity:{Id}";
        public TimeSpan CacheDuration => TimeSpan.FromMinutes(30);
        public IReadOnlyList<string> CacheTags => ["Entity", $"Entity:{Id}"];
    }

    public record TestNonCacheableQuery : IRequest<string>;

    #endregion
}
