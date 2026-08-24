using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WorkLedger.Contracts.Sync;
using WorkLedger_Client.Models;
using WorkLedger_Client.Sync;
using Xunit;

namespace WorkLedger.Tests
{
    // Every test here runs against a mocked HttpMessageHandler - no real network, no server.
    // The contract these assert against is WorkLedger-API's S3 (WL-76); see the "Server
    // contract (normative)" section of plans/06-api-sync-provider.md.
    public class ApiSyncProviderTests
    {
        private const string BaseUrl = "https://workledger.example.com";
        private const string DeviceId = "device-under-test";
        private const string BootstrapSecret = "operator-supplied-secret";

        // Records what was sent and replays a queued script of responses, so a test can set
        // up a 401-then-200 sequence without a stub framework.
        public class RecordingHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
            private readonly Func<HttpRequestMessage, HttpResponseMessage>? _fallback;

            public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? fallback = null)
                => _fallback = fallback;

            public List<RecordedRequest> Requests { get; } = new();

            public RecordingHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
            {
                _responders.Enqueue(responder);
                return this;
            }

            public RecordingHandler EnqueueJson(HttpStatusCode status, object body)
                => Enqueue(_ => Json(status, JsonSerializer.Serialize(body, body.GetType(), SyncJson.Options)));

            public RecordingHandler EnqueueRaw(HttpStatusCode status, string body)
                => Enqueue(_ => Json(status, body));

            public RecordingHandler EnqueueThrow(Exception exception)
                => Enqueue(_ => throw exception);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // Read the body here: the request is disposed once the provider returns.
                Requests.Add(new RecordedRequest(
                    request.Method,
                    request.RequestUri!,
                    request.Headers.Authorization?.Parameter,
                    request.Headers.TryGetValues("X-Bootstrap-Secret", out var secret) ? secret.FirstOrDefault() : null,
                    request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult()));

                var responder = _responders.Count > 0 ? _responders.Dequeue() : _fallback;
                if (responder is null)
                    throw new InvalidOperationException($"No response scripted for {request.Method} {request.RequestUri}");

