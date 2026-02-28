using Avalonia.Controls;
using Avalonia.Interactivity;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Views
{
    public partial class TaskDialog : Window
    {
        public TaskDialog()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }

        public TaskDialog(TaskItem task) : this()
        {
            Title = "Edit Task";
            var nameBox = this.FindControl<TextBox>("NameBox");
            var notesBox = this.FindControl<TextBox>("NotesBox");

            if (nameBox != null) nameBox.Text = task.Name;
            if (notesBox != null) notesBox.Text = task.Notes;
        }

        public string TaskName => this.FindControl<TextBox>("NameBox")?.Text ?? string.Empty;
        public string TaskNotes => this.FindControl<TextBox>("NotesBox")?.Text ?? string.Empty;

        private void OnCancel(object? sender, RoutedEventArgs e)
        {
            Close(null);
        }

        private void OnOk(object? sender, RoutedEventArgs e)
        {
            var t = new TaskItem
            {
                Name = TaskName,
                Notes = TaskNotes
            };
            Close(t);
        }
    }
}
