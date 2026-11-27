// System.Timers.Timer and System.Threading.Timer for zero mode: they fire on
// a fiber (lib/Fibers.cs) rather than a thread pool thread. And
// System.Threading.PeriodicTimer, whose wait blocks the fiber that calls it.
//
//   var timer = new System.Timers.Timer(1000);
//   timer.Elapsed += (sender, e) => Console.WriteLine("tick");
//   timer.Start();
//   Fiber.Run(); // fires the timers; returns once every one has stopped
//
//   var once = new System.Threading.Timer(_ => Console.WriteLine("done"), null, 500, Timeout.Infinite);
//   Fiber.Run();
//
//   using var periodic = new PeriodicTimer(TimeSpan.FromSeconds(1));
//   while (periodic.WaitForNextTick())
//       Console.WriteLine("tick");
//
// A running timer has a fiber of its own, which sleeps until the next tick,
// calls the handlers, and ends when the timer stops. So timers fire only
// while the thread is in Fiber.Run, and Run returns once every timer has
// stopped and every other fiber has ended. A handler runs on the same thread
// as everything else, taking turns with the other fibers: it needs no locks,
// it can use Console, Fiber.Sleep and channels, and two calls of the same
// timer's handlers never overlap. A handler that blocks the thread (reading
// standard input, a long loop without Fiber.Yield) delays every timer.
//
// Differences from .NET:
// - Nothing fires outside Fiber.Run (in .NET, a thread pool thread does it
//   while Main goes on, often in Console.ReadLine). So that .NET's way
//   doesn't go quietly wrong, Main returning while a timer is still running
//   stops the program with a message saying to call Fiber.Run.
//   Environment.Exit doesn't check.
// - A handler that takes longer than the period delays the next tick
//   instead of overlapping it; after it, the timer fires once at once and
//   keeps its rhythm from there, rather than making up every tick it missed.
// - System.Timers: SignalTime is a CalendarTime (lib/Clock.cs) in UTC, not a
//   local DateTime. Each timer reuses one ElapsedEventArgs, so a tick
//   allocates nothing: a handler that keeps e finds it changed by the next
//   tick. Elapsed is a list of handlers kept by the timer (delegates here
//   can't be combined); -= removes the same delegate object that += added.
//   No SynchronizingObject, Site or BeginInit/EndInit.
// - System.Threading.Timer: no Dispose(WaitHandle), DisposeAsync or
//   ActiveCount.
// - PeriodicTimer: WaitForNextTick, which blocks, besides .NET's
//   WaitForNextTickAsync(token), which waits on Task.Delay's timer fiber
//   and reuses its task, so an async loop over it costs no fiber and
//   allocates nothing. With a TimeProvider (lib/TimeProvider.cs) other than
//   the system's, it ticks on the provider's timer. WaitForNextTick needs no Fiber.Run:
//   in a fiber the wait lets the others run, outside one it sleeps the
//   thread.
// - StackSize, the size of the timer's fiber's stack, is new on both.
// - Not on WebAssembly, which has no fibers.

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

// What both timers are built on: a fiber that calls Tick at a due time and
// then every period. Each timer has one, made with it.
internal abstract class TimerFiber
{
    long _dueMs;
    long _periodMs;             // 0: fire once
    bool _running;
    bool _hasFiber;             // spawned and not yet ended
    // Bumped by every Schedule: the fiber then starts counting again.
    int _generation;
    Fiber.Record* _fiber;       // the fiber, once it has started
    internal int StackSize;

    // Timers running, on every thread, for the check when Main returns.
    static int s_running;

    internal bool Running => _running;

    protected abstract void Tick();

    // Fires dueMs from now, then every periodMs (0: once), counting from now
    // even if the timer was already running.
    internal void Schedule(long dueMs, long periodMs)
    {
        _dueMs = dueMs;
        _periodMs = periodMs;
        SetRunning(true);
        _generation++;
        if (!_hasFiber)
        {
            _hasFiber = true;
            Fiber.Spawn(this, static timer => timer.Run(), StackSize);
        }
        else
        {
            WakeFiber();
        }
    }

    // Stops the timer. Its fiber ends, so Fiber.Run can return.
    internal void Cancel()
    {
        if (!_running)
            return;
        SetRunning(false);
        WakeFiber();
    }

