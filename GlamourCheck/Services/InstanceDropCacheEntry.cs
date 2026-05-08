using System;

namespace GlamourCheck.Services;

public sealed record InstanceDropCacheEntry(
    uint ContentFinderConditionId,
    uint? GarlandInstanceId,
    DateTimeOffset FetchedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string PayloadJson);
