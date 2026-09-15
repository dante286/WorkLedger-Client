using System;
using WorkLedger.Contracts.Auth;
using WorkLedger_Client.ViewModels;
using Xunit;

namespace WorkLedger.Tests
{
    // WL-141/WL-144, plan 10. The device list's WORDING, tested without Avalonia.
    //
    // This file exists because the interesting part of the device list is not that it renders -
    // it is what the removal confirmation says. The server's own documentation calls "removing a
    // device deletes what it recorded" the single most plausible wrong assumption about that
    // endpoint, and this dialog is the last place to correct it. Copy that can only be read by
    // launching the app and clicking into a panel is copy nobody checks.
    public class AccountDeviceViewModelTests
    {
        private static DeviceSummary Device(
            string deviceId = "device-a",
            string? label = "Laptop",
            DateTime? lastSeenUtc = null,
            bool isCurrent = false)
            => new(deviceId, label, DateTime.UtcNow.AddDays(-30), lastSeenUtc, isCurrent);

        [Fact]
        public void TheRemovalConfirmation_SaysTheDevicesWorkStays_AndThatItCanSignInAgain()
        {
            var row = new AccountDeviceViewModel(Device());

            // Both halves are load-bearing. The first closes off the misreading; the second says
            // what actually happens, so removing a laptop you still own is not a one-way door.
            Assert.Contains("Nothing it recorded is deleted", row.ConfirmationMessage);
            Assert.Contains("belong to the account", row.ConfirmationMessage);
            Assert.Contains("sign in again", row.ConfirmationMessage);
        }

        [Fact]
        public void RemovingTheCurrentDevice_IsOffered_AndSaysItWillSignThisAppOut()
        {
            // Decided in WL-141 and worth pinning so it is not quietly "fixed" into a guard later:
            // the server permits this deliberately, IsCurrent exists so a client can label and warn,
            // and it is the only way to drop a token from the device holding it.
            var row = new AccountDeviceViewModel(Device(isCurrent: true));

            Assert.True(row.IsCurrent);
            Assert.Equal("This device", row.CurrentBadge);

            // The warning is on the button as well as in the dialog, because that is the one case
            // where the consequence lands on the person pressing it.
            Assert.Contains("signs this app out", row.RemoveLabel);
            Assert.Contains("signed out", row.ConfirmationMessage);

            // And it still promises the data survives - the reassurance is not dropped just
            // because the warning got longer.
            Assert.Contains("Nothing it recorded is deleted", row.ConfirmationMessage);
        }

        [Fact]
        public void ANonCurrentRow_CarriesNoBadgeAndNoWarning()
        {
            var row = new AccountDeviceViewModel(Device(isCurrent: false));

            Assert.Equal(string.Empty, row.CurrentBadge);
            Assert.Equal("Remove", row.RemoveLabel);
        }

        [Fact]
        public void TheDeviceIdIsCarriedThroughVerbatim()
        {
            // It is the only public identifier there is, the same value stamped as OriginDeviceId
            // on every row this install writes, and what DELETE /auth/devices/{id} takes. Nothing
            // may reformat or shorten it for display and then send the shortened form.
            const string id = "6f1b0f2e-0b6a-4a2c-9d3e-0c1a2b3c4d5e";

            Assert.Equal(id, new AccountDeviceViewModel(Device(deviceId: id)).DeviceId);
        }

        [Fact]
        public void LastSeen_IsLocalized_BecauseEverythingFromTheServerIsUtc()
        {
            var seenUtc = DateTime.UtcNow.AddHours(-3);
            var row = new AccountDeviceViewModel(Device(lastSeenUtc: seenUtc));

            Assert.Contains(seenUtc.ToLocalTime().ToString("g"), row.LastSeen);
        }

        [Fact]
        public void ANeverUsedDevice_SaysSo_AndFallsBackToWhenItWasAdded()
        {
            // LastSeenUtc is written on every login, so a null means a row created some other way.
            // "Never" plus a date is more useful than "never" alone.
            var created = DateTime.UtcNow.AddDays(-30);
            var summary = new DeviceSummary("device-b", "Phone", created, null, false);

            var row = new AccountDeviceViewModel(summary);

            Assert.Contains("Never used", row.LastSeen);
            Assert.Contains(created.ToLocalTime().ToString("g"), row.LastSeen);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AMissingLabel_GetsAnHonestPlaceholder(string? label)
            => Assert.Equal("Unnamed device", AccountDeviceViewModel.DescribeLabel(label));
    }
}