    void SetRunning(bool running)
    {
        if (running == _running)
            return;
        _running = running;
        if (!running)
        {
            s_running--;
            return;
        }
        if (s_running++ == 0 && s_installed == 0 && System.Threading.Interlocked.CompareExchange(ref s_installed, 1, 0) == 0)
            unsafe { s_next = Environment.AddAtMainReturn(&CheckAtMainReturn); } // SAFETY: a static method
    }

    static int s_installed;
    static unsafe delegate*<void> s_next;

    // Main returned with a timer running: it would never have fired again
    // (or at all), which in .NET it would have, on its own thread.
    static void CheckAtMainReturn()
    {
        unsafe
        {
            // SAFETY: the check this one replaced, a static method.
            if (s_next != null)
                s_next();
        }
        if (s_running > 0)
            Environment.FailFast("Timer: Main returned with a timer still running. Timers fire only inside Fiber.Run(): call it after starting them, instead of waiting in Console.ReadLine. Stop timers that shouldn't fire.");
    }

    // Ends the fiber's sleep, so it sees what changed now rather than at the
    // tick it was waiting for.
    void WakeFiber()
    {
        Fiber.Record* fiber = _fiber;
        if (fiber != null && fiber != Fiber.CurrentRecord)
            Fiber.WakeEarly(fiber);
    }

    // The timer's fiber: one at a time, alive while the timer runs.
    void Run()
    {
        _fiber = Fiber.CurrentRecord;
        int generation = _generation - 1; // so the first pass starts the count
        long due = 0;
        while (_running)
        {
            long now = Clock.Monotonic;
            if (generation != _generation)
            {
                generation = _generation;
                due = now + _dueMs * 1_000_000;
            }
            if (now < due)
            {
                // Milliseconds, rounded up: Sleep wakes at the earliest then.
                Fiber.Sleep((due - now + 999_999) / 1_000_000);
                continue;
            }

            // A timer that fires once is stopped before its handlers run, so
            // one of them can start it again, as in .NET.
            if (_periodMs == 0)
                SetRunning(false);
            Tick();
            if (generation != _generation)
                continue; // rescheduled by a handler: the loop starts the count

            due += _periodMs * 1_000_000;
            now = Clock.Monotonic;
            if (due < now)
                due = now; // late: fire once now, and keep the rhythm from here
        }
        _fiber = null;
        _hasFiber = false;
    }
}

namespace System.Timers
{
    /// <summary>Handles a timer's Elapsed event.</summary>
    public delegate void ElapsedEventHandler(object sender, ElapsedEventArgs e);

    /// <summary>What a timer's Elapsed event passes: when it fired.</summary>
    public class ElapsedEventArgs : EventArgs
    {
        internal long _unixMilliseconds;

        public ElapsedEventArgs(CalendarTime signalTime) => _unixMilliseconds = signalTime.ToUnixMilliseconds();

        internal ElapsedEventArgs() { }

        /// <summary>When the timer fired, in UTC (.NET gives a local DateTime).</summary>
        public CalendarTime SignalTime => CalendarTime.FromUnixMilliseconds(_unixMilliseconds);
    }

    /// <summary>
    /// Raises Elapsed after Interval milliseconds, and again every Interval
    /// while AutoReset is true. Fires on a fiber, inside Fiber.Run.
    /// </summary>
    public class Timer : IDisposable
    {
        sealed class Engine : TimerFiber
        {
            internal Timer Owner;
            protected override void Tick() => Owner.Fire();
        }

        readonly Engine _engine = new Engine();
        long _intervalMs = 100;     // Interval, rounded up to a millisecond, as .NET does
        double _interval = 100;
        bool _autoReset = true;
        bool _disposed;
        ElapsedEventHandler[] _handlers;
        int _handlerCount;
        ElapsedEventArgs _args;

        /// <summary>A timer with an interval of 100 ms, stopped.</summary>
        public Timer() => _engine.Owner = this;

        /// <summary>A timer with an interval of <paramref name="interval"/> milliseconds, stopped.</summary>
        public Timer(double interval) : this() => Interval = interval;

        /// <summary>A timer with an interval of <paramref name="interval"/>, stopped.</summary>
        public Timer(TimeSpan interval) : this(interval.TotalMilliseconds) { }

