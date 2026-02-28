using Avalonia.Controls;
using Avalonia.Interactivity;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Views
{
    public partial class ProjectDialog : Window
    {
        public Project? EditingProject { get; private set; }

        public ProjectDialog()
        {
            Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
        }

        public ProjectDialog(Project project) : this()
        {
            EditingProject = project;

            // Prefill fields with existing project values
            var nameBox = this.FindControl<TextBox>("NameBox");
            var codeBox = this.FindControl<TextBox>("CodeBox");
            var descBox = this.FindControl<TextBox>("DescriptionBox");

            if (nameBox != null) nameBox.Text = project.Name;
            if (codeBox != null) codeBox.Text = project.ProjectCode;
            if (descBox != null) descBox.Text = project.Description;
        }

        public string ProjectName => this.FindControl<TextBox>("NameBox")?.Text ?? string.Empty;
        public string ProjectCode => this.FindControl<TextBox>("CodeBox")?.Text ?? string.Empty;
        public string ProjectDescription => this.FindControl<TextBox>("DescriptionBox")?.Text ?? string.Empty;

        private void OnCancel(object? sender, RoutedEventArgs e)
        {
            this.Close(null);
        }

        private void OnOk(object? sender, RoutedEventArgs e)
        {
            if (EditingProject != null)
            {
                // Update the same instance
                EditingProject.Name = ProjectName;
                EditingProject.ProjectCode = ProjectCode;
                EditingProject.Description = ProjectDescription;
                this.Close(EditingProject);
                return;
            }

            var p = new Project
            {
                Name = ProjectName,
                ProjectCode = ProjectCode,
                Description = ProjectDescription,
            };

            this.Close(p);
        }
    }
}
