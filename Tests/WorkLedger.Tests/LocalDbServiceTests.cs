using System;
using System.IO;
using System.Threading.Tasks;
using WorkLedger_Client.Models;
using WorkLedger_Client.Services;
using Xunit;

namespace WorkLedger.Tests
{
    public class LocalDbServiceTests : IAsyncLifetime
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"WorkLedgerTests_{Guid.NewGuid()}.db");
        private LocalDbService _service = null!;

        public async Task InitializeAsync()
        {
            _service = new LocalDbService(_dbPath);
            // LocalDbService kicks off table creation as fire-and-forget; awaiting our own
            // (idempotent, CREATE TABLE IF NOT EXISTS) call guarantees the table exists
            // before any test body runs, without relying on that background task's timing.
            await _service.Connection.CreateTableAsync<Project>();
        }

        public async Task DisposeAsync()
        {
            await _service.Connection.CloseAsync();
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }

        [Fact]
        public async Task SaveProjectAsync_WithZeroId_Inserts()
        {
            var project = new Project { Name = "New Project" };

            await _service.SaveProjectAsync(project);

            Assert.NotEqual(0, project.Id);
            var stored = await _service.GetProjectByIdAsync(project.Id);
            Assert.NotNull(stored);
            Assert.Equal("New Project", stored!.Name);
        }

        [Fact]
        public async Task SaveProjectAsync_WithExistingId_Updates()
        {
            var project = new Project { Name = "Original Name" };
            await _service.SaveProjectAsync(project);
            var insertedId = project.Id;

            project.Name = "Updated Name";
            await _service.SaveProjectAsync(project);

            var stored = await _service.GetProjectByIdAsync(insertedId);
            Assert.NotNull(stored);
            Assert.Equal("Updated Name", stored!.Name);
            Assert.Equal(insertedId, stored.Id);

            var all = await _service.GetProjectsAsync();
            Assert.Single(all);
        }
    }
}
