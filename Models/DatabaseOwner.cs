using SQLite;
using System;

namespace WorkLedger_Client.Models
{
    // WL-138, plan 8 item 6. Who this database belongs to - one row, inside workledger.db.
    //
    // THE HAZARD. The local database has no user column: it holds this install's projects,
    // tasks and entries, and every sync path keys off SyncId alone. Sign in as a different user
    // against the same database and the sync loop pushes user A's rows into user B's account and
    // pulls B's rows into a database already full of A's, with no column that could ever separate
    // them again.
    //
    // Every server-side check passes while that happens. The token is valid, the scope is right,
    // the rows are well-formed, and last-write-wins does exactly what it documents. There is no
    // query the server could add that would notice, because from its side nothing is wrong: a
    // device belonging to user B pushed some rows, which is precisely what devices do. Under the
    // pre-S6 single seeded account this was unreachable - there was one account, so "a different
    // user" did not exist. After S6 it is one typo in a sign-in form, and the guard is entirely
    // this client's to build.
    //
    // WHY IT LIVES IN THE DATABASE AND NOT IN AppSettings. The failure mode is settings and
    // database becoming separated, and this app has three supported features that separate them:
    // backup/restore, the data-directory move, and the pre-merge backups. A guard keyed on
    // AppSettings.ApiEmail passes silently the moment a database is restored next to a different
    // settings.json - which is an operation the app offers, not a corner case. A marker inside
    // workledger.db travels with the rows through every one of those paths.
    //
    // It is also, deliberately, a fourth TABLE rather than columns on the entity tables. Nothing
    // in LocalDbService's queries or the orchestrator's merge lookups changes, and there is no
    // migration of Project, TaskItem or TimeEntry - which are the most load-bearing and
    // most-tested code in the repo. The stamp covers the whole database FILE, so it guards the
    // pending SyncQueue too: queue rows are addressed to whichever account authored them, and a
    // guard that covered the entity tables but not the queue would leak one user's work into
    // another's account on the very first push after a switch.
    public class DatabaseOwner
    {
        // There is exactly one of these, ever. A fixed primary key rather than an autoincrement
        // is what makes "claim" an idempotent upsert instead of something that can accumulate
        // rows and leave the reader picking between them.
        public const int SingletonId = 1;

        [PrimaryKey]
        public int Id { get; set; } = SingletonId;

        // The server's UserId, and the email normalized the way IsSameAccount compares it. Both
        // null on a "claimed by unknown" stamp - see LocalDbService.BackfillDatabaseOwnerAsync
        // for the one case that produces one.
        public int? UserId { get; set; }

        public string? Email { get; set; }

        // The endpoint this database was last claimed against. Recorded but deliberately NOT
        // blocked on - see IsSameAccount.
        public string? ApiEndpoint { get; set; }

        public DateTime ClaimedAtUtc { get; set; }

        // True once the stamp names an actual account. False for the claimed-by-unknown case,
        // which records "this data already has a history with some server" without being able to
        // say whose - and which the next sign-in fills in rather than being refused by.
        public bool HasIdentity => UserId.HasValue && !string.IsNullOrWhiteSpace(Email);

        // Emails are case-insensitive in practice and the server compares ConfirmEmail that way
        // too, so the stored form is lowercased and trimmed once, here, rather than at each
        // comparison.
        public static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

        // Same account = same normalized email AND same UserId.
        //
        // Both halves, because either alone is weaker than it looks: an email can be reassigned
        // to a new user on a rebuilt server, and a UserId is only a small integer that two
        // unrelated servers will both hand out.
        //
        // ApiEndpoint is deliberately NOT part of this. A server that moved from a LAN address to
        // a domain name is common and legitimate, and blocking on it would refuse a perfectly
        // ordinary migration; the endpoint is recorded and a change is warned about instead. The
        // residual risk that accepts, stated plainly so a future reader does not "tighten" this
        // into false positives: a test server and a production server that both have this email
        // at this same UserId would be treated as one account. Plausible enough to warn about,
        // rare enough not to block.
        public bool IsSameAccount(int userId, string? email)
            => UserId == userId && Email == NormalizeEmail(email);
    }

    // What LocalDbService.EvaluateOwnershipAsync concluded about a sign-in that is about to
    // happen. The three "yes" answers are separated because they want different things said to
    // the user - nothing at all, nothing at all, and a warning respectively.
    public enum DatabaseOwnershipVerdict
    {
        // No stamp: any account may claim this database. THE OFFLINE-FIRST HAPPY PATH - work for
        // a week with no server, then connect and push it all up - so it must not prompt, warn or
        // confirm anything.
        Unclaimed,

        // Stamped, and stamped with this account. The ordinary case, every time after the first.
        SameAccount,

        // Same account, different endpoint. Allowed, recorded, and worth one line to the user.
        EndpointChanged,

        // A different account. Refused - see the comment on the guard in SyncCoordinator for why
        // this is not a "are you sure?" dialog.
        DifferentAccount
    }
}