        /// <summary>
        /// Milliseconds between ticks: more than 0 and at most int.MaxValue,
        /// rounded up to a whole millisecond. Changing it while the timer runs
        /// starts the count again from now.
        /// </summary>
        public double Interval
        {
            get => _interval;
            set
            {
                if (!(value > 0) || value > int.MaxValue)
                    throw new ArgumentException("'" + value.ToString() + "' is not a valid value for 'Interval'. 'Interval' must be greater than 0 and at most int.MaxValue.", nameof(value));
                _interval = value;
                _intervalMs = (long)Math.Ceiling(value);
                if (_engine.Running)
                    Schedule();
            }
        }

        /// <summary>
        /// True (the default): fire every Interval. False: fire once, then stop.
        /// Changing it while the timer runs starts the count again from now.
        /// </summary>
        public bool AutoReset
        {
            get => _autoReset;
            set
            {
                if (value == _autoReset)
                    return;
                _autoReset = value;
                if (_engine.Running)
                    Schedule();
            }
        }

        /// <summary>Whether the timer runs: setting it is Start or Stop.</summary>
        public bool Enabled
        {
            get => _engine.Running;
            set
            {
                if (value)
                    Start();
                else
                    Stop();
            }
        }

        /// <summary>
        /// The stack size of the timer's fiber, which runs the handlers: 0 (the
        /// default) means Fiber.DefaultStackSize. Takes effect when the timer
        /// next gets a fiber. Not in .NET.
        /// </summary>
        public int StackSize
        {
            get => _engine.StackSize;
            set => _engine.StackSize = value;
        }

        /// <summary>Raised every Interval, on the timer's fiber.</summary>
        public event ElapsedEventHandler Elapsed
        {
            add
            {
                if (value == null)
                    return;
                // Copied rather than grown in place, so a handler that adds or
                // removes one doesn't change the list being called.
                var handlers = new ElapsedEventHandler[_handlerCount + 1];
                for (int i = 0; i < _handlerCount; i++)
                    handlers[i] = _handlers[i];
                handlers[_handlerCount] = value;
                _handlers = handlers;
                _handlerCount++;
            }
            remove
            {
                // The last one added goes first, as with .NET's delegates.
                for (int i = _handlerCount - 1; i >= 0; i--)
                {
                    if ((object)_handlers[i] != value)
                        continue;
                    var handlers = new ElapsedEventHandler[_handlerCount - 1];
                    for (int j = 0, k = 0; j < _handlerCount; j++)
                        if (j != i)
                            handlers[k++] = _handlers[j];
                    _handlers = handlers;
                    _handlerCount--;
                    return;
                }
            }
        }

        /// <summary>
        /// Starts the timer: Elapsed fires Interval milliseconds from now, once
        /// the thread is in Fiber.Run. Starting a running timer changes nothing.
        /// </summary>
        public void Start()
        {
            if (_engine.Running)
                return;
            if (_disposed)
                throw new ObjectDisposedException("Timer");
            Schedule();
        }

        /// <summary>Stops the timer. Its fiber ends, so Fiber.Run can return.</summary>
        public void Stop() => _engine.Cancel();

        /// <summary>Stops the timer for good: Start then throws ObjectDisposedException.</summary>
        public void Close() => Dispose();

        public void Dispose()
        {
            Stop();
            _disposed = true;
        }

        void Schedule() => _engine.Schedule(_intervalMs, _autoReset ? _intervalMs : 0);

        void Fire()
        {
            _args ??= new ElapsedEventArgs();
            _args._unixMilliseconds = Clock.UnixMilliseconds;
            ElapsedEventHandler[] handlers = _handlers;
            int count = _handlerCount;
            for (int i = 0; i < count; i++)
                handlers[i](this, _args);
        }
    }
}

namespace System.Threading
{
    /// <summary>What a System.Threading.Timer calls, with the state it was given.</summary>
    public delegate void TimerCallback(object state);

    /// <summary>The value that means "never" for a timer's due time or period.</summary>
    public static class Timeout
    {
        public const int Infinite = -1;
        public const uint UnsignedInfinite = unchecked((uint)-1);
        public static readonly TimeSpan InfiniteTimeSpan = new TimeSpan(-TimeSpan.TicksPerMillisecond);
    }

