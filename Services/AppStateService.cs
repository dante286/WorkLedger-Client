using System;
using System.Collections.Generic;
using System.Text;
using WorkLedger_Client.Models;

namespace WorkLedger_Client.Services
{
    public class AppStateService
    {
        public TaskItem? ActiveTask { get; set; }
        public TimeEntry? ActiveTimeEntry { get; set; }

        public bool HasActiveTimer =>
            ActiveTask != null && ActiveTimeEntry != null;

    }
}
