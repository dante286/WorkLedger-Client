using WorkLedger_Client.ViewModels;
using Xunit;

namespace WorkLedger.Tests
{
    // WL-176, plan 12 item 5. The gate between a server-supplied portal URL and the OS shell,
    // tested as the pure function it is - nothing here launches anything.
    //
    // The URL comes from the server's capabilities, and the launcher passes whatever it is given to
    // the operating system. So the refusals matter more than the acceptances: a file: path, a
    // javascript: URI or another application's custom scheme must never get as far as the shell
    // because a server said so.
    public class PortalLinkTests
    {
        [Theory]
        [InlineData("https://ledger.example.com/portal")]
        [InlineData("https://ledger.example.com:8443/portal")]
        [InlineData("  https://ledger.example.com/portal  ")]
        public void AnHttpsUrl_IsLaunched(string url)
        {
            var uri = PortalLink.ToLaunchableUri(url);

            Assert.NotNull(uri);
            Assert.Equal("https", uri!.Scheme);
        }

        // A developer running the server on this machine: plain HTTP that never leaves it. The
        // server applies the same rule to Web:PublicBaseUrl.
        [Theory]
        [InlineData("http://localhost:8080/portal")]
        [InlineData("http://127.0.0.1:8080/portal")]
        [InlineData("http://[::1]:8080/portal")]
        public void LoopbackHttp_IsLaunched(string url)
            => Assert.NotNull(PortalLink.ToLaunchableUri(url));

        [Theory]
        // Plain HTTP to anywhere else would send a session cookie and a password in the clear.
        [InlineData("http://ledger.example.com/portal")]
        // A hostname that merely starts with "localhost" is not loopback.
        [InlineData("http://localhost.example.com/portal")]
        // The schemes that would reach the shell as something other than a web page.
        [InlineData("file:///C:/Windows/System32/calc.exe")]
        [InlineData("file:///etc/passwd")]
        [InlineData("javascript:alert(1)")]
        [InlineData("workledger://open?thing=1")]
        [InlineData("ftp://ledger.example.com/portal")]
        // Relative, or not a URL at all. On Unix "/portal" parses as an absolute file: URI, which is
        // why the scheme check - not the absolute-URI parse - is the real test.
        [InlineData("/portal")]
        [InlineData("portal")]
        [InlineData("")]
        [InlineData("   ")]
        public void AnythingElse_IsRefused(string url)
            => Assert.Null(PortalLink.ToLaunchableUri(url));

        [Fact]
        public void NoUrl_IsRefused()
            => Assert.Null(PortalLink.ToLaunchableUri(null));
    }
}
