using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text;

namespace WorkLedger_Client.Models
{
    // WL-148, plan 11. One signed-in account's complete local database, and the registry entry
    // that names it.
    //
    // WL-138 made signing in as a second account on one machine SAFE - it is refused - and its
    // refusal message pointed at a manual remedy: "switch to a separate data directory in Settings
    // first". A profile is that remedy turned into a feature. Each one is a complete, standalone,
    // fully functional offline database under <dataDir>/profiles/<Id>/, which is what a shared
    // multi-tenant local database would not be; for an app whose local copy IS the user's real
    // work, that difference is the whole argument (the alternative - a UserId column on every
    // entity - is ruled out in writing in plan 8 item 6 and plan 11).
    //
    // The registry entry is DELIBERATELY ADVISORY. The authoritative statement of who a database
    // belongs to is the DatabaseOwner row inside the database file itself, because that is what
    // survives backup/restore, the data-directory move and the pre-merge backups. When the two
    // disagree the stamp wins and the entry is corrected from it - see
    // ProfileService.ReconcileActiveProfileAsync.
    public class Profile
    {
        // The profile every install starts in, before anyone has signed in. Kept as its Id even
        // after an account adopts it: profiles are looked up through the registry by account, not
        // by directory name, so there is never a reason to rename a directory out from under an
        // open SQLite connection.
        public const string LocalProfileId = "local";

        // The directory name under <dataDir>/profiles/. Filesystem-safe by construction - see
        // KeyFor - and unique within the registry.
        public string Id { get; set; } = LocalProfileId;

        // The account this profile belongs to, or null/empty when it has not been adopted yet.
        // Compared exactly the way DatabaseOwner.IsSameAccount compares - both halves, because
        // either alone is weaker than it looks.
        public int? UserId { get; set; }

        public string? Email { get; set; }

        // The endpoint this profile was last signed in against. Recorded for the profile list and
        // for the same reason DatabaseOwner records it: a moved server is worth mentioning and not
        // worth blocking on.
        public string? ApiEndpoint { get; set; }

        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        public DateTime? LastUsedAtUtc { get; set; }

        // Derived, so they are kept out of profiles.json: a registry file a human may one day open
        // should hold the facts and not restate them.
        [JsonIgnore]
        public bool HasIdentity => UserId.HasValue && !string.IsNullOrWhiteSpace(Email);

        // What the Settings tab shows. An unadopted profile gets something honest rather than a
        // blank: it is a real place holding real work, it just has no account yet.
        [JsonIgnore]
        public string DisplayName => HasIdentity ? Email! : "Local - not signed in";

        // The same comparison as DatabaseOwner.IsSameAccount, expressed once here because this is
        // read from the registry rather than from the database. If one of the two ever changes,
        // change both in the same breath.
        public bool IsFor(int userId, string? email)
            => UserId == userId && Email == DatabaseOwner.NormalizeEmail(email);

        /// <summary>
        /// A stable, filesystem-safe directory name for an account. WL-148's fourth open question.
        /// </summary>
        /// <remarks>
        /// WL-138 settles account identity as normalized email + the server's UserId, so the key is
        /// derived from exactly those two and nothing else. The readable half (the email's local
        /// part, sanitized) is there purely so the folder can be recognised in a file manager; the
        /// six hex characters of SHA-256 over <c>email|userId</c> are what actually make it unique.
        /// Uniqueness within the registry is still enforced by the caller - see
        /// <c>ProfileRegistry.UniqueId</c> - because a hash prefix is "in practice", not "always".
        /// </remarks>
        public static string KeyFor(int userId, string? email)
        {
            var normalized = DatabaseOwner.NormalizeEmail(email);

            var localPart = normalized.Split('@')[0];
            var readable = new string(localPart.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
                .Trim('-');

            if (readable.Length > 24)
                readable = readable[..24];

            if (string.IsNullOrEmpty(readable))
                readable = "account";

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{normalized}|{userId}"));
            return $"{readable}-{Convert.ToHexString(hash)[..6].ToLowerInvariant()}";
        }
    }

    // The contents of <dataDir>/profiles.json: which profiles exist and which one the app is
    // currently running as. Deliberately NOT part of AppSettings - it is the thing that decides
    // where the profile-scoped half of AppSettings is read from, so it has to be readable before
    // settings are loaded at all.
    public class ProfileRegistry
    {
        public string ActiveProfileId { get; set; } = Profile.LocalProfileId;

        public List<Profile> Profiles { get; set; } = new();

        public Profile? Find(string? profileId)
            => Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase));

        public Profile? FindForAccount(int userId, string? email)
            => Profiles.FirstOrDefault(p => p.IsFor(userId, email));

        // Also derived, and emphatically not serialized: writing it would put a second copy of
        // whichever profile is active into the file, which is one more thing that can disagree
        // with itself after a hand edit.
        [JsonIgnore]
        public Profile Active => Find(ActiveProfileId) ?? EnsureLocal();

        // The registry can never legitimately be empty: something has to be active. A file that
        // has been hand-edited, truncated or written by a failed switch gets the local profile
        // back rather than an exception on a path that runs before the UI exists.
        public Profile EnsureLocal()
        {
            var local = Find(Profile.LocalProfileId);
            if (local != null)
                return local;

            local = new Profile { Id = Profile.LocalProfileId };
            Profiles.Add(local);
            return local;
        }

        // A candidate directory name, suffixed if the registry already holds it. See
        // Profile.KeyFor for why "in practice unique" is not treated as "unique".
        public string UniqueId(string candidate)
        {
            if (Find(candidate) is null)
                return candidate;

            for (var suffix = 2; ; suffix++)
            {
                var attempt = $"{candidate}-{suffix}";
                if (Find(attempt) is null)
                    return attempt;
            }
        }
    }

    /// <summary>
    /// What one <see cref="Services.ProfileService.SwitchToAsync"/> did. WL-148.
    /// </summary>
    /// <remarks>
    /// A failed switch is a result rather than an exception for the same reason an
    /// <c>ISyncProvider</c> failure is: the caller is a sign-in that must still be able to fall
    /// back to WL-138's refusal, and a button on a settings screen.
    /// </remarks>
    public class ProfileSwitchResult
    {
        public bool Success { get; init; }

        public string Message { get; init; } = string.Empty;

        // The profile the app is running as now - the new one on success, the old one on failure.
        public Profile? Profile { get; init; }

        public static ProfileSwitchResult Ok(Profile profile, string message)
            => new() { Success = true, Profile = profile, Message = message };

        public static ProfileSwitchResult Failed(string message, Profile? current = null)
            => new() { Success = false, Message = message, Profile = current };
    }
}
