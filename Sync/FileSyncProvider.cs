using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Sync
{
    // Sync via a shared folder (Dropbox/OneDrive/network share) - zero backend dependency.
    // PushAsync writes one JSON file per push under "<syncFolder>/changes/"; PullAsync reads
    // every other device's files this instance hasn't returned yet. Plan 5.
    public class FileSyncProvider : ISyncProvider
    {
        // "yyyyMMddHHmmssfff" - fixed width, digits only, which is what lets
        // TryParseFileName split a filename from the right below.
        private const int TimestampLength = 17;
        private const string ChangesSubfolder = "changes";
        private static readonly TimeSpan DefaultRetentionWindow = TimeSpan.FromDays(30);

        private readonly string _syncFolderPath;
        private readonly string _deviceId;
        private readonly TimeSpan _retentionWindow;

        // Tracked by filename rather than the embedded timestamp: two devices' clocks can
        // drift, and a filename this instance has already returned once never needs
        // returning again regardless of how it sorts. This means `since` (below) is
        // accepted for interface compliance but isn't the dedup mechanism - a fresh
        // instance's empty set intentionally causes it to catch up on all remote history.
        private readonly HashSet<string> _processedFileNames = new(StringComparer.OrdinalIgnoreCase);

        // WL-49/WL-61: <syncFolder>/changes/*.json otherwise accumulates forever. retentionWindow
        // is a caller-supplied config value (see AppSettings.SyncRetentionDays), not something
        // this type reaches into SettingsService for - matching how deviceId is passed in below.
        public FileSyncProvider(string syncFolderPath, string deviceId, TimeSpan? retentionWindow = null)
        {
            _syncFolderPath = syncFolderPath;
            _deviceId = deviceId;
            _retentionWindow = retentionWindow ?? DefaultRetentionWindow;
        }

        private string ChangesFolder => Path.Combine(_syncFolderPath, ChangesSubfolder);

        public async Task<SyncResult> PushAsync(IEnumerable<SyncQueue> pendingChanges)
        {
            var changes = pendingChanges.ToList();
            if (changes.Count == 0)
                return new SyncResult();

            try
            {
                Directory.CreateDirectory(ChangesFolder);

                var entries = changes.Select(ToWireEntry).ToList();
                var fileName = $"{_deviceId}-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json";
                var filePath = Path.Combine(ChangesFolder, fileName);

                await WriteFileWithRetriesAsync(filePath, JsonSerializer.Serialize(entries));

                // A local file write either fully succeeds or throws - there's no
                // partial-acceptance case to model here, unlike a real network push.
                return new SyncResult
                {
                    AcceptedSyncQueueIds = changes.Select(c => c.Id).ToList()
                };
            }
            catch (Exception ex) when (IsRecoverableIoFailure(ex))
            {
                System.Diagnostics.Debug.WriteLine($"FileSyncProvider push failed: {ex.Message}");
                return new SyncResult
                {
                    Failures = changes.Select(c => new SyncFailure
                    {
                        SyncQueueId = c.Id,
                        ErrorMessage = ex.Message
                    }).ToList()
                };
            }
        }

        public Task<IEnumerable<RemoteChange>> PullAsync(DateTime since)
        {
            try
            {
                if (!Directory.Exists(ChangesFolder))
                    return Task.FromResult<IEnumerable<RemoteChange>>(Array.Empty<RemoteChange>());

                var results = new List<RemoteChange>();

                foreach (var filePath in Directory.GetFiles(ChangesFolder, "*.json").OrderBy(f => f))
                {
                    var fileName = Path.GetFileName(filePath);
                    if (!TryParseFileName(fileName, out var originDeviceId, out _))
                        continue;

                    // A device never needs to pull its own pushes back.
                    if (string.Equals(originDeviceId, _deviceId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!_processedFileNames.Add(fileName))
                        continue;

                    var entries = ReadEntries(filePath);
                    if (entries == null)
                        continue;

                    results.AddRange(entries.Select(ToRemoteChange));
                }

                // Runs after every file has had its chance to be returned at least once this
                // call, so a device that hasn't synced in a while still sees a change before
                // its file ages out - pruning only removes files no longer new to anyone here.
                PruneOldChangeFiles();

                return Task.FromResult<IEnumerable<RemoteChange>>(results);
            }
            catch (Exception ex) when (IsRecoverableIoFailure(ex))
            {
                System.Diagnostics.Debug.WriteLine($"FileSyncProvider pull failed: {ex.Message}");
                return Task.FromResult<IEnumerable<RemoteChange>>(Array.Empty<RemoteChange>());
            }
        }

        // SyncQueue itself doesn't carry Version/OriginDeviceId (see Models/SyncQueue.cs) -
        // they live on the serialized entity in PayloadJson (Project/TaskItem/TimeEntry all
        // implement IVersionedEntity). Surfaced at the wire entry's top level anyway,
        // mirroring RemoteChange's shape, so a puller can last-write-wins compare without
        // knowing the concrete entity type.
        private static SyncFileChangeEntry ToWireEntry(SyncQueue queueEntry)
        {
            var (version, originDeviceId) = ExtractVersionMetadata(queueEntry.PayloadJson);
            return new SyncFileChangeEntry
            {
                EntityType = queueEntry.EntityType ?? string.Empty,
                EntityId = queueEntry.EntityId,
                Operation = queueEntry.Operation ?? string.Empty,
                PayloadJson = queueEntry.PayloadJson,
                Version = version,
                OriginDeviceId = originDeviceId,
                CreatedAt = queueEntry.CreatedAt
            };
        }

        private static RemoteChange ToRemoteChange(SyncFileChangeEntry entry) => new RemoteChange
        {
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            Operation = entry.Operation,
            PayloadJson = entry.PayloadJson,
            Version = entry.Version,
            OriginDeviceId = entry.OriginDeviceId
        };

        private static (int Version, string? OriginDeviceId) ExtractVersionMetadata(string? payloadJson)
        {
            if (string.IsNullOrEmpty(payloadJson))
                return (0, null);

            try
            {
                var envelope = JsonSerializer.Deserialize<VersionEnvelope>(payloadJson);
                return (envelope?.Version ?? 0, envelope?.OriginDeviceId);
            }
            catch (JsonException)
            {
                return (0, null);
            }
        }

        private static List<SyncFileChangeEntry>? ReadEntries(string filePath)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                return JsonSerializer.Deserialize<List<SyncFileChangeEntry>>(json);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                System.Diagnostics.Debug.WriteLine($"FileSyncProvider: skipping unreadable change file '{filePath}': {ex.Message}");
                return null;
            }
        }

        // Expected shape: "<deviceId>-<yyyyMMddHHmmssfff>.json". DeviceId is itself a Guid
        // string full of hyphens, so this splits from the right using the timestamp's
        // fixed, digits-only width rather than splitting on the first '-'.
        private static bool TryParseFileName(string fileName, out string deviceId, out DateTime timestampUtc)
        {
            deviceId = string.Empty;
            timestampUtc = default;
            var nameNoExt = Path.GetFileNameWithoutExtension(fileName);

            if (nameNoExt.Length <= TimestampLength + 1)
                return false;

            var separatorIndex = nameNoExt.Length - TimestampLength - 1;
            if (nameNoExt[separatorIndex] != '-')
                return false;

            var timestampPart = nameNoExt.Substring(separatorIndex + 1);
            if (!DateTime.TryParseExact(
                    timestampPart, "yyyyMMddHHmmssfff",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
                    out var parsed))
                return false;

            deviceId = nameNoExt.Substring(0, separatorIndex);
            timestampUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        // WL-49/WL-61: deletes change files (from any device, including this one's own
        // pushes) older than _retentionWindow. Age comes from the filename's embedded
        // timestamp rather than the file's OS last-write time, since a cloud-sync client
        // re-touching a file on download shouldn't reset its retention clock.
        private void PruneOldChangeFiles()
        {
            // Never let a pruning failure clobber the results a caller already pulled -
            // this is best-effort housekeeping, not the pull itself.
            try
            {
                var cutoffUtc = DateTime.UtcNow - _retentionWindow;

                foreach (var filePath in Directory.GetFiles(ChangesFolder, "*.json"))
                {
                    var fileName = Path.GetFileName(filePath);
                    if (!TryParseFileName(fileName, out _, out var timestampUtc))
                        continue;

                    if (timestampUtc >= cutoffUtc)
                        continue;

                    File.Delete(filePath);
                    _processedFileNames.Remove(fileName);
                }
            }
            catch (Exception ex) when (IsRecoverableIoFailure(ex))
            {
                System.Diagnostics.Debug.WriteLine($"FileSyncProvider: prune pass failed: {ex.Message}");
            }
        }

        // Mirrors SettingsService.TryCopyFileWithRetriesAsync's retry shape for transient
        // IO failures (e.g. a cloud-sync client briefly locking the folder).
        private static async Task WriteFileWithRetriesAsync(string path, string contents, int maxAttempts = 3)
        {
            var attempt = 0;
            Exception? lastEx = null;
            while (attempt < maxAttempts)
            {
                try
                {
                    await File.WriteAllTextAsync(path, contents);
                    return;
                }
                catch (Exception ex) when (IsRecoverableIoFailure(ex))
                {
                    lastEx = ex;
                    attempt++;
                    await Task.Delay(250);
                }
            }

            throw lastEx ?? new IOException("Unknown write failure");
        }

        private static bool IsRecoverableIoFailure(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException;

        private class SyncFileChangeEntry
        {
            public string EntityType { get; set; } = string.Empty;
            public Guid EntityId { get; set; }
            public string Operation { get; set; } = string.Empty;
            public string? PayloadJson { get; set; }
            public int Version { get; set; }
            public string? OriginDeviceId { get; set; }
            public DateTime CreatedAt { get; set; }
        }

        private class VersionEnvelope
        {
            public int Version { get; set; }
            public string? OriginDeviceId { get; set; }
        }
    }
}
