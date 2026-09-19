using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Services
{
    /// <summary>
    /// Something that has to be quiesced before the database underneath it is swapped, and woken
    /// up again afterwards. WL-148.
    /// </summary>
    /// <remarks>
    /// Registration order is the quiesce order and it matters: <see cref="TimerService"/> writes to
    /// the database, so it stops first; <see cref="SyncCoordinator"/> stops its tick next; the view
    /// model is last in and first out because it only reads. Both halves must be total - a switch
    /// is not the moment to discover that a reload threw.
    /// </remarks>
    public interface IProfileAware
    {
        Task OnProfileChangingAsync();

        Task OnProfileChangedAsync();
    }

    // WL-148, plan 11. The registry of per-account profiles, the legacy migration into it, and the
    // live switch between them.
    //
    // WHAT A PROFILE IS. One account's complete local database, under
    // <dataDir>/profiles/<Id>/workledger.db, with its account-scoped settings beside it in
    // profile.json. Everything else - the device's identity, the theme, the backup settings, the
    // sync mode and endpoint - stays in the root settings.json, because it belongs to the INSTALL
    // and not to whoever is signed in. DeviceId in particular is emphatically not per-profile: it
    // is the OriginDeviceId stamped on every row this install writes, and one machine that looked
    // like several to the server would break self-echo suppression in the orchestrator.
    //
    // WHY IT EXISTS. WL-138 made signing in as a second account SAFE by refusing it, and pointed
    // at a manual remedy - "use a separate data directory". This is that remedy, supported. The
    // alternative shape, a UserId column on every entity, is ruled out in writing in plan 8 item 6:
    // identical safety, but the churn spreads across LocalDbService's queries, ReportsService and
    // the orchestrator's merge lookups instead of being confined to path resolution.
    //
    // THE ONE INVARIANT WORTH MEMORISING. The DatabaseOwner stamp INSIDE a database is
    // authoritative; a registry entry is advisory. Backup/restore, the data-directory move and the
    // pre-merge backups all separate a database from the files around it, so anything that keeps
    // ownership outside the database file is wrong the moment one of them is used. When the two
    // disagree, the registry is corrected from the stamp - see ReconcileActiveProfileAsync.
    public class ProfileService
    {
        public const string RegistryFileName = "profiles.json";
        public const string ProfilesFolderName = "profiles";

        private readonly string _dataDirectory;
        private readonly string _registryPath;
        private readonly List<IProfileAware> _aware = new();

        // Assigned by Attach rather than by the constructor: this class is built BEFORE the
        // settings service, because it is what decides which profile directory that service is
        // pointed at. Null until then, and SwitchToAsync says so rather than assuming.
        private SettingsService? _settingsService;
        private LocalDbService? _db;
        private AppSettings? _settings;

        private ProfileRegistry _registry;

        /// <summary>
        /// Reads (or creates) the registry and migrates a pre-WL-148 layout into it.
        /// </summary>
        /// <remarks>
        /// Constructed before <see cref="SettingsService"/> at startup, because it is what decides
        /// which profile directory that service is pointed at. The live-switch dependencies arrive
        /// afterwards through <see cref="Attach"/>: they cannot be constructor arguments without a
        /// cycle, since <see cref="SettingsService"/> needs this class's answer first.
        /// </remarks>
        public ProfileService(string dataDirectory)
        {
            _dataDirectory = dataDirectory;
            _registryPath = Path.Combine(dataDirectory, RegistryFileName);

            _registry = LoadRegistry();
            MigrateLegacyLayoutIfNeeded();
        }

        /// <summary>
        /// Hands over the objects a live switch re-points. WL-148.
        /// </summary>
        /// <remarks>
        /// The three of them keep their object identity across every switch - the connection is
        /// re-opened, the settings service is re-pointed, and the one live AppSettings instance has
        /// its four account-scoped fields reloaded in place - which is exactly why switching is a
        /// path-resolution change rather than a rewiring of the application graph.
        /// </remarks>
        public ProfileService Attach(SettingsService settingsService, LocalDbService db, AppSettings settings)
        {
            _settingsService = settingsService;
            _db = db;
            _settings = settings;
            return this;
        }

        public void Register(IProfileAware aware)
        {
            if (!_aware.Contains(aware))
                _aware.Add(aware);
        }

        public IReadOnlyList<Profile> Profiles => _registry.Profiles;

        public Profile ActiveProfile => _registry.Active;

        public string ActiveProfileDirectory => DirectoryFor(_registry.Active.Id);

        public string DirectoryFor(string profileId)
            => Path.Combine(_dataDirectory, ProfilesFolderName, profileId);

        public string DatabasePathFor(string profileId)
            => Path.Combine(DirectoryFor(profileId), SettingsService.DefaultDbFileName);

        public string ProfileSettingsPathFor(string profileId)
            => Path.Combine(DirectoryFor(profileId), SettingsService.ProfileSettingsFileName);

        public Profile? FindForAccount(int userId, string? email) => _registry.FindForAccount(userId, email);

        /// <summary>
        /// The profile for an account, creating one if this install has never signed in as it.
        /// </summary>
        public Profile EnsureProfileForAccount(int userId, string email, string? apiEndpoint)
        {
            if (_registry.FindForAccount(userId, email) is { } existing)
                return existing;

            var profile = new Profile
            {
                Id = _registry.UniqueId(Profile.KeyFor(userId, email)),
                UserId = userId,
                Email = DatabaseOwner.NormalizeEmail(email),
                ApiEndpoint = apiEndpoint,
                CreatedAtUtc = DateTime.UtcNow
            };

            Directory.CreateDirectory(DirectoryFor(profile.Id));
            _registry.Profiles.Add(profile);
            SaveRegistry();

            return profile;
        }

        /// <summary>
        /// Records that the active profile now belongs to an account. WL-148's adoption path.
        /// </summary>
        /// <remarks>
        /// The mirror of WL-138's stamp, and it happens at the same moment: the first successful
        /// sign-in against an unclaimed database. The profile keeps its Id - <c>local</c>, for the
        /// one every install starts in - because lookup goes through this registry and not through
        /// the directory name, so there is nothing to gain by renaming a folder that an open SQLite
        /// connection is sitting in.
        /// </remarks>
        public Profile AdoptActiveProfile(int userId, string email, string? apiEndpoint)
        {
            var profile = _registry.Active;

            profile.UserId = userId;
            profile.Email = DatabaseOwner.NormalizeEmail(email);
            profile.ApiEndpoint = apiEndpoint;
            profile.LastUsedAtUtc = DateTime.UtcNow;

            SaveRegistry();
            return profile;
        }

        /// <summary>
        /// Corrects the active profile's registry entry from the database's own ownership stamp.
        /// WL-148's one invariant.
        /// </summary>
        /// <remarks>
        /// The stamp travels inside workledger.db; the registry entry does not. Restore a backup of
        /// account A's database into account B's profile and the entry is simply wrong until this
        /// runs - which is why it runs on startup and after every switch, rather than being a
        /// special case bolted onto the restore command.
        /// </remarks>
        public async Task ReconcileActiveProfileAsync(LocalDbService db)
        {
            try
            {
                // The schema has to exist before the stamp can be read - this runs at startup,
                // where LocalDbService's initialization is still fire-and-forget.
                await db.Initialization;

                var owner = await db.GetDatabaseOwnerAsync();
                if (owner is null || !owner.HasIdentity)
                    return;

                var profile = _registry.Active;
                if (profile.IsFor(owner.UserId!.Value, owner.Email))
                    return;

                profile.UserId = owner.UserId;
                profile.Email = DatabaseOwner.NormalizeEmail(owner.Email);
                profile.ApiEndpoint = owner.ApiEndpoint;
                SaveRegistry();
            }
            catch (Exception ex)
            {
                // Advisory metadata. A database that cannot be read here has much larger problems,
                // and none of them are improved by failing startup.
                System.Diagnostics.Debug.WriteLine($"Profile reconciliation failed: {ex.Message}");
            }
        }

        public void TouchActiveProfile()
        {
            _registry.Active.LastUsedAtUtc = DateTime.UtcNow;
            SaveRegistry();
        }

        /// <summary>
        /// Switches the running app to another profile. WL-148 - live, with no restart.
        /// </summary>
        /// <remarks>
        /// The sequence is the safety argument, in order: quiesce every registered
        /// <see cref="IProfileAware"/> (the work timer stops and its entry is saved to the profile
        /// it was recorded in; the sync tick stops), persist the settings of the profile being
        /// left, re-open the database on the new path, re-point the settings service, reload the
        /// four account-scoped fields into the one live <see cref="AppSettings"/> instance, then
        /// wake everything up again. Nothing is in flight against the old database when it closes,
        /// and nothing is rebuilt.
        /// <para>
        /// Never throws. A switch that cannot proceed comes back as a failed result, because one
        /// caller is a sign-in that has to be able to fall back to WL-138's refusal and the other
        /// is a button on a settings screen.
        /// </para>
        /// </remarks>
        public async Task<ProfileSwitchResult> SwitchToAsync(string profileId)
        {
            if (_settingsService is null || _db is null || _settings is null)
                return ProfileSwitchResult.Failed("Profile switching isn't available.", _registry.Active);

            var target = _registry.Find(profileId);
            if (target is null)
                return ProfileSwitchResult.Failed("That profile no longer exists.", _registry.Active);

            if (string.Equals(target.Id, _registry.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
                return ProfileSwitchResult.Ok(target, $"Already using {target.DisplayName}.");

            try
            {
                foreach (var aware in _aware)
                    await aware.OnProfileChangingAsync();

                // The profile being left keeps everything it had: its settings are written before
                // anything is re-pointed, so a switch is never the reason a token or a cursor was
                // lost.
                _settingsService.SaveSettings(_settings);

                Directory.CreateDirectory(DirectoryFor(target.Id));

                await _db.SwitchDatabaseAsync(DatabasePathFor(target.Id));

                _settingsService.UseProfile(DirectoryFor(target.Id));
                ApplyProfileSettingsTo(_settings, ProfileSettingsPathFor(target.Id));

                _registry.ActiveProfileId = target.Id;
                target.LastUsedAtUtc = DateTime.UtcNow;
                SaveRegistry();

                await ReconcileActiveProfileAsync(_db);

                foreach (var aware in _aware)
                    await aware.OnProfileChangedAsync();

                return ProfileSwitchResult.Ok(target, $"Switched to {target.DisplayName}.");
            }
            catch (Exception ex)
            {
                // The caller's fallback is WL-138's refusal, which is the correct answer to "we
                // could not give this account its own database": refusing costs a sign-in, and
                // proceeding would commingle two people's work permanently.
                System.Diagnostics.Debug.WriteLine($"Profile switch failed: {ex.Message}");
                return ProfileSwitchResult.Failed($"Couldn't switch profiles: {ex.Message}", _registry.Active);
            }
        }

        // Reads one profile's account-scoped settings straight off disk, without re-pointing the
        // settings service. Used by the switch, and by the token hand-back in SyncCoordinator.
        internal static void ApplyProfileSettingsTo(AppSettings settings, string profileSettingsPath)
        {
            var profile = SettingsService.ReadProfileSettings(profileSettingsPath);

            settings.ApiEmail = profile.ApiEmail ?? string.Empty;
            settings.ApiToken = profile.ApiToken ?? string.Empty;
            settings.ApiTokenExpiresAtUtc = profile.ApiTokenExpiresAtUtc;
            settings.LastSyncedAtUtc = profile.LastSyncedAtUtc;
        }

        // --------------------------------------------------------------------------- registry I/O

        private ProfileRegistry LoadRegistry()
        {
            try
            {
                if (!File.Exists(_registryPath))
                {
                    var fresh = new ProfileRegistry();
                    fresh.EnsureLocal();
                    return fresh;
                }

                var registry = JsonSerializer.Deserialize<ProfileRegistry>(File.ReadAllText(_registryPath))
                               ?? new ProfileRegistry();

                registry.EnsureLocal();
                return registry;
            }
            catch (Exception ex)
            {
                // A registry that cannot be read must not cost anyone their data: falling back to
                // the local profile leaves every profile directory on disk exactly where it is, and
                // the next save rewrites a valid file.
                System.Diagnostics.Debug.WriteLine($"Failed to read the profile registry: {ex.Message}");

                var fallback = new ProfileRegistry();
                fallback.EnsureLocal();
                return fallback;
            }
        }

        private void SaveRegistry()
        {
            try
            {
                Directory.CreateDirectory(_dataDirectory);
                File.WriteAllText(
                    _registryPath,
                    JsonSerializer.Serialize(_registry, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write the profile registry: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------------------- migration

        /// <summary>
        /// Moves a pre-WL-148 install into <c>profiles/local/</c>, once.
        /// </summary>
        /// <remarks>
        /// The database is <b>moved, not copied</b>. Two copies of one database is the WL-74 bug
        /// class - the launch-time "default -> custom" copy that silently overwrote a live database
        /// with a stale snapshot - and leaving a second copy at the old path would let a later
        /// mistake resurrect months-old work over the real thing.
        /// <para>
        /// The four account-scoped keys are lifted out of the root settings.json by parsing the raw
        /// JSON, because the properties they map to are <c>[JsonIgnore]</c>d and would deserialize
        /// as empty. The root file is then rewritten without them, so an access token is not left
        /// behind in a file nothing reads it from any more.
        /// </para>
        /// </remarks>
        private void MigrateLegacyLayoutIfNeeded()
        {
            try
            {
                if (File.Exists(_registryPath))
                    return;

                var localDirectory = DirectoryFor(Profile.LocalProfileId);
                Directory.CreateDirectory(localDirectory);

                var legacyDb = Path.Combine(_dataDirectory, SettingsService.DefaultDbFileName);
                var profileDb = Path.Combine(localDirectory, SettingsService.DefaultDbFileName);
                if (File.Exists(legacyDb) && !File.Exists(profileDb))
                    File.Move(legacyDb, profileDb);

                MigrateLegacySettings(localDirectory);

                _registry.EnsureLocal();
                _registry.ActiveProfileId = Profile.LocalProfileId;
                SaveRegistry();
            }
            catch (Exception ex)
            {
                // A failed migration leaves the legacy files exactly where they were, and the app
                // starts on an empty local profile rather than not at all. Loud in the debug log,
                // recoverable by hand, and it cannot destroy anything: nothing here deletes.
                System.Diagnostics.Debug.WriteLine($"Profile migration failed: {ex.Message}");
            }
        }

        private void MigrateLegacySettings(string localDirectory)
        {
            var settingsPath = Path.Combine(_dataDirectory, SettingsService.SettingsFileName);
            if (!File.Exists(settingsPath))
                return;

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(settingsPath));
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"Legacy settings.json is not readable: {ex.Message}");
                return;
            }

            if (root is not JsonObject settings)
                return;

            var profileSettingsPath = Path.Combine(localDirectory, SettingsService.ProfileSettingsFileName);
            if (!File.Exists(profileSettingsPath))
            {
                SettingsService.WriteProfileSettings(profileSettingsPath, new ProfileSettings
                {
                    ApiEmail = ReadString(settings, nameof(AppSettings.ApiEmail)),
                    ApiToken = ReadString(settings, nameof(AppSettings.ApiToken)),
                    ApiTokenExpiresAtUtc = ReadDate(settings, nameof(AppSettings.ApiTokenExpiresAtUtc)),
                    LastSyncedAtUtc = ReadDate(settings, nameof(AppSettings.LastSyncedAtUtc))
                });
            }

            var removedAny = false;
            foreach (var key in new[]
                     {
                         nameof(AppSettings.ApiEmail),
                         nameof(AppSettings.ApiToken),
                         nameof(AppSettings.ApiTokenExpiresAtUtc),
                         nameof(AppSettings.LastSyncedAtUtc)
                     })
            {
                removedAny |= settings.Remove(key);
            }

            if (removedAny)
            {
                File.WriteAllText(
                    settingsPath,
                    settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }

        private static string ReadString(JsonObject settings, string key)
            => settings.TryGetPropertyValue(key, out var node) ? node?.GetValue<string>() ?? string.Empty : string.Empty;

        private static DateTime? ReadDate(JsonObject settings, string key)
        {
            if (!settings.TryGetPropertyValue(key, out var node) || node is null)
                return null;

            // RoundtripKind, deliberately. A plain DateTime.TryParse converts a "Z" instant to
            // local time and hands back a shifted value - which for LastSyncedAtUtc is a pull
            // cursor several hours out, silently re-pulling or silently skipping history. (It also
            // cannot be combined with AdjustToUniversal: that pair throws.)
            return DateTime.TryParse(
                node.ToString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
                ? parsed
                : null;
        }
    }
}
