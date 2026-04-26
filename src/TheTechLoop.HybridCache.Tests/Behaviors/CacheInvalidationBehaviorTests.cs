using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TheTechLoop.HybridCache.Abstractions;
using TheTechLoop.HybridCache.MediatR.Abstractions;
using TheTechLoop.HybridCache.MediatR.Behaviors;
using TheTechLoop.HybridCache.Keys;

namespace TheTechLoop.HybridCache.Tests.Behaviors;

public class CacheInvalidationBehaviorTests
{
    private readonly Mock<ICacheService> _cacheMock;
    private readonly Mock<ICacheInvalidationPublisher> _publisherMock;
    private readonly Mock<ICacheTagInvalidationService> _tagInvalidationMock;
    private readonly Mock<ICacheTagInvalidationPublisher> _tagPublisherMock;
    private readonly CacheKeyBuilder _keyBuilder;

    public CacheInvalidationBehaviorTests()
    {
        _cacheMock = new Mock<ICacheService>();
        _publisherMock = new Mock<ICacheInvalidationPublisher>();
        _tagInvalidationMock = new Mock<ICacheTagInvalidationService>();
        _tagPublisherMock = new Mock<ICacheTagInvalidationPublisher>();
        _keyBuilder = new CacheKeyBuilder("test-svc", "v1");
    }

