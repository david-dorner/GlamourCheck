using System;

namespace GlamourCheck.Services;

public sealed record GarlandPayloadResult(
    string CacheKey,
    string Url,
    string PayloadJson,
    bool FromCache,
    DateTimeOffset FetchedAtUtc,
    DateTimeOffset ExpiresAtUtc);
