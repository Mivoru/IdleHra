using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using Npgsql;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// TransientDbConflict: a Postgres serialization failure or deadlock is
    /// retried (and recognised when EF wraps it), anything else is not.
    /// </summary>
    public class TransientDbConflictTests
    {
        private static PostgresException Pg(string sqlState) => new("conflict", "ERROR", "ERROR", sqlState);

        [Fact]
        public void SerializationFailureAndDeadlockAreConflicts_EvenWhenWrapped()
        {
            Assert.True(TransientDbConflict.IsConflict(Pg(PostgresErrorCodes.SerializationFailure)));
            Assert.True(TransientDbConflict.IsConflict(Pg(PostgresErrorCodes.DeadlockDetected)));
            // EF's "likely due to a transient failure" wraps the Postgres error.
            Assert.True(TransientDbConflict.IsConflict(new InvalidOperationException("transient", Pg(PostgresErrorCodes.SerializationFailure))));
        }

        [Fact]
        public void OtherErrorsAreNotConflicts()
        {
            Assert.False(TransientDbConflict.IsConflict(Pg(PostgresErrorCodes.UniqueViolation)));
            Assert.False(TransientDbConflict.IsConflict(new InvalidOperationException("boom")));
            Assert.False(TransientDbConflict.IsConflict(null));
        }

        [Fact]
        public async Task AConflictIsRetriedUntilTheAttemptSucceeds()
        {
            int calls = 0;
            int result = await TransientDbConflict.RetryAsync(() =>
            {
                calls++;
                if (calls < 3) throw Pg(PostgresErrorCodes.SerializationFailure);
                return Task.FromResult(42);
            });
            Assert.Equal(42, result);
            Assert.Equal(3, calls);
        }

        [Fact]
        public async Task TheLastConflictIsThrownOnceTheAttemptsAreSpent()
        {
            int calls = 0;
            await Assert.ThrowsAsync<PostgresException>(() => TransientDbConflict.RetryAsync(() =>
            {
                calls++;
                throw Pg(PostgresErrorCodes.SerializationFailure);
            }));
            Assert.Equal(TransientDbConflict.DefaultAttempts, calls);
        }

        [Fact]
        public async Task AnotherErrorIsNotRetried()
        {
            int calls = 0;
            await Assert.ThrowsAsync<InvalidOperationException>(() => TransientDbConflict.RetryAsync(() =>
            {
                calls++;
                throw new InvalidOperationException("not a conflict");
            }));
            Assert.Equal(1, calls);
        }
    }
}
