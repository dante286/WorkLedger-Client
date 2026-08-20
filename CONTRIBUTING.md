# Contributing to WorkLedger

Thank you for your interest in contributing to WorkLedger! This document provides guidelines and instructions for contributing to the project.

## 🚀 Getting Started

### Development Environment Setup

1. **Prerequisites:**
   - [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later
   - Visual Studio 2022, VS Code, or JetBrains Rider
   - Git

2. **Clone and Build:**
   ```bash
   git clone https://github.com/dante286/WorkLedger-Client.git
   cd WorkLedger-Client
   dotnet restore
   dotnet build
   ```

3. **Run the Application:**
   ```bash
   dotnet run
   ```

## 📋 How to Contribute

### Reporting Issues
- Use the GitHub issue tracker to report bugs
- Include detailed steps to reproduce the issue
- Specify your environment (.NET version, OS, etc.)
- Attach screenshots if applicable

### Suggesting Features
- Open an issue with the "enhancement" label
- Clearly describe the feature and its use case
- Explain how it aligns with WorkLedger's goals

### Submitting Pull Requests

1. **Fork the repository** and create a new branch from `main`
2. **Make your changes** following the code style guidelines below
3. **Test your changes** thoroughly
4. **Commit using Conventional Commits** (see below)
5. **Push to your fork** and submit a pull request

## 📝 Conventional Commits

WorkLedger uses [Conventional Commits](https://www.conventionalcommits.org/) with [Release Please](https://github.com/googleapis/release-please) for automated versioning and changelog generation.

### Commit Message Format

```
<type>: <description>

[optional body]

[optional footer]
```

### Commit Types and Version Bumps

| Commit Prefix | Version Bump | Example |
|--------------|--------------|---------|
| `fix:` | Patch (v0.1.X) | `fix: resolve timer not stopping on task switch` |
| `feat:` | Minor (v0.X.0) | `feat: add task archiving functionality` |
| `feat!:` or `BREAKING CHANGE:` | Major (vX.0.0) | `feat!: redesign sync architecture` |
| `chore:`, `docs:`, `refactor:`, `style:`, `test:` | No bump | `docs: update README with setup instructions` |

### Examples

**Bug fix:**
```
fix: prevent duplicate time entries on rapid clicks

Adds debouncing to the timer start button to prevent
multiple time entries from being created.

Closes #123
```

**New feature:**
```
feat: add weekly time report export

Implements CSV export functionality for weekly time reports
grouped by project and task.
```

**Breaking change:**
```
feat!: migrate from Entity Framework to sqlite-net-pcl

BREAKING CHANGE: Database schema has changed. Users will need
to export data before upgrading and re-import after migration.
```

## 🎨 Code Style Guidelines

### C# / .NET Conventions
- Follow [Microsoft C# Coding Conventions](https://docs.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions)
- Use **PascalCase** for public members, **camelCase** for private fields with `_` prefix
- Use **async/await** for asynchronous operations
- Prefer **nullable reference types** (`string?` instead of `string`)

### MVVM Architecture
- **Models:** Plain data classes with SQLite attributes
- **ViewModels:** Inherit from `ViewModelBase`, use `CommunityToolkit.Mvvm` attributes
- **Views:** Avalonia XAML with code-behind for UI-only logic
- **Services:** Stateless or singleton services for business logic

### Example Code Style

```csharp
public class ExampleService
{
    private readonly LocalDbService _localDbService;
    
    public ExampleService(LocalDbService localDbService)
    {
        _localDbService = localDbService;
    }
    
    public async Task<List<Project>> GetActiveProjectsAsync()
    {
        var projects = await _localDbService.GetProjectsAsync();
        return projects.Where(p => !p.IsArchived).ToList();
    }
}
```

## 🧪 Testing

- Test your changes manually before submitting a PR
- Ensure the application builds without warnings
- Verify that existing functionality still works
- Test on Windows (primary platform) if possible

**Future:** Unit tests and integration tests will be added as the project matures.

## 🔄 Pull Request Process

1. **Update documentation** if you're changing functionality
2. **Follow the PR template** (if provided)
3. **Link related issues** using "Closes #123" or "Fixes #456"
4. **Wait for review** - PRs will be reviewed and merged when ready
5. **Address feedback** - Make requested changes promptly

### PR Checklist
- [ ] Code follows the style guidelines
- [ ] Commit messages use Conventional Commits format
- [ ] Changes have been tested locally
- [ ] Documentation has been updated (if needed)
- [ ] No build warnings or errors

## 🏗️ Project Structure

Understanding the codebase structure will help you contribute effectively:

```
WorkLedger-Client/
├── Models/              # Data models (Project, TaskItem, TimeEntry, SyncQueue, WeeklyReport)
├── ViewModels/          # MVVM view models with business logic
├── Views/               # Avalonia XAML UI definitions
├── Services/            # Core services (LocalDb, Settings, Timer, AppState,
│                        #   SyncOrchestrator, SyncCoordinator, Reports)
├── Sync/                # ISyncProvider and its implementations, plus the vendored
│                        #   WorkLedger.Contracts wire DTOs under Sync/Contracts
├── Converters/          # Value converters for data binding
├── Tests/               # WorkLedger.Tests (xUnit)
├── Auth/                # [Placeholder] Client-side auth beyond the device token
└── LocalDb/             # [Placeholder] Database migrations
```

### Current Implementation Status
- ✅ **Implemented:** Desktop UI, local SQLite, project/task management, time tracking,
  folder- and API-based sync, weekly reports with CSV export
- 📋 **Planned:** Mobile apps, server-side report rollups in the client, team features

## 🤝 Code of Conduct

- Be respectful and constructive in discussions
- Welcome newcomers and help them get started
- Focus on what's best for the project and community
- Report unacceptable behavior to the project maintainers

## 📞 Getting Help

- **Questions?** Open a GitHub Discussion or issue
- **Stuck?** Check the README troubleshooting section
- **Ideas?** Share them in GitHub Discussions

## 📄 License

By contributing to WorkLedger, you agree that your contributions will be licensed under the same license as the project (TBD - likely MIT or Apache 2.0).

---

Thank you for contributing to WorkLedger! 🎉
