    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (!_config.Enabled || IsCircuitOpen())
            return;

        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
            _metrics.RecordEviction(key);
            LogDebug("Cache removed for key: {Key}", key);
        }
        catch (Exception ex)
        {
            _metrics.RecordError(key);
            _circuitBreaker.RecordFailure();
            _logger.LogError(ex, "Error removing cache for key: {Key}", key);
        }
    }