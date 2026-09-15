using System;
using WorkLedger.Contracts.Auth;

namespace WorkLedger_Client.ViewModels
{
    // WL-141, plan 10 item 2. One row of the device list, as the panel shows it.
    //
    // A plain class with no Avalonia in it, for the same reason AuthPanelState is one: the
    // interesting part of this ticket is the WORDING - specifically the removal confirmation,
    // which has to close off the wrong assumption the server's own docs call the most plausible
    // one about that endpoint - and copy that can only be read by launching the app is copy
    // nobody checks.
    //
    // Immutable. The list is rebuilt from a fresh GET /auth/devices rather than mutated, because
    // LastSeenUtc moves under us on every sync from every device and a row that quietly disagrees
    // with the server is worse than one refresh.
    public sealed class AccountDeviceViewModel
    {
        public AccountDeviceViewModel(DeviceSummary device)
        {
            // The DeviceId GUID is the only public identifier there is - the same value stamped as
            // OriginDeviceId on every row this install writes, and what DELETE /auth/devices/{id}
            // takes. The server's internal row id is never on the wire, exactly as synced entities
            // are addressed by SyncId and never by their local Id.
            DeviceId = device.DeviceId;
            IsCurrent = device.IsCurrent;
            Label = DescribeLabel(device.Label);
            LastSeen = DescribeLastSeen(device.LastSeenUtc, device.CreatedAtUtc);
        }

        public string DeviceId { get; }

        public string Label { get; }

        public string LastSeen { get; }

        public bool IsCurrent { get; }

        /// <summary>
        /// Marks the row the user is reading the list on, so "which of these is me?" never has to
        /// be answered by comparing GUIDs by eye.
        /// </summary>
        public string CurrentBadge => IsCurrent ? "This device" : string.Empty;

        /// <summary>
        /// The removal button's own text carries the warning for the current device, because that
        /// is the one case where the consequence lands on the person pressing it.
        /// </summary>
        public string RemoveLabel => IsCurrent ? "Remove (signs this app out)" : "Remove";

        /// <summary>
        /// The confirmation shown before <c>DELETE /auth/devices/{deviceId}</c>.
        /// </summary>
        /// <remarks>
        /// The first sentence exists to close off the wrong assumption: the server's own
        /// documentation calls "removing a device deletes what it recorded" the single most
        /// plausible misreading of this endpoint, and it is the one this dialog is the last chance
        /// to correct. Projects, tasks and entries belong to the <i>user</i>;
        /// <c>OriginDeviceId</c> on a row is provenance, not ownership.
        /// <para>
        /// Removing the current device is <b>offered</b>, not blocked. It is permitted server-side
        /// and means "sign this device out" - the only way to drop a token from the device holding
        /// it, which is what someone handing a laptop on actually wants. A silent guard here is how
        /// surprising-but-correct behaviour gets "fixed" later, so the row says plainly what will
        /// happen instead of pretending the action does not exist.
        /// </para>
        /// </remarks>
        public string ConfirmationMessage => IsCurrent
            ? $"Remove {Label} - this device - from the account?\n\n"
              + "Nothing it recorded is deleted. Your projects, tasks and time entries belong to "
              + "the account and stay on it, and they stay on this machine too.\n\n"
              + "This app will be signed out, because removing a device revokes its access. You "
              + "can sign in again whenever you like and it will reconnect as the same device."
            : $"Remove {Label} from the account?\n\n"
              + "Nothing it recorded is deleted. Your projects, tasks and time entries belong to "
              + "the account, not to the device that happened to create them, so everything it "
              + "contributed stays exactly where it is.\n\n"
              + "What it loses is access: that device is signed out and has to sign in again to "
              + "reconnect.";

        /// <summary>
        /// The label the server holds, or an honest placeholder. Cosmetic either way - the server
        /// sets it from the machine name at sign-in and nothing keys off it.
        /// </summary>
        public static string DescribeLabel(string? label)
            => string.IsNullOrWhiteSpace(label) ? "Unnamed device" : label.Trim();

        /// <summary>
        /// When this device last made a request, in the reader's own time zone.
        /// </summary>
        /// <remarks>
        /// Everything from the server is UTC, and showing it raw would have every timestamp in the
        /// app disagree with every other one by the offset. Falls back to when the device was first
        /// enrolled: <c>LastSeenUtc</c> is written on every login, so a null means a row created
        /// some other way, and "never" plus a date is more useful than "never" alone.
        /// </remarks>
        public static string DescribeLastSeen(DateTime? lastSeenUtc, DateTime createdAtUtc)
            => lastSeenUtc is { } seen
                ? $"Last seen {seen.ToLocalTime():g}"
                : $"Never used - added {createdAtUtc.ToLocalTime():g}";
    }
}
