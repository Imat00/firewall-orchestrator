using FWO.Middleware.Server.Services;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using Quartz;

namespace FWO.Test
{
    [TestFixture]
    [Parallelizable]
    internal class QuartzSchedulerServiceBaseTest
    {
        private sealed class TestJob : IJob
        {
            public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
        }

        private sealed class TestSchedulerService : QuartzSchedulerServiceBase<TestJob>
        {
            private TestSchedulerService()
                : base(
                    null!,
                    null!,
                    null!,
                    null!,
                    new QuartzSchedulerOptions("Test", "Test", "Test", "Test"))
            { }

            protected override int SleepTime => 1;

            protected override DateTimeOffset StartAt => DateTime.MinValue;

            protected override TimeSpan Interval => TimeSpan.FromSeconds(1);

            public static DateTimeOffset CalculateStartTimeForTest(DateTimeOffset configuredStartTime, TimeSpan interval, DateTimeOffset now)
            {
                return CalculateStartTime(configuredStartTime, interval, now);
            }
        }

        [Test]
        public void CalculateStartTime_ReturnsFutureStartTime()
        {
            DateTimeOffset now = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset configuredStartTime = now.AddMinutes(-12);
            TimeSpan interval = TimeSpan.FromMinutes(5);

            DateTimeOffset result = TestSchedulerService.CalculateStartTimeForTest(configuredStartTime, interval, now);

            DateTimeOffset expected = now.AddMinutes(3);
            ClassicAssert.AreEqual(expected, result);
        }

        [Test]
        public void CalculateStartTime_PreservesFutureStartTime()
        {
            DateTimeOffset now = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset configuredStartTime = now.AddMinutes(10);
            TimeSpan interval = TimeSpan.FromMinutes(5);

            DateTimeOffset result = TestSchedulerService.CalculateStartTimeForTest(configuredStartTime, interval, now);

            ClassicAssert.AreEqual(configuredStartTime, result);
        }

        [Test]
        public void CalculateStartTime_ThrowsOnNonPositiveInterval()
        {
            DateTimeOffset now = new(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
            DateTimeOffset configuredStartTime = now;

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TestSchedulerService.CalculateStartTimeForTest(configuredStartTime, TimeSpan.Zero, now));
        }
    }
}
