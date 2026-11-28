// TimeProvider, as in .NET 8: where time comes from, so that code which waits
// or measures can be given a different clock, a fake one in a test that moves
// only when the test says so.
//
//   static async Task Retry(TimeProvider time)
//   {
//       await Task.Delay(TimeSpan.FromSeconds(5), time);
//       ...
//   }
//
//   await Retry(TimeProvider.System);   // waits 5 s
//   await Retry(fake);                  // waits until the test advances `fake`
//
// TimeProvider.System is the real clock: Clock.Monotonic for timestamps and
// System.Threading.Timer for timers, which fire on a fiber inside Fiber.Run
// (lib/Timers.cs). A provider of your own overrides GetTimestamp,
// TimestampFrequency, GetUtcNow and CreateTimer. Task.Delay,
// CancellationTokenSource and PeriodicTimer take a provider, as in .NET,
// and then wait on the timers it creates.
//
// What differs from .NET:
// - No LocalTimeZone or GetLocalNow.
// - No Task.WaitAsync(TimeSpan, TimeProvider): a timed-out wait would fault
//   its task with a TimeoutException, and tasks here can't fault. Use
//   Task.WhenAny with Task.Delay(timeout, provider) instead.
// - Not on WebAssembly, which has no fibers (timestamps work there).

using System.Threading;

namespace System
{
    /// <summary>Where time comes from: TimeProvider.System, or one of your own (a fake one in tests).</summary>
    public abstract class TimeProvider
    {
        /// <summary>The system's clock, and timers that fire inside Fiber.Run.</summary>
        public static TimeProvider System { get; } = new SystemTimeProvider();

        protected TimeProvider() { }

        /// <summary>The current date and time in UTC.</summary>
        public virtual DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;

        /// <summary>Ticks of GetTimestamp per second: 1,000,000,000 for the system's (nanoseconds).</summary>
        public virtual long TimestampFrequency => 1_000_000_000;

        /// <summary>A reading of a clock that only moves forward, in TimestampFrequency ticks per second.</summary>
        public virtual long GetTimestamp() => Clock.Monotonic;

        /// <summary>The time between two GetTimestamp readings.</summary>
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp)
        {
            long frequency = TimestampFrequency;
            long ticks = endingTimestamp - startingTimestamp;
            if (frequency == TimeSpan.TicksPerSecond)
                return new TimeSpan(ticks);
            // Whole seconds and the rest apart, so a long time doesn't overflow.
            long seconds = ticks / frequency;
            long rest = ticks % frequency;
            return new TimeSpan(seconds * TimeSpan.TicksPerSecond + rest * TimeSpan.TicksPerSecond / frequency);
        }

        /// <summary>The time since a GetTimestamp reading.</summary>
        public TimeSpan GetElapsedTime(long startingTimestamp) => GetElapsedTime(startingTimestamp, GetTimestamp());

        /// <summary>
        /// A timer that calls <paramref name="callback"/>(<paramref name="state"/>)
        /// after <paramref name="dueTime"/>, then every <paramref name="period"/>
        /// (Timeout.InfiniteTimeSpan: once; a due time of InfiniteTimeSpan: not
        /// until Change). The system's is a System.Threading.Timer.
        /// </summary>
        public virtual ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            return new Timer(callback, state!, dueTime, period);
        }

        sealed class SystemTimeProvider : TimeProvider { }
    }
}

namespace System.Threading
{
    /// <summary>A timer from TimeProvider.CreateTimer: Change it, or Dispose it to stop it.</summary>
    public interface ITimer : IDisposable, IAsyncDisposable
    {
        /// <summary>Fires after <paramref name="dueTime"/>, then every <paramref name="period"/>. True if the timer could be changed.</summary>
        bool Change(TimeSpan dueTime, TimeSpan period);
    }
}
