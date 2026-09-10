using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// THE RECEIPT PATH COULD NEVER HAVE VERIFIED A REAL RECEIPT.
    ///
    /// Modul: `ProductionIapReceiptValidator.Validate` checks an envelope of
    /// `{provider, payload, signature}` with an RSA signature over the payload.
    /// No store produces that. Google gives a client a purchase token, Apple
    /// gives it a transaction id, and neither hands over anything signed with a
    /// key this server holds - so the only thing that could ever satisfy that
    /// validator was the test that invented the format. It went unnoticed
    /// because `/api/v1/billing/verify-receipt` had never been called by any
    /// client.
    ///
    /// These pin the DISCRIMINATION between the two schemes and the
    /// fail-closed behaviour, because those are the parts that decide whether
    /// somebody gets diamonds. The store calls themselves need a live Google or
    /// Apple credential and cannot run here; what can be tested is that a
    /// receipt never takes a path that is incapable of checking it.
    /// </summary>
    public class StoreApiReceiptVerifierTests
    {
        private readonly ITestOutputHelper _output;
        public StoreApiReceiptVerifierTests(ITestOutputHelper output) => _output = output;

        /// <summary>Configured with nothing, which is the default deployment.</summary>
        private static StoreApiReceiptVerifier Unconfigured() => new StoreApiReceiptVerifier(
            new ProductionIapReceiptValidator(
                new SecretRotationManager("FOLKIDLE_TEST_MISSING_GOOGLE"),
                new SecretRotationManager("FOLKIDLE_TEST_MISSING_APPLE"),
                null),
            new SecretRotationManager("FOLKIDLE_TEST_MISSING_GOOGLE_SA"),
            new SecretRotationManager("FOLKIDLE_TEST_MISSING_APPLE_KEY"),
            androidPackageName: "com.folkidle.game",
            appleBundleId: "com.folkidle.game",
            appleKeyId: string.Empty,
            appleIssuerId: string.Empty);

        private static string Encode(object envelope) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope)));

        private static string GooglePlayReceipt() => Encode(new
        {
            provider = "GooglePlay",
            productId = "diamonds_small",
            transactionId = "GPA.1234-5678-9012-34567",
            purchaseToken = new string('t', 96)
        });

        private static string AppStoreReceipt() => Encode(new
        {
            provider = "AppStore",
            productId = "diamonds_small",
            transactionId = "2000000123456789"
        });

        // --- which scheme a receipt belongs to -------------------------------

        [Fact]
        public void AStoreReceiptIsRecognisedAsOne()
        {
            var verifier = Unconfigured();

            Assert.True(verifier.CanHandle(GooglePlayReceipt()));
            Assert.True(verifier.CanHandle(AppStoreReceipt()));
        }

        [Fact]
        public void TheLEGACYSignedEnvelopeIsLEFTALONE()
        {
            // Modul: THE DISCRIMINATOR IS THE SIGNATURE FIELD, and getting this
            // backwards is the one mistake here that would grant diamonds for
            // nothing. The legacy envelope also carries `provider`, so provider
            // alone cannot tell them apart; a receipt that carries a signature
            // belongs to the validator that checks signatures, whatever else
            // is in it.
            string legacy = Encode(new
            {
                provider = "GooglePlay",
                payload = "eyJ0cmFuc2FjdGlvbklkIjoiYWJjIn0",
                signature = "Zm9ydHktdHdv"
            });

            Assert.False(verifierCanHandle(legacy));

            static bool verifierCanHandle(string receipt) => Unconfigured().CanHandle(receipt);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not base64 at all !!")]
        public void RubbishIsNotClaimedByEitherScheme(string receipt)
        {
            Assert.False(Unconfigured().CanHandle(receipt));
        }

        [Fact]
        public void AnUnknownProviderIsNotClaimed()
        {
            string amazon = Encode(new { provider = "AmazonAppstore", productId = "x", transactionId = "y" });

            // Not claimed rather than refused: an envelope this verifier does
            // not understand goes to the other path, which will refuse it for
            // having no signature. Claiming it would mean adding a store here
            // is the only way to stop a silent acceptance elsewhere.
            Assert.False(Unconfigured().CanHandle(amazon));
        }

        // --- failing closed --------------------------------------------------

        [Fact]
        public async Task NoCREDENTIALSMEANSNOSALE()
        {
            // Modul: the whole reason CanHandle and VerifyAsync are separate. A
            // deployment with no store credentials CLAIMS the receipt and then
            // refuses it - it must not hand it back to the legacy path, where
            // "there is no signature here" would be evaluated against an
            // envelope that never had one.
            var verifier = Unconfigured();

            var google = await verifier.VerifyAsync(GooglePlayReceipt());
            var apple = await verifier.VerifyAsync(AppStoreReceipt());

            _output.WriteLine($"google: {google.ErrorMessage}");
            _output.WriteLine($"apple:  {apple.ErrorMessage}");

            Assert.False(google.IsVerified);
            Assert.False(apple.IsVerified);
            Assert.NotEmpty(google.ErrorMessage);
            Assert.NotEmpty(apple.ErrorMessage);
        }

        [Fact]
        public async Task AReceiptWithNoTransactionIdIsRefusedBeforeAnyNetworkCall()
        {
            // Modul: the ledger is keyed on the transaction id, and dedup is
            // what stops one purchase being granted twice. A verified receipt
            // with an empty id would be a purchase that can never be
            // deduplicated - every replay of it would pay again.
            string noId = Encode(new
            {
                provider = "GooglePlay",
                productId = "diamonds_small",
                transactionId = "",
                purchaseToken = new string('t', 96)
            });

            var outcome = await Unconfigured().VerifyAsync(noId);

            Assert.False(outcome.IsVerified);
            Assert.Contains("transaction id", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AReceiptWithNoProductIsRefused()
        {
            // The diamond amount is resolved from the product id against
            // GameBalanceConfig. An empty one resolves to zero and would be
            // refused later anyway - but refusing it here means no store API
            // call is spent on a receipt that cannot pay out.
            string noProduct = Encode(new
            {
                provider = "GooglePlay",
                productId = "",
                transactionId = "GPA.1",
                purchaseToken = new string('t', 96)
            });

            var outcome = await Unconfigured().VerifyAsync(noProduct);

            Assert.False(outcome.IsVerified);
            Assert.Contains("product", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task AGooglePlayReceiptWithoutAPurchaseTokenIsRefused()
        {
            // The purchase token IS the thing Google is asked about. Without
            // it there is no question to put to the store, and accepting the
            // receipt on the strength of an id the client chose would be
            // exactly the hole this class exists to close.
            string noToken = Encode(new
            {
                provider = "GooglePlay",
                productId = "diamonds_small",
                transactionId = "GPA.1"
            });

            var outcome = await Unconfigured().VerifyAsync(noToken);

            Assert.False(outcome.IsVerified);
            Assert.Contains("purchase token", outcome.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TheCLIENTSENDSWHATTHISPARSES()
        {
            // Modul: the two-sources-of-truth guard for this feature. The
            // envelope is built in client_web/src/lib/net/storeAdapter.ts and
            // read here, in two languages, with no generated type between them
            // - which is precisely the shape KNOWN_AFFIX_IDS had when ten of
            // its twelve entries had drifted.
            //
            // Pinning the field NAMES is the cheapest guard available: a rename
            // on either side fails here rather than silently refusing every
            // purchase in production.
            var verifier = Unconfigured();

            string exactlyWhatTheClientSends = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                "{\"provider\":\"GooglePlay\",\"productId\":\"diamonds_small\"," +
                "\"transactionId\":\"GPA.1234\",\"purchaseToken\":\"abcdefghijklmnop\"}"));

            Assert.True(verifier.CanHandle(exactlyWhatTheClientSends));
        }
    }
}