    [Fact]
    public async Task Handle_NonInvalidatableRequest_JustCallsHandler()
    {
        var sut = CreateBehavior<TestNonInvalidatableCommand, bool>();
        var handlerCalled = false;

        var result = await sut.Handle(
            new TestNonInvalidatableCommand(),
            ct => { handlerCalled = true; return Task.FromResult(true); },
            CancellationToken.None);

        result.Should().BeTrue();
        handlerCalled.Should().BeTrue();
        _cacheMock.Verify(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _publisherMock.Verify(p => p.PublishAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_InvalidatableCommand_ExecutesHandlerFirst()
    {
        var sut = CreateBehavior<TestInvalidatableCommand, bool>();
        var order = new List<string>();

        _cacheMock
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("cache-remove"))
            .Returns(Task.CompletedTask);

        await sut.Handle(
            new TestInvalidatableCommand(42),
            ct => { order.Add("handler"); return Task.FromResult(true); },
            CancellationToken.None);

        order.First().Should().Be("handler");
    }

    [Fact]
    public async Task Handle_InvalidatableCommand_RemovesExactKeys()
    {
        var sut = CreateBehavior<TestInvalidatableCommand, bool>();
        var removedKeys = new List<string>();

        _cacheMock
            .Setup(c => c.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((key, _) => removedKeys.Add(key))
            .Returns(Task.CompletedTask);

        await sut.Handle(
            new TestInvalidatableCommand(42),
            ct => Task.FromResult(true),
            CancellationToken.None);

        removedKeys.Should().Contain("test-svc:v1:Entity:42");
    }

    [Fact]
    public async Task Handle_InvalidatableCommand_RemovesPrefixPatterns()
    {
        var sut = CreateBehavior<TestInvalidatableCommand, bool>();
        var removedPrefixes = new List<string>();

        _cacheMock
            .Setup(c => c.RemoveByPrefixAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((prefix, _) => removedPrefixes.Add(prefix))
            .Returns(Task.CompletedTask);

        await sut.Handle(
            new TestInvalidatableCommand(42),
            ct => Task.FromResult(true),
            CancellationToken.None);

        removedPrefixes.Should().Contain("test-svc:v1:Entity:Search");
        removedPrefixes.Should().Contain("test-svc:v1:Entity:List");
    }

    [Fact]
    public async Task Handle_InvalidatableCommand_PublishesCrossServiceInvalidation()
    {
        var sut = CreateBehavior<TestInvalidatableCommand, bool>();

        await sut.Handle(
            new TestInvalidatableCommand(42),
            ct => Task.FromResult(true),
            CancellationToken.None);

        _publisherMock.Verify(
            p => p.PublishAsync("test-svc:v1:Entity:42", It.IsAny<CancellationToken>()),
            Times.Once);

        _publisherMock.Verify(
            p => p.PublishPrefixAsync("test-svc:v1:Entity:Search", It.IsAny<CancellationToken>()),
            Times.Once);

        _publisherMock.Verify(
            p => p.PublishPrefixAsync("test-svc:v1:Entity:List", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_WithoutPublisher_StillRemovesCacheLocally()
    {
        // Create behavior WITHOUT publisher (null)
        var sut = new CacheInvalidationBehavior<TestInvalidatableCommand, bool>(
            _cacheMock.Object,
            _keyBuilder,
            NullLogger<CacheInvalidationBehavior<TestInvalidatableCommand, bool>>.Instance,
            publisher: null,
            tagInvalidationService: null,
            tagPublisher: null);

        await sut.Handle(
            new TestInvalidatableCommand(42),
            ct => Task.FromResult(true),
            CancellationToken.None);

        // Cache removal should still happen
        _cacheMock.Verify(c => c.RemoveAsync(
            "test-svc:v1:Entity:42", It.IsAny<CancellationToken>()), Times.Once);

        // No publisher calls
        _publisherMock.Verify(
            p => p.PublishAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Handle_InvalidatableCommand_ReturnsHandlerResponse()
    {
        var sut = CreateBehavior<TestInvalidatableCommand, bool>();

        var result = await sut.Handle(
            new TestInvalidatableCommand(42),
            ct => Task.FromResult(true),
            CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_TagInvalidatableCommand_RemovesScopedTags()
    {
        var sut = CreateBehavior<TestTagInvalidatableCommand, bool>();

        await sut.Handle(
            new TestTagInvalidatableCommand(42),
            ct => Task.FromResult(true),
            CancellationToken.None);

        _tagInvalidationMock.Verify(
            s => s.RemoveByTagAsync("test-svc:v1:Entity", It.IsAny<CancellationToken>()),
            Times.Once);

        _tagInvalidationMock.Verify(
            s => s.RemoveByTagAsync("test-svc:v1:Entity:42", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_TagInvalidatableCommand_PublishesScopedTags()
    {
        var sut = CreateBehavior<TestTagInvalidatableCommand, bool>();

        await sut.Handle(
            new TestTagInvalidatableCommand(42),
            ct => Task.FromResult(true),
            CancellationToken.None);

        _tagPublisherMock.Verify(
            p => p.PublishTagAsync("test-svc:v1:Entity", It.IsAny<CancellationToken>()),
            Times.Once);

        _tagPublisherMock.Verify(
            p => p.PublishTagAsync("test-svc:v1:Entity:42", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Handle_TagInvalidatableCommand_WithoutTagServices_DoesNotThrow()
    {
        var sut = new CacheInvalidationBehavior<TestTagInvalidatableCommand, bool>(
            _cacheMock.Object,
            _keyBuilder,
            NullLogger<CacheInvalidationBehavior<TestTagInvalidatableCommand, bool>>.Instance,
            _publisherMock.Object,
            tagInvalidationService: null,
            tagPublisher: null);

        var act = () => sut.Handle(
            new TestTagInvalidatableCommand(42),
            ct => Task.FromResult(true),
            CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    #region Helpers

    private CacheInvalidationBehavior<TRequest, TResponse> CreateBehavior<TRequest, TResponse>()
        where TRequest : IRequest<TResponse>
    {
        return new CacheInvalidationBehavior<TRequest, TResponse>(
            _cacheMock.Object,
            _keyBuilder,
            NullLogger<CacheInvalidationBehavior<TRequest, TResponse>>.Instance,
            _publisherMock.Object,
            _tagInvalidationMock.Object,
            _tagPublisherMock.Object);
    }

    #endregion

    #region Test Request Types

    public record TestNonInvalidatableCommand : IRequest<bool>;

    public record TestInvalidatableCommand(int Id) : IRequest<bool>, ICacheInvalidatable
    {
        public IReadOnlyList<string> CacheKeysToInvalidate => [$"Entity:{Id}"];
        public IReadOnlyList<string> CachePrefixesToInvalidate => ["Entity:Search", "Entity:List"];
    }

    public record TestTagInvalidatableCommand(int Id) : IRequest<bool>, ICacheTagInvalidatable
    {
        public IReadOnlyList<string> CacheTagsToInvalidate => ["Entity", $"Entity:{Id}"];
    }

    #endregion
}
