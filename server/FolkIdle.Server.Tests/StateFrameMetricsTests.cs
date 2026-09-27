using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 46 (audit item 8, "8a - measure"). StateFrameMetrics is exercised
    /// directly here - no database, no socket - because what needs pinning is
    /// the counter arithmetic itself: that a JSON frame adds to both the byte
    /// total and the serialize histogram, that a binary frame adds bytes with
    /// no serialize sample, and that the histogram buckets are cumulative
    /// (Prometheus's le="..." convention: a sample lands in every bucket whose
    /// threshold it is under, not just the tightest one). SendToPlayer's own
    /// choice of JSON vs. binary is proven separately by the protocol tests.
    /// </summary>
    public class StateFrameMetricsTests
    {
        // Modul: these are process-wide static counters, deliberately, like
        // every other /metrics counter in this file (see StateFrameMetrics'
        // own remarks) - so each test resets them first. xUnit constructs a
        // fresh instance of the test class per [Fact], which makes a
        // constructor the equivalent of a per-test setup here.
        public StateFrameMetricsTests()
        {
            StateFrameMetrics.ResetForTests();
        }

        [Fact]
        public void RecordJsonFrame_AddsBytesAndOneFrameAndSamplesTheDuration()
        {
            StateFrameMetrics.RecordJsonFrame(bytes: 900, serializeMicroseconds: 42);

            Assert.Equal(900, StateFrameMetrics.BytesTotal);
            Assert.Equal(1, StateFrameMetrics.FramesTotal);
            Assert.Equal(42, StateFrameMetrics.SerializeSumMicroseconds);
            Assert.Equal(1, StateFrameMetrics.SerializeCount);
        }

        [Fact]
        public void RecordJsonFrame_BucketsAreCumulativeUpToTheMatchingThreshold()
        {
            StateFrameMetrics.RecordJsonFrame(bytes: 900, serializeMicroseconds: 42);

            // 42us is under every bucket's threshold, so it counts in all of
            // them - that is what "le" (less-or-equal) means in the exposition
            // format, and it is why a Prometheus histogram_quantile() over
            // these buckets is meaningful rather than a per-bucket count.
            Assert.Equal(1, StateFrameMetrics.Bucket100Us);
            Assert.Equal(1, StateFrameMetrics.Bucket250Us);
            Assert.Equal(1, StateFrameMetrics.Bucket500Us);
            Assert.Equal(1, StateFrameMetrics.Bucket1000Us);
            Assert.Equal(1, StateFrameMetrics.Bucket2500Us);
            Assert.Equal(1, StateFrameMetrics.BucketInfUs);
        }

        [Fact]
        public void RecordJsonFrame_ADurationOverEveryNamedThresholdOnlyReachesInf()
        {
            StateFrameMetrics.RecordJsonFrame(bytes: 900, serializeMicroseconds: 3000);

            Assert.Equal(0, StateFrameMetrics.Bucket100Us);
            Assert.Equal(0, StateFrameMetrics.Bucket250Us);
            Assert.Equal(0, StateFrameMetrics.Bucket500Us);
            Assert.Equal(0, StateFrameMetrics.Bucket1000Us);
            Assert.Equal(0, StateFrameMetrics.Bucket2500Us);
            Assert.Equal(1, StateFrameMetrics.BucketInfUs);
        }

        [Fact]
        public void RecordBinaryFrame_AddsBytesAndOneFrameButNoSerializeSample()
        {
            StateFrameMetrics.RecordBinaryFrame(bytes: 801);

            Assert.Equal(801, StateFrameMetrics.BytesTotal);
            Assert.Equal(1, StateFrameMetrics.FramesTotal);

            // The binary path is one memcpy, not a Utf8JsonWriter walk - see
            // StateFrameMetrics' remarks on why it is deliberately not timed.
            Assert.Equal(0, StateFrameMetrics.SerializeCount);
            Assert.Equal(0, StateFrameMetrics.SerializeSumMicroseconds);
            Assert.Equal(0, StateFrameMetrics.BucketInfUs);
        }

        [Fact]
        public void MixedJsonAndBinaryFrames_AccumulateIntoTheSameByteAndFrameTotals()
        {
            StateFrameMetrics.RecordJsonFrame(bytes: 900, serializeMicroseconds: 50);
            StateFrameMetrics.RecordBinaryFrame(bytes: 801);
            StateFrameMetrics.RecordJsonFrame(bytes: 950, serializeMicroseconds: 5000);

            Assert.Equal(900 + 801 + 950, StateFrameMetrics.BytesTotal);
            Assert.Equal(3, StateFrameMetrics.FramesTotal);
            Assert.Equal(2, StateFrameMetrics.SerializeCount);
            Assert.Equal(50 + 5000, StateFrameMetrics.SerializeSumMicroseconds);
        }
    }
}
