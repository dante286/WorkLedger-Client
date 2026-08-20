using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using WorkLedger.Contracts.Auth;
using WorkLedger.Contracts.Sync;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Sync
{
    // Sync against a WorkLedger-API instance. Plan 6.
    //
    // The wire types come from WorkLedger.Contracts (vendored under Sync/Contracts) rather
    // than being redefined here - that shared compilation is the entire reason the server
    // is C#. The rules this implements are the server's, not choices made here; see the
    // "Server contract (normative)" section of plans/06-api-sync-provider.md, which mirrors
    // WorkLedger-API/plans/S3-sync-endpoints.md. Two of them decide the shape of this class:
    //
    //   * Push results are POSITIONAL. SyncPushResponse.Results holds exactly one entry per
    //     input change, in request order. SyncItemResult.EntityId is not a usable key - one
    //     batch legitimately carries several queue rows for the same entity (create ->
    //     update -> delete), so a Guid maps to N rows, not one. See WL-94.
    //   * Accepted = false is FINAL. The server rejects either for a validation failure or
    //     because its own version won, and neither changes on a re-push. Those rows are
    //     retired, not retried.
    //
    // Two server behaviours look like bugs from here and are not:
    //   * An accepted delete comes back with a HIGHER AuthoritativeVersion than was pushed.
    //     The server stores tombstones at existing.Version + 1 so they beat every peer's
    //     copy - an accommodation for WL-93. Not a rejection.
    //   * An accepted change may not have been written at all. A same-or-lower version from
    //     the device that authored the server's row is a replay/self-echo/lost ack, and the
    //     server acks it without writing. "Accepted" never means "the server's row now
    //     equals what I sent".
    public class ApiSyncProvider : ISyncProvider
    {
        private const string BootstrapSecretHeader = "X-Bootstrap-Secret";

        // Re-bootstrap this far ahead of expiry rather than waiting to be told no. A token
        // that dies mid-batch still works - the 401 path below catches it - but only at the
        // cost of a wasted round trip on a request that was going to fail.
        private static readonly TimeSpan TokenRenewalMargin = TimeSpan.FromMinutes(5);

        private readonly HttpClient _http;
        private readonly Uri _baseUri;
        private readonly string _deviceId;
        private readonly IApiTokenStore _tokens;
        private readonly Func<string?> _bootstrapSecretAccessor;
        private readonly string? _deviceLabel;

        // HttpClient is injected, never constructed here: one socket-exhausting instance per
        // provider is the classic way to break an app that syncs on a timer.
        //
        // The bootstrap secret arrives through an accessor rather than a stored string
        // because it is operator-supplied, entered once, and deliberately not persisted
        // alongside the token it mints - a caller that holds it only for the duration of a
        // setup dialog can hand over a closure that returns null afterwards.
        public ApiSyncProvider(
            HttpClient httpClient,
            string baseUrl,
            string deviceId,
            IApiTokenStore tokenStore,
            Func<string?>? bootstrapSecretAccessor = null,
            string? deviceLabel = null)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new ArgumentException("An API endpoint is required.", nameof(baseUrl));

            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
            _deviceId = deviceId;
            _tokens = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
            _bootstrapSecretAccessor = bootstrapSecretAccessor ?? (() => null);
            _deviceLabel = deviceLabel;
        }

        public async Task<SyncResult> PushAsync(IEnumerable<SyncQueue> pendingChanges)
        {
            // Order is the join key, so it's fixed here and never re-sorted. The server
            // re-orders internally to apply the batch but answers in request order.
            var changes = pendingChanges.ToList();
            if (changes.Count == 0)
                return new SyncResult();

            try
            {
                var request = new SyncPushRequest(changes.Select(ToChangeDto).ToList());
                var response = await SendForJsonAsync<SyncPushResponse>(
                    () => BuildJsonRequest(HttpMethod.Post, "sync/push", request));
                return MapPushResults(changes, response);
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider push failed: {ex.Message}");

                // Transport-level failure: nothing was decided, so every row stays pending
                // and is retried on the next tick. Note these go to Failures only - putting
                // them in RejectedSyncQueueIds would discard changes the server never saw.
                return new SyncResult
                {
                    Failures = changes
                        .Select(change => new SyncFailure { SyncQueueId = change.Id, ErrorMessage = ex.Message })
                        .ToList()
                };
            }
        }

        public async Task<PullResult> PullAsync(DateTime since)
        {
            try
            {
                // "o" round-trips unambiguously. The server normalizes whatever it gets to
                // UTC (its columns are timestamptz) and treats DateTime.MinValue as a
                // legitimate request for everything.
                var path = $"sync/pull?since={Uri.EscapeDataString(since.ToString("o"))}";
                var response = await SendForJsonAsync<SyncPullResponse>(
                    () => new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, path)));

                return new PullResult
                {
                    Changes = response.Changes.Select(ToRemoteChange).ToList(),
                    ServerTime = response.ServerTime
                };
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider pull failed: {ex.Message}");
                return PullResult.Failed(ex.Message);
            }
        }

        // Cheap liveness/credential check - useful to a settings screen wanting to say
        // "connected" without pushing anything. Kept here rather than in the UI so the
        // 401-and-re-bootstrap path is shared with the real calls.
        public async Task<WhoAmIResponse?> TryWhoAmIAsync()
        {
            try
            {
                return await SendForJsonAsync<WhoAmIResponse>(
                    () => new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "auth/whoami")));
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider whoami failed: {ex.Message}");
                return null;
            }
        }

        // Registers this device and stores the JWT it gets back. Idempotent server-side, so
        // "I lost my token" is a supported, non-destructive path rather than a reinstall.
        // Returns false rather than throwing when there is no secret to bootstrap with -
        // that's a configuration state, not a failure.
        public async Task<bool> TryBootstrapAsync()
        {
            var secret = _bootstrapSecretAccessor();
            if (string.IsNullOrWhiteSpace(secret))
                return false;

            try
            {
                var registered = await SendOnceForJsonAsync<RegisterDeviceResponse>(() =>
                {
                    var request = BuildJsonRequest(
                        HttpMethod.Post, "auth/register-device", new RegisterDeviceRequest(_deviceId, _deviceLabel));
                    request.Headers.TryAddWithoutValidation(BootstrapSecretHeader, secret);
                    return request;
                });

                _tokens.Save(registered.Token, registered.ExpiresAtUtc);
                return true;
            }
            catch (SyncProviderException ex)
            {
                Debug.WriteLine($"ApiSyncProvider bootstrap failed: {ex.Message}");
                return false;
            }
        }

        private static SyncChangeDto ToChangeDto(SyncQueue queueEntry)
        {
            // SyncQueue carries no Version/OriginDeviceId of its own - both live on the
            // serialized entity inside PayloadJson. Lifting them to the top level is the
            // same move FileSyncProvider.ToWireEntry makes, for the same reason: a receiver
            // can then run last-write-wins without deserializing per entity type.
            var (version, originDeviceId) = ExtractVersionMetadata(queueEntry.PayloadJson);

            return new SyncChangeDto(
                EntityType: queueEntry.EntityType ?? string.Empty,
                // Already the entity's SyncId, not the local autoincrement Id - see WL-47.
                EntityId: queueEntry.EntityId,
                Operation: queueEntry.Operation ?? string.Empty,
                PayloadJson: queueEntry.PayloadJson,
                Version: version,
                OriginDeviceId: originDeviceId ?? string.Empty,
                CreatedAt: queueEntry.CreatedAt);
        }

        private static RemoteChange ToRemoteChange(SyncChangeDto dto) => new()
        {
            EntityType = dto.EntityType,
            EntityId = dto.EntityId,
            Operation = dto.Operation,
            PayloadJson = dto.PayloadJson,
            Version = dto.Version,
            OriginDeviceId = dto.OriginDeviceId
        };

        private static (int Version, string? OriginDeviceId) ExtractVersionMetadata(string? payloadJson)
        {
            if (string.IsNullOrEmpty(payloadJson))
                return (0, null);

            try
            {
                var envelope = JsonSerializer.Deserialize<VersionEnvelope>(payloadJson, SyncJson.Options);
                return (envelope?.Version ?? 0, envelope?.OriginDeviceId);
            }
            catch (JsonException)
            {
                return (0, null);
            }
        }

        // WL-94: zip request and response by index. The response is guaranteed one entry per
        // input change in request order, so a count mismatch means the contract is broken
        // and there is no safe way to guess the alignment - fail the whole batch rather than
        // retire the wrong rows.
        private static SyncResult MapPushResults(List<SyncQueue> changes, SyncPushResponse response)
        {
            if (response.Results.Count != changes.Count)
            {
                throw new SyncProviderException(
                    $"Server returned {response.Results.Count} push results for {changes.Count} changes; " +
                    "results must be positional and one-per-change.");
            }

            var accepted = new List<int>();
            var rejected = new List<int>();
            var failures = new List<SyncFailure>();
            var serverWon = false;

            for (var i = 0; i < changes.Count; i++)
            {
                var queueEntry = changes[i];
                var result = response.Results[i];

                if (result.Accepted)
                {
                    // Note there is no check that AuthoritativeVersion equals what we sent.
                    // An accepted delete legitimately comes back higher (the tombstone bump),
                    // and an accepted replay comes back at the version already stored.
                    accepted.Add(queueEntry.Id);
                    continue;
                }

                rejected.Add(queueEntry.Id);
                failures.Add(new SyncFailure
                {
                    SyncQueueId = queueEntry.Id,
                    ErrorMessage = result.Reason ?? "Rejected by server."
                });

                // An authoritative payload is how the server distinguishes "my row won" from
                // "your payload was invalid" - it only fills one in for the former.
                //
                // result.AuthoritativeOperation (WL-98) names what that payload is - "update"
                // or "delete" - which is everything needed to adopt it here and skip the
                // re-pull. Deliberately not read yet - that is WL-101; see SyncResult.RequiresFullPull.
                if (!string.IsNullOrEmpty(result.AuthoritativePayloadJson))
                    serverWon = true;
            }

            return new SyncResult
            {
                AcceptedSyncQueueIds = accepted,
                RejectedSyncQueueIds = rejected,
                RequiresFullPull = serverWon,
                Failures = failures
            };
        }

        private HttpRequestMessage BuildJsonRequest(HttpMethod method, string relativePath, object body)
        {
            var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath))
            {
                // SyncJson.Options for the envelope too: it writes PascalCase, which the
                // server's own case-insensitive reader accepts, and reads case-insensitively,
                // so ASP.NET Core's camelCase responses parse. One policy, both directions.
                Content = new StringContent(
                    JsonSerializer.Serialize(body, body.GetType(), SyncJson.Options),
                    Encoding.UTF8,
                    "application/json")
            };
            return request;
        }

        // Sends with the bearer token attached, renewing it first if it's missing or about
        // to expire, and re-bootstrapping exactly once if the server rejects it anyway.
        private async Task<T> SendForJsonAsync<T>(Func<HttpRequestMessage> requestFactory)
        {
            if (NeedsToken())
                await TryBootstrapAsync();

            try
            {
                return await SendOnceForJsonAsync<T>(requestFactory, authorize: true);
            }
            catch (SyncProviderException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The token was rejected. Re-registering is idempotent server-side, so this
                // is safe to do mid-sync. Exactly one retry - a second 401 means something
                // is wrong that retrying won't fix.
                if (!await TryBootstrapAsync())
                    throw;

                return await SendOnceForJsonAsync<T>(requestFactory, authorize: true);
            }
        }

        // One attempt, no renewal. A fresh HttpRequestMessage per call because a message
        // that has been sent cannot be sent again - hence the factory rather than an
        // instance parameter.
        private async Task<T> SendOnceForJsonAsync<T>(Func<HttpRequestMessage> requestFactory, bool authorize = false)
        {
            using var request = requestFactory();

            if (authorize)
            {
                var token = _tokens.Token;
                if (string.IsNullOrWhiteSpace(token))
                    throw new SyncProviderException("No API token available; register this device first.", HttpStatusCode.Unauthorized);

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request);
            }
            catch (HttpRequestException ex)
            {
                // Unreachable host, DNS failure, refused connection, TLS failure.
                throw new SyncProviderException($"Could not reach the sync server: {ex.Message}", innerException: ex);
            }
            catch (TaskCanceledException ex)
            {
                // HttpClient surfaces its own timeout as a cancellation.
                throw new SyncProviderException("The sync server did not respond in time.", innerException: ex);
            }

            using (response)
            {
                var body = await ReadBodyAsync(response);

                if (!response.IsSuccessStatusCode)
                {
                    throw new SyncProviderException(
                        $"Sync server returned {(int)response.StatusCode} {response.ReasonPhrase}." +
                        (string.IsNullOrWhiteSpace(body) ? string.Empty : $" {Truncate(body)}"),
                        response.StatusCode);
                }

                try
                {
                    var parsed = JsonSerializer.Deserialize<T>(body, SyncJson.Options);
                    if (parsed is null)
                        throw new SyncProviderException("Sync server returned an empty response body.", response.StatusCode);

                    return parsed;
                }
                catch (JsonException ex)
                {
                    throw new SyncProviderException(
                        $"Sync server returned a response that could not be parsed: {ex.Message}",
                        response.StatusCode,
                        ex);
                }
            }
        }

        private static async Task<string> ReadBodyAsync(HttpResponseMessage response)
        {
            try
            {
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
                // A connection that dies mid-body is still just an unreachable server.
                throw new SyncProviderException($"Could not read the sync server's response: {ex.Message}", response.StatusCode, ex);
            }
        }

        private bool NeedsToken()
        {
            if (string.IsNullOrWhiteSpace(_tokens.Token))
                return true;

            var expiry = _tokens.ExpiresAtUtc;
            return expiry.HasValue && expiry.Value - TokenRenewalMargin <= DateTime.UtcNow;
        }

        // Server error bodies land in user-facing toasts (Plan 7); an HTML error page would
        // otherwise arrive in full.
        private static string Truncate(string value, int maxLength = 200)
            => value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";

        private class VersionEnvelope
        {
            public int Version { get; set; }
            public string? OriginDeviceId { get; set; }
        }
    }
}
