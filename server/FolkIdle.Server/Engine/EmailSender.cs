using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Sends one email. Swappable, like IOAuthTokenValidator and
    /// IIapReceiptValidator beside it - every caller depends on this and never
    /// on which provider is registered, so moving from Resend to Postmark or
    /// SES is a line in Program.cs.
    /// </summary>
    public interface IEmailSender
    {
        /// <summary>
        /// Returns false when the mail could not be handed over. Callers must
        /// NOT tell the player which - see PasswordResetEngine on why every
        /// outcome of a reset request looks identical from outside.
        /// </summary>
        Task<bool> SendAsync(string toAddress, string subject, string body);
    }

    /// <summary>
    /// Writes the mail to the console instead of sending it.
    ///
    /// FOR DEVELOPMENT, and it is the reason the reset flow can be driven end
    /// to end with no account anywhere: the link is printed, and whoever is
    /// testing pastes it. Registered outside Production only.
    /// </summary>
    public sealed class ConsoleEmailSender : IEmailSender
    {
        public Task<bool> SendAsync(string toAddress, string subject, string body)
        {
            Console.WriteLine("---- email (development sender; nothing was sent) ----");
            Console.WriteLine($"  to:      {toAddress}");
            Console.WriteLine($"  subject: {subject}");
            Console.WriteLine(body);
            Console.WriteLine("------------------------------------------------------");
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Refuses to send, and is what production gets when nothing is configured.
    ///
    /// FAILS CLOSED, the same shape as DisabledOAuthTokenValidator and the
    /// admin endpoint's missing key. The alternative - falling back to the
    /// console sender - would mean a live server silently printing password
    /// reset links into its own log and telling every player "check your
    /// email", which is worse than the feature not existing.
    /// </summary>
    public sealed class DisabledEmailSender : IEmailSender
    {
        public Task<bool> SendAsync(string toAddress, string subject, string body)
        {
            Console.WriteLine("Email send refused: no provider configured (set FOLKIDLE_RESEND_API_KEY).");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Resend, over its HTTP API.
    ///
    /// CHOSEN FOR HAVING NO SDK. One POST with a bearer key and a JSON body,
    /// which matches this codebase's stated preference for self-contained
    /// primitives over a package dependency (see AuthenticationEngine's
    /// hand-rolled JWT, and the XorShift32 PRNGs). SES would need the AWS SDK
    /// and request signing for the same one call; Postmark is the same shape as
    /// this and would be a twenty-line sibling class.
    ///
    /// Configured by FOLKIDLE_RESEND_API_KEY and FOLKIDLE_MAIL_FROM. Both must
    /// be set or Program.cs registers the disabled sender instead.
    /// </summary>
    public sealed class ResendEmailSender : IEmailSender
    {
        private const string Endpoint = "https://api.resend.com/emails";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _apiKey;
        private readonly string _fromAddress;

        public ResendEmailSender(IHttpClientFactory httpClientFactory, string apiKey, string fromAddress)
        {
            _httpClientFactory = httpClientFactory;
            _apiKey = apiKey;
            _fromAddress = fromAddress;
        }

        public async Task<bool> SendAsync(string toAddress, string subject, string body)
        {
            try
            {
                var payload = new
                {
                    from = _fromAddress,
                    to = new[] { toAddress },
                    subject,
                    text = body,
                };

                using var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                request.Content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    // The address is deliberately NOT logged. A reset request
                    // is a claim about an email, and a failure log full of them
                    // would rebuild the enumeration oracle this whole flow was
                    // careful not to be.
                    Console.WriteLine($"Email send failed: provider returned {(int)response.StatusCode}.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Email send failed: {ex.Message}");
                return false;
            }
        }
    }
}
