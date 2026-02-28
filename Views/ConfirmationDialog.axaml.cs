using Avalonia.Controls;
using Avalonia.Interactivity;

namespace WorkLedger_Client.Views
{
    public partial class ConfirmationDialog : Window
    {
        public string ItemType { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;

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
