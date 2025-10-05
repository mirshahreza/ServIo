using Microsoft.Extensions.Caching.Memory;
using PowNet.Abstractions.Api;
using PowNet.Abstractions.Authentication;
using PowNet.Configuration;
using PowNet.Services;

namespace ServIo;

/// <summary>
/// Sized cache service wrapper to satisfy MemoryCache size-limit requirement while reusing PowNet shared cache.
/// </summary>
internal sealed class SizedApiCacheService : IApiCacheService
{
    private readonly IMemoryCache _cache = MemoryService.SharedMemoryCache;

    public string BuildKey(IApiCallInfo call, IUserIdentity user, IApiConfiguration apiConfiguration)
    {
        bool perUser = false;
        if (apiConfiguration is PowNet.Configuration.ApiConfiguration concrete)
        {
            perUser = concrete.CacheLevel == PowNet.Common.CacheLevel.PerUser;
        }
        return $"Response::{call.Controller}_{call.Action}{(perUser ? "_" + user.UserName : string.Empty)}";
    }

    public bool TryGet(string key, out CachedApiResponse? response)
    {
        if (_cache.TryGetValue(key, out CachedApiResponse? val))
        {
            response = val; return true;
        }
        response = null; return false;
    }

    public void Set(string key, CachedApiResponse response, TimeSpan ttl)
    {
        var opts = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = ttl,
            Size = response.Content?.Length ?? 1 // satisfy size limit if enabled
        };
        _cache.Set(key, response, opts);
    }
}
