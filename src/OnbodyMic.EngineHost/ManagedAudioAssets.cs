namespace OnbodyMic.EngineHost;

using System.Runtime.CompilerServices;
using OnbodyMic.Audio;
using OnbodyMic.Media;
using OnbodyMic.Platform.Windows;

internal sealed class OwnedClipDecoder(CancellationToken stopping) : IClipDecoder, IDisposable
{
    private sealed record DecodedAsset(string Path);
    private readonly NAudioClipDecoder _decoder = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly ConditionalWeakTable<float[], DecodedAsset> _assets = new();

    public DecodedClip Decode(string path)
    {
        _slot.Wait(stopping);
        try
        {
            stopping.ThrowIfCancellationRequested();
            var decoded = _decoder.Decode(path);
            stopping.ThrowIfCancellationRequested();
            _assets.AddOrUpdate(decoded.Samples, new DecodedAsset(Path.GetFullPath(path)));
            return decoded;
        }
        finally { _slot.Release(); }
    }

    public bool MatchesAsset(float[] samples, string path) => _assets.TryGetValue(samples, out var asset)
        && string.Equals(asset.Path, path, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _slot.Dispose();
}

/// <summary>Stable-id cache access also checks the decoded asset identity, so an old library/decode cannot populate a new library.</summary>
internal sealed class ManagedClipSampleStore(OwnedClipDecoder decoder) : IClipSampleStore
{
    private readonly object _gate = new();
    private Dictionary<string, string> _assets = new(StringComparer.Ordinal);
    private ClipCache _cache = new(ClipCache.DefaultCapacityFrames);

    public ClipCache Cache
    {
        get { lock (_gate) return _cache; }
    }

    public void SetAssets(Dictionary<string, string> assets, bool reset)
    {
        lock (_gate)
        {
            if (reset)
            {
                _cache = new ClipCache(ClipCache.DefaultCapacityFrames);
            }
            else
            {
                foreach (var id in _assets.Keys)
                    if (!assets.TryGetValue(id, out var path) || !string.Equals(path, _assets[id], StringComparison.OrdinalIgnoreCase))
                        _cache.Remove(id);
            }
            _assets = assets;
        }
    }

    public bool TryGet(string clipId, out float[] samples)
    {
        lock (_gate)
        {
            if (_assets.TryGetValue(clipId, out var path) && _cache.TryGet(clipId, out samples!))
            {
                if (decoder.MatchesAsset(samples, path)) return true;
                _cache.Remove(clipId);
            }
            samples = [];
            return false;
        }
    }

    public void Store(string clipId, float[] samples)
    {
        lock (_gate)
        {
            if (_assets.TryGetValue(clipId, out var path) && decoder.MatchesAsset(samples, path))
                _cache.Store(clipId, samples);
        }
    }

    public void Remove(string clipId)
    {
        lock (_gate)
        {
            _assets.Remove(clipId);
            _cache.Remove(clipId);
        }
    }

    public ClipCacheStats Stats()
    {
        lock (_gate) return _cache.Stats();
    }
}
