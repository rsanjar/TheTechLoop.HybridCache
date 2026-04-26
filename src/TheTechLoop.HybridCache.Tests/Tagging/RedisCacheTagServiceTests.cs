using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using TheTechLoop.HybridCache.Configuration;
using TheTechLoop.HybridCache.Tagging;

namespace TheTechLoop.HybridCache.Tests.Tagging;

public class RedisCacheTagServiceTests
{
    private readonly Mock<IConnectionMultiplexer> _redisMock;
    private readonly Mock<IDatabase> _dbMock;
    private readonly Mock<IBatch> _batchMock;
    private readonly CacheConfig _config;
    private readonly RedisCacheTagService _sut;

    public RedisCacheTagServiceTests()
    {
        _redisMock = new Mock<IConnectionMultiplexer>();
        _dbMock = new Mock<IDatabase>();
        _batchMock = new Mock<IBatch>();
        _config = new CacheConfig
        {
            EnableTagging = true,
            TagIndexTtlMinutes = 1440, // 24 hours
            InstanceName = "app:"
        };

        _redisMock.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_dbMock.Object);
        _dbMock.Setup(d => d.CreateBatch(It.IsAny<object>()))
            .Returns(_batchMock.Object);

        // Batch operations return completed tasks by default
        _batchMock.Setup(b => b.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        _batchMock.Setup(b => b.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(1L));
        _batchMock.Setup(b => b.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        _batchMock.Setup(b => b.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        _batchMock.Setup(b => b.SetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        _batchMock.Setup(b => b.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));

        _sut = new RedisCacheTagService(
            _redisMock.Object,
            NullLogger<RedisCacheTagService>.Instance,
            Options.Create(_config));
    }

    #region AddTagsAsync

    [Fact]
    public async Task AddTagsAsync_EmptyTags_DoesNotCallRedis()
    {
        await _sut.AddTagsAsync("key", []);

        _dbMock.Verify(d => d.CreateBatch(It.IsAny<object>()), Times.Never);
    }

    [Fact]
    public async Task AddTagsAsync_AddsForwardAndReverseIndices()
    {
        await _sut.AddTagsAsync("user:42", ["User", "Session"]);

        // Forward: tag:User → user:42, tag:Session → user:42
        _batchMock.Verify(b => b.SetAddAsync(
            (RedisKey)"tag:User", (RedisValue)"user:42", It.IsAny<CommandFlags>()), Times.Once);
        _batchMock.Verify(b => b.SetAddAsync(
            (RedisKey)"tag:Session", (RedisValue)"user:42", It.IsAny<CommandFlags>()), Times.Once);

        // Reverse: key:tags:user:42 → {User, Session}
        _batchMock.Verify(b => b.SetAddAsync(
            (RedisKey)"key:tags:user:42",
            It.Is<RedisValue[]>(v => v.Length == 2),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task AddTagsAsync_SetsTtlOnIndexKeys()
    {
        var ttlCalls = new List<(RedisKey Key, TimeSpan? Ttl)>();

        _batchMock.Setup(b => b.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, TimeSpan?, CommandFlags>((k, t, _) => ttlCalls.Add((k, t)))
            .Returns(Task.FromResult(true));
        _batchMock.Setup(b => b.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, TimeSpan?, ExpireWhen, CommandFlags>((k, t, _, _) => ttlCalls.Add((k, t)))
            .Returns(Task.FromResult(true));

        await _sut.AddTagsAsync("key1", ["TagA"]);

        // Should have set TTL on both tag:TagA and key:tags:key1
        var expectedTtl = TimeSpan.FromMinutes(1440);
        ttlCalls.Should().Contain(c => c.Key == (RedisKey)"tag:TagA" && c.Ttl == expectedTtl);
        ttlCalls.Should().Contain(c => c.Key == (RedisKey)"key:tags:key1" && c.Ttl == expectedTtl);
    }

    [Fact]
    public async Task AddTagsAsync_TtlDisabled_DoesNotSetExpiry()
    {
        var config = new CacheConfig { TagIndexTtlMinutes = 0 };
        var sut = new RedisCacheTagService(
            _redisMock.Object,
            NullLogger<RedisCacheTagService>.Instance,
            Options.Create(config));

        var ttlCalls = new List<RedisKey>();

        _batchMock.Setup(b => b.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, TimeSpan?, CommandFlags>((k, _, _) => ttlCalls.Add(k))
            .Returns(Task.FromResult(true));
        _batchMock.Setup(b => b.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .Callback<RedisKey, TimeSpan?, ExpireWhen, CommandFlags>((k, _, _, _) => ttlCalls.Add(k))
            .Returns(Task.FromResult(true));

        await sut.AddTagsAsync("key1", ["TagA"]);

        ttlCalls.Should().BeEmpty("no TTL should be set when TagIndexTtlMinutes is 0");
    }

    [Fact]
    public async Task AddTagsAsync_RedisError_DoesNotThrow()
    {
        _dbMock.Setup(d => d.CreateBatch(It.IsAny<object>()))
            .Throws(new RedisException("Redis down"));

        var act = () => _sut.AddTagsAsync("key", ["tag"]);

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region RemoveAsync

    [Fact]
    public async Task RemoveAsync_WithTags_RemovesFromAllTagSets()
    {
        _dbMock.Setup(d => d.SetMembersAsync("key:tags:user:42", It.IsAny<CommandFlags>()))
            .ReturnsAsync([new RedisValue("User"), new RedisValue("Session")]);

        await _sut.RemoveAsync("user:42");

        // Should remove user:42 from both tag forward indices
        _batchMock.Verify(b => b.SetRemoveAsync(
            (RedisKey)"tag:User", (RedisValue)"user:42", It.IsAny<CommandFlags>()), Times.Once);
        _batchMock.Verify(b => b.SetRemoveAsync(
            (RedisKey)"tag:Session", (RedisValue)"user:42", It.IsAny<CommandFlags>()), Times.Once);

        // Should delete the reverse index
        _batchMock.Verify(b => b.KeyDeleteAsync(
            (RedisKey)"key:tags:user:42", It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_NoTags_StillDeletesReverseIndex()
    {
        _dbMock.Setup(d => d.SetMembersAsync("key:tags:orphan", It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);

        await _sut.RemoveAsync("orphan");

        // Should still clean up the reverse index key
        _dbMock.Verify(d => d.KeyDeleteAsync("key:tags:orphan", It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAsync_RedisError_DoesNotThrow()
    {
        _dbMock.Setup(d => d.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisException("Redis down"));

        var act = () => _sut.RemoveAsync("key");

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region GetKeysByTagAsync

    [Fact]
    public async Task GetKeysByTagAsync_ReturnsAllMembers()
    {
        _dbMock.Setup(d => d.SetMembersAsync("tag:User", It.IsAny<CommandFlags>()))
            .ReturnsAsync([new RedisValue("user:1"), new RedisValue("user:2")]);

        var result = await _sut.GetKeysByTagAsync("User");

        result.Should().BeEquivalentTo(["user:1", "user:2"]);
    }

    [Fact]
    public async Task GetKeysByTagAsync_EmptySet_ReturnsEmptyList()
    {
        _dbMock.Setup(d => d.SetMembersAsync("tag:Empty", It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);

        var result = await _sut.GetKeysByTagAsync("Empty");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetKeysByTagAsync_RedisError_ReturnsEmpty()
    {
        _dbMock.Setup(d => d.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisException("Redis down"));

        var result = await _sut.GetKeysByTagAsync("tag");

        result.Should().BeEmpty();
    }

    #endregion

    #region RemoveByTagAsync

    [Fact]
    public async Task RemoveByTagAsync_CallsLuaScript()
    {
        _dbMock.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)3));

        await _sut.RemoveByTagAsync("User");

        _dbMock.Verify(d => d.ScriptEvaluateAsync(
            It.Is<string>(s => s.Contains("SMEMBERS") && s.Contains("UNLINK")),
            It.Is<RedisKey[]>(k => k.Length == 1 && k[0] == (RedisKey)"tag:User"),
            It.Is<RedisValue[]>(v => v.Length == 2
                && v[0] == (RedisValue)"key:tags:"
                && v[1] == (RedisValue)"app:"),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task RemoveByTagAsync_RedisError_DoesNotThrow()
    {
        _dbMock.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisException("Redis down"));

        var act = () => _sut.RemoveByTagAsync("tag");

        await act.Should().NotThrowAsync();
    }

    #endregion
}
