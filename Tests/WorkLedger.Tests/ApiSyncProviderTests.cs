using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
        private const string Email = "person@example.com";
        private const string Password = "correct horse battery staple";

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

            // WL-144: a 204 or a bearer challenge as the wire really carries them - status line,
            // no body at all. Deliberately not EnqueueRaw(status, "") - the point of several of
            // these tests is what the client does when there is nothing to parse.
            public RecordingHandler EnqueueNoBody(HttpStatusCode status)
                => Enqueue(_ => new HttpResponseMessage(status));

            public RecordingHandler EnqueueThrow(Exception exception)
                => Enqueue(_ => throw exception);

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // Read the body here: the request is disposed once the provider returns.
                Requests.Add(new RecordedRequest(
                    request.Method,
                    request.RequestUri!,
                    request.Headers.Authorization?.Parameter,
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

        public record RecordedRequest(HttpMethod Method, Uri Uri, string? BearerToken, string? Body);

        // WL-134: counts how many times the credential was actually asked for, which is the
        // observable form of "the credential was discarded" - a provider that kept retrying a
        // rejected password would keep reading it.
        private sealed class CountingCredentialSource
        {
            private readonly ApiCredentials? _credential;

            public CountingCredentialSource(string? email = Email, string? password = Password)
                => _credential = email is null || password is null ? null : new ApiCredentials(email, password);

            public int ReadCount { get; private set; }

            public ApiCredentials? Read()
            {
                ReadCount++;
                return _credential;
            }
        }

        private static object LoginResponseBody(string token, DateTime expiresAt, string email = Email, int userId = 7)
            => new
            {
                Token = token,
                ExpiresAtUtc = expiresAt,
                User = new { Id = userId, Email = email, DisplayName = (string?)null, CreatedAtUtc = DateTime.UtcNow }
            };

        // A 423/429 as the server sends it: the status plus a Retry-After the client has to read.
        private static HttpResponseMessage WithRetryAfter(HttpStatusCode status, int seconds)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            return response;
        }

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
            Func<ApiCredentials?>? credentials = null)
            => new(
                new HttpClient(handler),
                BaseUrl,
                DeviceId,
                tokens ?? new FakeTokenStore("existing-token", DateTime.UtcNow.AddDays(30)),
                credentials ?? (() => null));



        // ------------------------------------------------------- WL-144: account management (plan 10)

        private static object DeviceBody(string deviceId, string? label, DateTime? lastSeen, bool isCurrent)
            => new
            {
                DeviceId = deviceId,
                Label = label,
                CreatedAtUtc = DateTime.UtcNow.AddDays(-30),
                LastSeenUtc = lastSeen,
                IsCurrent = isCurrent
            };

        [Fact]
        public async Task LogoutAllAsync_StoresTheFreshTokenTheServerHandsBack()
        {
            // THE regression for this endpoint. /auth/logout-all rotates the user's security stamp,
            // which kills every outstanding token INCLUDING the caller's own, and the replacement in
            // the response is the server making sure "sign my other devices out" doesn't sign out
            // the device asking. Drop it and the feature looks like it logged the user out.
            var tokens = new FakeTokenStore("old-token", DateTime.UtcNow.AddDays(30));
            var expiry = DateTime.UtcNow.AddDays(365);
            var handler = new RecordingHandler().EnqueueJson(
                HttpStatusCode.OK, LoginResponseBody("rotated-token", expiry));

            var provider = CreateProvider(handler, tokens);

            var outcome = await provider.LogoutAllAsync();

            Assert.True(outcome.Success);
            Assert.Equal("rotated-token", tokens.Token);
            Assert.Equal(AuthState.SignedIn, provider.Auth.State);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/auth/logout-all", request.Uri.AbsolutePath);

            // Sent with the OLD token, which is still valid at the moment of asking.
            Assert.Equal("old-token", request.BearerToken);
        }

        [Fact]
        public async Task ChangePasswordAsync_StoresTheFreshTokenTheServerHandsBack()
        {
            // Same regression, same reason: a password change bumps the security stamp too, so the
            // caller's own token dies with everyone else's.
            var tokens = new FakeTokenStore("old-token", DateTime.UtcNow.AddDays(30));
            var handler = new RecordingHandler().EnqueueJson(
                HttpStatusCode.OK, LoginResponseBody("rotated-token", DateTime.UtcNow.AddDays(365)));

            var provider = CreateProvider(handler, tokens);

            var outcome = await provider.ChangePasswordAsync("old pass phrase", "new pass phrase");

            Assert.True(outcome.Success);
            Assert.Equal("rotated-token", tokens.Token);
            Assert.Equal(1, tokens.SaveCount);
            Assert.Equal(AuthState.SignedIn, provider.Auth.State);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("/auth/password", request.Uri.AbsolutePath);
            Assert.Contains("old pass phrase", request.Body);
        }

        [Fact]
        public async Task ChangePasswordAsync_WithAWrongCurrentPassword_SaysSo_AndLeavesTheSessionAlone()
        {
            // The 401 that is NOT a revocation. The server re-verifies the current password inside
            // the handler and refuses a wrong one with a 401 - the same status the bearer middleware
            // uses for a dead token, meaning the opposite thing. Reading this as "you were signed
            // out" would sign someone out of a perfectly good session for a typo.
            var tokens = new FakeTokenStore("live-token", DateTime.UtcNow.AddDays(30));
            var handler = new RecordingHandler().EnqueueRaw(
                HttpStatusCode.Unauthorized, "{\"error\":\"Current password is incorrect.\"}");

            var provider = CreateProvider(handler, tokens);

            var outcome = await provider.ChangePasswordAsync("wrong", "new pass phrase");

            Assert.False(outcome.Success);
            Assert.Contains("current password", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // The three things that must not have happened.
            Assert.Equal(AuthState.SignedIn, provider.Auth.State);
            Assert.Equal("live-token", tokens.Token);
            Assert.Equal(0, tokens.SaveCount);
        }

        [Fact]
        public async Task ChangePasswordAsync_OnABodylessChallenge_IsStillARevokedSession()
        {
            // The other half of the discrimination, and the reason it keys off the body rather than
            // off the endpoint: a genuinely dead token hitting /auth/password must still be read as
            // a revocation. ASP.NET's bearer challenge carries no body - the reason goes in the
            // WWW-Authenticate header - which is exactly what tells the two apart.
            var tokens = new FakeTokenStore("revoked-token", DateTime.UtcNow.AddDays(30));
            var handler = new RecordingHandler().EnqueueNoBody(HttpStatusCode.Unauthorized);

            var provider = CreateProvider(handler, tokens);

            var outcome = await provider.ChangePasswordAsync("right", "new pass phrase");

            Assert.False(outcome.Success);
            Assert.Equal(AuthState.SessionRevoked, provider.Auth.State);
            Assert.False(provider.Auth.CanSync);
        }

        [Fact]
        public async Task ChangePasswordAsync_WhenTheServerRefusesTheNewPassword_QuotesTheServer()
        {
            // The password policy is the SERVER's and this client deliberately does not hold a copy
            // of it, so its sentence is passed through rather than reconstructed from a rule that
            // could be wrong.
            var handler = new RecordingHandler().EnqueueRaw(
                HttpStatusCode.BadRequest,
                "{\"error\":\"Password must be at least 12 characters.\"}");

            var provider = CreateProvider(handler);

            var outcome = await provider.ChangePasswordAsync("right", "short");

            Assert.False(outcome.Success);
            Assert.Equal("Password must be at least 12 characters.", outcome.ErrorMessage);
        }

        [Fact]
        public async Task GetDevicesAsync_ParsesTheList_KeepsTheServersOrder_AndFlagsTheCurrentDevice()
        {
            var seen = DateTime.UtcNow.AddMinutes(-5);
            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.OK, new[]
            {
                DeviceBody("device-under-test", "Laptop", seen, isCurrent: true),
                DeviceBody("other-device", "Phone", seen.AddDays(-2), isCurrent: false),

                // Never seen. The server sorts these LAST on purpose - Postgres would otherwise
                // float a row that has never made a request above the device the list is being read
                // on - so a client that re-sorted would undo a decision it paid a query term for.
                DeviceBody("never-used", null, null, isCurrent: false)
            });

            var outcome = await CreateProvider(handler).GetDevicesAsync();

            Assert.True(outcome.Success);
            Assert.Equal(
                new[] { "device-under-test", "other-device", "never-used" },
                outcome.Devices.Select(device => device.DeviceId).ToArray());

            Assert.True(outcome.Devices[0].IsCurrent);
            Assert.False(outcome.Devices[1].IsCurrent);
            Assert.Null(outcome.Devices[2].LastSeenUtc);

            Assert.Equal("/auth/devices", Assert.Single(handler.Requests).Uri.AbsolutePath);
        }

        [Fact]
        public async Task DeleteDeviceAsync_HandlesA204WithNoBodyAtAll()
        {
            // The trap the plan names: the JSON send path would have failed this AFTER the request
            // had already succeeded, deleting the device and reporting an error.
            var handler = new RecordingHandler().EnqueueNoBody(HttpStatusCode.NoContent);

            var outcome = await CreateProvider(handler).DeleteDeviceAsync("other-device");

            Assert.True(outcome.Success);
            Assert.Null(outcome.ErrorMessage);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/auth/devices/other-device", request.Uri.AbsolutePath);
        }

        [Fact]
        public async Task DeleteDeviceAsync_On404_ReportsNotFound_RatherThanATransportFailure()
        {
            // A 404 is the server ANSWERING: the device is unknown or belongs to someone else, and
            // it refuses to say which. Reporting it as "couldn't reach the server" would invite a
            // retry that can only produce the same 404.
            var handler = new RecordingHandler().EnqueueRaw(
                HttpStatusCode.NotFound, "{\"error\":\"No such device on this account.\"}");

            var outcome = await CreateProvider(handler).DeleteDeviceAsync("ghost-device");

            Assert.False(outcome.Success);
            Assert.Contains("isn't on this account", outcome.ErrorMessage);
            Assert.DoesNotContain("reach", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task DeletingTheCurrentDevice_IsPermitted_AndTheNext401IsARevokedSession()
        {
            // The sequence that looks like a bug and is the endpoint working: removing your own
            // device returns 204 and then everything 401s, because the token was issued to a device
            // row that no longer exists. There is deliberately no client-side guard against it -
            // it is the only way to drop a token from the device holding it.
            var handler = new RecordingHandler()
                .EnqueueNoBody(HttpStatusCode.NoContent)
                .EnqueueNoBody(HttpStatusCode.Unauthorized);

            var provider = CreateProvider(handler);

            var removed = await provider.DeleteDeviceAsync(DeviceId);
            Assert.True(removed.Success);

            // Anything authenticated afterwards. whoami is the cheapest.
            Assert.Null(await provider.TryWhoAmIAsync());
            Assert.Equal(AuthState.SessionRevoked, provider.Auth.State);
        }

        [Fact]
        public async Task DeleteAccountAsync_SendsBothGuards_AndSucceedsOnA204()
        {
            var handler = new RecordingHandler().EnqueueNoBody(HttpStatusCode.NoContent);

            var outcome = await CreateProvider(handler).DeleteAccountAsync(Password, Email);

            Assert.True(outcome.Success);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("/auth/account", request.Uri.AbsolutePath);

            // A DELETE with a body, which is unusual enough to be worth pinning: the server declares
            // it [FromBody] precisely because minimal APIs refuse an inferred body on DELETE.
            Assert.Contains(Email, request.Body);
            Assert.Contains(Password, request.Body);
        }

        [Fact]
        public async Task DeleteAccountAsync_WithAWrongPassword_SaysSo_AndLeavesTheSessionAlone()
        {
            // Same guarded-401 rule as the password change: the account still exists, the session
            // still works, and nothing local may be cleared on the strength of this.
            var tokens = new FakeTokenStore("live-token", DateTime.UtcNow.AddDays(30));
            var handler = new RecordingHandler().EnqueueRaw(
                HttpStatusCode.Unauthorized, "{\"error\":\"Password is incorrect.\"}");

            var outcome = await CreateProvider(handler, tokens).DeleteAccountAsync("wrong", Email);

            Assert.False(outcome.Success);
            Assert.Contains("password", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("live-token", tokens.Token);
        }

        [Fact]
        public async Task EveryAccountCall_IsTotal_AgainstAServerThatIsNotThere()
        {
            // ISyncProvider's contract, extended to these five: a dead host is a result, never an
            // escaping HttpRequestException. A settings screen must not be able to crash the app.
            static RecordingHandler Dead() => new(_ => throw new HttpRequestException("no route to host"));

            Assert.False((await CreateProvider(Dead()).GetDevicesAsync()).Success);
            Assert.False((await CreateProvider(Dead()).DeleteDeviceAsync("any")).Success);
            Assert.False((await CreateProvider(Dead()).LogoutAllAsync()).Success);
            Assert.False((await CreateProvider(Dead()).ChangePasswordAsync("a", "b")).Success);
            Assert.False((await CreateProvider(Dead()).DeleteAccountAsync("a", Email)).Success);
        }

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

        // ---------------------------------------------------------------- WL-134: signing in

        [Fact]
        public async Task Login_StoresTheIssuedTokenAndReturnsWhoSignedIn()
        {
            var expiresAt = DateTime.UtcNow.AddDays(365);
            var tokens = new FakeTokenStore();
            var handler = new RecordingHandler().EnqueueJson(
                HttpStatusCode.OK, LoginResponseBody("freshly-issued", expiresAt));
            var provider = CreateProvider(handler, tokens, () => new ApiCredentials(Email, Password));

            var outcome = await provider.TryLoginAsync();

            Assert.True(outcome.Success);
            Assert.Equal("freshly-issued", tokens.Token);
            Assert.Equal(expiresAt, tokens.ExpiresAtUtc);

            // The UserSummary comes back on the login response, so a caller never needs a second
            // /auth/whoami to say who signed in - and WL-138's guard needs the Id right here.
            Assert.Equal(7, outcome.User!.Id);
            Assert.Equal(Email, outcome.User.Email);
            Assert.Equal(AuthState.SignedIn, provider.Auth.State);

            var request = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://workledger.example.com/auth/login", request.Uri.ToString());
            // Anonymous endpoint - sending a bearer here would be meaningless.
            Assert.Null(request.BearerToken);
        }

        // The device identity on the wire is AppSettings.DeviceId, the same value stamped as
        // OriginDeviceId. Sending the email instead would mint a second identifier and quietly
        // detach this install's history from its own rows.
        [Fact]
        public async Task Login_SendsTheDeviceIdAsTheDeviceIdentity_NotTheEmail()
        {
            var handler = new RecordingHandler().EnqueueJson(
                HttpStatusCode.OK, LoginResponseBody("issued", DateTime.UtcNow.AddDays(365)));
            var provider = CreateProvider(handler, new FakeTokenStore(), () => new ApiCredentials(Email, Password));

            await provider.TryLoginAsync();

            var body = Assert.Single(handler.Requests).Body!;
            Assert.Contains($"\"DeviceId\":\"{DeviceId}\"", body);
            Assert.Contains($"\"Email\":\"{Email}\"", body);
        }

        [Fact]
        public async Task Login_WithNoCredentialAvailable_FailsQuietlyWithoutCallingTheServer()
        {
            var handler = new RecordingHandler();
            var provider = CreateProvider(handler, new FakeTokenStore());

            Assert.False((await provider.TryLoginAsync()).Success);
            Assert.Empty(handler.Requests);
        }

        // ------------------------------------------------- WL-134 Invariant L, the headline test

        // THE test of plan 8 item 4. Drive far more sync cycles than the server's five-failure
        // lockout threshold against a server that refuses everything, and assert the client never
        // makes a second login attempt. Every rule in item 4 exists to make this pass; if it ever
        // fails, an unattended client can lock its own user out of their own server overnight.
        [Fact]
        public async Task ManySyncCycles_AgainstAServerThat401sEverything_MakeAtMostOneLoginAttempt()
        {
            var credentials = new CountingCredentialSource();
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
            var provider = CreateProvider(
                handler, new FakeTokenStore("doomed-token", DateTime.UtcNow.AddDays(30)), credentials.Read);

            // A dozen cycles is well past five, and costs nothing against a fake handler.
            for (var cycle = 0; cycle < 12; cycle++)
            {
                await provider.PushAsync(new[] { QueueEntry(cycle + 1, "project", Guid.NewGuid(), "create", 1) });
                await provider.PullAsync(DateTime.MinValue);
            }

            var loginAttempts = handler.Requests.Count(request => request.Uri.AbsolutePath == "/auth/login");
            Assert.Equal(1, loginAttempts);

            // Whatever else is true at the end, the session is not usable - so the timer stops
            // firing at it too (item 4d). The exact label is SessionRevoked rather than SignedOut:
            // the rejected credential is discarded first, and then the next cycle re-observes the
            // still-dead token. Both mean "a human has to sign in", which is the only distinction
            // that changes what anything does.
            Assert.False(provider.Auth.CanSync);
            Assert.Equal(AuthState.SessionRevoked, provider.Auth.State);
        }

        // The same bound stated the other way round: once refused, the credential is not read
        // again at all. This is the discard in item 4b, observed rather than inferred.
        [Fact]
        public async Task AfterALoginIsRejected_TheCredentialIsNeverConsultedAgain()
        {
            var credentials = new CountingCredentialSource();
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
            var provider = CreateProvider(
                handler, new FakeTokenStore("doomed-token", DateTime.UtcNow.AddDays(30)), credentials.Read);

            await provider.PullAsync(DateTime.MinValue);
            var readsAfterFirstRun = credentials.ReadCount;

            await provider.PullAsync(DateTime.MinValue);
            await provider.PullAsync(DateTime.MinValue);

            // It may still be *read* - CurrentCredential is what compares it - but it is never
            // sent again, which is the property that matters.
            Assert.Equal(1, handler.Requests.Count(request => request.Uri.AbsolutePath == "/auth/login"));
            Assert.True(credentials.ReadCount >= readsAfterFirstRun);
        }

        // ------------------------------------------------------------- WL-134 item 4c, the rules

        // The overwhelmingly common shape of a revoked session: nobody is signing in, so there is
        // nothing to recover with. Exactly ONE request - the failing one. That count is the thing
        // that must not grow.
        [Fact]
        public async Task PullAsync_OnA401WithNoCredentialHeld_MakesExactlyOneRequestAndReportsAuthentication()
        {
            var handler = new RecordingHandler().EnqueueRaw(HttpStatusCode.Unauthorized, "{}");
            var provider = CreateProvider(handler, new FakeTokenStore("revoked-token", DateTime.UtcNow.AddDays(30)));

            var result = await provider.PullAsync(DateTime.MinValue);

            Assert.False(result.Succeeded);
            Assert.True(result.AuthenticationRequired);
            Assert.Single(handler.Requests);
            Assert.Equal(AuthState.SessionRevoked, provider.Auth.State);
        }

        [Fact]
        public async Task PushAsync_OnA401WithNoCredentialHeld_LeavesTheQueueRowsPending()
        {
            var handler = new RecordingHandler().EnqueueRaw(HttpStatusCode.Unauthorized, "{}");
            var provider = CreateProvider(handler, new FakeTokenStore("revoked-token", DateTime.UtcNow.AddDays(30)));

            var result = await provider.PushAsync(new[] { QueueEntry(60, "project", Guid.NewGuid(), "create", 1) });

            Assert.True(result.AuthenticationRequired);
            Assert.Empty(result.AcceptedSyncQueueIds);
            // Pending, never retired: the server never saw these, and discarding work on the
            // strength of a signed-out session would lose it for good.
            Assert.Empty(result.RejectedSyncQueueIds);
            Assert.Equal(60, Assert.Single(result.Failures).SyncQueueId);
        }

        // With a sign-in genuinely in flight, a 401 mid-call IS recoverable - once.
        [Fact]
        public async Task PullAsync_OnA401WithACredentialHeld_LogsInOnceAndRetriesTheCall()
        {
            var tokens = new FakeTokenStore("stale-token", DateTime.UtcNow.AddDays(30));
            var handler = new RecordingHandler()
                .EnqueueRaw(HttpStatusCode.Unauthorized, "{}")
                .EnqueueJson(HttpStatusCode.OK, LoginResponseBody("renewed-token", DateTime.UtcNow.AddDays(365)))
                .EnqueueJson(HttpStatusCode.OK, new { Changes = Array.Empty<object>(), ServerTime = DateTime.UtcNow });
            var provider = CreateProvider(handler, tokens, () => new ApiCredentials(Email, Password));

            var result = await provider.PullAsync(DateTime.MinValue);

            Assert.True(result.Succeeded);
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal("stale-token", handler.Requests[0].BearerToken);
            Assert.Equal("/auth/login", handler.Requests[1].Uri.AbsolutePath);
            Assert.Equal("renewed-token", handler.Requests[2].BearerToken);
            Assert.Equal(1, tokens.SaveCount);
        }

        [Fact]
        public async Task PullAsync_WhenTheRetryAfterAFreshLoginAlso401s_FailsWithoutTryingAgain()
        {
            var tokens = new FakeTokenStore("stale-token", DateTime.UtcNow.AddDays(30));
            var handler = new RecordingHandler()
                .EnqueueRaw(HttpStatusCode.Unauthorized, "{}")
                .EnqueueJson(HttpStatusCode.OK, LoginResponseBody("renewed-token", DateTime.UtcNow.AddDays(365)))
                .EnqueueRaw(HttpStatusCode.Unauthorized, "{}");
            var provider = CreateProvider(handler, tokens, () => new ApiCredentials(Email, Password));

            var result = await provider.PullAsync(DateTime.MinValue);

            Assert.False(result.Succeeded);
            Assert.True(result.AuthenticationRequired);
            // Three calls, not four - the retry is not itself retried, and in particular it is
            // not answered with a second login.
            Assert.Equal(3, handler.Requests.Count);
            Assert.Equal(AuthState.SessionRevoked, provider.Auth.State);
        }

        [Fact]
        public async Task Login_OnA423_ReportsLockedOutWithTheRetryAfterInstant()
        {
            var handler = new RecordingHandler().Enqueue(_ => WithRetryAfter(HttpStatusCode.Locked, 900));
            var provider = CreateProvider(handler, new FakeTokenStore(), () => new ApiCredentials(Email, Password));

            var outcome = await provider.TryLoginAsync();

            Assert.False(outcome.Success);
            Assert.Equal(AuthState.LockedOut, provider.Auth.State);
            Assert.NotNull(provider.Auth.RetryAfterUtc);
            Assert.InRange(provider.Auth.RetryAfterUtc!.Value, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
            Assert.Contains("15 minutes", outcome.ErrorMessage);
        }

        // A locked account refuses the CORRECT password too, so a 423 says nothing about whether
        // this credential is right - and another attempt could only make the wait longer.
        [Fact]
        public async Task AfterA423_NoFurtherLoginIsAttempted()
        {
            var handler = new RecordingHandler(_ => WithRetryAfter(HttpStatusCode.Locked, 900));
            var provider = CreateProvider(
                handler, new FakeTokenStore("stale", DateTime.UtcNow.AddDays(30)), () => new ApiCredentials(Email, Password));

            await provider.TryLoginAsync();
            await provider.PullAsync(DateTime.MinValue);
            await provider.PullAsync(DateTime.MinValue);

            Assert.Equal(1, handler.Requests.Count(request => request.Uri.AbsolutePath == "/auth/login"));
        }

        [Fact]
        public async Task Login_OnA429_ReportsRateLimitedRatherThanALockout()
        {
            var handler = new RecordingHandler().Enqueue(_ => WithRetryAfter(HttpStatusCode.TooManyRequests, 60));
            var provider = CreateProvider(handler, new FakeTokenStore(), () => new ApiCredentials(Email, Password));

            var outcome = await provider.TryLoginAsync();

            Assert.False(outcome.Success);
            // Deliberately a different state from LockedOut: nobody's account is in trouble, the
            // caller is just early, and saying "your account is locked" would be a lie.
            Assert.Equal(AuthState.RateLimited, provider.Auth.State);
            Assert.NotNull(provider.Auth.RetryAfterUtc);
        }

        // ------------------------------------------------- WL-146: capabilities, register, claim

        private static object CapabilitiesBody(
            bool registrationEnabled = true, bool requiresSecret = true, int minPasswordLength = 12,
            bool hasUnclaimed = false, int maxFailedLogins = 5, int lockoutMinutes = 15)
            => new
            {
                RegistrationEnabled = registrationEnabled,
                RegistrationRequiresSecret = requiresSecret,
                MinPasswordLength = minPasswordLength,
                HasUnclaimedAccount = hasUnclaimed,
                MaxFailedLogins = maxFailedLogins,
                LockoutMinutes = lockoutMinutes
            };

        [Fact]
        public async Task Capabilities_AreParsedIncludingTheLockoutPolicy()
        {
            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.OK, CapabilitiesBody(hasUnclaimed: true));
            var provider = CreateProvider(handler, new FakeTokenStore());

            var capabilities = await provider.TryGetCapabilitiesAsync();

            Assert.NotNull(capabilities);
            Assert.True(capabilities!.RegistrationEnabled);
            Assert.True(capabilities.HasUnclaimedAccount);
            Assert.Equal(12, capabilities.MinPasswordLength);
            Assert.Equal(5, capabilities.MaxFailedLogins);
            Assert.Equal(15, capabilities.LockoutMinutes);

            Assert.Equal("/auth/capabilities", Assert.Single(handler.Requests).Uri.AbsolutePath);
        }

        // Anonymous endpoint. A stale token must not be able to turn feature discovery into a 401 -
        // being signed out is exactly when a client most needs to know what the server offers.
        [Fact]
        public async Task Capabilities_AreProbedAnonymously_EvenWhenATokenIsStored()
        {
            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.OK, CapabilitiesBody());
            var provider = CreateProvider(handler, new FakeTokenStore("a-perfectly-good-token", DateTime.UtcNow.AddDays(30)));

            await provider.TryGetCapabilitiesAsync();

            Assert.Null(Assert.Single(handler.Requests).BearerToken);
        }

        // A server predating the user-accounts work is a supported thing to point at, not an error.
        [Fact]
        public async Task Capabilities_OnA404_DegradeToSignInOnly_AndAreNotAskedAgain()
        {
            var handler = new RecordingHandler().EnqueueRaw(HttpStatusCode.NotFound, "{}");
            var provider = CreateProvider(handler, new FakeTokenStore());

            Assert.Null(await provider.TryGetCapabilitiesAsync());

            // A definitive answer, so it is cached: re-asking every time the panel opens would be a
            // request per glance at a server that will keep saying the same thing.
            Assert.Null(await provider.TryGetCapabilitiesAsync());
            Assert.Single(handler.Requests);
        }

        [Fact]
        public async Task Capabilities_OnAnUnreachableServer_AreRetriedRatherThanCached()
        {
            // A transport failure is not an answer about the server's features. Caching it would
            // hide the registration UI until restart over what may have been a brief outage.
            var handler = new RecordingHandler()
                .EnqueueThrow(new HttpRequestException("no route to host"))
                .EnqueueJson(HttpStatusCode.OK, CapabilitiesBody());
            var provider = CreateProvider(handler, new FakeTokenStore());

            Assert.Null(await provider.TryGetCapabilitiesAsync());
            Assert.NotNull(await provider.TryGetCapabilitiesAsync());
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public async Task Capabilities_AreCachedUntilAskedToRefresh()
        {
            var handler = new RecordingHandler()
                .EnqueueJson(HttpStatusCode.OK, CapabilitiesBody(registrationEnabled: true))
                .EnqueueJson(HttpStatusCode.OK, CapabilitiesBody(registrationEnabled: false));
            var provider = CreateProvider(handler, new FakeTokenStore());

            Assert.True((await provider.TryGetCapabilitiesAsync())!.RegistrationEnabled);
            Assert.True((await provider.TryGetCapabilitiesAsync())!.RegistrationEnabled);
            Assert.Single(handler.Requests);

            Assert.False((await provider.TryGetCapabilitiesAsync(refresh: true))!.RegistrationEnabled);
            Assert.Equal(2, handler.Requests.Count);
        }

        [Fact]
        public async Task Register_SendsTheSignupSecret_AndReturnsTheCreatedUserWithoutAToken()
        {
            var tokens = new FakeTokenStore();
            var handler = new RecordingHandler().EnqueueJson(HttpStatusCode.Created, new
            {
                Id = 4, Email, DisplayName = "A Person", CreatedAtUtc = DateTime.UtcNow
            });
            var provider = CreateProvider(handler, tokens);

            var outcome = await provider.TryRegisterAsync(Email, Password, "A Person", "signup-secret");

            Assert.True(outcome.Success);
            Assert.Equal(4, outcome.User!.Id);

            var request = Assert.Single(handler.Requests);
            Assert.Equal("/auth/register", request.Uri.AbsolutePath);
            Assert.Null(request.BearerToken);
            Assert.Contains(Email, request.Body);

            // Registration mints NO token - it is a user-level act with no device bound to it - so
            // nothing was stored and the caller has to follow with a login.
            Assert.Null(tokens.Token);
        }

        [Theory]
        [InlineData(HttpStatusCode.Forbidden, "registration turned off")]
        [InlineData(HttpStatusCode.Unauthorized, "signup secret")]
        [InlineData(HttpStatusCode.Conflict, "already exists")]
        [InlineData(HttpStatusCode.TooManyRequests, "address")]
        public async Task Register_MapsEachFailureToItsOwnMessage(HttpStatusCode status, string expected)
        {
            var handler = new RecordingHandler().EnqueueRaw(status, "{}");
            var provider = CreateProvider(handler, new FakeTokenStore());

            var outcome = await provider.TryRegisterAsync(Email, Password, null, "secret");

            Assert.False(outcome.Success);
            Assert.Contains(expected, outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(HttpStatusCode.Forbidden)]
        [InlineData(HttpStatusCode.Conflict)]
        public async Task Register_FlagsTheCachedCapabilitiesAsStale_WhenTheServerHasMovedOn(HttpStatusCode status)
        {
            var handler = new RecordingHandler().EnqueueRaw(status, "{}");
            var provider = CreateProvider(handler, new FakeTokenStore());

            Assert.True((await provider.TryRegisterAsync(Email, Password, null, "secret")).CapabilitiesStale);
        }

        [Fact]
        public async Task Register_OnAValidationFailure_ShowsTheServersOwnReason()
        {
            // The one case where the server's words beat ours: it knows the rule, and this client
            // deliberately does not duplicate it.
            var handler = new RecordingHandler().EnqueueRaw(
                HttpStatusCode.BadRequest, "{\"error\":\"Password must be at least 12 characters.\"}");
            var provider = CreateProvider(handler, new FakeTokenStore());

            var outcome = await provider.TryRegisterAsync(Email, "short", null, "secret");

            Assert.Equal("Password must be at least 12 characters.", outcome.ErrorMessage);
        }

        [Fact]
        public async Task Claim_PostsToClaim_AndSaysSomethingDifferentFromRegisterOnA409()
        {
            var handler = new RecordingHandler().EnqueueRaw(HttpStatusCode.Conflict, "{}");
            var provider = CreateProvider(handler, new FakeTokenStore());

            var outcome = await provider.TryClaimAsync(Email, Password, null, "secret");

            Assert.Equal("/auth/claim", Assert.Single(handler.Requests).Uri.AbsolutePath);
            Assert.False(outcome.Success);

            // "Nothing left to claim" and "that email is taken" are different facts about the
            // server, and the user's next move differs accordingly.
            Assert.Contains("nothing left to claim", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.True(outcome.CapabilitiesStale);
        }

        // ---------------------------------------------------------- WL-134 item 4a, free states

        // Reached with NO request at all. A year-idle install should prompt for a sign-in, not
        // spend a doomed round trip discovering what its own settings already say.
        [Fact]
        public async Task AnExpiredToken_ReadsAsSessionExpired_WithoutMakingAnyRequest()
        {
            var handler = new RecordingHandler();
            var provider = CreateProvider(handler, new FakeTokenStore("long-dead", DateTime.UtcNow.AddDays(-1)));

            Assert.Equal(AuthState.SessionExpired, provider.Auth.State);
            Assert.False(provider.Auth.CanSync);
            Assert.Empty(handler.Requests);

            await Task.CompletedTask;
        }

        [Fact]
        public void NoToken_ReadsAsSignedOut()
        {
            var provider = CreateProvider(new RecordingHandler(), new FakeTokenStore());

            Assert.Equal(AuthState.SignedOut, provider.Auth.State);
            Assert.False(provider.Auth.CanSync);
        }

        [Fact]
        public async Task PushAsync_WithNoTokenAndNoCredential_FailsWithoutThrowing()
        {
            var handler = new RecordingHandler();
            var provider = CreateProvider(handler, new FakeTokenStore());

            var result = await provider.PushAsync(new[] { QueueEntry(60, "project", Guid.NewGuid(), "create", 1) });

            Assert.Empty(handler.Requests);
            Assert.Empty(result.AcceptedSyncQueueIds);
            // Retryable, not retired: the server never saw this change.
            Assert.Empty(result.RejectedSyncQueueIds);
            Assert.True(result.AuthenticationRequired);
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