    /// <summary>
    /// Calls a callback after a due time, then every period. Fires on a fiber,
    /// inside Fiber.Run. Times are in milliseconds; Timeout.Infinite (-1) for
    /// the due time keeps the timer stopped, and for the period (or 0) makes
    /// it fire once.
    /// </summary>
    public sealed class Timer : ITimer
    {
        sealed class Engine : TimerFiber
        {
            internal Timer Owner;
            protected override void Tick() => Owner._callback(Owner._state);
        }

        // Timer's largest due time and period, as in .NET: 2^32 - 2 ms.
        const long MaxTime = 0xFFFF_FFFE;

        readonly Engine _engine = new Engine();
        readonly TimerCallback _callback;
        readonly object _state;
        bool _disposed;

        /// <summary>A stopped timer, whose state is the timer itself. Change starts it.</summary>
        public Timer(TimerCallback callback)
            : this(callback, null, Timeout.Infinite, Timeout.Infinite) => _state = this;

        /// <summary>
        /// A timer that calls <paramref name="callback"/>(<paramref name="state"/>)
        /// <paramref name="dueTime"/> ms from now, then every <paramref name="period"/> ms.
        /// </summary>
        public Timer(TimerCallback callback, object state, int dueTime, int period)
            : this(callback, state, (long)dueTime, (long)period) { }

        public Timer(TimerCallback callback, object state, uint dueTime, uint period)
            : this(callback, state, Unsigned(dueTime), Unsigned(period)) { }

        public Timer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
            : this(callback, state, Milliseconds(dueTime, nameof(dueTime)), Milliseconds(period, nameof(period))) { }

        public Timer(TimerCallback callback, object state, long dueTime, long period)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            _callback = callback;
            _state = state;
            _engine.Owner = this;
            Change(dueTime, period);
        }

        /// <summary>
        /// The stack size of the timer's fiber, which runs the callback: 0 (the
        /// default) means Fiber.DefaultStackSize. Takes effect when the timer
        /// next gets a fiber. Not in .NET.
        /// </summary>
        public int StackSize
        {
            get => _engine.StackSize;
            set => _engine.StackSize = value;
        }

        /// <summary>
        /// Fires <paramref name="dueTime"/> ms from now (Timeout.Infinite: stops
        /// the timer), then every <paramref name="period"/> ms (Timeout.Infinite
        /// or 0: once). Returns true.
        /// </summary>
        public bool Change(int dueTime, int period) => Change((long)dueTime, (long)period);

        public bool Change(uint dueTime, uint period) => Change(Unsigned(dueTime), Unsigned(period));

        public bool Change(TimeSpan dueTime, TimeSpan period)
            => Change(Milliseconds(dueTime, nameof(dueTime)), Milliseconds(period, nameof(period)));

        public bool Change(long dueTime, long period)
        {
            if (dueTime < -1)
                throw new ArgumentOutOfRangeException(nameof(dueTime), "Number must be either non-negative and less than or equal to Int32.MaxValue or -1.");
            if (period < -1)
                throw new ArgumentOutOfRangeException(nameof(period), "Number must be either non-negative and less than or equal to Int32.MaxValue or -1.");
            if (dueTime > MaxTime)
                throw new ArgumentOutOfRangeException(nameof(dueTime), "Time-out interval must be less than 2^32-2.");
            if (period > MaxTime)
                throw new ArgumentOutOfRangeException(nameof(period), "Time-out interval must be less than 2^32-2.");
            if (_disposed)
                throw new ObjectDisposedException("Timer");
            if (dueTime == Timeout.Infinite)
                _engine.Cancel();
            else
                _engine.Schedule(dueTime, period == Timeout.Infinite ? 0 : period);
            return true;
        }

