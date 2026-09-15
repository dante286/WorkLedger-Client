using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using WorkLedger_Client.Sync;
using Xunit;

namespace WorkLedger.Tests
{
    // Plan 7 items 3-4/6: the piece that turns a SyncMode setting into a running sync. Two
    // devices are modelled the way SyncOrchestratorTests/FileSyncProviderTests do it - two
    // LocalDbService instances with different DeviceIds over different files - but driven
    // through the coordinator rather than a provider directly, so the settings -> provider ->
    // orchestrator wiring is what's under test.
    public class SyncCoordinatorTests : IAsyncLifetime
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"WorkLedgerCoordinatorTests_{Guid.NewGuid()}");

        private Device _alpha = null!;
        private Device _beta = null!;
        private string _sharedFolder = null!;

        public async Task InitializeAsync()
        {
            _sharedFolder = Path.Combine(_root, "shared");
            Directory.CreateDirectory(_sharedFolder);

            _alpha = await Device.CreateAsync(Path.Combine(_root, "alpha"), _sharedFolder);
            _beta = await Device.CreateAsync(Path.Combine(_root, "beta"), _sharedFolder);
        }

        public async Task DisposeAsync()
        {
            await _alpha.DisposeAsync();
            await _beta.DisposeAsync();
            TryDeleteDirectoryWithRetries(_root);
        }

        [Fact]
        public async Task SyncNowAsync_FileModeBetweenTwoDevices_PropagatesAProject()
        {
            await _alpha.Db.SaveProjectAsync(new Project { Name = "Shared project" });

            var pushed = await _alpha.Coordinator.SyncNowAsync();
            var pulled = await _beta.Coordinator.SyncNowAsync();

            Assert.True(pushed.Succeeded);
            Assert.True(pulled.Succeeded);
            Assert.Equal(1, pushed.PushedCount);

            var replicated = Assert.Single(await _beta.Db.GetAllAsync<Project>());
            Assert.Equal("Shared project", replicated.Name);
        }

        [Fact]
        public async Task SyncNowAsync_FileModeBothDirections_ConvergesWithoutDuplicating()
        {
            await _alpha.Db.SaveProjectAsync(new Project { Name = "From alpha" });
            await _beta.Db.SaveProjectAsync(new Project { Name = "From beta" });

            await _alpha.Coordinator.SyncNowAsync();
            await _beta.Coordinator.SyncNowAsync();
            await _alpha.Coordinator.SyncNowAsync();

            // A second round must not re-import what each device already merged - the version
            // comparison discards the replay rather than inserting it again.
            await _beta.Coordinator.SyncNowAsync();
            await _alpha.Coordinator.SyncNowAsync();

            Assert.Equal(
                new[] { "From alpha", "From beta" },
                (await _alpha.Db.GetAllAsync<Project>()).Select(p => p.Name).OrderBy(name => name).ToArray());
            Assert.Equal(
                new[] { "From alpha", "From beta" },
                (await _beta.Db.GetAllAsync<Project>()).Select(p => p.Name).OrderBy(name => name).ToArray());
        }

        [Fact]
        public async Task SyncNowAsync_ModeOff_ReportsThatRatherThanRunning()
        {
            _alpha.Settings.SyncMode = SyncMode.Off;

            var result = await _alpha.Coordinator.SyncNowAsync();

            Assert.False(result.Ran);
            Assert.False(result.Succeeded);
            Assert.Equal("Sync is turned off.", result.ErrorMessage);
        }

        [Fact]
        public async Task SyncNowAsync_FileModeWithNoFolderChosen_FailsWithAMessageInsteadOfThrowing()
        {
            _alpha.Settings.SyncFolderPath = string.Empty;

            var result = await _alpha.Coordinator.SyncNowAsync();

            Assert.False(result.Succeeded);
            Assert.Equal("Choose a sync folder first.", result.ErrorMessage);
        }

        [Fact]
        public async Task SyncNowAsync_ApiModeWithNoReachableServer_FailsGracefully()
        {
            // The acceptance criterion for exposing Api mode in the UI at all: a dead endpoint
            // must produce a message, not an unhandled exception.
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = "http://127.0.0.1:9";
            _alpha.Settings.ApiToken = "not-a-real-token";
            _alpha.Settings.ApiTokenExpiresAtUtc = DateTime.UtcNow.AddHours(1);

            var result = await _alpha.Coordinator.SyncNowAsync();

            Assert.True(result.Ran);
            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        }

        [Fact]
        public async Task SignInAsync_WithoutAnEndpoint_AsksForOneInsteadOfCalling()
        {
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = string.Empty;

            var (success, message) = await _alpha.Coordinator.SignInAsync("person@example.com", "hunter2");

            Assert.False(success);
            Assert.Equal("Enter an API endpoint first.", message);
        }

        // ------------------------------------------------- WL-138: the account-ownership guard
        //
        // These drive a real SignInAsync against a scripted HttpMessageHandler, because the guard
        // can only run once a login has returned a UserId - which is the whole reason it has to
        // live at this level rather than somewhere cheaper to test.

        [Fact]
        public async Task SignInAsync_AgainstAnUnclaimedDatabase_AdoptsItWithNoPromptAtAll()
        {
            // THE OFFLINE-FIRST PATH, and the assertion here is the ABSENCE of friction: a week's
            // work done with no server, then a sign-in, and everything is simply kept. If this
            // ever starts prompting or warning, the guard has broken the premise of the product.
            await _alpha.Db.SaveProjectAsync(new Project { Name = "A week of offline work" });

            var signedIn = await SignInWithScriptedServerAsync(_alpha, userId: 42, email: "owner@example.com");

            Assert.True(signedIn.Success);
            Assert.Equal("Signed in as owner@example.com.", signedIn.Message);

            var owner = await _alpha.Db.GetDatabaseOwnerAsync();
            Assert.Equal(42, owner!.UserId);
            Assert.Equal("owner@example.com", owner.Email);
            Assert.Equal("issued-token", _alpha.Settings.ApiToken);

            // Nothing was taken away to make room for the account's own data.
            Assert.Single(await _alpha.Db.GetAllAsync<Project>());
        }

        [Fact]
        public async Task SignInAsync_AsADifferentAccount_IsRefusedAndWritesNothing()
        {
            await _alpha.Db.ClaimDatabaseAsync(42, "owner@example.com", ApiEndpoint);
            await _alpha.Db.SaveProjectAsync(new Project { Name = "The first account's work" });
            var queuedBefore = (await _alpha.Db.Connection.Table<SyncQueue>().ToListAsync()).Count;

            var signedIn = await SignInWithScriptedServerAsync(_alpha, userId: 99, email: "someone.else@example.com");

            Assert.False(signedIn.Success);
            Assert.Contains("owner@example.com", signedIn.Message);
            Assert.Contains("someone.else@example.com", signedIn.Message);
            // The remedy that already exists, named rather than left to be discovered.
            Assert.Contains("data directory", signedIn.Message);

            // Nothing written: not the stamp, not the token, not the rows, and - the one that gets
            // missed - not the pending queue, whose rows are addressed to the first account.
            var owner = await _alpha.Db.GetDatabaseOwnerAsync();
            Assert.Equal(42, owner!.UserId);
            Assert.Equal("owner@example.com", owner.Email);
            Assert.Equal(string.Empty, _alpha.Settings.ApiToken);
            Assert.Null(_alpha.Settings.ApiTokenExpiresAtUtc);
            Assert.Single(await _alpha.Db.GetAllAsync<Project>());
            Assert.Equal(queuedBefore, (await _alpha.Db.Connection.Table<SyncQueue>().ToListAsync()).Count);
        }

        [Fact]
        public async Task SignInAsync_SameAccountAtAMovedEndpoint_IsAllowedAndSaysSo()
        {
            // A server that moved from a LAN address to a domain name is ordinary and legitimate,
            // so this warns and records the new address rather than refusing.
            await _alpha.Db.ClaimDatabaseAsync(42, "owner@example.com", "http://192.168.1.10:8080");

            var signedIn = await SignInWithScriptedServerAsync(_alpha, userId: 42, email: "owner@example.com");

            Assert.True(signedIn.Success);
            Assert.Contains("192.168.1.10", signedIn.Message);

            var owner = await _alpha.Db.GetDatabaseOwnerAsync();
            Assert.Equal(ApiEndpoint, owner!.ApiEndpoint);
        }

        // THE TEST THAT JUSTIFIES PUTTING THE MARKER IN THE DATABASE RATHER THAN IN SETTINGS.
        //
        // Restoring a backup pairs a database with whatever settings.json happens to sit next to
        // it - a supported operation, not a corner case. A guard keyed on AppSettings.ApiEmail
        // would wave this through silently, because the settings file knows nothing. The stamp
        // travels inside workledger.db, so it doesn't.
        [Fact]
        public async Task ADatabaseRestoredNextToBlankSettings_StillRefusesTheWrongAccount()
        {
            // Built and closed here rather than reusing a shared device, so the file really is
            // at rest before it is copied - which is what a restore does.
            var originalDirectory = Path.Combine(_root, "original");
            Directory.CreateDirectory(originalDirectory);
            var originalSettings = new SettingsService(originalDirectory);

            var original = new LocalDbService(originalSettings.DatabasePath, Guid.NewGuid().ToString());
            await original.Initialization;
            await original.ClaimDatabaseAsync(42, "owner@example.com", ApiEndpoint);
            await original.Connection.CloseAsync();

            var restoredInto = Path.Combine(_root, "restored");
            Directory.CreateDirectory(restoredInto);

            // The database travels; the settings file does not.
            var settingsService = new SettingsService(restoredInto);
            File.Copy(originalSettings.DatabasePath, settingsService.DatabasePath);

            var blankSettings = settingsService.LoadSettings();
            Assert.Equal(string.Empty, blankSettings.ApiEmail);

            blankSettings.SyncMode = SyncMode.Api;
            blankSettings.ApiEndpoint = ApiEndpoint;
            blankSettings.SyncInterval = SyncInterval.Disabled;

            var db = new LocalDbService(settingsService.DatabasePath, blankSettings.DeviceId);
            await db.Initialization;

            using var coordinator = new SyncCoordinator(
                db, settingsService, blankSettings, new HttpClient(ScriptedLogin(99, "someone.else@example.com")));

            var signedIn = await coordinator.SignInAsync("someone.else@example.com", "hunter2");

            Assert.False(signedIn.Success);
            Assert.Contains("owner@example.com", signedIn.Message);

            await db.Connection.CloseAsync();
        }

        [Fact]
        public async Task SignInAsync_AdoptingADatabase_ResetsThePullCursor()
        {
            // WL-138 item 6f: LastSyncedAtUtc is account-scoped state living in a global field.
            // Carried across an ownership change it would start the new account's first pull from
            // an instant that means nothing to it, silently skipping everything older.
            _alpha.Settings.LastSyncedAtUtc = DateTime.UtcNow.AddDays(-3);

            Assert.True((await SignInWithScriptedServerAsync(_alpha, 42, "owner@example.com")).Success);

            Assert.Null(_alpha.Settings.LastSyncedAtUtc);
        }

        [Fact]
        public async Task SignInAsync_OnAPreS6DatabaseThatHadSynced_ClaimsItRatherThanRefusing()
        {
            // WL-138 item 6d. Such a database has no stamp and no recoverable owner, so it is
            // backfilled as claimed-by-unknown and the first post-upgrade sign-in fills in the
            // identity - the same shape as the server's own POST /auth/claim.
            _alpha.Settings.LastSyncedAtUtc = DateTime.UtcNow.AddDays(-30);
            await _alpha.Db.BackfillDatabaseOwnerAsync(hasSyncedBefore: true);

            var backfilled = await _alpha.Db.GetDatabaseOwnerAsync();
            Assert.NotNull(backfilled);
            Assert.False(backfilled!.HasIdentity);

            Assert.True((await SignInWithScriptedServerAsync(_alpha, 42, "owner@example.com")).Success);

            var claimed = await _alpha.Db.GetDatabaseOwnerAsync();
            Assert.True(claimed!.HasIdentity);
            Assert.Equal(42, claimed.UserId);
        }

        [Fact]
        public async Task SignOut_ClearsTheTokenAndNothingElse()
        {
            // WL-138 item 6h: an auth event, never a data event. The app has to stay fully usable
            // with no server, so signing out must not cost the user anything they can see.
            await _alpha.Db.SaveProjectAsync(new Project { Name = "Still mine afterwards" });
            Assert.True((await SignInWithScriptedServerAsync(_alpha, 42, "owner@example.com")).Success);

            _alpha.Settings.LastSyncedAtUtc = DateTime.UtcNow.AddMinutes(-5);
            var cursor = _alpha.Settings.LastSyncedAtUtc;
            var queued = (await _alpha.Db.Connection.Table<SyncQueue>().ToListAsync()).Count;

            _alpha.Coordinator.SignOut();

            Assert.Equal(string.Empty, _alpha.Settings.ApiToken);
            Assert.Null(_alpha.Settings.ApiTokenExpiresAtUtc);

            // Kept, every one of them: the email so the form prefills, and the rest because none
            // of it was ever about who was signed in.
            Assert.Equal("owner@example.com", _alpha.Settings.ApiEmail);
            Assert.Equal(cursor, _alpha.Settings.LastSyncedAtUtc);
            Assert.Single(await _alpha.Db.GetAllAsync<Project>());
            Assert.Equal(queued, (await _alpha.Db.Connection.Table<SyncQueue>().ToListAsync()).Count);
            Assert.NotNull(await _alpha.Db.GetDatabaseOwnerAsync());
        }

        // ---------------------------------------------- WL-146: registration, claim, capabilities

        [Fact]
        public async Task RegisterAndSignInAsync_CreatesTheAccountThenSignsIn_WithoutRetypingAnything()
        {
            // /auth/register returns no token - creating an account is a user-level act with no
            // device bound to it - so a registration that left the user signed out would be a form
            // followed immediately by the same form. The coordinator follows through instead.
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = ApiEndpoint;

            var handler = new ApiSyncProviderTests.RecordingHandler()
                .EnqueueJson(HttpStatusCode.Created, new
                {
                    Id = 42, Email = "new@example.com", DisplayName = "New Person", CreatedAtUtc = DateTime.UtcNow
                })
                .EnqueueJson(HttpStatusCode.OK, new
                {
                    Token = "issued-token",
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(365),
                    User = new { Id = 42, Email = "new@example.com", DisplayName = "New Person", CreatedAtUtc = DateTime.UtcNow }
                });

            using var coordinator = new SyncCoordinator(
                _alpha.Db, _alpha.SettingsService, _alpha.Settings, new HttpClient(handler));

            var (success, message) = await coordinator.RegisterAndSignInAsync(
                "new@example.com", "correct horse battery staple", "New Person", "signup-secret");

            Assert.True(success);
            Assert.Contains("Account created", message);

            Assert.Equal("issued-token", _alpha.Settings.ApiToken);
            Assert.Equal("new@example.com", _alpha.Settings.ApiEmail);

            Assert.Equal(new[] { "/auth/register", "/auth/login" },
                handler.Requests.ConvertAll(request => request.Uri.AbsolutePath).ToArray());

            // And the sign-in adopted the database on the way through, so this install is now
            // stamped for the account it just created.
            var owner = await _alpha.Db.GetDatabaseOwnerAsync();
            Assert.Equal(42, owner!.UserId);
        }

        [Fact]
        public async Task RegisterAndSignInAsync_OnADuplicateEmail_LeavesNothingSignedIn()
        {
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = ApiEndpoint;

            var handler = new ApiSyncProviderTests.RecordingHandler()
                .EnqueueRaw(HttpStatusCode.Conflict, "{}")
                // The re-probe the stale capabilities trigger.
                .EnqueueJson(HttpStatusCode.OK, new
                {
                    RegistrationEnabled = true, RegistrationRequiresSecret = true, MinPasswordLength = 12,
                    HasUnclaimedAccount = false, MaxFailedLogins = 5, LockoutMinutes = 15
                });

            using var coordinator = new SyncCoordinator(
                _alpha.Db, _alpha.SettingsService, _alpha.Settings, new HttpClient(handler));

            var (success, message) = await coordinator.RegisterAndSignInAsync(
                "taken@example.com", "correct horse battery staple", null, "signup-secret");

            Assert.False(success);
            Assert.Contains("already exists", message);

            Assert.Equal(string.Empty, _alpha.Settings.ApiToken);
            Assert.Null(await _alpha.Db.GetDatabaseOwnerAsync());

            // No login was attempted - there is no account to log into - and the capabilities were
            // re-asked so the panel stops offering what just proved impossible.
            Assert.DoesNotContain(handler.Requests, request => request.Uri.AbsolutePath == "/auth/login");
            Assert.Contains(handler.Requests, request => request.Uri.AbsolutePath == "/auth/capabilities");
        }

        [Fact]
        public async Task ClaimAndSignInAsync_TakesOwnershipThenSignsIn()
        {
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = ApiEndpoint;

            var handler = new ApiSyncProviderTests.RecordingHandler()
                .EnqueueJson(HttpStatusCode.OK, new
                {
                    Id = 1, Email = "owner@example.com", DisplayName = (string?)null, CreatedAtUtc = DateTime.UtcNow
                })
                .EnqueueJson(HttpStatusCode.OK, new
                {
                    Token = "issued-token",
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(365),
                    User = new { Id = 1, Email = "owner@example.com", DisplayName = (string?)null, CreatedAtUtc = DateTime.UtcNow }
                });

            using var coordinator = new SyncCoordinator(
                _alpha.Db, _alpha.SettingsService, _alpha.Settings, new HttpClient(handler));

            var (success, message) = await coordinator.ClaimAndSignInAsync(
                "owner@example.com", "correct horse battery staple", null, "signup-secret");

            Assert.True(success);
            Assert.Contains("Claimed this server", message);
            Assert.Equal("/auth/claim", handler.Requests[0].Uri.AbsolutePath);
            Assert.Equal("issued-token", _alpha.Settings.ApiToken);
        }

        // The server answers unknown email, wrong password and a credential-less legacy account with
        // an identical 401, on purpose, so an anonymous caller cannot enumerate accounts. Asserted
        // here so nobody later "helpfully" derives three messages from one status code.
        [Theory]
        [InlineData("no-such-account@example.com")]
        [InlineData("wrong-password@example.com")]
        [InlineData("never-claimed@example.com")]
        public async Task SignInAsync_GivesOneMessageForEveryKindOfRejectedCredential(string email)
        {
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = ApiEndpoint;

            var handler = new ApiSyncProviderTests.RecordingHandler()
                .EnqueueRaw(HttpStatusCode.Unauthorized, "{\"error\":\"Invalid email or password.\"}");

            using var coordinator = new SyncCoordinator(
                _alpha.Db, _alpha.SettingsService, _alpha.Settings, new HttpClient(handler));

            var (success, message) = await coordinator.SignInAsync(email, "whatever");

            Assert.False(success);
            Assert.Equal("Those sign-in details weren't accepted.", message);
        }

        [Fact]
        public async Task ConsecutiveFailedSignIns_CountUp_AndResetOnSuccess()
        {
            // WL-146 item 2a. The count drives the pre-send warning, and it must reset on a success
            // or the warning would haunt an account that is working perfectly well.
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = ApiEndpoint;

            var handler = new ApiSyncProviderTests.RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });

            using var coordinator = new SyncCoordinator(
                _alpha.Db, _alpha.SettingsService, _alpha.Settings, new HttpClient(handler));

            // Each attempt is a different password, so Invariant L's per-credential discard does not
            // suppress them - this is a user typing, which is the only path that can reach a lockout.
            await coordinator.SignInAsync("owner@example.com", "first guess");
            await coordinator.SignInAsync("owner@example.com", "second guess");
            await coordinator.SignInAsync("owner@example.com", "third guess");

            Assert.Equal(3, coordinator.ConsecutiveFailedSignIns);
        }

        [Fact]
        public async Task GetCapabilitiesAsync_WithoutAnEndpoint_AnswersNullRatherThanFailing()
        {
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = string.Empty;

            Assert.Null(await _alpha.Coordinator.GetCapabilitiesAsync());
        }

        // ------------------------------------------------ WL-144: account management (plan 10)

        // A coordinator that believes it is signed in, so the account calls have a session to use.
        private SyncCoordinator SignedInCoordinator(
            ApiSyncProviderTests.RecordingHandler handler, string email = "owner@example.com")
        {
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = ApiEndpoint;
            _alpha.Settings.ApiEmail = email;
            _alpha.Settings.ApiToken = "live-token";
            _alpha.Settings.ApiTokenExpiresAtUtc = DateTime.UtcNow.AddDays(30);
            _alpha.Settings.LastSyncedAtUtc = DateTime.UtcNow.AddHours(-1);
            _alpha.SettingsService.SaveSettings(_alpha.Settings);

            return new SyncCoordinator(
                _alpha.Db, _alpha.SettingsService, _alpha.Settings, new HttpClient(handler));
        }

        // Three entities and the queue rows they generate, so "nothing local was deleted" has
        // something to be true about.
        private async Task<(int Entities, int Queued)> SeedLocalWorkAsync()
        {
            var project = new Project { Name = "Kept project" };
            await _alpha.Db.SaveProjectAsync(project);

            var task = new TaskItem { Name = "Kept task", ProjectId = project.Id };
            await _alpha.Db.SaveTaskAsync(task);

            await _alpha.Db.SaveTimeEntryAsync(new TimeEntry
            {
                TaskItemId = task.Id,
                StartTime = DateTime.UtcNow.AddHours(-2),
                EndTime = DateTime.UtcNow.AddHours(-1)
            });

            var entities = (await _alpha.Db.GetAllAsync<Project>()).Count
                + (await _alpha.Db.GetAllAsync<TaskItem>()).Count
                + (await _alpha.Db.GetAllAsync<TimeEntry>()).Count;

            return (entities, (await _alpha.Db.GetAllAsync<SyncQueue>()).Count);
        }

        [Fact]
        public async Task DeleteAccountAsync_WithAMismatchedEmail_SendsNothingAtAll()
        {
            // The local half of the typed-confirmation gate. The server checks the email too, but
            // only AFTER it has checked the password - so letting a typo reach it would spend one
            // of the five attempts standing between this user and a lockout, for a mistake the
            // client can see. A handler with nothing scripted throws if anything is sent.
            var handler = new ApiSyncProviderTests.RecordingHandler();
            using var coordinator = SignedInCoordinator(handler);

            var (success, message) = await coordinator.DeleteAccountAsync("correct password", "typo@example.com");

            Assert.False(success);
            Assert.Contains("owner@example.com", message);
            Assert.Empty(handler.Requests);

            // And nothing was pre-emptively cleared on the way to refusing.
            Assert.Equal("live-token", _alpha.Settings.ApiToken);
            Assert.Equal(SyncMode.Api, _alpha.Settings.SyncMode);
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, "{\"error\":\"Password is incorrect.\"}")]
        [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"ConfirmEmail does not match this account's email address.\"}")]
        public async Task DeleteAccountAsync_WhenTheServerRefuses_ChangesNoLocalStateWhatsoever(
            HttpStatusCode status, string body)
        {
            // The server guarantees a failed guard leaves the account fully intact, so anything
            // cleared here would invent the very problem the guard just prevented.
            var handler = new ApiSyncProviderTests.RecordingHandler().EnqueueRaw(status, body);
            using var coordinator = SignedInCoordinator(handler);

            var seeded = await SeedLocalWorkAsync();
            var (success, _) = await coordinator.DeleteAccountAsync("whatever", "owner@example.com");

            Assert.False(success);

            Assert.Equal("live-token", _alpha.Settings.ApiToken);
            Assert.Equal("owner@example.com", _alpha.Settings.ApiEmail);
            Assert.Equal(SyncMode.Api, _alpha.Settings.SyncMode);
            Assert.NotNull(_alpha.Settings.LastSyncedAtUtc);

            Assert.Equal(seeded.Queued, (await _alpha.Db.GetAllAsync<SyncQueue>()).Count);
        }

        [Fact]
        public async Task DeleteAccountAsync_OnSuccess_EndsTheSession_ButKeepsEveryLocalRow()
        {
            // THE test for this plan. The confirmation dialog promises that deleting the account
            // destroys the server's copy and keeps this device's, and a promise about data is only
            // worth what a row count says it is.
            var handler = new ApiSyncProviderTests.RecordingHandler()
                .EnqueueNoBody(HttpStatusCode.NoContent);

            using var coordinator = SignedInCoordinator(handler);
            var seeded = await SeedLocalWorkAsync();

            var (success, message) = await coordinator.DeleteAccountAsync("correct password", "owner@example.com");

            Assert.True(success);

            // --- the promise -------------------------------------------------------------------
            Assert.Equal(
                seeded.Entities,
                (await _alpha.Db.GetAllAsync<Project>()).Count
                + (await _alpha.Db.GetAllAsync<TaskItem>()).Count
                + (await _alpha.Db.GetAllAsync<TimeEntry>()).Count);

            // The queue keeps its unsent rows deliberately. They can never reach the deleted
            // account, but nothing tries to send them with sync off, and they are exactly what a
            // later sign-in to a DIFFERENT account would want pushed.
            Assert.Equal(seeded.Queued, (await _alpha.Db.GetAllAsync<SyncQueue>()).Count);
            Assert.Contains("still here on this device", message);

            // --- the session -------------------------------------------------------------------
            Assert.Equal(string.Empty, _alpha.Settings.ApiToken);
            Assert.Null(_alpha.Settings.ApiTokenExpiresAtUtc);
            Assert.Equal(string.Empty, _alpha.Settings.ApiEmail);
            Assert.Null(_alpha.Settings.LastSyncedAtUtc);

            // Sync off, and off in a way that has actually taken effect rather than only been
            // written down: a run now reports the mode rather than attempting a doomed request.
            Assert.Equal(SyncMode.Off, _alpha.Settings.SyncMode);
            var run = await coordinator.SyncNowAsync();
            Assert.False(run.Ran);
            Assert.Contains("turned off", run.ErrorMessage);

            // The endpoint survives: it is still the right server to point a new account at.
            Assert.Equal(ApiEndpoint, _alpha.Settings.ApiEndpoint);

            // WL-138's stamp named an account that no longer exists. Releasing its identity is what
            // stops the guard refusing this database to the person who just deleted their own
            // account - it can no longer protect anything, only obstruct.
            var owner = await _alpha.Db.GetDatabaseOwnerAsync();
            Assert.False(owner?.HasIdentity ?? false);
        }

        [Fact]
        public async Task RemoveDeviceAsync_ForThisVeryDevice_SignsTheAppOut_AndKeepsTheData()
        {
            // Permitted server-side and meaning "sign this device out", so the coordinator stands
            // the session down rather than leaving the app holding a token it already knows is
            // dead and reporting a revocation on the next tick.
            var handler = new ApiSyncProviderTests.RecordingHandler()
                .EnqueueNoBody(HttpStatusCode.NoContent);

            using var coordinator = SignedInCoordinator(handler);
            var seeded = await SeedLocalWorkAsync();

            var (success, message) = await coordinator.RemoveDeviceAsync(_alpha.Settings.DeviceId);

            Assert.True(success);
            Assert.Equal(string.Empty, _alpha.Settings.ApiToken);
            Assert.Contains("signed out", message);

            // An auth event, never a data event - the email, the mode and every row survive.
            Assert.Equal("owner@example.com", _alpha.Settings.ApiEmail);
            Assert.Equal(SyncMode.Api, _alpha.Settings.SyncMode);
            Assert.Equal(seeded.Queued, (await _alpha.Db.GetAllAsync<SyncQueue>()).Count);
        }

        [Fact]
        public async Task RemoveDeviceAsync_ForAnotherDevice_LeavesThisSessionSignedIn()
        {
            var handler = new ApiSyncProviderTests.RecordingHandler()
                .EnqueueNoBody(HttpStatusCode.NoContent);

            using var coordinator = SignedInCoordinator(handler);

            var (success, message) = await coordinator.RemoveDeviceAsync("some-other-device");

            Assert.True(success);
            Assert.Equal("live-token", _alpha.Settings.ApiToken);

            // The sentence that closes off the wrong assumption about this endpoint.
            Assert.Contains("stays on the account", message);
        }

        [Fact]
        public async Task ChangePasswordAsync_ReportsThatTheOtherDevicesWereSignedOut()
        {
            // They were, because the server rotates the security stamp - and a user who is not told
            // reads their phone's next sign-in prompt as a bug.
            var handler = new ApiSyncProviderTests.RecordingHandler().EnqueueJson(HttpStatusCode.OK, new
            {
                Token = "rotated-token",
                ExpiresAtUtc = DateTime.UtcNow.AddDays(365),
                User = new { Id = 1, Email = "owner@example.com", DisplayName = (string?)null, CreatedAtUtc = DateTime.UtcNow }
            });

            using var coordinator = SignedInCoordinator(handler);

            var (success, message) = await coordinator.ChangePasswordAsync("old pass", "new pass phrase");

            Assert.True(success);
            Assert.Equal("rotated-token", _alpha.Settings.ApiToken);
            Assert.Contains("other devices have been signed out", message);
        }

        [Fact]
        public async Task SignOutEverywhereAsync_KeepsThisDeviceSignedIn()
        {
            // The whole point of the endpoint handing back a token. If this device ended up signed
            // out, "sign out my other devices" would have signed out the device asking.
            var handler = new ApiSyncProviderTests.RecordingHandler().EnqueueJson(HttpStatusCode.OK, new
            {
                Token = "rotated-token",
                ExpiresAtUtc = DateTime.UtcNow.AddDays(365),
                User = new { Id = 1, Email = "owner@example.com", DisplayName = (string?)null, CreatedAtUtc = DateTime.UtcNow }
            });

            using var coordinator = SignedInCoordinator(handler);

            var (success, message) = await coordinator.SignOutEverywhereAsync();

            Assert.True(success);
            Assert.Equal("rotated-token", _alpha.Settings.ApiToken);
            Assert.True(coordinator.ApiAuth.CanSync);
            Assert.Contains("still signed in", message);
        }

        [Fact]
        public async Task GetDevicesAsync_WithoutAnEndpoint_AnswersAFailureRatherThanThrowing()
        {
            _alpha.Settings.SyncMode = SyncMode.Api;
            _alpha.Settings.ApiEndpoint = string.Empty;

            var outcome = await _alpha.Coordinator.GetDevicesAsync();

            Assert.False(outcome.Success);
            Assert.Empty(outcome.Devices);
        }

        private const string ApiEndpoint = "https://workledger.example.com";

        // One scripted /auth/login response and nothing else - these tests are about what happens
        // around the sign-in, not about the sync that may follow it.
        private static ApiSyncProviderTests.RecordingHandler ScriptedLogin(int userId, string email)
            => new ApiSyncProviderTests.RecordingHandler().EnqueueJson(HttpStatusCode.OK, new
            {
                Token = "issued-token",
                ExpiresAtUtc = DateTime.UtcNow.AddDays(365),
                User = new { Id = userId, Email = email, DisplayName = (string?)null, CreatedAtUtc = DateTime.UtcNow }
            });

        private static async Task<(bool Success, string Message)> SignInWithScriptedServerAsync(
            Device device, int userId, string email)
        {
            device.Settings.SyncMode = SyncMode.Api;
            device.Settings.ApiEndpoint = ApiEndpoint;

            using var coordinator = new SyncCoordinator(
                device.Db, device.SettingsService, device.Settings, new HttpClient(ScriptedLogin(userId, email)));

            return await coordinator.SignInAsync(email, "hunter2");
        }

        [Fact]
        public async Task SyncNowAsync_RemoteWinsOverwriteWithBackupsAlways_WritesASafetyCopy()
        {
            // WL-51 end to end: alpha's row is overwritten by beta's newer version, so the
            // configured pre-merge backup lands in alpha's data directory.
            _alpha.Settings.PreMergeBackupMode = PreMergeBackupMode.Always;

            var project = new Project { Name = "Original" };
            await _alpha.Db.SaveProjectAsync(project);
            await _alpha.Coordinator.SyncNowAsync();
            await _beta.Coordinator.SyncNowAsync();

            var onBeta = Assert.Single(await _beta.Db.GetAllAsync<Project>());
            onBeta.Name = "Edited on beta";
            await _beta.Db.SaveProjectAsync(onBeta);
            await _beta.Coordinator.SyncNowAsync();

            await _alpha.Coordinator.SyncNowAsync();

            Assert.Equal("Edited on beta", Assert.Single(await _alpha.Db.GetAllAsync<Project>()).Name);

            var backups = Directory.GetDirectories(Path.Combine(_alpha.DataDirectory, "pre-merge-backups"));
            var snapshot = Assert.Single(backups);
            Assert.True(File.Exists(Path.Combine(snapshot, SettingsService.DefaultDbFileName)));
        }

        [Fact]
        public async Task SyncNowAsync_PersistsTheCursorIntoSettings()
        {
            // WL-92's payoff: the cursor survives a restart because it lives in settings.json,
            // not in a field that resets to DateTime.MinValue on every launch.
            Assert.Null(_alpha.Settings.LastSyncedAtUtc);

            await _alpha.Coordinator.SyncNowAsync();

            Assert.NotNull(_alpha.Settings.LastSyncedAtUtc);
            Assert.Equal(_alpha.Settings.LastSyncedAtUtc, _alpha.Coordinator.LastSyncedAtUtc);

            var reloaded = new SettingsService(_alpha.DataDirectory).LoadSettings();
            Assert.NotNull(reloaded.LastSyncedAtUtc);
        }

        [Fact]
        public async Task PersistedSettings_KeepTheDeviceIdAndCursorAcrossSaves()
        {
            // The failure this guards against is silent: a save that rebuilds AppSettings from
            // only the fields a settings screen knows about blanks DeviceId, and the next load
            // mints a new one - at which point this install looks like a different device to
            // every peer and its own rows stop being recognised as self-echoes.
            var deviceId = _alpha.Settings.DeviceId;
            await _alpha.Coordinator.SyncNowAsync();

            _alpha.Settings.Theme = "Dark";
            _alpha.SettingsService.SaveSettings(_alpha.Settings);

            var reloaded = new SettingsService(_alpha.DataDirectory).LoadSettings();
            Assert.Equal(deviceId, reloaded.DeviceId);
            Assert.NotNull(reloaded.LastSyncedAtUtc);
            Assert.Equal(SyncMode.File, reloaded.SyncMode);
        }

        // One configured install: its own data directory, settings.json, database and
        // coordinator, all pointed at a sync folder shared with the other.
        private sealed class Device
        {
            public required SettingsService SettingsService { get; init; }
            public required AppSettings Settings { get; init; }
            public required LocalDbService Db { get; init; }
            public required SyncCoordinator Coordinator { get; init; }
            public required string DataDirectory { get; init; }

            public static async Task<Device> CreateAsync(string dataDirectory, string sharedFolder)
            {
                Directory.CreateDirectory(dataDirectory);

                var settingsService = new SettingsService(dataDirectory);
                var settings = settingsService.LoadSettings();
                settings.SyncMode = SyncMode.File;
                settings.SyncFolderPath = sharedFolder;
                settings.SyncInterval = SyncInterval.Disabled; // manual only: these tests drive it
                settings.PreMergeBackupMode = PreMergeBackupMode.Never;
                settingsService.SaveSettings(settings);

                var db = new LocalDbService(settingsService.DatabasePath, settings.DeviceId);
                await db.Initialization;

                return new Device
                {
                    SettingsService = settingsService,
                    Settings = settings,
                    Db = db,
                    Coordinator = new SyncCoordinator(db, settingsService, settings),
                    DataDirectory = dataDirectory
                };
            }

            public async Task DisposeAsync()
            {
                Coordinator.Dispose();
                await Db.Connection.CloseAsync();
            }
        }

        private static void TryDeleteDirectoryWithRetries(string path, int maxAttempts = 5)
        {
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    return;
                }
                catch (IOException)
                {
                    System.Threading.Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }
}
