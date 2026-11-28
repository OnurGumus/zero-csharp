// Test: the timers in lib/Timers.cs. System.Timers.Timer and
// System.Threading.Timer fire on a fiber inside Fiber.Run: repeating and
// one-shot timers, stopping and restarting from a handler, stopping another
// timer's sleeping fiber, changing the interval, and removing handlers.
// PeriodicTimer's wait blocks, in a fiber or outside one. And TimeSpan, and
// TimeProvider: the system's, and a fake one whose time moves only when told.
// Not on WebAssembly, which has no fibers.
// Exit code = number of failed checks.
//
// `Timers forgotten` starts a timer and returns from Main without
// Fiber.Run, and must be stopped with a message. test.sh checks it.
// Build: ./build.sh Timers

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using PeriodicTimer = System.Threading.PeriodicTimer;
using Timer = System.Timers.Timer;

namespace TimersSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void End(string section)
    {
        Out.Write(s_sectionFailures == 0 ? "PASS " : "FAIL ");
        Out.Write(section);
        Out.Write(" (");
        Out.Write(s_sectionChecks);
        Out.Line(" checks)");
        s_failures += s_sectionFailures;
        s_sectionChecks = 0;
        s_sectionFailures = 0;
    }

    static void Check(string name, bool ok)
    {
        s_sectionChecks++;
        if (!ok)
        {
            s_sectionFailures++;
            Out.Write("  failed: ");
            Out.Line(name);
        }
    }

    static long Milliseconds(long since) => (Clock.Monotonic - since) / 1_000_000;

    static int Main(string[] args)
    {
        // A timer started and never given Fiber.Run, as a .NET program that
        // waits in Console.ReadLine would do: Main returning must stop the
        // program with a message. test.sh checks it.
        if (args.Length > 0 && args[0] == "forgotten")
        {
            new Timer(10).Start();
            return 0;
        }

        // A repeating timer, stopped by its own handler on the fifth tick:
        // Run returns once it has stopped.
        {
            long start = Clock.Monotonic;
            // docs:timer
            // Fires every 20 ms, on a fiber of its own, until its handler
            // stops it. Fiber.Run returns once every timer has stopped.
            var timer = new Timer(20);
            int ticks = 0;
            timer.Elapsed += (sender, e) =>
            {
                Console.WriteLine($"tick {++ticks}");
                if (ticks == 5)
                    timer.Stop();
            };
            timer.Start();
            Fiber.Run();
            // docs:end
            long took = Milliseconds(start);
            Check("five ticks", ticks == 5);
            Check("stopped", !timer.Enabled);
            Check("took at least 5 intervals", took >= 100);
            End("Repeat");
        }

        // A one-shot timer fires once and stops before its handler runs; a
        // handler that starts it again makes it fire again.
        {
            var once = new Timer(10) { AutoReset = false };
            int onceTicks = 0;
            bool enabledInHandler = true;
            once.Elapsed += (sender, e) =>
            {
                onceTicks++;
                enabledInHandler = once.Enabled;
            };
            once.Start();
            Fiber.Run();
            Check("one tick", onceTicks == 1);
            Check("stopped before the handler", !enabledInHandler);

            var rearmed = new Timer(10) { AutoReset = false };
            int rearmedTicks = 0;
            rearmed.Elapsed += (sender, e) =>
            {
                if (++rearmedTicks < 3)
                    rearmed.Start();
            };
            rearmed.Enabled = true;

            // Started, stopped and started again before Run: still one fiber,
            // so one tick.
            var toggled = new Timer(10) { AutoReset = false };
            int toggledTicks = 0;
            toggled.Elapsed += (sender, e) => toggledTicks++;
            toggled.Start();
            toggled.Stop();
            toggled.Start();

            Fiber.Run();
            Check("restarted from its handler", rearmedTicks == 3);
            Check("toggled before Run", toggledTicks == 1);
            End("Once");
        }

        // Stop wakes a sleeping timer's fiber: an hour-long timer stopped by
        // another after 10 ms lets Run return at once.
        {
            var hour = new Timer(3_600_000);
            int hourTicks = 0;
            hour.Elapsed += (sender, e) => hourTicks++;
            var stopper = new Timer(10) { AutoReset = false };
            stopper.Elapsed += (sender, e) => hour.Stop();
            hour.Start();
            stopper.Start();
            long start = Clock.Monotonic;
            Fiber.Run();
            Check("never fired", hourTicks == 0);
            Check("Run returned at once", Milliseconds(start) < 1000);
            End("Stop");
        }

        // Two timers take turns: one every 20 ms and one every 50 ms. The
        // slower one stops both on its second tick, by which time the faster
        // one has fired about four times. (Loose bounds: a busy machine can
        // wake a sleeper late.)
        {
            var fast = new Timer(20);
            var slow = new Timer(50);
            int fastTicks = 0, slowTicks = 0;
            fast.Elapsed += (sender, e) => fastTicks++;
            slow.Elapsed += (sender, e) =>
            {
                if (++slowTicks == 2)
                {
                    fast.Stop();
                    slow.Stop();
                }
            };
            fast.Start();
            slow.Start();
            Fiber.Run();
            Check("fast ticks", fastTicks >= 2 && fastTicks <= 8);
            Check("slow ticks", slowTicks == 2);
            End("Together");
        }

        // Setting Interval while running starts the count again; -= removes
        // a handler; Dispose stops the timer.
        {
            var timer = new Timer(3_600_000);
            Check("starts stopped", !timer.Enabled && timer.AutoReset);
            int first = 0, second = 0;
            bool senderIsTimer = true;
            ElapsedEventHandler counter = (sender, e) => first++;
            timer.Elapsed += counter;
            timer.Elapsed += (sender, e) =>
            {
                senderIsTimer &= sender == timer;
                if (++second == 2)
                    timer.Elapsed -= counter;
                else if (second == 4)
                    timer.Dispose();
            };
            timer.Start();
            Check("started", timer.Enabled);
            timer.Interval = 10;
            Check("interval", timer.Interval == 10);
            Fiber.Run();
            Check("removed after two", first == 2);
            Check("four ticks", second == 4);
            Check("sender", senderIsTimer);
            Check("disposed is stopped", !timer.Enabled);
            End("Changes");
        }

        // System.Threading.Timer: a due time, then a period. The callback
        // gets the state; Change(Infinite, Infinite) stops the timer, a
        // period of Infinite fires once, and Dispose stops it for good.
        {
            var counted = new Counted();
            System.Threading.Timer periodic = null;
            periodic = new System.Threading.Timer(state =>
            {
                if (++((Counted)state).Count == 3)
                    periodic.Change(Infinite, Infinite);
            }, counted, 0, 20);

            int onceTicks = 0;
            new System.Threading.Timer(_ => onceTicks++, null, 10, Infinite);

            // Made stopped, with itself as the state; Change starts it.
            object seen = null;
            var later = new System.Threading.Timer(state => seen = state);
            later.Change(10, Infinite);

            int disposedTicks = 0;
            System.Threading.Timer disposed = null;
            disposed = new System.Threading.Timer(_ =>
            {
                if (++disposedTicks == 2)
                    disposed.Dispose();
            }, null, 10, 10);

            Fiber.Run();
            Check("stopped by Change", counted.Count == 3);
            Check("once", onceTicks == 1);
            Check("state is the timer", seen == later);
            Check("stopped by Dispose", disposedTicks == 2);
            End("Threading");
        }

        // PeriodicTimer outside a fiber: each wait sleeps the thread until
        // the next tick. Ticks missed while not waiting count as one.
        {
            var periodic = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
            long start = Clock.Monotonic;
            int ticks = 0;
            while (ticks < 3 && periodic.WaitForNextTick())
                ticks++;
            Check("three ticks", ticks == 3);
            Check("took at least 3 periods", Milliseconds(start) >= 60);

            // Ticks at 100, 200 and 300 ms pass unwaited for: the first wait
            // returns at once, the next at the tick at 400 ms.
            periodic.Period = TimeSpan.FromMilliseconds(100);
            Fiber.Sleep(350);
            start = Clock.Monotonic;
            periodic.WaitForNextTick();
            Check("missed ticks: at once", Milliseconds(start) < 40);
            start = Clock.Monotonic;
            periodic.WaitForNextTick();
            Check("missed ticks: counted once", Milliseconds(start) >= 10);
            periodic.Dispose();
            Check("disposed", !periodic.WaitForNextTick());
            End("Periodic");
        }

        // PeriodicTimer in a fiber: the wait lets other fibers run, and
        // Dispose from another fiber ends it with false, at once.
        {
            var waited = new Waited();
            Fiber.Spawn(waited, static w =>
            {
                while (w.Timer.WaitForNextTick())
                    w.Ticks++;
                w.Ended = true;
            });
            Fiber.Spawn(waited, static w =>
            {
                Fiber.Sleep(10);
                w.Timer.Dispose();
            });
            long start = Clock.Monotonic;
            Fiber.Run();
            Check("ended by Dispose", waited.Ended && waited.Ticks == 0);
            Check("at once", Milliseconds(start) < 1000);
            End("Periodic in a fiber");
        }

        // TimeSpan, and the timers' TimeSpan overloads.
        {
            var span = new TimeSpan(1, 2, 3, 4, 5);
            Check("parts", span.Days == 1 && span.Hours == 2 && span.Minutes == 3
                && span.Seconds == 4 && span.Milliseconds == 5);
            Check("text", span.ToString() == "1.02:03:04.0050000");
            Check("negative", (-TimeSpan.FromMinutes(90)).ToString() == "-01:30:00");
            Check("from double", TimeSpan.FromSeconds(1.5).TotalMilliseconds == 1500);
            Check("compare", TimeSpan.FromSeconds(1) < TimeSpan.FromMilliseconds(1001)
                && TimeSpan.FromHours(1) == TimeSpan.FromMinutes(60));

            int ticks = 0;
            new System.Threading.Timer(_ => ticks++, null,
                TimeSpan.FromMilliseconds(10), System.Threading.Timeout.InfiniteTimeSpan);
            var timer = new Timer(TimeSpan.FromMilliseconds(10)) { AutoReset = false };
            timer.Elapsed += (sender, e) => ticks++;
            timer.Start();
            Fiber.Run();
            Check("timers", ticks == 2);
            End("TimeSpan");
        }

        // TimeProvider.System: the real clock, and System.Threading.Timer.
        {
            TimeProvider time = TimeProvider.System;
            long start = time.GetTimestamp();
            Fiber.Sleep(20);
            TimeSpan elapsed = time.GetElapsedTime(start);
            Check("elapsed", elapsed.TotalMilliseconds >= 19 && elapsed.TotalMilliseconds < 5000);
            Check("now", time.GetUtcNow().Year >= 2024);
            int fired = 0;
            ITimer once = time.CreateTimer(_ => fired++, null,
                TimeSpan.FromMilliseconds(5), System.Threading.Timeout.InfiniteTimeSpan);
            Fiber.Run();
            Check("timer", fired == 1);
            once.Dispose();
            End("TimeProvider.System");
        }

        // A fake TimeProvider: time stands still until the test moves it, so
        // what waits for an hour is checked at once.
        {
            // docs:fake-time
            var time = new ManualTime();
            Task delay = Task.Delay(TimeSpan.FromHours(1), time);
            time.Advance(TimeSpan.FromMinutes(59));
            bool early = delay.IsCompleted;     // false
            time.Advance(TimeSpan.FromMinutes(1));
            bool done = delay.IsCompleted;      // true
            // docs:end
            Check("not yet", !early);
            Check("an hour later", done);

            long start = time.GetTimestamp();
            time.Advance(TimeSpan.FromSeconds(90));
            Check("elapsed", time.GetElapsedTime(start) == TimeSpan.FromSeconds(90));

            var source = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
            time.Advance(TimeSpan.FromSeconds(9));
            Check("source waits", !source.IsCancellationRequested);
            time.Advance(TimeSpan.FromSeconds(1));
            Check("source canceled", source.IsCancellationRequested);
            source.Dispose(); // and its timer

            var stop = new CancellationTokenSource();
            Task stopped = Task.Delay(TimeSpan.FromSeconds(5), time, stop.Token);
            stop.Cancel();
            Check("delay canceled", stopped.IsCanceled && time.Live == 0);

            using var periodic = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
            ValueTask<bool> tick = periodic.WaitForNextTickAsync();
            Check("no tick yet", !tick.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(1));
            Check("ticked", tick.IsCompleted && tick.Result);
            time.Advance(TimeSpan.FromSeconds(3)); // three ticks, nobody waiting
            Check("missed ticks count once", periodic.WaitForNextTickAsync().IsCompleted
                && !periodic.WaitForNextTickAsync().IsCompleted);

            // An async method that waits three times, driven by the test.
            Task<int> retries = RetryAsync(time);
            int steps = 0;
            while (!retries.IsCompleted && steps++ < 10)
            {
                time.Advance(TimeSpan.FromSeconds(1));
                Fiber.Run(); // the continuations that are due
            }
            Check("retried", retries.IsCompleted && retries.Result == 3 && steps == 3);
            End("Fake TimeProvider");
        }

        return s_failures;
    }

    const int Infinite = System.Threading.Timeout.Infinite;

    // Tries three times, a second apart by the provider's clock.
    static async Task<int> RetryAsync(TimeProvider time)
    {
        int tries = 0;
        for (int i = 0; i < 3; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), time);
            tries++;
        }
        return tries;
    }
}

