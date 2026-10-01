using System;

namespace WorkLedger_Client.ViewModels
{
    // WL-176, plan 12 item 5. The one gate between a server-supplied URL and the OS shell.
    //
    // AuthCapabilitiesResponse.PortalUrl comes from the server, and the launcher hands whatever it
    // is given to the operating system. A file: path, a javascript: URI or a custom scheme
    // registered by some other application must never get that far just because a server - or
    // anything impersonating one - said so. The server validates the value too (S11 requires
    // https, or http for a loopback host), but that is not something a client should have to trust.
    //
    // A pure function on purpose: the rule is the interesting part, and it is testable without a
    // window, a launcher or anything actually being opened.
    public static class PortalLink
    {
        /// <summary>
        /// The URI to launch for <paramref name="url"/>, or null when it must not be launched.
        /// </summary>
        /// <remarks>
        /// Accepts an absolute <c>https://</c> URL, or <c>http://</c> for a loopback host
        /// (<c>localhost</c>, <c>127.0.0.0/8</c>, <c>::1</c>) - a developer running the server
        /// locally, which is the one case where plain HTTP never leaves the machine. Everything
        /// else is refused: other schemes, a non-loopback <c>http://</c>, and anything relative.
        /// </remarks>
        public static Uri? ToLaunchableUri(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            // Absolute only. Note that on Unix a rooted path like "/portal" parses as an absolute
            // file: URI, which is exactly why the scheme check below is the real test and this is
            // only the first one.
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return null;

            if (uri.Scheme == Uri.UriSchemeHttps)
                return uri;

            if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
                return uri;

            return null;
        }
    }
}
