using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using WorkLedger_Client.Sync;
using Xunit;

namespace WorkLedger.Tests
{
    // WL-148, plan 11. One complete local database per signed-in account.
    //
    // Two halves, and they are tested here together on purpose: the mechanics (the registry, the
    // one-time migration of a pre-profiles install, the live switch) and the rule that drives them
    // (what SignInAsync does when the account signing in is not the one this database belongs to).
    // Separating them would leave the interesting part - a sign-in that moves the app to a
    // different database while it is running - covered by neither.
    //
    // Note what is NOT here: WL-138's refusal tests in SyncCoordinatorTests are untouched and still
    // pass, because a coordinator built without a ProfileService still refuses. That is the design,
    // not an accident of test setup - the ownership guard is the invariant and profiles are the
    // convenience built on top of it.
    public class ProfileServiceTests : IAsyncLifetime
    {
        private const string ApiEndpoint = "https://workledger.example.com";

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"WorkLedgerProfileTests_{Guid.NewGuid()}");
        private readonly List<Install> _installs = new();

        public Task InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            foreach (var install in _installs)
                await install.DisposeAsync();

            TryDeleteDirectoryWithRetries(_root);
        }

        // ------------------------------------------------------------------- migration

        [Fact]
        public async Task Migration_MovesAPreProfilesDatabaseIntoTheLocalProfile()
        {
            // The database is MOVED, not copied. Two copies of one database is the WL-74 bug class
            // - the launch-time copy that silently overwrote a live database with a stale snapshot
            // - so the assertion that the old path is empty afterwards is the point of this test,
            // not a detail of it.
            var dataDirectory = NewDataDirectory();
            var legacyDb = Path.Combine(dataDirectory, SettingsService.DefaultDbFileName);

            var legacy = new LocalDbService(legacyDb, Guid.NewGuid().ToString());
            await legacy.Initialization;
            await legacy.SaveProjectAsync(new Project { Name = "Work done before profiles existed" });
            await legacy.Connection.CloseAsync();

            var profiles = new ProfileService(dataDirectory);

            Assert.False(File.Exists(legacyDb));
            Assert.Equal(Profile.LocalProfileId, profiles.ActiveProfile.Id);

            var moved = new LocalDbService(profiles.DatabasePathFor(Profile.LocalProfileId), "device");
            await moved.Initialization;
            Assert.Equal("Work done before profiles existed", Assert.Single(await moved.GetAllAsync<Project>()).Name);
            await moved.Connection.CloseAsync();
        }

        [Fact]
        public void Migration_LiftsTheAccountSettingsOutOfTheRootSettingsFile()
        {
            // The four account-scoped fields move into the profile, and are REMOVED from the root
            // file rather than being left behind as a second copy - an access token in a file
            // nothing reads it from any more is exactly the kind of thing that turns up in a
            // support bundle years later.
            var dataDirectory = NewDataDirectory();
            File.WriteAllText(Path.Combine(dataDirectory, SettingsService.SettingsFileName), """
                {
                    "Theme": "Dark",
                    "DeviceId": "device-that-must-survive",
                    "ApiEndpoint": "https://workledger.example.com",
                    "ApiEmail": "owner@example.com",
                    "ApiToken": "a-token-from-before-profiles",
                    "ApiTokenExpiresAtUtc": "2027-01-01T00:00:00Z",
                    "LastSyncedAtUtc": "2026-09-01T12:00:00Z"
                }
                """);

            var profiles = new ProfileService(dataDirectory);
            var settingsService = new SettingsService(dataDirectory, profiles.ActiveProfileDirectory);
            var settings = settingsService.LoadSettings();

            Assert.Equal("owner@example.com", settings.ApiEmail);
            Assert.Equal("a-token-from-before-profiles", settings.ApiToken);
            Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), settings.ApiTokenExpiresAtUtc);
            Assert.Equal(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), settings.LastSyncedAtUtc);

            // Device-scoped settings stayed exactly where they were.
            Assert.Equal("device-that-must-survive", settings.DeviceId);
            Assert.Equal("Dark", settings.Theme);

            var rootJson = File.ReadAllText(Path.Combine(dataDirectory, SettingsService.SettingsFileName));
            Assert.DoesNotContain("a-token-from-before-profiles", rootJson);
            Assert.DoesNotContain("ApiToken", rootJson);
        }

        [Fact]
        public void Migration_RunsOnceAndIsAThingStartupCanRepeat()
        {
            var dataDirectory = NewDataDirectory();
            File.WriteAllText(
                Path.Combine(dataDirectory, SettingsService.SettingsFileName),
                """{ "ApiToken": "first" }""");

            new ProfileService(dataDirectory);

            // A second launch must not re-lift anything: the profile's own session is now the
            // authority, and a repeat migration reading an emptied root file would blank it.
            var profileSettings = Path.Combine(
                dataDirectory, ProfileService.ProfilesFolderName, Profile.LocalProfileId,
                SettingsService.ProfileSettingsFileName);
            File.WriteAllText(profileSettings, """{ "ApiToken": "renewed-since" }""");

            var second = new ProfileService(dataDirectory);
            var settings = new SettingsService(dataDirectory, second.ActiveProfileDirectory).LoadSettings();

            Assert.Equal("renewed-since", settings.ApiToken);
        }

        // ------------------------------------------------------------------- keys and registry

        [Fact]
        public void KeyFor_IsFilesystemSafeAndSeparatesAccountsThatShareAnEmail()
        {
            // WL-138 settles identity as normalized email AND UserId, so the key derives from both:
            // an email reassigned to a new user on a rebuilt server is a different account and must
            // not land in the same directory.
            var first = Profile.KeyFor(42, "Owner+Tag@Example.com");
            var second = Profile.KeyFor(99, "owner+tag@example.com");

            Assert.NotEqual(first, second);
            Assert.Equal(first, Profile.KeyFor(42, "  OWNER+tag@example.com "));
            Assert.All(first, c => Assert.True(char.IsLetterOrDigit(c) || c == '-', $"unsafe character '{c}'"));
            Assert.DoesNotContain(Path.GetInvalidFileNameChars(), first.Contains);
        }

        [Fact]
        public async Task ReconcileActiveProfileAsync_CorrectsTheRegistryFromTheDatabaseStamp()
        {
            // THE ONE INVARIANT: the stamp inside workledger.db is authoritative, the registry
            // entry is advisory. This is what makes restoring account A's backup into account B's
            // profile re-label the profile rather than silently mis-describe it.
            var install = await NewInstallAsync();
            await install.Db.ClaimDatabaseAsync(7, "restored@example.com", ApiEndpoint);

            await install.Profiles.ReconcileActiveProfileAsync(install.Db);

            var active = install.Profiles.ActiveProfile;
            Assert.Equal(7, active.UserId);
            Assert.Equal("restored@example.com", active.Email);
        }

        // ------------------------------------------------------------------- the switch

        [Fact]
        public async Task SwitchToAsync_GivesEachProfileItsOwnRows_AndHandsThemBackOnTheWayIn()
        {
            var install = await NewInstallAsync();
            await install.Db.SaveProjectAsync(new Project { Name = "First account's work" });

            var second = install.Profiles.EnsureProfileForAccount(99, "second@example.com", ApiEndpoint);
            var switched = await install.Profiles.SwitchToAsync(second.Id);

            Assert.True(switched.Success);
            Assert.Empty(await install.Db.GetAllAsync<Project>());

            await install.Db.SaveProjectAsync(new Project { Name = "Second account's work" });

            Assert.True((await install.Profiles.SwitchToAsync(Profile.LocalProfileId)).Success);

            // Nothing was merged, nothing was deleted, and neither account can see the other's.
            Assert.Equal(
                "First account's work",
                Assert.Single(await install.Db.GetAllAsync<Project>()).Name);
        }

        [Fact]
        public async Task SwitchToAsync_KeepsEachProfilesSessionInItsOwnFile()
        {
            var install = await NewInstallAsync();
            install.Settings.ApiEmail = "first@example.com";
            install.Settings.ApiToken = "first-token";
            install.Settings.LastSyncedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            install.SettingsService.SaveSettings(install.Settings);

            var second = install.Profiles.EnsureProfileForAccount(99, "second@example.com", ApiEndpoint);
            await install.Profiles.SwitchToAsync(second.Id);

            // A profile nobody has signed in to has no session at all - emphatically not the
            // previous one's.
            Assert.Equal(string.Empty, install.Settings.ApiToken);
            Assert.Equal(string.Empty, install.Settings.ApiEmail);
            Assert.Null(install.Settings.LastSyncedAtUtc);

            await install.Profiles.SwitchToAsync(Profile.LocalProfileId);

            Assert.Equal("first-token", install.Settings.ApiToken);
            Assert.Equal("first@example.com", install.Settings.ApiEmail);
            Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), install.Settings.LastSyncedAtUtc);
        }

        [Fact]
        public async Task SwitchToAsync_QuiescesEveryListenerBeforeTouchingTheDatabase()
        {
            // The sequence IS the safety argument: nothing may be in flight against the old
            // database when it closes, so every listener is stopped first and woken afterwards.
            var install = await NewInstallAsync();
            var listener = new RecordingProfileAware();
            install.Profiles.Register(listener);

            var second = install.Profiles.EnsureProfileForAccount(99, "second@example.com", ApiEndpoint);
            await install.Profiles.SwitchToAsync(second.Id);

            Assert.Equal(new[] { "changing", "changed" }, listener.Calls);
        }

        [Fact]
        public async Task SwitchToAsync_ToAProfileThatIsNotThere_FailsWithoutMovingAnything()
        {
            var install = await NewInstallAsync();
            await install.Db.SaveProjectAsync(new Project { Name = "Still here afterwards" });

            var result = await install.Profiles.SwitchToAsync("no-such-profile");

            Assert.False(result.Success);
            Assert.Equal(Profile.LocalProfileId, install.Profiles.ActiveProfile.Id);
            Assert.Single(await install.Db.GetAllAsync<Project>());
        }

        [Fact]
        public async Task SwitchToAsync_BeforeAttach_SaysSoRatherThanHalfSwitching()
        {
            var dataDirectory = NewDataDirectory();
            var profiles = new ProfileService(dataDirectory);

            var result = await profiles.SwitchToAsync(Profile.LocalProfileId);

            // Switching to the one you are already on is a no-op, so ask for a real one.
            var other = profiles.EnsureProfileForAccount(1, "someone@example.com", ApiEndpoint);
            result = await profiles.SwitchToAsync(other.Id);

            Assert.False(result.Success);
            Assert.Equal(Profile.LocalProfileId, profiles.ActiveProfile.Id);
        }

        [Fact]
        public async Task SwitchToAsync_StopsARunningTimerAndSavesItToTheProfileItWasRecordedIn()
        {
            // A running timer is the one thing in the app actively writing to the database, so it
            // is quiesced first - and its entry belongs to the profile it was recorded in, not to
            // the account being switched to. Losing the last stretch of tracked work would be a
            // poor way to reward using a feature.
            var install = await NewInstallAsync();
            var state = new AppStateService();
            var timer = new TimerService(install.Db, state);
            install.Profiles.Register(timer);

            var project = new Project { Name = "Tracked" };
            await install.Db.SaveProjectAsync(project);
            var task = new TaskItem { Name = "Tracked task", ProjectId = project.Id };
            await install.Db.SaveTaskAsync(task);
            await timer.StartTimerAsync(task);
            Assert.True(timer.IsRunning);

            var second = install.Profiles.EnsureProfileForAccount(99, "second@example.com", ApiEndpoint);
            Assert.True((await install.Profiles.SwitchToAsync(second.Id)).Success);

            Assert.False(timer.IsRunning);
            Assert.Null(state.ActiveTimeEntry);
            Assert.Empty(await install.Db.GetAllAsync<TimeEntry>());

            await install.Profiles.SwitchToAsync(Profile.LocalProfileId);

            var entry = Assert.Single(await install.Db.GetAllAsync<TimeEntry>());
            Assert.NotNull(entry.EndTime);
        }

        // ------------------------------------------------------------------- the sign-in rule

        [Fact]
        public async Task SignInAsync_AsADifferentAccount_MovesToItsOwnProfileInsteadOfRefusing()
        {
            // The case WL-138 could only refuse. Both accounts end up with a complete database of
            // their own, and the first one's work is exactly where it was left.
            var install = await NewInstallAsync();
            await install.Db.ClaimDatabaseAsync(42, "first@example.com", ApiEndpoint);
            await install.Db.SaveProjectAsync(new Project { Name = "First account's work" });

            var signedIn = await install.SignInAsync(99, "second@example.com");

            Assert.True(signedIn.Success);
            Assert.Contains("second@example.com", signedIn.Message);

            var active = install.Profiles.ActiveProfile;
            Assert.NotEqual(Profile.LocalProfileId, active.Id);
            Assert.Equal(99, active.UserId);

            // A fresh, empty database for the account that just signed in, holding its token.
            Assert.Empty(await install.Db.GetAllAsync<Project>());
            Assert.Equal("issued-token", install.Settings.ApiToken);
            Assert.Equal("second@example.com", install.Settings.ApiEmail);

            // And the first account's work, untouched, one switch away.
            Assert.True((await install.Profiles.SwitchToAsync(Profile.LocalProfileId)).Success);
            Assert.Equal("First account's work", Assert.Single(await install.Db.GetAllAsync<Project>()).Name);
        }

        [Fact]
        public async Task SignInAsync_LeavesTheOutgoingProfilesOwnTokenBehindIntact()
        {
            // The token dance. ApiSyncProvider writes the issued token into the LIVE settings,
            // which still belong to the profile being signed in FROM - so without handing that
            // profile its own session back first, switching would file account B's token under
            // account A and signing back into A would need a password it never lost.
            var install = await NewInstallAsync();
            await install.Db.ClaimDatabaseAsync(42, "first@example.com", ApiEndpoint);

            install.Settings.ApiEmail = "first@example.com";
            install.Settings.ApiToken = "first-accounts-token";
            install.Settings.ApiTokenExpiresAtUtc = DateTime.UtcNow.AddDays(30);
            install.SettingsService.SaveSettings(install.Settings);

            Assert.True((await install.SignInAsync(99, "second@example.com")).Success);
            Assert.Equal("issued-token", install.Settings.ApiToken);

            await install.Profiles.SwitchToAsync(Profile.LocalProfileId);

            Assert.Equal("first-accounts-token", install.Settings.ApiToken);
            Assert.Equal("first@example.com", install.Settings.ApiEmail);
        }

        [Fact]
        public async Task SignInAsync_WhenAProfileAlreadyExistsForTheAccount_SwitchesToItRatherThanCreatingASecond()
        {
            // One account, one profile - always. Two would split the same person's work across two
            // databases with nothing to say which half is which.
            var install = await NewInstallAsync();
            await install.Db.ClaimDatabaseAsync(42, "first@example.com", ApiEndpoint);

            var existing = install.Profiles.EnsureProfileForAccount(99, "second@example.com", ApiEndpoint);
            var profileCount = install.Profiles.Profiles.Count;

            Assert.True((await install.SignInAsync(99, "second@example.com")).Success);

            Assert.Equal(existing.Id, install.Profiles.ActiveProfile.Id);
            Assert.Equal(profileCount, install.Profiles.Profiles.Count);
        }

        [Fact]
        public async Task SignInAsync_AgainstAnUnclaimedProfile_AdoptsItInPlaceWithNoSwitchAtAll()
        {
            // THE OFFLINE-FIRST PATH, and the assertion is the absence of movement: a week of work
            // with no server, then a sign-in, and everything is simply kept where it is. No new
            // profile, no switch, no prompt.
            var install = await NewInstallAsync();
            await install.Db.SaveProjectAsync(new Project { Name = "A week of offline work" });

            var signedIn = await install.SignInAsync(42, "owner@example.com");

            Assert.True(signedIn.Success);
            Assert.Equal("Signed in as owner@example.com.", signedIn.Message);
            Assert.Equal(Profile.LocalProfileId, install.Profiles.ActiveProfile.Id);
            Assert.Single(install.Profiles.Profiles);
            Assert.Equal(42, install.Profiles.ActiveProfile.UserId);
            Assert.Single(await install.Db.GetAllAsync<Project>());
        }

        [Fact]
        public async Task SignInAsync_WhenTheSwitchCannotHappen_FallsBackToWl138sRefusal()
        {
            // A ProfileService that was never attached cannot switch, which stands in for every
            // way the switch could fail on a real machine - a locked file, a full disk. The answer
            // is the refusal, because refusing costs a sign-in and proceeding would commingle two
            // people's work permanently.
            var install = await NewInstallAsync(attach: false);
            await install.Db.ClaimDatabaseAsync(42, "first@example.com", ApiEndpoint);
            await install.Db.SaveProjectAsync(new Project { Name = "Not to be mixed with anyone else's" });

            var signedIn = await install.SignInAsync(99, "second@example.com");

            Assert.False(signedIn.Success);
            Assert.Contains("first@example.com", signedIn.Message);
            Assert.Contains("second@example.com", signedIn.Message);

            var owner = await install.Db.GetDatabaseOwnerAsync();
            Assert.Equal(42, owner!.UserId);
            Assert.Equal(Profile.LocalProfileId, install.Profiles.ActiveProfile.Id);
            Assert.Single(await install.Db.GetAllAsync<Project>());
        }

        [Fact]
        public async Task EveryProfileSharesOneDeviceId()
        {
            // DeviceId is the DEVICE's identity and the OriginDeviceId stamped on every row this
            // install writes. A per-profile one would make one machine look like several to the
            // server and break self-echo suppression in the orchestrator.
            var install = await NewInstallAsync();
            var deviceId = install.Settings.DeviceId;
            Assert.False(string.IsNullOrWhiteSpace(deviceId));

            var second = install.Profiles.EnsureProfileForAccount(99, "second@example.com", ApiEndpoint);
            await install.Profiles.SwitchToAsync(second.Id);

            Assert.Equal(deviceId, install.Settings.DeviceId);
            Assert.Equal(deviceId, install.SettingsService.LoadSettings().DeviceId);
        }

        // ------------------------------------------- WL-176: "I've reset my password" (plan 12 item 6)

        [Fact]
        public async Task ForgetServerVerdict_AfterALockout_SendsNothing_AndTheNewPasswordSignsInFirstTime()
        {
            // The trap the button exists for: the account locked itself out, its owner reset the
            // password in the web portal - which clears the server's counters - and came back to an
            // app still holding a LockedOut verdict that would keep Sign in disabled for the rest of
            // the window.
            var install = await NewInstallAsync();
            Assert.True((await install.SignInAsync(42, "owner@example.com")).Success);
            var profileId = install.Profiles.ActiveProfile.Id;

            var handler = new ApiSyncProviderTests.RecordingHandler()
                .EnqueueRaw(HttpStatusCode.Locked, "{}")
                .EnqueueJson(HttpStatusCode.OK, new
                {
                    Token = "token-after-reset",
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(365),
                    User = new { Id = 42, Email = "owner@example.com", DisplayName = (string?)null, CreatedAtUtc = DateTime.UtcNow }
                });

            using var coordinator = new SyncCoordinator(
                install.Db, install.SettingsService, install.Settings, new HttpClient(handler), install.Profiles);

            Assert.False((await coordinator.SignInAsync("owner@example.com", "the forgotten password")).Success);
            Assert.Equal(AuthState.LockedOut, coordinator.ApiAuth.State);
            Assert.Single(handler.Requests);

            Assert.True(coordinator.ForgetServerVerdict());

            // Sends nothing - not a login, not a whoami. It only re-enables Sign in.
            Assert.Single(handler.Requests);
            Assert.NotEqual(AuthState.LockedOut, coordinator.ApiAuth.State);

            // A reset rotates the security stamp, so the token held from before is dead and is not
            // kept around to be mistaken for a working session.
            Assert.Equal(string.Empty, install.Settings.ApiToken);

            // And the new password works on the first attempt: exactly one more request, a login.
            Assert.True((await coordinator.SignInAsync("owner@example.com", "the new password")).Success);
            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, request => Assert.Equal("/auth/login", request.Uri.AbsolutePath));
            Assert.Equal("token-after-reset", install.Settings.ApiToken);

            // Not a profile event: the account and its database are the same, only the credential
            // changed.
            Assert.Equal(profileId, install.Profiles.ActiveProfile.Id);
        }

        // ----------------------------------------------- WL-176: Detach from account (plan 12 item 7)

        [Fact]
        public async Task DetachFromAccount_KeepsEveryRow_AndTheNextSignInTakesTheDataOverInPlace()
        {
            // An account deleted in the web portal. This install never hears about it, so its stamp
            // and registry entry keep naming an account that no longer exists.
            var install = await NewInstallAsync();
            await install.Db.SaveProjectAsync(new Project { Name = "Work from a deleted account" });
            Assert.True((await install.SignInAsync(42, "gone@example.com")).Success);
            install.Coordinator.SignOut();

            var projects = (await install.Db.GetAllAsync<Project>()).Count;
            var queued = (await install.Db.GetAllAsync<SyncQueue>()).Count;
            var profileId = install.Profiles.ActiveProfile.Id;
            var profileCount = install.Profiles.Profiles.Count;

            var detached = await install.Coordinator.DetachFromAccountAsync();
            Assert.True(detached.Success);

            // Both halves: the stamp reads as Unclaimed and the registry entry names nobody.
            Assert.False((await install.Db.GetDatabaseOwnerAsync())?.HasIdentity ?? false);
            Assert.False(install.Profiles.ActiveProfile.HasIdentity);

            // Not one row touched, the unsent queue included - it is what the next account wants.
            Assert.Equal(projects, (await install.Db.GetAllAsync<Project>()).Count);
            Assert.Equal(queued, (await install.Db.GetAllAsync<SyncQueue>()).Count);

            // The account-scoped settings belonged to the account being forgotten.
            Assert.Equal(string.Empty, install.Settings.ApiEmail);
            Assert.Null(install.Settings.LastSyncedAtUtc);

            // The same person re-registers in the portal and gets a new UserId. Their data is
            // adopted where it is: no new profile, no switch, nothing stranded.
            Assert.True((await install.SignInAsync(77, "gone@example.com")).Success);

            Assert.Equal(profileId, install.Profiles.ActiveProfile.Id);
            Assert.Equal(profileCount, install.Profiles.Profiles.Count);
            Assert.Equal(77, install.Profiles.ActiveProfile.UserId);
            Assert.Equal("Work from a deleted account", Assert.Single(await install.Db.GetAllAsync<Project>()).Name);
        }

        [Fact]
        public async Task ReleasingOnlyTheStamp_StillStrandsTheData_WhichIsWhyDetachDoesBoth()
        {
            // The reason ProfileService.ReleaseActiveProfile exists. With the database unclaimed but
            // the registry still naming the old account, the sign-in rule does not adopt in place -
            // it creates a fresh profile, and the old work is left behind in the one it came from.
            var install = await NewInstallAsync();
            await install.Db.SaveProjectAsync(new Project { Name = "Stranded work" });
            Assert.True((await install.SignInAsync(42, "gone@example.com")).Success);
            install.Coordinator.SignOut();
            var profileId = install.Profiles.ActiveProfile.Id;

            await install.Db.ReleaseDatabaseOwnerAsync();
            Assert.True((await install.SignInAsync(77, "gone@example.com")).Success);

            Assert.NotEqual(profileId, install.Profiles.ActiveProfile.Id);
            Assert.Empty(await install.Db.GetAllAsync<Project>());
        }

        [Fact]
        public async Task DetachFromAccount_IsRefusedWhileSignedIn()
        {
            // Signed in, detaching would contradict the session in hand.
            var install = await NewInstallAsync();
            Assert.True((await install.SignInAsync(42, "owner@example.com")).Success);

            var detached = await install.Coordinator.DetachFromAccountAsync();

            Assert.False(detached.Success);
            Assert.Equal(42, (await install.Db.GetDatabaseOwnerAsync())!.UserId);
            Assert.True(install.Profiles.ActiveProfile.HasIdentity);
            Assert.Equal("issued-token", install.Settings.ApiToken);
        }

        [Fact]
        public async Task DetachFromAccount_OnDataThatWasNeverAttached_SaysSoAndChangesNothing()
        {
            var install = await NewInstallAsync();

            var detached = await install.Coordinator.DetachFromAccountAsync();

            Assert.False(detached.Success);
            Assert.Null(await install.Db.GetDatabaseOwnerAsync());
        }

        // ------------------------------------------------------------------- harness

        private string NewDataDirectory()
        {
            var path = Path.Combine(_root, $"install_{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return path;
        }

        private async Task<Install> NewInstallAsync(bool attach = true)
        {
            var install = await Install.CreateAsync(NewDataDirectory(), attach);
            _installs.Add(install);
            return install;
        }

        // One install: its own data directory, profile registry, settings, database and
        // coordinator - wired together the way App.axaml.cs wires them.
        private sealed class Install
        {
            public required ProfileService Profiles { get; init; }
            public required SettingsService SettingsService { get; init; }
            public required AppSettings Settings { get; init; }
            public required LocalDbService Db { get; init; }
            public required SyncCoordinator Coordinator { get; init; }

            public static async Task<Install> CreateAsync(string dataDirectory, bool attach)
            {
                var profiles = new ProfileService(dataDirectory);
                var settingsService = new SettingsService(dataDirectory, profiles.ActiveProfileDirectory);

                var settings = settingsService.LoadSettings();
                settings.SyncMode = SyncMode.Api;
                settings.ApiEndpoint = ApiEndpoint;
                settings.SyncInterval = SyncInterval.Disabled;
                settings.PreMergeBackupMode = PreMergeBackupMode.Never;
                settingsService.SaveSettings(settings);

                var db = new LocalDbService(settingsService.DatabasePath, settings.DeviceId);
                await db.Initialization;

                if (attach)
                    profiles.Attach(settingsService, db, settings);

                var coordinator = new SyncCoordinator(db, settingsService, settings, profiles: profiles);
                profiles.Register(coordinator);

                return new Install
                {
                    Profiles = profiles,
                    SettingsService = settingsService,
                    Settings = settings,
                    Db = db,
                    Coordinator = coordinator
                };
            }

            // A sign-in against a scripted server, through a coordinator built for this one call -
            // the HttpClient is what carries the script, and each sign-in needs its own.
            public Task<(bool Success, string Message)> SignInAsync(int userId, string email)
            {
                var handler = new ApiSyncProviderTests.RecordingHandler().EnqueueJson(HttpStatusCode.OK, new
                {
                    Token = "issued-token",
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(365),
                    User = new { Id = userId, Email = email, DisplayName = (string?)null, CreatedAtUtc = DateTime.UtcNow }
                });

                var coordinator = new SyncCoordinator(
                    Db, SettingsService, Settings, new HttpClient(handler), Profiles);

                Profiles.Register(coordinator);
                return coordinator.SignInAsync(email, "hunter2");
            }

            public async Task DisposeAsync()
            {
                Coordinator.Dispose();
                await Db.Connection.CloseAsync();
            }
        }

        private sealed class RecordingProfileAware : IProfileAware
        {
            public List<string> Calls { get; } = new();

            public Task OnProfileChangingAsync()
            {
                Calls.Add("changing");
                return Task.CompletedTask;
            }

            public Task OnProfileChangedAsync()
            {
                Calls.Add("changed");
                return Task.CompletedTask;
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
