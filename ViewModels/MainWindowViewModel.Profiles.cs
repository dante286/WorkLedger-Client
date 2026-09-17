using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;

namespace WorkLedger_Client.ViewModels
{
    // The Settings tab's Profiles section (WL-148, plan 11 item 6), and the view model's half of a
    // live profile switch.
    //
    // Everything that decides *how* a switch happens lives in ProfileService; this half picks one,
    // reports the outcome, and re-reads the screen afterwards. The re-read is the part that is easy
    // to underestimate: a switch replaces the database underneath an open projects list, a selected
    // task and a week's total, and none of them notice on their own.
    public partial class MainWindowViewModel : IProfileAware
    {
        public ObservableCollection<Profile> Profiles { get; } = new();

        [ObservableProperty]
        private Profile? _selectedProfile;

        // What the Settings tab shows above the list. Kept separate from SelectedProfile: the
        // selection is what the user is pointing at, this is what the app is actually running as,
        // and conflating them makes a half-finished switch look finished.
        [ObservableProperty]
        private string _activeProfileName = string.Empty;

        [ObservableProperty]
        private bool _isSwitchingProfile;

        // Only shown when there is more than one, so a single-account install - which is nearly
        // everyone - never has to think about profiles at all.
        public bool HasMultipleProfiles => Profiles.Count > 1;

        [RelayCommand]
        private async Task SwitchProfileAsync()
        {
            if (_profileService is null || IsSwitchingProfile)
                return;

            if (SelectedProfile is not { } target)
            {
                _toastService?.ShowToast("Pick a profile to switch to", 2000);
                return;
            }

            if (string.Equals(target.Id, _profileService.ActiveProfile.Id, StringComparison.OrdinalIgnoreCase))
            {
                _toastService?.ShowToast($"Already using {target.DisplayName}", 2000);
                return;
            }

            IsSwitchingProfile = true;
            try
            {
                // Anything typed but not yet autosaved belongs to the profile being left, and the
                // switch persists the live settings on its way out - so this has to happen first or
                // it would be written into the profile being switched to.
                PersistSettings();

                var result = await _profileService.SwitchToAsync(target.Id);
                _toastService?.ShowToast(result.Message, result.Success ? 3000 : 4500);

                // The network half of the refresh, once the settings have settled. Deliberately
                // out here rather than in OnProfileChangedAsync: that runs in the middle of the
                // switch, and during a sign-in-driven switch the token for the profile being moved
                // into has not been written yet - an authenticated request sent then would earn a
                // 401 and leave the panel reporting a revoked session that never happened.
                if (result.Success)
                    await RefreshAuthPanelAsync();
            }
            finally
            {
                IsSwitchingProfile = false;
            }
        }

        /// <summary>
        /// WL-148. Nothing to stop here - this half only reads - so the work is all in the resume.
        /// </summary>
        public Task OnProfileChangingAsync() => Task.CompletedTask;

        /// <summary>
        /// Re-reads everything that came from the database or the account-scoped settings.
        /// </summary>
        /// <remarks>
        /// Runs on the UI thread: both callers - the command above and
        /// <c>SyncCoordinator.SignInAsync</c> - are awaited from a command, so there is no
        /// dispatcher hop to make and none is invented. Total, like every other
        /// <see cref="IProfileAware"/> half: a switch is not the moment to discover that a reload
        /// threw.
        /// </remarks>
        public async Task OnProfileChangedAsync()
        {
            try
            {
                // The selection belonged to the previous database, where its Id meant something
                // else entirely - int primary keys are unique within one device's database and
                // nothing more (WL-47).
                SelectedProject = null;
                SelectedTask = null;
                SelectedTimeEntry = null;

                ApiEmail = _settings.ApiEmail ?? string.Empty;

                // The auth panel toasts on the EDGE into a signed-out state (WL-134 item 4e), and
                // a switch crosses that edge for a reason the user already knows about - they are
                // looking at a different account now. Forgetting the previous observation makes the
                // first one after a switch a level rather than an edge, so the switch doesn't
                // announce a sign-out that did not happen.
                _authStateObserved = false;

                RefreshProfiles();
                await ReloadAfterSyncAsync();

                // Local only - see the comment in SwitchProfileAsync for why the network half of
                // the refresh is the caller's job and not this one's.
                RefreshSyncStatus();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Profile reload failed: {ex.Message}");
            }
        }

        // Re-reads the registry into the bound collection. Called at construction and after every
        // switch, including one a sign-in performed without the user touching this section.
        private void RefreshProfiles()
        {
            if (_profileService is null)
                return;

            var active = _profileService.ActiveProfile;

            Profiles.Clear();
            foreach (var profile in _profileService.Profiles.OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
                Profiles.Add(profile);

            SelectedProfile = Profiles.FirstOrDefault(p =>
                string.Equals(p.Id, active.Id, StringComparison.OrdinalIgnoreCase));

            ActiveProfileName = active.DisplayName;
            OnPropertyChanged(nameof(HasMultipleProfiles));
        }
    }
}
