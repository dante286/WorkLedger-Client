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
        public SyncProviderException(
            string message,
            HttpStatusCode? statusCode = null,
            Exception? innerException = null,
            DateTime? retryAfterUtc = null,
            string? responseBody = null)
            : base(message, innerException)
        {
            StatusCode = statusCode;
            RetryAfterUtc = retryAfterUtc;
            ResponseBody = responseBody;
        }

        // Set when the failure was an HTTP response rather than a transport-level fault.
        public HttpStatusCode? StatusCode { get; }

        // WL-134: the server's Retry-After, resolved to an instant, for the two statuses that
        // send one - 423 (this account is locked out) and 429 (this IP is rate-limited). Both
        // lift by themselves, and saying when is the difference between a message someone waits
        // out and one they go looking for an administrator about.
        public DateTime? RetryAfterUtc { get; }

        // WL-146: the raw error body, kept apart from Message so a caller can show the server's own
        // words rather than "Sync server returned 400 Bad Request. {...}". It matters for exactly
        // one case - registration validation, where the server knows why the password was refused
        // and this client deliberately does not duplicate the rule.
        public string? ResponseBody { get; }
    }
}
