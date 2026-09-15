using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WorkLedger_Client.Views
{
    public partial class ConfirmationDialog : Window
    {
        public string ItemType { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;

        // WL-141/WL-143. The original dialog composes one fixed sentence - "are you sure you want
        // to delete this project?" - which is the right size for deleting a project and far too
        // small for the account panel, where the whole point of the confirmation is the paragraph
        // explaining what is NOT deleted. Message is that paragraph; when it is set it replaces
        // the composed sentence entirely.
        public string Message { get; set; } = string.Empty;

        public bool HasMessage => !string.IsNullOrEmpty(Message);

        public bool HasPrompt => !HasMessage;

        // Named buttons, because "Yes" is a poor answer to "delete this account permanently?" -
        // the label is the last place to restate what is about to happen.
        public string ConfirmLabel { get; set; } = "Yes";

        public string CancelLabel { get; set; } = "No";

        public ConfirmationDialog()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }

        public ConfirmationDialog(string action, string itemType) : this()
        {
            Action = action;
            ItemType = itemType;
            DataContext = this;
        }

        /// <summary>
        /// A confirmation that has something to explain. WL-141/WL-143.
        /// </summary>
        /// <remarks>
        /// Sized to its content rather than to the 350x120 the one-line form uses: the account
        /// panel's confirmations are several sentences and a clipped warning is worse than none.
        /// </remarks>
        public ConfirmationDialog(string title, string message, string confirmLabel, string cancelLabel = "Cancel")
            : this()
        {
            Title = title;
            Message = message;
            ConfirmLabel = confirmLabel;
            CancelLabel = cancelLabel;

            Width = 460;
            SizeToContent = SizeToContent.Height;

            DataContext = this;
        }

        private void OnNo(object? sender, RoutedEventArgs e)
        {
            this.Close(false);
        }

        private void OnYes(object? sender, RoutedEventArgs e)
        {
            this.Close(true);
        }
    }
}
