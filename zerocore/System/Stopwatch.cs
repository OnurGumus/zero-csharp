using System.Runtime;

namespace System.Diagnostics
{
    // Measures elapsed time on the monotonic clock (the C library's clock 1,
    // as lib/Clock.cs's Monotonic), with .NET's members. Timestamps are
    // nanoseconds, so Frequency is 1,000,000,000, as .NET's on Linux and
    // macOS. The resolution depends on the target: about a millisecond on DOS.
    public unsafe class Stopwatch
    {
        public static readonly long Frequency = 1_000_000_000;
        public static readonly bool IsHighResolution = true;

        private long _elapsed;
        private long _startTimestamp;
        private bool _isRunning;

        public Stopwatch() { }

        public static Stopwatch StartNew()
        {
            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();
            return stopwatch;
        }

        public static long GetTimestamp()
        {
            long* timespec = stackalloc long[2];
            if (Platform.clock_gettime(1, timespec) != 0)
                return 0;
            return timespec[0] * 1_000_000_000 + timespec[1];
        }

        public static TimeSpan GetElapsedTime(long startingTimestamp) => GetElapsedTime(startingTimestamp, GetTimestamp());

        public static TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp)
            => new TimeSpan((endingTimestamp - startingTimestamp) / TimeSpan.NanosecondsPerTick);

        public void Start()
        {
            if (!_isRunning)
            {
                _startTimestamp = GetTimestamp();
                _isRunning = true;
            }
        }

        public void Stop()
        {
            if (_isRunning)
            {
                _elapsed += GetTimestamp() - _startTimestamp;
                _isRunning = false;
                if (_elapsed < 0)
                    _elapsed = 0;
            }
        }

        public void Reset()
        {
            _elapsed = 0;
            _isRunning = false;
            _startTimestamp = 0;
        }

        public void Restart()
        {
            _elapsed = 0;
            _startTimestamp = GetTimestamp();
            _isRunning = true;
        }

        public bool IsRunning => _isRunning;

        // In timestamp ticks (nanoseconds), as .NET's.
        public long ElapsedTicks => _isRunning ? _elapsed + GetTimestamp() - _startTimestamp : _elapsed;

        public TimeSpan Elapsed => new TimeSpan(ElapsedTicks / TimeSpan.NanosecondsPerTick);

        public long ElapsedMilliseconds => ElapsedTicks / 1_000_000;

        public override string ToString() => Elapsed.ToString();
    }
}