// A TimeProvider for tests, like .NET's FakeTimeProvider: its time moves only
// when Advance says, firing the timers that come due on the way, in order.
public sealed class ManualTime : TimeProvider
{
    long _now;                                  // in TimeSpan ticks
    ManualTimer[] _timers = new ManualTimer[4];
    int _count;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _now;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(_now / TimeSpan.TicksPerMillisecond);

    // Timers made and not disposed.
    public int Live
    {
        get
        {
            int live = 0;
            for (int i = 0; i < _count; i++)
                if (!_timers[i].Disposed)
                    live++;
            return live;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        if (_count == _timers.Length)
        {
            var more = new ManualTimer[_count * 2];
            _timers.AsSpan().CopyTo(more);
            _timers = more;
        }
        _timers[_count++] = timer;
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        long end = _now + by.Ticks;
        while (true)
        {
            ManualTimer next = null;
            for (int i = 0; i < _count; i++)
            {
                ManualTimer t = _timers[i];
                if (t.Due >= 0 && t.Due <= end && (next == null || t.Due < next.Due))
                    next = t;
            }
            if (next == null)
                break;
            _now = next.Due;
            next.Fire();
        }
        _now = end;
    }

    internal long Now => _now;
}

public sealed class ManualTimer(ManualTime time, TimerCallback callback, object state) : ITimer
{
    public long Due = -1;       // in the provider's ticks; -1: stopped
    long _period;               // 0: once
    public bool Disposed;

    public bool Change(TimeSpan dueTime, TimeSpan period)
    {
        if (Disposed)
            return false;
        Due = dueTime == Timeout.InfiniteTimeSpan ? -1 : time.Now + dueTime.Ticks;
        _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
        return true;
    }

    internal void Fire()
    {
        Due = _period == 0 ? -1 : Due + _period;
        callback(state);
    }

    public void Dispose()
    {
        Disposed = true;
        Due = -1;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }
}

public sealed class Counted
{
    public int Count;
}

public sealed class Waited
{
    public PeriodicTimer Timer = new PeriodicTimer(System.Threading.Timeout.InfiniteTimeSpan);
    public int Ticks;
    public bool Ended;
}
