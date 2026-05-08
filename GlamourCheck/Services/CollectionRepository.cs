namespace GlamourCheck.Services;

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

/// <summary>
/// Serialized SQLite access for collection snapshots, remote payload caches, Duty Finder
/// summary caches, and local seeded drop tables. UI and sync services should go through this
/// abstraction instead of opening their own database connections.
/// </summary>
public sealed class CollectionRepository : ICollectionRepository
{
    private const int CurrentSchemaVersion = 1;

    private static readonly (string Key, string DisplayName, bool IsServerSide)[] BuiltInSources =
    [
        (CollectionSource.Inventory, "Inventory", false),
        (CollectionSource.Equipped, "Equipped Gear", false),
        (CollectionSource.ArmoryChest, "Armory Chest", false),
        (CollectionSource.GlamourDresser, "Glamour Dresser", true),
        (CollectionSource.Armoire, "Armoire", true),
        (CollectionSource.ChocoboSaddlebag, "Chocobo Saddlebag", true),
    ];

    private readonly string databasePath;
    private readonly object syncRoot = new();
    private SqliteConnection? connection;

    public CollectionRepository(string databasePath)
    {
        this.databasePath = databasePath;
    }

    public void Initialize()
    {
        lock (syncRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString());
            connection.Open();

            ExecuteNonQuery("PRAGMA foreign_keys = ON;");
            ExecuteNonQuery("PRAGMA journal_mode = WAL;");
            CreateSchema();
            SeedSources();
        }
    }

    public void UpsertCharacter(string characterKey, string? displayName, uint? worldId)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                INSERT INTO characters (character_key, display_name, world_id, last_seen_utc)
                VALUES ($character_key, $display_name, $world_id, $last_seen_utc)
                ON CONFLICT(character_key) DO UPDATE SET
                    display_name = excluded.display_name,
                    world_id = excluded.world_id,
                    last_seen_utc = excluded.last_seen_utc;
                """;
            command.Parameters.AddWithValue("$character_key", characterKey);
            command.Parameters.AddWithNullableValue("$display_name", displayName);
            command.Parameters.AddWithNullableValue("$world_id", worldId);
            command.Parameters.AddWithValue("$last_seen_utc", FormatUtc(DateTimeOffset.UtcNow));
            command.ExecuteNonQuery();
        }
    }

    public void ReplaceSourceSnapshot(string characterKey, string sourceKey, IEnumerable<uint> normalizedItemIds, DateTimeOffset syncedAtUtc, string? staleReason = null, string? displayName = null)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            var distinctItemIds = normalizedItemIds
                .Where(itemId => itemId != 0)
                .Distinct()
                .Order()
                .ToArray();

            using var transaction = connection!.BeginTransaction();
            var sourceId = EnsureSource(sourceKey, transaction, displayName);

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM collected_items
                    WHERE character_key = $character_key AND source_id = $source_id;
                    """;
                delete.Parameters.AddWithValue("$character_key", characterKey);
                delete.Parameters.AddWithValue("$source_id", sourceId);
                delete.ExecuteNonQuery();
            }

            foreach (var itemId in distinctItemIds)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO collected_items (character_key, source_id, normalized_item_id, first_seen_utc, last_seen_utc)
                    VALUES ($character_key, $source_id, $normalized_item_id, $first_seen_utc, $last_seen_utc);
                    """;
                insert.Parameters.AddWithValue("$character_key", characterKey);
                insert.Parameters.AddWithValue("$source_id", sourceId);
                insert.Parameters.AddWithValue("$normalized_item_id", itemId);
                insert.Parameters.AddWithValue("$first_seen_utc", FormatUtc(syncedAtUtc));
                insert.Parameters.AddWithValue("$last_seen_utc", FormatUtc(syncedAtUtc));
                insert.ExecuteNonQuery();
            }

            using (var upsertSnapshot = connection.CreateCommand())
            {
                upsertSnapshot.Transaction = transaction;
                upsertSnapshot.CommandText = """
                    INSERT INTO source_snapshots (character_key, source_id, synced_at_utc, stale_reason, item_count)
                    VALUES ($character_key, $source_id, $synced_at_utc, $stale_reason, $item_count)
                    ON CONFLICT(character_key, source_id) DO UPDATE SET
                        synced_at_utc = excluded.synced_at_utc,
                        stale_reason = excluded.stale_reason,
                        item_count = excluded.item_count;
                    """;
                upsertSnapshot.Parameters.AddWithValue("$character_key", characterKey);
                upsertSnapshot.Parameters.AddWithValue("$source_id", sourceId);
                upsertSnapshot.Parameters.AddWithValue("$synced_at_utc", FormatUtc(syncedAtUtc));
                upsertSnapshot.Parameters.AddWithNullableValue("$stale_reason", staleReason);
                upsertSnapshot.Parameters.AddWithValue("$item_count", distinctItemIds.Length);
                upsertSnapshot.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public bool IsCollected(string characterKey, uint normalizedItemId)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM collected_items
                WHERE character_key = $character_key AND normalized_item_id = $normalized_item_id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$character_key", characterKey);
            command.Parameters.AddWithValue("$normalized_item_id", normalizedItemId);
            return command.ExecuteScalar() is not null;
        }
    }

    public bool IsCollectedInSource(string characterKey, string sourceKey, uint normalizedItemId)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM collected_items AS ci
                JOIN sources AS s ON s.source_id = ci.source_id
                WHERE ci.character_key = $character_key
                    AND s.source_key = $source_key
                    AND ci.normalized_item_id = $normalized_item_id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$character_key", characterKey);
            command.Parameters.AddWithValue("$source_key", sourceKey);
            command.Parameters.AddWithValue("$normalized_item_id", normalizedItemId);
            return command.ExecuteScalar() is not null;
        }
    }

    public HashSet<uint> GetCollectedItems(string characterKey)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT normalized_item_id
                FROM collected_items
                WHERE character_key = $character_key;
                """;
            command.Parameters.AddWithValue("$character_key", characterKey);

            using var reader = command.ExecuteReader();
            var itemIds = new HashSet<uint>();
            while (reader.Read())
            {
                itemIds.Add((uint)reader.GetInt64(0));
            }

            return itemIds;
        }
    }

    public HashSet<uint> GetCollectedItemsInSource(string characterKey, string sourceKey)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                SELECT DISTINCT ci.normalized_item_id
                FROM collected_items AS ci
                JOIN sources AS s ON s.source_id = ci.source_id
                WHERE ci.character_key = $character_key AND s.source_key = $source_key;
                """;
            command.Parameters.AddWithValue("$character_key", characterKey);
            command.Parameters.AddWithValue("$source_key", sourceKey);

            using var reader = command.ExecuteReader();
            var itemIds = new HashSet<uint>();
            while (reader.Read())
            {
                itemIds.Add((uint)reader.GetInt64(0));
            }

            return itemIds;
        }
    }

    public Dictionary<uint, string[]> GetItemSourceMap(string characterKey)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                SELECT ci.normalized_item_id, s.source_key
                FROM collected_items AS ci
                JOIN sources AS s ON s.source_id = ci.source_id
                WHERE ci.character_key = $character_key;
                """;
            command.Parameters.AddWithValue("$character_key", characterKey);

            using var reader = command.ExecuteReader();
            var result = new Dictionary<uint, List<string>>();
            while (reader.Read())
            {
                var itemId = (uint)reader.GetInt64(0);
                var sourceKey = reader.GetString(1);
                if (!result.TryGetValue(itemId, out var list))
                {
                    list = [];
                    result[itemId] = list;
                }
                list.Add(sourceKey);
            }

            return result.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        }
    }

    public IReadOnlyList<SourceSnapshotInfo> GetSourceSnapshots(string characterKey)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                SELECT s.source_key, s.display_name, s.is_server_side, ss.synced_at_utc, ss.stale_reason, ss.item_count
                FROM sources AS s
                LEFT JOIN source_snapshots AS ss
                    ON ss.source_id = s.source_id AND ss.character_key = $character_key
                ORDER BY s.source_key;
                """;
            command.Parameters.AddWithValue("$character_key", characterKey);

            using var reader = command.ExecuteReader();
            var snapshots = new List<SourceSnapshotInfo>();
            while (reader.Read())
            {
                snapshots.Add(new SourceSnapshotInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2) != 0,
                    reader.IsDBNull(3) ? null : ParseUtc(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? 0 : reader.GetInt32(5)));
            }

            return snapshots;
        }
    }

    public void UpsertLocalSeededDrops(LocalSeededDropSet seed)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var transaction = connection!.BeginTransaction();
            long seedId;
            using (var upsert = connection.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO local_seeded_drop_sets (
                        garland_instance_id,
                        instance_name,
                        instance_category,
                        instance_patch,
                        seed_item_id,
                        seed_item_name,
                        seed_prefix,
                        item_level,
                        seed_patch,
                        wing,
                        slot_filter,
                        created_at_utc)
                    VALUES (
                        $garland_instance_id,
                        $instance_name,
                        $instance_category,
                        $instance_patch,
                        $seed_item_id,
                        $seed_item_name,
                        $seed_prefix,
                        $item_level,
                        $seed_patch,
                        $wing,
                        $slot_filter,
                        $created_at_utc)
                    ON CONFLICT(garland_instance_id) DO UPDATE SET
                        instance_name = excluded.instance_name,
                        instance_category = excluded.instance_category,
                        instance_patch = excluded.instance_patch,
                        seed_item_id = excluded.seed_item_id,
                        seed_item_name = excluded.seed_item_name,
                        seed_prefix = excluded.seed_prefix,
                        item_level = excluded.item_level,
                        seed_patch = excluded.seed_patch,
                        wing = excluded.wing,
                        slot_filter = excluded.slot_filter,
                        created_at_utc = excluded.created_at_utc;
                    """;
                upsert.Parameters.AddWithValue("$garland_instance_id", seed.GarlandInstanceId);
                upsert.Parameters.AddWithValue("$instance_name", seed.InstanceName);
                upsert.Parameters.AddWithValue("$instance_category", seed.InstanceCategory);
                upsert.Parameters.AddWithNullableValue("$instance_patch", seed.InstancePatch);
                upsert.Parameters.AddWithValue("$seed_item_id", seed.SeedItemId);
                upsert.Parameters.AddWithValue("$seed_item_name", seed.SeedItemName);
                upsert.Parameters.AddWithValue("$seed_prefix", seed.SeedPrefix);
                upsert.Parameters.AddWithValue("$item_level", seed.ItemLevel);
                upsert.Parameters.AddWithNullableValue("$seed_patch", seed.SeedPatch);
                upsert.Parameters.AddWithNullableValue("$wing", seed.Wing);
                upsert.Parameters.AddWithValue("$slot_filter", seed.SlotFilter);
                upsert.Parameters.AddWithValue("$created_at_utc", FormatUtc(seed.CreatedAtUtc));
                upsert.ExecuteNonQuery();
            }

            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT seed_id FROM local_seeded_drop_sets WHERE garland_instance_id = $garland_instance_id;";
                select.Parameters.AddWithValue("$garland_instance_id", seed.GarlandInstanceId);
                seedId = (long)select.ExecuteScalar()!;
            }

            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM local_seeded_drop_items WHERE seed_id = $seed_id;";
                delete.Parameters.AddWithValue("$seed_id", seedId);
                delete.ExecuteNonQuery();
            }

            foreach (var item in seed.Items.DistinctBy(item => item.ItemId).OrderBy(item => item.ItemId))
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO local_seeded_drop_items (
                        seed_id,
                        item_id,
                        name,
                        slot,
                        armor_category,
                        item_level,
                        icon_id)
                    VALUES (
                        $seed_id,
                        $item_id,
                        $name,
                        $slot,
                        $armor_category,
                        $item_level,
                        $icon_id);
                    """;
                insert.Parameters.AddWithValue("$seed_id", seedId);
                insert.Parameters.AddWithValue("$item_id", item.ItemId);
                insert.Parameters.AddWithValue("$name", item.Name);
                insert.Parameters.AddWithValue("$slot", item.Slot.ToString());
                insert.Parameters.AddWithValue("$armor_category", item.ArmorCategory);
                insert.Parameters.AddWithNullableValue("$item_level", item.ItemLevel);
                insert.Parameters.AddWithValue("$icon_id", item.IconId);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public LocalSeededDropSet? GetLocalSeededDrops(uint garlandInstanceId)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var select = connection!.CreateCommand();
            select.CommandText = """
                SELECT
                    seed_id,
                    garland_instance_id,
                    instance_name,
                    instance_category,
                    instance_patch,
                    seed_item_id,
                    seed_item_name,
                    seed_prefix,
                    item_level,
                    seed_patch,
                    wing,
                    slot_filter,
                    created_at_utc
                FROM local_seeded_drop_sets
                WHERE garland_instance_id = $garland_instance_id
                LIMIT 1;
                """;
            select.Parameters.AddWithValue("$garland_instance_id", garlandInstanceId);

            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var seedId = reader.GetInt64(0);
            var seedGarlandInstanceId = (uint)reader.GetInt64(1);
            var instanceName = reader.GetString(2);
            var instanceCategory = reader.GetString(3);
            double? instancePatch = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            var seedItemId = (uint)reader.GetInt64(5);
            var seedItemName = reader.GetString(6);
            var seedPrefix = reader.GetString(7);
            var itemLevel = (uint)reader.GetInt64(8);
            double? seedPatch = reader.IsDBNull(9) ? null : reader.GetDouble(9);
            var wing = reader.IsDBNull(10) ? null : reader.GetString(10);
            var slotFilter = reader.GetString(11);
            var createdAtUtc = ParseUtc(reader.GetString(12));
            reader.Dispose();

            return new LocalSeededDropSet(
                seedGarlandInstanceId,
                instanceName,
                instanceCategory,
                instancePatch,
                seedItemId,
                seedItemName,
                seedPrefix,
                itemLevel,
                seedPatch,
                wing,
                slotFilter,
                createdAtUtc,
                GetLocalSeededDropItems(seedId));
        }
    }

    public IReadOnlyList<LocalSeededDropSummary> GetLocalSeededDropSummaries()
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                SELECT
                    s.garland_instance_id,
                    s.instance_name,
                    s.instance_category,
                    s.seed_item_id,
                    s.seed_item_name,
                    s.seed_prefix,
                    s.item_level,
                    s.seed_patch,
                    s.wing,
                    s.slot_filter,
                    COUNT(i.item_id),
                    s.created_at_utc
                FROM local_seeded_drop_sets AS s
                LEFT JOIN local_seeded_drop_items AS i ON i.seed_id = s.seed_id
                GROUP BY s.seed_id
                ORDER BY s.instance_name;
                """;

            using var reader = command.ExecuteReader();
            var result = new List<LocalSeededDropSummary>();
            while (reader.Read())
            {
                result.Add(new LocalSeededDropSummary(
                    (uint)reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    (uint)reader.GetInt64(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    (uint)reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetDouble(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetString(9),
                    reader.GetInt32(10),
                    ParseUtc(reader.GetString(11))));
            }

            return result;
        }
    }

    public bool DeleteLocalSeededDrops(uint garlandInstanceId)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var delete = connection!.CreateCommand();
            delete.CommandText = "DELETE FROM local_seeded_drop_sets WHERE garland_instance_id = $garland_instance_id;";
            delete.Parameters.AddWithValue("$garland_instance_id", garlandInstanceId);
            return delete.ExecuteNonQuery() > 0;
        }
    }

    public InstanceDropCacheEntry? GetValidInstanceDropCacheEntry(uint contentFinderConditionId, DateTimeOffset nowUtc)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                SELECT content_finder_condition_id, garland_instance_id, fetched_at_utc, expires_at_utc, payload_json
                FROM instance_drop_cache
                WHERE content_finder_condition_id = $content_finder_condition_id
                    AND expires_at_utc > $now_utc
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$content_finder_condition_id", contentFinderConditionId);
            command.Parameters.AddWithValue("$now_utc", FormatUtc(nowUtc));

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new InstanceDropCacheEntry(
                (uint)reader.GetInt64(0),
                reader.IsDBNull(1) ? null : (uint)reader.GetInt64(1),
                ParseUtc(reader.GetString(2)),
                ParseUtc(reader.GetString(3)),
                reader.GetString(4));
        }
    }

    public IReadOnlyList<InstanceDropCacheEntry> GetValidInstanceDropCacheEntries(DateTimeOffset nowUtc)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                SELECT content_finder_condition_id, garland_instance_id, fetched_at_utc, expires_at_utc, payload_json
                FROM instance_drop_cache
                WHERE expires_at_utc > $now_utc;
                """;
            command.Parameters.AddWithValue("$now_utc", FormatUtc(nowUtc));

            using var reader = command.ExecuteReader();
            var entries = new List<InstanceDropCacheEntry>();
            while (reader.Read())
            {
                entries.Add(new InstanceDropCacheEntry(
                    (uint)reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : (uint)reader.GetInt64(1),
                    ParseUtc(reader.GetString(2)),
                    ParseUtc(reader.GetString(3)),
                    reader.GetString(4)));
            }

            return entries;
        }
    }

    public void UpsertInstanceDropCacheEntry(
        uint contentFinderConditionId,
        uint? garlandInstanceId,
        string payloadJson,
        DateTimeOffset fetchedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                INSERT INTO instance_drop_cache (
                    content_finder_condition_id,
                    garland_instance_id,
                    fetched_at_utc,
                    expires_at_utc,
                    payload_json)
                VALUES (
                    $content_finder_condition_id,
                    $garland_instance_id,
                    $fetched_at_utc,
                    $expires_at_utc,
                    $payload_json)
                ON CONFLICT(content_finder_condition_id) DO UPDATE SET
                    garland_instance_id = excluded.garland_instance_id,
                    fetched_at_utc = excluded.fetched_at_utc,
                    expires_at_utc = excluded.expires_at_utc,
                    payload_json = excluded.payload_json;
                """;
            command.Parameters.AddWithValue("$content_finder_condition_id", contentFinderConditionId);
            command.Parameters.AddWithNullableValue("$garland_instance_id", garlandInstanceId);
            command.Parameters.AddWithValue("$fetched_at_utc", FormatUtc(fetchedAtUtc));
            command.Parameters.AddWithValue("$expires_at_utc", FormatUtc(expiresAtUtc));
            command.Parameters.AddWithValue("$payload_json", payloadJson);
            command.ExecuteNonQuery();
        }
    }

    public int DeleteInstanceDropCacheEntriesForGarlandInstance(uint garlandInstanceId)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = "DELETE FROM instance_drop_cache WHERE garland_instance_id = $garland_instance_id;";
            command.Parameters.AddWithValue("$garland_instance_id", garlandInstanceId);
            return command.ExecuteNonQuery();
        }
    }

    public RemoteCacheEntry? GetValidRemoteCacheEntry(string cacheKey, DateTimeOffset nowUtc)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                SELECT cache_key, url, fetched_at_utc, expires_at_utc, payload_json
                FROM garland_cache
                WHERE cache_key = $cache_key AND expires_at_utc > $now_utc
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$cache_key", cacheKey);
            command.Parameters.AddWithValue("$now_utc", FormatUtc(nowUtc));

            using var cacheReader = command.ExecuteReader();
            if (!cacheReader.Read())
            {
                return null;
            }

            return new RemoteCacheEntry(
                cacheReader.GetString(0),
                cacheReader.GetString(1),
                ParseUtc(cacheReader.GetString(2)),
                ParseUtc(cacheReader.GetString(3)),
                cacheReader.GetString(4));
        }
    }

    public void UpsertRemoteCacheEntry(string cacheKey, string url, string payloadJson, DateTimeOffset fetchedAtUtc, DateTimeOffset expiresAtUtc)
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var command = connection!.CreateCommand();
            command.CommandText = """
                INSERT INTO garland_cache (cache_key, url, fetched_at_utc, expires_at_utc, payload_json)
                VALUES ($cache_key, $url, $fetched_at_utc, $expires_at_utc, $payload_json)
                ON CONFLICT(cache_key) DO UPDATE SET
                    url = excluded.url,
                    fetched_at_utc = excluded.fetched_at_utc,
                    expires_at_utc = excluded.expires_at_utc,
                    payload_json = excluded.payload_json;
                """;
            command.Parameters.AddWithValue("$cache_key", cacheKey);
            command.Parameters.AddWithValue("$url", url);
            command.Parameters.AddWithValue("$fetched_at_utc", FormatUtc(fetchedAtUtc));
            command.Parameters.AddWithValue("$expires_at_utc", FormatUtc(expiresAtUtc));
            command.Parameters.AddWithValue("$payload_json", payloadJson);
            command.ExecuteNonQuery();
        }
    }

    public int ClearRemoteCache()
    {
        lock (syncRoot)
        {
            EnsureInitialized();

            using var deleteGarlandCache = connection!.CreateCommand();
            deleteGarlandCache.CommandText = "DELETE FROM garland_cache;";
            var deletedGarlandRows = deleteGarlandCache.ExecuteNonQuery();

            using var deleteInstanceCache = connection.CreateCommand();
            deleteInstanceCache.CommandText = "DELETE FROM instance_drop_cache;";
            var deletedInstanceRows = deleteInstanceCache.ExecuteNonQuery();

            return deletedGarlandRows + deletedInstanceRows;
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (connection is not null)
            {
                connection.Dispose();
                SqliteConnection.ClearPool(connection);
                connection = null;
            }
        }
    }

    private void CreateSchema()
    {
        ExecuteNonQuery("""
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS characters (
                character_key TEXT PRIMARY KEY,
                display_name TEXT NULL,
                world_id INTEGER NULL,
                last_seen_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sources (
                source_id INTEGER PRIMARY KEY,
                source_key TEXT NOT NULL UNIQUE,
                display_name TEXT NOT NULL,
                is_server_side INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS source_snapshots (
                character_key TEXT NOT NULL,
                source_id INTEGER NOT NULL,
                synced_at_utc TEXT NOT NULL,
                stale_reason TEXT NULL,
                item_count INTEGER NOT NULL,
                PRIMARY KEY (character_key, source_id),
                FOREIGN KEY (character_key) REFERENCES characters(character_key),
                FOREIGN KEY (source_id) REFERENCES sources(source_id)
            );

            CREATE TABLE IF NOT EXISTS collected_items (
                character_key TEXT NOT NULL,
                source_id INTEGER NOT NULL,
                normalized_item_id INTEGER NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                PRIMARY KEY (character_key, source_id, normalized_item_id),
                FOREIGN KEY (character_key) REFERENCES characters(character_key),
                FOREIGN KEY (source_id) REFERENCES sources(source_id)
            );

            CREATE INDEX IF NOT EXISTS idx_collected_items_lookup
            ON collected_items(character_key, normalized_item_id);

            CREATE TABLE IF NOT EXISTS garland_cache (
                cache_key TEXT PRIMARY KEY,
                url TEXT NOT NULL,
                fetched_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS instance_drop_cache (
                content_finder_condition_id INTEGER PRIMARY KEY,
                garland_instance_id INTEGER NULL,
                fetched_at_utc TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00',
                payload_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS local_seeded_drop_sets (
                seed_id INTEGER PRIMARY KEY AUTOINCREMENT,
                garland_instance_id INTEGER NOT NULL UNIQUE,
                instance_name TEXT NOT NULL,
                instance_category TEXT NOT NULL,
                instance_patch REAL NULL,
                seed_item_id INTEGER NOT NULL,
                seed_item_name TEXT NOT NULL,
                seed_prefix TEXT NOT NULL,
                item_level INTEGER NOT NULL,
                seed_patch REAL NULL,
                wing TEXT NULL,
                slot_filter TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS local_seeded_drop_items (
                seed_id INTEGER NOT NULL,
                item_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                slot TEXT NOT NULL,
                armor_category TEXT NOT NULL,
                item_level INTEGER NULL,
                icon_id INTEGER NOT NULL,
                PRIMARY KEY (seed_id, item_id),
                FOREIGN KEY (seed_id) REFERENCES local_seeded_drop_sets(seed_id) ON DELETE CASCADE
            );
            """);

        using var countCommand = connection!.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM schema_version;";
        var count = Convert.ToInt32(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (count == 0)
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.CommandText = "INSERT INTO schema_version (version) VALUES ($version);";
            insertCommand.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            insertCommand.ExecuteNonQuery();
        }
        else
        {
            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = "UPDATE schema_version SET version = $version;";
            updateCommand.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            updateCommand.ExecuteNonQuery();
        }

        EnsureColumnExists("instance_drop_cache", "expires_at_utc", "TEXT NOT NULL DEFAULT '1970-01-01T00:00:00.0000000+00:00'");
    }

    private void EnsureColumnExists(string tableName, string columnName, string columnDefinition)
    {
        using var pragma = connection!.CreateCommand();
        pragma.CommandText = $"PRAGMA table_info({tableName});";

        using (var reader = pragma.ExecuteReader())
        {
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alter.ExecuteNonQuery();
    }

    private void SeedSources()
    {
        using var transaction = connection!.BeginTransaction();
        foreach (var source in BuiltInSources)
        {
            UpsertSource(source.Key, source.DisplayName, source.IsServerSide, transaction);
        }

        transaction.Commit();
    }

    private long EnsureSource(string sourceKey, SqliteTransaction transaction, string? displayName = null)
    {
        var builtIn = BuiltInSources.FirstOrDefault(source => source.Key == sourceKey);
        if (builtIn.Key is not null)
        {
            UpsertSource(builtIn.Key, builtIn.DisplayName, builtIn.IsServerSide, transaction);
        }
        else
        {
            UpsertSource(sourceKey, displayName ?? sourceKey, true, transaction);
        }

        using var select = connection!.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT source_id FROM sources WHERE source_key = $source_key;";
        select.Parameters.AddWithValue("$source_key", sourceKey);
        return (long)select.ExecuteScalar()!;
    }

    private void UpsertSource(string sourceKey, string displayName, bool isServerSide, SqliteTransaction transaction)
    {
        using var command = connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sources (source_key, display_name, is_server_side)
            VALUES ($source_key, $display_name, $is_server_side)
            ON CONFLICT(source_key) DO UPDATE SET
                display_name = excluded.display_name,
                is_server_side = excluded.is_server_side;
            """;
        command.Parameters.AddWithValue("$source_key", sourceKey);
        command.Parameters.AddWithValue("$display_name", displayName);
        command.Parameters.AddWithValue("$is_server_side", isServerSide ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private IReadOnlyList<GarlandLootItemInfo> GetLocalSeededDropItems(long seedId)
    {
        using var command = connection!.CreateCommand();
        command.CommandText = """
            SELECT item_id, name, slot, armor_category, item_level, icon_id
            FROM local_seeded_drop_items
            WHERE seed_id = $seed_id
            ORDER BY armor_category, slot, name;
            """;
        command.Parameters.AddWithValue("$seed_id", seedId);

        using var reader = command.ExecuteReader();
        var items = new List<GarlandLootItemInfo>();
        while (reader.Read())
        {
            items.Add(new GarlandLootItemInfo(
                (uint)reader.GetInt64(0),
                reader.GetString(1),
                Enum.TryParse<GearSlot>(reader.GetString(2), out var slot) ? slot : GearSlot.Unknown,
                reader.GetString(3),
                reader.IsDBNull(4) ? null : (uint)reader.GetInt64(4),
                (uint)reader.GetInt64(5)));
        }

        return items;
    }

    private void ExecuteNonQuery(string commandText)
    {
        EnsureInitialized(allowUnopenedConnection: true);

        using var command = connection!.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private void EnsureInitialized(bool allowUnopenedConnection = false)
    {
        if (connection is null || (!allowUnopenedConnection && connection.State != ConnectionState.Open))
        {
            throw new InvalidOperationException("Collection repository has not been initialized.");
        }
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtc(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }
}

internal static class SqliteParameterCollectionExtensions
{
    public static void AddWithNullableValue(this SqliteParameterCollection parameters, string parameterName, object? value)
    {
        parameters.AddWithValue(parameterName, value ?? DBNull.Value);
    }
}