                return Task.FromResult(responder(request));
            }

            private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        public record RecordedRequest(HttpMethod Method, Uri Uri, string? BearerToken, string? BootstrapSecret, string? Body);

        private class FakeTokenStore : IApiTokenStore
        {
            public FakeTokenStore(string? token = null, DateTime? expiresAtUtc = null)
            {
                Token = token;
                ExpiresAtUtc = expiresAtUtc;
            }

            public string? Token { get; private set; }
            public DateTime? ExpiresAtUtc { get; private set; }
            public int SaveCount { get; private set; }

            public void Save(string token, DateTime expiresAtUtc)
            {
                Token = token;
                ExpiresAtUtc = expiresAtUtc;
                SaveCount++;
            }
        }

        private static ApiSyncProvider CreateProvider(
            RecordingHandler handler,
            IApiTokenStore? tokens = null,
            string? bootstrapSecret = BootstrapSecret)
            => new(
                new HttpClient(handler),
                BaseUrl,
                DeviceId,
                tokens ?? new FakeTokenStore("existing-token", DateTime.UtcNow.AddDays(30)),
                () => bootstrapSecret);

        private static SyncQueue QueueEntry(int id, string entityType, Guid entityId, string operation, int version)
            => new()
            {
                Id = id,
                EntityType = entityType,
                EntityId = entityId,
                Operation = operation,
                // Version/OriginDeviceId live inside the payload, never on SyncQueue itself -
                // lifting them to the wire's top level is the provider's job.
                PayloadJson = JsonSerializer.Serialize(
                    new { Version = version, OriginDeviceId = DeviceId, Name = "Payload" }, SyncJson.Options),
                CreatedAt = DateTime.UtcNow
            };

        private static object PushResponse(params object[] results)
            => new { Results = results, ServerTime = DateTime.UtcNow };

        private static object ItemResult(Guid entityId, bool accepted, int version, string? payload = null, string? reason = null)
            => new
            {
                EntityId = entityId,
                Accepted = accepted,
                AuthoritativeVersion = version,
                AuthoritativePayloadJson = payload,
                Reason = reason
            };

        // The same result with WL-98's AuthoritativeOperation attached, as the live server sends it.
        private static object ItemResultWithOperation(
            Guid entityId, bool accepted, int version, string? payload, string? reason, string? operation)
            => new
            {
                EntityId = entityId,
                Accepted = accepted,
                AuthoritativeVersion = version,
                AuthoritativePayloadJson = payload,
                Reason = reason,
                AuthoritativeOperation = operation
            };

        [Fact]
        public async Task PushAsync_SendsTheExpectedRequest_AndAcceptsTheServersResults()
        {
            var entityId = Guid.NewGuid();
            var handler = new RecordingHandler().EnqueueJson(
                HttpStatusCode.OK, PushResponse(ItemResult(entityId, accepted: true, version: 3)));
            var provider = CreateProvider(handler);

            var result = await provider.PushAsync(new[] { QueueEntry(11, "project", entityId, "create", 3) });

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://workledger.example.com/sync/push", request.Uri.ToString());
            Assert.Equal("existing-token", request.BearerToken);

            var sent = JsonSerializer.Deserialize<SyncPushRequest>(request.Body!, SyncJson.Options)!;
            var change = Assert.Single(sent.Changes);
            Assert.Equal("project", change.EntityType);
            Assert.Equal(entityId, change.EntityId);
            Assert.Equal("create", change.Operation);
            // Lifted out of PayloadJson - the server reads these at the top level and never
            // digs into the payload for them.
            Assert.Equal(3, change.Version);
            Assert.Equal(DeviceId, change.OriginDeviceId);

            Assert.Equal(new[] { 11 }, result.AcceptedSyncQueueIds);
            Assert.Empty(result.RejectedSyncQueueIds);
            Assert.False(result.RequiresFullPull);
        }

        [Fact]
        public async Task PushAsync_WithNothingPending_MakesNoRequest()
        {
            var handler = new RecordingHandler();
            var provider = CreateProvider(handler);

            var result = await provider.PushAsync(Array.Empty<SyncQueue>());

            Assert.Empty(handler.Requests);
            Assert.Empty(result.AcceptedSyncQueueIds);
        }

        // WL-94, the regression this whole mapping exists for. One batch, three queue rows,
        // ONE entity - so SyncItemResult.EntityId cannot disambiguate them and only the index
        // can. The middle result is the rejection, and it must land on queue row 21.
        [Fact]
        public async Task PushAsync_MapsResultsPositionally_WhenOneBatchHoldsSeveralChangesForOneEntity()
        {
            var entityId = Guid.NewGuid();
            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.OK, PushResponse(
                ItemResult(entityId, accepted: true, version: 1),
                ItemResult(entityId, accepted: false, version: 9, reason: "Version must be at least 1."),
                ItemResult(entityId, accepted: true, version: 3)));
            var provider = CreateProvider(handler);

            var result = await provider.PushAsync(new[]
            {
                QueueEntry(20, "project", entityId, "create", 1),
                QueueEntry(21, "project", entityId, "update", 2),
                QueueEntry(22, "project", entityId, "delete", 3)
            });

            Assert.Equal(new[] { 20, 22 }, result.AcceptedSyncQueueIds);
            Assert.Equal(new[] { 21 }, result.RejectedSyncQueueIds);
            Assert.Equal(21, Assert.Single(result.Failures).SyncQueueId);
        }

        // The server answers in request order even though it applies the batch in a different
        // one (projects -> tasks -> time entries), so the provider must not re-sort either.
        [Fact]
        public async Task PushAsync_PreservesQueueOrder_EvenWhenTypesAreInterleaved()
        {
            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.OK, PushResponse(
                ItemResult(Guid.NewGuid(), accepted: true, version: 1),
                ItemResult(Guid.NewGuid(), accepted: true, version: 1),
                ItemResult(Guid.NewGuid(), accepted: true, version: 1)));
            var provider = CreateProvider(handler);

            await provider.PushAsync(new[]
            {
                QueueEntry(1, "timeentry", Guid.NewGuid(), "create", 1),
                QueueEntry(2, "project", Guid.NewGuid(), "create", 1),
                QueueEntry(3, "task", Guid.NewGuid(), "create", 1)
            });

            var sent = JsonSerializer.Deserialize<SyncPushRequest>(handler.Requests.Single().Body!, SyncJson.Options)!;
            Assert.Equal(new[] { "timeentry", "project", "task" }, sent.Changes.Select(c => c.EntityType));
        }

        // The server stores tombstones at existing.Version + 1 so they beat every peer's copy
        // (its accommodation for WL-93). Treating "came back higher than I sent" as a
        // rejection would leave the delete queued and re-pushed forever.
        [Fact]
        public async Task PushAsync_AcceptedDeleteComingBackAtAHigherVersion_IsStillAccepted()
        {
            var entityId = Guid.NewGuid();
            var handler = new RecordingHandler().EnqueueJson(
                HttpStatusCode.OK, PushResponse(ItemResult(entityId, accepted: true, version: 8)));
            var provider = CreateProvider(handler);

            var result = await provider.PushAsync(new[] { QueueEntry(30, "project", entityId, "delete", 5) });

            Assert.Equal(new[] { 30 }, result.AcceptedSyncQueueIds);
            Assert.Empty(result.Failures);
        }

        // WL-101: a rejection carrying an authoritative payload means the server's row won, and
        // AuthoritativeOperation says what that payload is. Both cases are asserted separately
        // because "delete" is the one this provider must never quietly turn into an update: the
        // adopted change has to still be a delete when it reaches the merge.
        [Theory]
        [InlineData("update")]
        [InlineData("delete")]
        public async Task PushAsync_ServerWinsStatingTheOperation_AdoptsTheWinnerInsteadOfRePulling(string operation)
        {
            var entityId = Guid.NewGuid();
            var payload = JsonSerializer.Serialize(
                new { Version = 9, OriginDeviceId = "other-device", Name = "Winner" }, SyncJson.Options);
            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.OK, PushResponse(
                ItemResultWithOperation(
                    entityId, accepted: false, version: 9, payload: payload,
                    reason: "Server version wins.", operation: operation)));
            var provider = CreateProvider(handler);

            var result = await provider.PushAsync(new[] { QueueEntry(41, "project", entityId, "update", 4) });

            // Retiring the row is unchanged - Accepted = false is final either way (WL-94).
            Assert.Empty(result.AcceptedSyncQueueIds);
            Assert.Equal(new[] { 41 }, result.RejectedSyncQueueIds);
            Assert.False(result.RequiresFullPull);

            var adopted = Assert.Single(result.AuthoritativeChanges);
            Assert.Equal(operation, adopted.Operation);
            // Taken from the queue row we pushed, since results are positional and
            // SyncItemResult.EntityId is not a usable key (WL-94).
            Assert.Equal("project", adopted.EntityType);
            Assert.Equal(entityId, adopted.EntityId);
            Assert.Equal(payload, adopted.PayloadJson);
            // AuthoritativeVersion, not the payload's - for a tombstone the two differ (WL-93).
            Assert.Equal(9, adopted.Version);
            // Lifted out of the payload, so the merge can still recognise a self-echo.
            Assert.Equal("other-device", adopted.OriginDeviceId);
        }

        // The fallback path. A server predating WL-98 sends the winning payload with no operation,
        // and the payload alone cannot be told apart from a tombstone's - so the provider asks for
        // the cursor rewind rather than guessing. See SyncResult.RequiresFullPull.
        [Fact]
        public async Task PushAsync_ServerWinsWithoutStatingTheOperation_FallsBackToAFullPull()
        {
            var entityId = Guid.NewGuid();
            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.OK, PushResponse(
                ItemResult(entityId, accepted: false, version: 7, payload: "{\"Version\":7}", reason: "Server version wins.")));
            var provider = CreateProvider(handler);

            var result = await provider.PushAsync(new[] { QueueEntry(40, "project", entityId, "update", 4) });

            Assert.Empty(result.AcceptedSyncQueueIds);
            Assert.Equal(new[] { 40 }, result.RejectedSyncQueueIds);
            Assert.True(result.RequiresFullPull);
            Assert.Empty(result.AuthoritativeChanges);
        }

        // An operation this client doesn't recognize is the same situation as none at all - the
        // payload is not self-describing to *us*, whatever the server meant by it - so it takes
        // the same safe fallback rather than being adopted as an upsert.
        [Fact]
        public async Task PushAsync_ServerWinsStatingAnUnknownOperation_FallsBackToAFullPull()
        {
            var entityId = Guid.NewGuid();
            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.OK, PushResponse(
                ItemResultWithOperation(
                    entityId, accepted: false, version: 9, payload: "{\"Version\":9}",
                    reason: "Server version wins.", operation: "archive")));
            var provider = CreateProvider(handler);

            var result = await provider.PushAsync(new[] { QueueEntry(42, "project", entityId, "update", 4) });

            Assert.Equal(new[] { 42 }, result.RejectedSyncQueueIds);
            Assert.True(result.RequiresFullPull);
            Assert.Empty(result.AuthoritativeChanges);
        }

        // A validation failure carries no authoritative payload - the row is retired, but
        // there's nothing newer on the server to go and fetch.
        [Fact]
        public async Task PushAsync_WhenTheServerRejectsAsInvalid_RetiresTheRowWithoutRequestingAFullPull()
        {
            var entityId = Guid.NewGuid();
            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.OK, PushResponse(
                ItemResult(entityId, accepted: false, version: 0, reason: "EntityId (the entity's SyncId) is required.")));
            var provider = CreateProvider(handler);

            var result = await provider.PushAsync(new[] { QueueEntry(41, "task", entityId, "create", 1) });

            Assert.Equal(new[] { 41 }, result.RejectedSyncQueueIds);
            Assert.False(result.RequiresFullPull);
            Assert.Empty(result.AuthoritativeChanges);
            Assert.Equal("EntityId (the entity's SyncId) is required.", Assert.Single(result.Failures).ErrorMessage);
        }

        // A result count that doesn't match the request breaks the positional contract, and
        // there's no safe way to guess the alignment - so nothing is retired.
        [Fact]
        public async Task PushAsync_WhenResultCountDoesNotMatch_FailsTheBatchWithoutRetiringAnything()
        {
            var handler = new RecordingHandler().EnqueueJson(
                HttpStatusCode.OK, PushResponse(ItemResult(Guid.NewGuid(), accepted: true, version: 1)));
            var provider = CreateProvider(handler);

            var result = await provider.PushAsync(new[]
            {
                QueueEntry(50, "project", Guid.NewGuid(), "create", 1),
                QueueEntry(51, "project", Guid.NewGuid(), "create", 1)
            });

            Assert.Empty(result.AcceptedSyncQueueIds);
            Assert.Empty(result.RejectedSyncQueueIds);
            Assert.Equal(2, result.Failures.Count);
        }

        [Fact]
        public async Task PullAsync_RequestsTheCursorAsIso8601_AndSurfacesServerTime()
        {
            var entityId = Guid.NewGuid();
            var serverTime = new DateTime(2026, 8, 11, 12, 30, 0, DateTimeKind.Utc);
            var since = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);

            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.OK, new
            {
                Changes = new[]
                {
                    new
                    {
                        EntityType = "project",
                        EntityId = entityId,
                        Operation = "update",
                        PayloadJson = "{\"Name\":\"Remote\"}",
                        Version = 6,
                        OriginDeviceId = "other-device",
                        CreatedAt = DateTime.UtcNow
                    }
                },
                ServerTime = serverTime
            });
            var provider = CreateProvider(handler);

            var result = await provider.PullAsync(since);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith("https://workledger.example.com/sync/pull?since=", request.Uri.ToString());
            Assert.Contains(Uri.EscapeDataString(since.ToString("o")), request.Uri.ToString());

            Assert.True(result.Succeeded);
            Assert.Equal(serverTime, result.ServerTime);

            var change = Assert.Single(result.Changes);
            Assert.Equal("project", change.EntityType);
            Assert.Equal(entityId, change.EntityId);
            Assert.Equal(6, change.Version);
            Assert.Equal("other-device", change.OriginDeviceId);
        }

        // ASP.NET Core serializes the response envelope with web defaults (camelCase), so the
        // provider has to read case-insensitively or every field silently comes back default.
        [Fact]
        public async Task PullAsync_ParsesACamelCaseEnvelope_AsTheServerActuallySendsIt()
        {
            var serverTime = new DateTime(2026, 8, 11, 12, 30, 0, DateTimeKind.Utc);
            var handler = new RecordingHandler().EnqueueRaw(HttpStatusCode.OK, $$"""
                {"changes":[{"entityType":"task","entityId":"{{Guid.Empty}}","operation":"delete",
                "payloadJson":null,"version":4,"originDeviceId":"other-device","createdAt":"2026-08-11T00:00:00Z"}],
                "serverTime":"{{serverTime:o}}"}
                """);
            var provider = CreateProvider(handler);

            var result = await provider.PullAsync(DateTime.MinValue);

            Assert.True(result.Succeeded);
            Assert.Equal(serverTime, result.ServerTime);
            var change = Assert.Single(result.Changes);
            Assert.Equal("task", change.EntityType);
            Assert.Equal("delete", change.Operation);
            Assert.Equal(4, change.Version);
        }

        [Fact]
        public async Task Bootstrap_StoresTheIssuedTokenAndExpiry()
        {
            var expiresAt = DateTime.UtcNow.AddDays(365);
            var tokens = new FakeTokenStore();
            var handler = new RecordingHandler().EnqueueJson(
                HttpStatusCode.OK, new { Token = "freshly-issued", ExpiresAtUtc = expiresAt });
            var provider = CreateProvider(handler, tokens);

            var bootstrapped = await provider.TryBootstrapAsync();

            Assert.True(bootstrapped);
            Assert.Equal("freshly-issued", tokens.Token);
            Assert.Equal(expiresAt, tokens.ExpiresAtUtc);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://workledger.example.com/auth/register-device", request.Uri.ToString());
            Assert.Equal(BootstrapSecret, request.BootstrapSecret);
            // Anonymous endpoint - sending a bearer here would be meaningless.
            Assert.Null(request.BearerToken);
            Assert.Contains(DeviceId, request.Body);
        }

        [Fact]
        public async Task Bootstrap_WithNoSecretConfigured_FailsQuietlyWithoutCallingTheServer()
        {
            var handler = new RecordingHandler();
            var provider = CreateProvider(handler, bootstrapSecret: null);

            Assert.False(await provider.TryBootstrapAsync());
            Assert.Empty(handler.Requests);
        }

        // Re-registration is idempotent server-side, so recovering from a rejected token
        // mid-sync is safe. Exactly one retry: a second 401 won't be fixed by a third try.
        [Fact]
        public async Task PullAsync_OnA401_ReBootstrapsOnceAndRetriesTheCall()
        {
            var tokens = new FakeTokenStore("stale-token", DateTime.UtcNow.AddDays(30));
            var handler = new RecordingHandler()
                .EnqueueRaw(HttpStatusCode.Unauthorized, "{}")
                .EnqueueJson(HttpStatusCode.OK, new { Token = "renewed-token", ExpiresAtUtc = DateTime.UtcNow.AddDays(365) })
                .EnqueueJson(HttpStatusCode.OK, new { Changes = Array.Empty<object>(), ServerTime = DateTime.UtcNow });
            var provider = CreateProvider(handler, tokens);

            var result = await provider.PullAsync(DateTime.MinValue);

            Assert.True(result.Succeeded);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal("stale-token", handler.Requests[0].BearerToken);
            Assert.Equal("/auth/register-device", handler.Requests[1].Uri.AbsolutePath);
            Assert.Equal("renewed-token", handler.Requests[2].BearerToken);
            Assert.Equal(1, tokens.SaveCount);
        }

        [Fact]
        public async Task PullAsync_WhenTheSecond401CannotBeRecovered_FailsCleanly()
        {
            var tokens = new FakeTokenStore("stale-token", DateTime.UtcNow.AddDays(30));
            var handler = new RecordingHandler()
                .EnqueueRaw(HttpStatusCode.Unauthorized, "{}")
                .EnqueueJson(HttpStatusCode.OK, new { Token = "renewed-token", ExpiresAtUtc = DateTime.UtcNow.AddDays(365) })
                .EnqueueRaw(HttpStatusCode.Unauthorized, "{}");
            var provider = CreateProvider(handler, tokens);

            var result = await provider.PullAsync(DateTime.MinValue);

            Assert.False(result.Succeeded);
            Assert.Contains("401", result.ErrorMessage);
            // Three calls, not four - the retry is not itself retried.
            Assert.Equal(3, handler.Requests.Count);
        }

        // A token that is about to lapse is renewed before the call rather than after being
        // told no, so a long sync doesn't die halfway through.
        [Fact]
        public async Task PullAsync_WithAnAlmostExpiredToken_RenewsBeforeCalling()
        {
            var tokens = new FakeTokenStore("about-to-lapse", DateTime.UtcNow.AddMinutes(1));
            var handler = new RecordingHandler()
                .EnqueueJson(HttpStatusCode.OK, new { Token = "renewed-token", ExpiresAtUtc = DateTime.UtcNow.AddDays(365) })
                .EnqueueJson(HttpStatusCode.OK, new { Changes = Array.Empty<object>(), ServerTime = DateTime.UtcNow });
            var provider = CreateProvider(handler, tokens);

            Assert.True((await provider.PullAsync(DateTime.MinValue)).Succeeded);

            Assert.Equal("/auth/register-device", handler.Requests[0].Uri.AbsolutePath);
            Assert.Equal("renewed-token", handler.Requests[1].BearerToken);
        }

        [Fact]
        public async Task PushAsync_WithNoTokenAndNoBootstrapSecret_FailsWithoutThrowing()
        {
            var handler = new RecordingHandler();
            var provider = CreateProvider(handler, new FakeTokenStore(), bootstrapSecret: null);

            var result = await provider.PushAsync(new[] { QueueEntry(60, "project", Guid.NewGuid(), "create", 1) });

            Assert.Empty(handler.Requests);
            Assert.Empty(result.AcceptedSyncQueueIds);
            // Retryable, not retired: the server never saw this change.
            Assert.Empty(result.RejectedSyncQueueIds);
            Assert.Equal(60, Assert.Single(result.Failures).SyncQueueId);
        }

        // The failure-mode matrix. SyncMode = Api pointed at a dead server must not take the
        // app down, so each of these has to come back as a structured failure rather than an
        // escaping HttpRequestException/TaskCanceledException.
        public static TheoryData<string, Func<RecordingHandler>> FailureModes() => new()
        {
            { "unreachable host", () => new RecordingHandler().EnqueueThrow(new HttpRequestException("No such host is known.")) },
            { "timeout", () => new RecordingHandler().EnqueueThrow(new TaskCanceledException("The request timed out.")) },
            { "server error", () => new RecordingHandler().EnqueueRaw(HttpStatusCode.InternalServerError, "<html>oops</html>") },
            { "not found", () => new RecordingHandler().EnqueueRaw(HttpStatusCode.NotFound, "{}") },
            { "malformed json", () => new RecordingHandler().EnqueueRaw(HttpStatusCode.OK, "{ this is not json") },
            { "empty body", () => new RecordingHandler().EnqueueRaw(HttpStatusCode.OK, "null") }
        };

        [Theory]
        [MemberData(nameof(FailureModes))]
        public async Task PushAsync_EveryFailureMode_BecomesAStructuredFailure(string _, Func<RecordingHandler> makeHandler)
        {
            var provider = CreateProvider(makeHandler());

            var result = await provider.PushAsync(new[] { QueueEntry(70, "project", Guid.NewGuid(), "create", 1) });

            Assert.Empty(result.AcceptedSyncQueueIds);
            // Nothing was decided, so the row must stay queued for the next attempt.
            Assert.Empty(result.RejectedSyncQueueIds);
            var failure = Assert.Single(result.Failures);
            Assert.Equal(70, failure.SyncQueueId);
            Assert.False(string.IsNullOrWhiteSpace(failure.ErrorMessage));
        }

        [Theory]
        [MemberData(nameof(FailureModes))]
        public async Task PullAsync_EveryFailureMode_BecomesAStructuredFailure(string _, Func<RecordingHandler> makeHandler)
        {
            var provider = CreateProvider(makeHandler());

            var result = await provider.PullAsync(DateTime.MinValue);

            Assert.False(result.Succeeded);
            Assert.Empty(result.Changes);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        // A 500's body can be a full HTML error page, and this message ends up in a toast.
        [Fact]
        public async Task PullAsync_TruncatesALongServerErrorBody()
        {
            var handler = new RecordingHandler().EnqueueRaw(HttpStatusCode.InternalServerError, new string('x', 5000));
            var provider = CreateProvider(handler);

            var result = await provider.PullAsync(DateTime.MinValue);

            Assert.False(result.Succeeded);
            Assert.True(result.ErrorMessage!.Length < 400, $"Error message was {result.ErrorMessage.Length} chars.");
        }

        [Fact]
        public void Constructor_RejectsAnEmptyEndpoint()
        {
            var handler = new RecordingHandler();
            Assert.Throws<ArgumentException>(() =>
                new ApiSyncProvider(new HttpClient(handler), string.Empty, DeviceId, new FakeTokenStore()));
        }

        // A base URL with or without a trailing slash has to resolve to the same place -
        // Uri's relative resolution silently drops the last path segment otherwise.
        [Theory]
        [InlineData("https://workledger.example.com")]
        [InlineData("https://workledger.example.com/")]
        public async Task BaseUrl_ResolvesTheSame_WithOrWithoutATrailingSlash(string baseUrl)
        {
            var handler = new RecordingHandler().EnqueueJson(
                HttpStatusCode.OK, new { Changes = Array.Empty<object>(), ServerTime = DateTime.UtcNow });
            var provider = new ApiSyncProvider(
                new HttpClient(handler), baseUrl, DeviceId,
                new FakeTokenStore("token", DateTime.UtcNow.AddDays(30)));

            await provider.PullAsync(DateTime.MinValue);

            Assert.Equal("/sync/pull", handler.Requests.Single().Uri.AbsolutePath);
        }
    }
}
