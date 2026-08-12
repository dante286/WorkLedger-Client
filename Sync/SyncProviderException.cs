using System;
using System.Net;

namespace WorkLedger_Client.Sync
{
    // Every way a remote sync can fail, collapsed into one catchable type: an unreachable
    // host, DNS failure, timeout, non-2xx status, malformed response body, or a token that
    // couldn't be renewed. Providers translate transport exceptions into this rather than
    // letting an HttpRequestException or TaskCanceledException escape - SyncMode = Api
    // pointed at a dead server must not take the app down with it.
    public class SyncProviderException : Exception
    {
        public SyncProviderException(string message, HttpStatusCode? statusCode = null, Exception? innerException = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
        }

        // Set when the failure was an HTTP response rather than a transport-level fault.
        public HttpStatusCode? StatusCode { get; }
    }
}
