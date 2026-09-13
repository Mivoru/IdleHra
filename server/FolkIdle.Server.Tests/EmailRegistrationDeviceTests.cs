using System;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: A GUEST COULD NEVER BECOME A REGISTERED PLAYER ON THE SAME DEVICE,
    /// found live 2026-09-13.
    ///
    /// The client sends its stored deviceId with every registration, and that is
    /// the same deviceId "Play as guest" provisions an anonymous account against.
    /// PlayerRecords.DeviceId is unique, so the insert violated
    /// IX_PlayerRecords_DeviceId - whose name contains neither "Email" nor
    /// "Username" - and fell through to EmailRegisterOutcome.Failed, a 500. Three
    /// attempts in eight seconds in the production log, each one a player who had
    /// tried the game first and liked it enough to make an account.
    ///
    /// exercise.mjs registers in a fresh browser context with a fresh deviceId,
    /// which is exactly why it never saw this.
    /// </summary>
    [Collection("Postgres collection")]
    public class EmailRegistrationDeviceTests
    {
        private readonly PostgresTestFixture _fixture;

        public EmailRegistrationDeviceTests(PostgresTestFixture fixture) => _fixture = fixture;

        [Fact]
        public async Task AGuestDeviceCanRegisterAnEmailAccount()
        {
            const string device = "device_guest_then_register_970013101";

            var guest = await AuthenticationEngine.LoginOrProvisionAsync(_fixture.RetryingOptions, device);
            Assert.NotEqual(0L, guest.PlayerId);

            var registration = await AuthenticationEngine.RegisterWithEmailAsync(
                _fixture.RetryingOptions,
                "guest_then_register_970013101@example.com",
                "GuestThenReg101",
                "a good long password",
                device);

            Assert.Equal(EmailRegisterOutcome.Success, registration.Outcome);
            Assert.NotEqual(guest.PlayerId, registration.PlayerId);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();

            // The guest keeps the device: its deviceId is the ONLY credential an
            // anonymous account has, so moving it would orphan that progress.
            var guestRow = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == guest.PlayerId);
            Assert.Equal(device, guestRow.DeviceId);

            // The new account is reached by its email and password (and the
            // session token the registration issues), not by the device.
            var newRow = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == registration.PlayerId);
            Assert.Null(newRow.DeviceId);

            var login = await AuthenticationEngine.LoginWithEmailAsync(
                _fixture.RetryingOptions, "guest_then_register_970013101@example.com", "a good long password", null);
            Assert.Equal(EmailLoginOutcome.Success, login.Outcome);
            Assert.Equal(registration.PlayerId, login.PlayerId);
        }

        [Fact]
        public async Task AFreshDeviceIsStillBoundToTheNewAccount()
        {
            const string device = "device_fresh_register_970013102";

            var registration = await AuthenticationEngine.RegisterWithEmailAsync(
                _fixture.RetryingOptions,
                "fresh_register_970013102@example.com",
                "FreshRegister102",
                "a good long password",
                device);

            Assert.Equal(EmailRegisterOutcome.Success, registration.Outcome);

            await using var db = await _fixture.DbContextFactory.CreateDbContextAsync();
            var row = await db.PlayerRecords.AsNoTracking().SingleAsync(p => p.Id == registration.PlayerId);
            Assert.Equal(device, row.DeviceId);
        }
    }
}
