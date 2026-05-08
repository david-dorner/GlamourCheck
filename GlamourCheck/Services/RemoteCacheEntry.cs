using System;

namespace GlamourCheck.Services;

public sealed record RemoteCacheEntry(
    string CacheKey,
    string Url,
    DateTimeOffset FetchedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PayloadJson);
