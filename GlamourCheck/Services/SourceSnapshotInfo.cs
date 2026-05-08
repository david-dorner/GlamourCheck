using System;

namespace GlamourCheck.Services;

public sealed record SourceSnapshotInfo(
    string SourceKey,
    string DisplayName,
    bool IsServerSide,
    DateTimeOffset? SyncedAtUtc,
    string? StaleReason,
    int ItemCount);
