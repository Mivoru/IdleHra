using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Npgsql pools up to 100 connections by default; the production database
    /// refuses the sixteenth. That mismatch is enforced by the SERVER, as an
    /// exception thrown at whichever operation happens to ask - and one of those
    /// killed the combat loot worker. The pool has to be bounded on this side,
    /// where the same load becomes a queue instead.
    /// </summary>
    public class ConnectionPoolBoundTests
    {
        [Fact]
        public void AnUnboundedConnectionStringIsCapped()
        {
            string bounded = ConnectionStringDefaults.WithBoundedPool(
                "Host=db.example;Database=folkidle;Username=u;Password=p", null);

            var parsed = new Npgsql.NpgsqlConnectionStringBuilder(bounded);
            Assert.Equal(ConnectionStringDefaults.DefaultMaxPoolSize, parsed.MaxPoolSize);
            Assert.True(parsed.MaxPoolSize < 15,
                "the cap must sit below the database's own client limit, or it caps nothing");
        }

        [Fact]
        public void AnExplicitPoolSizeIsLeftAlone()
        {
            // Stated intent always wins - this is a floor under carelessness,
            // not a policy that overrides an operator.
            string bounded = ConnectionStringDefaults.WithBoundedPool(
                "Host=db.example;Database=folkidle;Username=u;Password=p;Maximum Pool Size=40", null);

            Assert.Equal(40, new Npgsql.NpgsqlConnectionStringBuilder(bounded).MaxPoolSize);
        }

        [Fact]
        public void TheEnvironmentOverrideIsHonoured()
        {
            string bounded = ConnectionStringDefaults.WithBoundedPool(
                "Host=db.example;Database=folkidle;Username=u;Password=p", "7");

            Assert.Equal(7, new Npgsql.NpgsqlConnectionStringBuilder(bounded).MaxPoolSize);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void NothingInNothingOut(string input)
        {
            Assert.Equal(input, ConnectionStringDefaults.WithBoundedPool(input, null));
        }

        [Fact]
        public void AJunkOverrideFallsBackToTheDefault()
        {
            string bounded = ConnectionStringDefaults.WithBoundedPool(
                "Host=db.example;Database=folkidle;Username=u;Password=p", "not-a-number");

            Assert.Equal(ConnectionStringDefaults.DefaultMaxPoolSize,
                new Npgsql.NpgsqlConnectionStringBuilder(bounded).MaxPoolSize);
        }
    }
}