        /// <summary>Stops the timer for good: Change then throws ObjectDisposedException.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _engine.Cancel();
        }

        /// <summary>Dispose, as a task (ITimer's IAsyncDisposable).</summary>
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        static long Unsigned(uint time) => time == Timeout.UnsignedInfinite ? Timeout.Infinite : time;

        // Whole milliseconds, as .NET takes them: truncated, -1 for InfiniteTimeSpan.
        internal static long Milliseconds(TimeSpan time, string name)
        {
            long milliseconds = time.Ticks / TimeSpan.TicksPerMillisecond;
            if (milliseconds < -1)
                throw new ArgumentOutOfRangeException(name, "Number must be either non-negative and less than or equal to Int32.MaxValue or -1.");
            if (milliseconds > MaxTime)
                throw new ArgumentOutOfRangeException(name, "Time-out interval must be less than 2^32-2.");
            return milliseconds;
        }
    }

    /// <summary>
    /// A timer to wait on: each WaitForNextTick returns at the next tick of
    /// Period. Ticks missed while not waiting count as one, so a slow loop
    /// doesn't make up for lost time. In a fiber the wait lets the other
    /// fibers run; outside one it sleeps the thread.
    /// </summary>
    public sealed class PeriodicTimer : IDisposable
    {
        TimeSpan _period;
        long _periodNs;             // 0: Timeout.InfiniteTimeSpan, never ticks
        long _due;                  // Clock.Monotonic of the next tick
        bool _disposed;
        bool _waiting;
        Fiber.Record* _waiter;      // the fiber waiting, if one is
        // WaitForNextTickAsync's: the task it hands out (reused: one wait at a
        // time), the delay it waits on, on Task.Delay's list (reused too), and
        // the token's callback. So a loop over it allocates nothing.
        Task<bool>? _tick;
        TickDelay? _delay;
        CancellationTokenRegistration _registration;
        Registration? _onCancel;
        // With a TimeProvider other than the system's: its timer, which
        // ticks every Period, and whether a tick came since the last wait.
        TimeProvider? _provider;
        ITimer? _providerTimer;
        bool _ticked;

        bool AsyncWaiting => _tick != null && !_tick.IsCompleted;

        /// <summary>
        /// A timer that ticks every <paramref name="period"/>: at least a
        /// millisecond, at most 2^32 - 2 ms, or Timeout.InfiniteTimeSpan.
        /// </summary>
        public PeriodicTimer(TimeSpan period) => Period = period;

        /// <summary>
        /// A timer that ticks every <paramref name="period"/> by
        /// <paramref name="timeProvider"/>'s clock: on a timer the provider
        /// creates.
        /// </summary>
        public PeriodicTimer(TimeSpan period, TimeProvider timeProvider)
        {
            if (timeProvider == null)
                throw new ArgumentNullException(nameof(timeProvider));
            if (timeProvider != TimeProvider.System)
                _provider = timeProvider;
            Period = period;
        }

        /// <summary>
        /// The time between ticks. Setting it starts the count again from now,
        /// as in .NET.
        /// </summary>
        public TimeSpan Period
        {
            get => _period;
            set
            {
                long milliseconds = value.Ticks / TimeSpan.TicksPerMillisecond;
                if (value != Timeout.InfiniteTimeSpan && (milliseconds < 1 || milliseconds > 0xFFFF_FFFE))
                    throw new ArgumentOutOfRangeException(nameof(value));
                _period = value;
                if (_provider != null)
                {
                    _ticked = false;
                    _providerTimer ??= _provider.CreateTimer(static t => ((PeriodicTimer)t!).ProviderTick(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _providerTimer.Change(value, value);
                    return;
                }
                _periodNs = value == Timeout.InfiniteTimeSpan ? 0 : milliseconds * 1_000_000;
                _due = Clock.Monotonic + _periodNs;
                WakeWaiter(); // to wait for the new time
                if (AsyncWaiting)
                    Arm();
            }
        }

        /// <summary>
        /// Waits for the next tick and returns true, at once if one came since
        /// the last wait; returns false once the timer is disposed (also when
        /// Dispose is called during the wait). One wait at a time.
        /// </summary>
        public bool WaitForNextTick()
        {
            if (_disposed)
                return false;
            if (_waiting)
                throw new InvalidOperationException("Operation is not valid due to the current state of the object.");
            _waiting = true;
            _waiter = Fiber.CurrentRecord;
            bool ticked = false;
            while (!_disposed)
            {
                if (_provider != null)
                {
                    // ProviderTick wakes the sleep.
                    if (ticked = TryTick())
                        break;
                    Fiber.Sleep(1_000_000);
                    continue;
                }
                long now = Clock.Monotonic;
                if (_periodNs != 0 && now >= _due)
                {
                    // The ticks since: one, and the next keeps the rhythm.
                    _due += ((now - _due) / _periodNs + 1) * _periodNs;
                    ticked = true;
                    break;
                }
                // Milliseconds, rounded up, and at most 1000 s at a time, which
                // the C library's sleep takes in microseconds.
                long wait = _periodNs == 0 ? 1_000_000 : (_due - now + 999_999) / 1_000_000;
                Fiber.Sleep(wait < 1_000_000 ? wait : 1_000_000);
            }
            _waiting = false;
            _waiter = null;
            return ticked;
        }

        /// <summary>
        /// Waits for the next tick, as WaitForNextTick does, without blocking:
        /// true at the tick, false once the timer is disposed. Canceled with
        /// <paramref name="cancellationToken"/> (then awaiting it ends the
        /// program, as .NET's OperationCanceledException would: check the
        /// token, or Dispose the timer, to stop a loop instead). It waits on
        /// Task.Delay's timer fiber, with no fiber of its own.
        /// </summary>
        public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
                return new ValueTask<bool>(false);
            if (_waiting)
                throw new InvalidOperationException("Operation is not valid due to the current state of the object.");
            if (cancellationToken.IsCancellationRequested)
                return ValueTask.FromCanceled<bool>(cancellationToken);
            if (TryTick())
                return new ValueTask<bool>(true);
            _waiting = true;
            if (_tick == null)
                _tick = new Task<bool> { Reusable = true, Owned = true };
            else
                _tick.Reset();
            if (cancellationToken.CanBeCanceled)
            {
                _onCancel ??= new Registration(null, null, static t => ((PeriodicTimer)t!).CancelWait(), this);
                Arm();
                _registration = cancellationToken.Attach(_onCancel); // may cancel at once
            }
            else
            {
                Arm();
            }
            return new ValueTask<bool>(_tick);
        }

        // A tick has come since the last wait: counts it (all those since
        // as one) and keeps the rhythm.
        bool TryTick()
        {
            if (_provider != null)
            {
                bool ticked = _ticked;
                _ticked = false;
                return ticked;
            }
            long now = Clock.Monotonic;
            if (_periodNs == 0 || now < _due)
                return false;
            _due += ((now - _due) / _periodNs + 1) * _periodNs;
            return true;
        }

        // Waits on Task.Delay's timer for the next tick (or 1000 s at a time
        // when the period is infinite), counting from now.
        void Arm()
        {
            if (_provider != null)
                return; // ProviderTick ends the wait
            _delay ??= new TickDelay(this);
            System.Threading.Tasks.DelayTimer.Remove(_delay);
            long wait = _periodNs == 0 ? 1_000_000 : (_due - Clock.Monotonic + 999_999) / 1_000_000;
            _delay.Schedule(wait < 1 ? 1 : wait < 1_000_000 ? (int)wait : 1_000_000);
        }

        sealed class TickDelay(PeriodicTimer timer) : System.Threading.Tasks.DelayTimer
        {
            internal override void Due()
            {
                if (timer.TryTick())
                    timer.Finish(ticked: true, canceled: false);
                else
                    timer.Arm();
            }
        }

        // The provider's timer ticked: ends a wait, or counts for the next.
        void ProviderTick()
        {
            if (_disposed)
                return;
            _ticked = true;
            if (AsyncWaiting && TryTick())
                Finish(ticked: true, canceled: false);
            WakeWaiter();
        }

        // Ends WaitForNextTickAsync's wait: with a result, or canceled.
        void Finish(bool ticked, bool canceled)
        {
            if (_delay != null)
                System.Threading.Tasks.DelayTimer.Remove(_delay);
            _registration.Dispose();
            _registration = default;
            _waiting = false;
            if (canceled)
                _tick!.Complete(true);
            else
                _tick!.TrySetResult(ticked);
        }

        void CancelWait()
        {
            if (AsyncWaiting)
                Finish(ticked: false, canceled: true);
        }

        /// <summary>Stops the timer: a wait in progress, and every later one, returns false.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _providerTimer?.Dispose();
            WakeWaiter();
            if (AsyncWaiting)
                Finish(ticked: false, canceled: false);
        }

        void WakeWaiter()
        {
            Fiber.Record* waiter = _waiter;
            if (waiter != null && waiter != Fiber.CurrentRecord)
                Fiber.WakeEarly(waiter);
        }
    }
}
