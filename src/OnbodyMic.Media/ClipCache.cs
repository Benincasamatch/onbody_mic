namespace OnbodyMic.Media;

/// <summary>
/// Bounded LRU cache of pre-decoded engine-format clips (docs/01 §5.3:
/// frequently used short clips preload under a total capacity cap — never the
/// whole library in memory). Thread-safe; safe to call from the session
/// command thread and the prefill worker.
/// </summary>
public sealed class ClipCache : IClipSampleStore
{
    private readonly object _gate = new();
    private readonly int _capacityFrames;
    private readonly LinkedList<CacheEntry> _lru = new();
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _byId = [];
    private int _totalFrames;
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _declinedOversize;

    public ClipCache(int capacityFrames)
    {
        if (capacityFrames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        }

        _capacityFrames = capacityFrames;
    }

    /// <summary>Default budget: 60 s of engine audio (~10.6 MB float32 mono at 44.1 kHz).</summary>
    public static int DefaultCapacityFrames { get; } = EngineFormat.TimeToFrames(TimeSpan.FromSeconds(60));

    public bool TryGet(string clipId, out float[] samples)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(clipId, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                _hits++;
                samples = node.Value.Samples;
                return true;
            }

            _misses++;
            samples = [];
            return false;
        }
    }

    public void Store(string clipId, float[] samples) => StoreCore(clipId, samples, evict: true);

    public void Remove(string clipId)
    {
        lock (_gate)
        {
            if (_byId.Remove(clipId, out var node))
            {
                _lru.Remove(node);
                _totalFrames -= node.Value.Samples.Length;
            }
        }
    }

    public ClipCacheStats Stats()
    {
        lock (_gate)
        {
            return new ClipCacheStats(_lru.Count, _totalFrames, _capacityFrames, _hits, _misses, _evictions, _declinedOversize);
        }
    }

    /// <summary>
    /// Decodes and caches library candidates in priority order (favorites
    /// first, then newest). Preloading never evicts already-cached clips, so
    /// the highest-priority set wins when the library exceeds the budget.
    /// </summary>
    public int Prefill(ClipLibrary library, IClipDecoder decoder, Action<string>? progress = null)
    {
        var cached = 0;
        foreach (var candidate in library.PrefillCandidates())
        {
            var path = library.AssetAbsolutePath(candidate);
            if (!File.Exists(path))
            {
                continue;
            }

            lock (_gate)
            {
                if (_byId.ContainsKey(candidate.ClipId))
                {
                    continue;
                }
            }

            try
            {
                var decoded = decoder.Decode(path);
                lock (_gate)
                {
                    if (_byId.ContainsKey(candidate.ClipId))
                    {
                        continue;
                    }
                }

                if (!StoreCore(candidate.ClipId, decoded.Samples, evict: false))
                {
                    break;
                }

                cached++;
                progress?.Invoke(candidate.DisplayName);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                // a stale/undecodable asset must not abort prefill of the rest
            }
        }

        return cached;
    }

    /// <summary>Insert with MRU placement. Returns false when the clip cannot fit without eviction.</summary>
    private bool StoreCore(string clipId, float[] samples, bool evict)
    {
        lock (_gate)
        {
            if (_byId.TryGetValue(clipId, out var existing))
            {
                _totalFrames -= existing.Value.Samples.Length;
                _lru.Remove(existing);
                _byId.Remove(clipId);
            }

            if (samples.Length > _capacityFrames)
            {
                _declinedOversize++;
                return false;
            }

            while (_totalFrames + samples.Length > _capacityFrames)
            {
                if (!evict || _lru.Count == 0)
                {
                    _declinedOversize++;
                    return false;
                }

                var victim = _lru.Last!;
                _lru.RemoveLast();
                _byId.Remove(victim.Value.ClipId);
                _totalFrames -= victim.Value.Samples.Length;
                _evictions++;
            }

            var node = _lru.AddFirst(new CacheEntry(clipId, samples));
            _byId[clipId] = node;
            _totalFrames += samples.Length;
            return true;
        }
    }

    private readonly record struct CacheEntry(string ClipId, float[] Samples);
}

/// <summary>Desensitized cache counters for the metrics panel.</summary>
public readonly record struct ClipCacheStats(
    int Count,
    int TotalFrames,
    int CapacityFrames,
    long Hits,
    long Misses,
    long Evictions,
    long DeclinedOversize);
