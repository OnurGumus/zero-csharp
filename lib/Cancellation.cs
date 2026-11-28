// Cancellation, as in .NET: a CancellationTokenSource hands out tokens; code
// that can stop early checks token.IsCancellationRequested or registers a
// callback, and Cancel() (or CancelAfter's timer) asks it to stop.
//
//   using var cts = new CancellationTokenSource(500);  // cancels after 500 ms
//   Task delay = Task.Delay(10_000, cts.Token);
//   await delay.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
//   if (delay.IsCanceled) ...                           // after 500 ms
//
// Zero mode has no exceptions: `throw` ends the program. So where .NET
// throws OperationCanceledException, the program ends here: awaiting a
// canceled task plainly, its Result or Wait(), and
// ThrowIfCancellationRequested. The way that doesn't end it is .NET 8's
// ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing), then a look at
// IsCanceled, or checking IsCancellationRequested in a loop. Code ported
// from .NET that catches OperationCanceledException has to be changed so.
//
// Callbacks run in Cancel, on the fiber (or stack) that calls it, the last
// registered first, as in .NET. Everything is one thread's: like the fibers,
// a source and its tokens aren't meant to be shared between threads.

using System.Threading;
using System.Threading.Tasks;

namespace System
{
    /// <summary>What .NET throws when an operation is canceled. Here, `throw` ends the program with its message.</summary>
    public class OperationCanceledException : Exception
    {
        public OperationCanceledException() : this("The operation was canceled.") { }
        public OperationCanceledException(string message) : base(message) { }
        public OperationCanceledException(CancellationToken token) : this() { CancellationToken = token; }
        public OperationCanceledException(string message, CancellationToken token) : base(message) { CancellationToken = token; }

        public CancellationToken CancellationToken { get; }
    }
}

namespace System.Threading.Tasks
{
    /// <summary>What .NET throws when a canceled task is awaited. Here, it ends the program.</summary>
    public class TaskCanceledException : OperationCanceledException
    {
        public TaskCanceledException() : base("A task was canceled.") { }
        public TaskCanceledException(string message) : base(message) { }
    }
}

namespace System.Threading
{
    /// <summary>Cancels the tokens it hands out: Cancel, or CancelAfter a delay.</summary>
    public class CancellationTokenSource : IDisposable
    {
        bool _canceled;
        // Callbacks, the latest first.
        internal Registration? _callbacks;
        // CancelAfter's timer, on Task.Delay's list; or, for a source made
        // with a TimeProvider other than the system's, the provider's timer.
        DelayTimer? _timer;
        TimeProvider? _provider;
        ITimer? _providerTimer;
        // A linked source's registrations on its parents, removed by Dispose.
        CancellationTokenRegistration[]? _links;

        internal static readonly CancellationTokenSource s_canceled = Canceled();

        static CancellationTokenSource Canceled()
        {
            var source = new CancellationTokenSource();
            source._canceled = true;
            return source;
        }

        public CancellationTokenSource() { }

        /// <summary>A source that cancels itself after <paramref name="millisecondsDelay"/>.</summary>
        public CancellationTokenSource(int millisecondsDelay) => CancelAfter(millisecondsDelay);

        /// <summary>A source that cancels itself after <paramref name="delay"/>.</summary>
        public CancellationTokenSource(TimeSpan delay) => CancelAfter(delay);

        /// <summary>
        /// A source that cancels itself after <paramref name="delay"/> by
        /// <paramref name="timeProvider"/>'s clock; its later CancelAfter calls
        /// use that clock too.
        /// </summary>
        public CancellationTokenSource(TimeSpan delay, TimeProvider timeProvider)
        {
            if (timeProvider == null)
                throw new ArgumentNullException(nameof(timeProvider));
            if (timeProvider != TimeProvider.System)
                _provider = timeProvider;
            CancelAfter(delay);
        }

        public bool IsCancellationRequested => _canceled;

        public CancellationToken Token => new CancellationToken(this);

        /// <summary>Cancels: runs the callbacks, the last registered first. Does nothing the second time.</summary>
        public void Cancel()
        {
            if (_canceled)
                return;
            _canceled = true;
            StopTimer();
            while (_callbacks != null)
            {
                Registration r = _callbacks;
                Unlink(r);
                r.Invoke();
            }
        }

        /// <summary>Cancel. The argument changes nothing: a callback that throws ends the program.</summary>
        public void Cancel(bool throwOnFirstException) => Cancel();

        /// <summary>Cancel, returning a completed task.</summary>
        public Task CancelAsync()
        {
            Cancel();
            return Task.CompletedTask;
        }

        // Made by the static constructor, outside any object scope: a lambda the compiler caches on first use could be made inside one (scripts/static-stores).
        static readonly TimerCallback s_cancelOnTimer = static s => ((CancellationTokenSource)s!).Cancel();

        /// <summary>
        /// Cancels after <paramref name="millisecondsDelay"/>, counting from now
        /// in place of any earlier CancelAfter: 0 cancels now, -1 (Timeout.Infinite)
        /// only stops the earlier timer. The timer runs on Task.Delay's fiber,
        /// so Fiber.Run waits for it: Dispose the source to stop it.
        /// </summary>
        public void CancelAfter(int millisecondsDelay)
        {
            if (millisecondsDelay < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsDelay));
            if (_canceled)
                return;
            StopTimer();
            if (millisecondsDelay == -1)
                return;
            if (millisecondsDelay == 0)
            {
                Cancel();
                return;
            }
            if (_provider != null)
            {
                _providerTimer ??= _provider.CreateTimer(s_cancelOnTimer, this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                _providerTimer.Change(TimeSpan.FromMilliseconds(millisecondsDelay), Timeout.InfiniteTimeSpan);
                return;
            }
            _timer ??= new CancelTimer(this);
            _timer.Schedule(millisecondsDelay);
        }

        /// <summary>CancelAfter, with the delay rounded up to milliseconds.</summary>
        public void CancelAfter(TimeSpan delay) => CancelAfter(DelayTimer.Milliseconds(delay));

        /// <summary>Stops CancelAfter's timer, if the source hasn't been canceled: false if it has.</summary>
        public bool TryReset()
        {
            if (_canceled)
                return false;
            StopTimer();
            return true;
        }

        /// <summary>Stops CancelAfter's timer, and a linked source's links to its parents.</summary>
        public void Dispose()
        {
            StopTimer();
            _providerTimer?.Dispose();
            _providerTimer = null;
            if (_links != null)
            {
                foreach (CancellationTokenRegistration link in _links)
                    link.Dispose();
                _links = null;
            }
        }

        void StopTimer()
        {
            if (_timer != null)
                DelayTimer.Remove(_timer); // kept, to schedule again
            _providerTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        // CancelAfter's timer, one per source, scheduled again by each call.
        sealed class CancelTimer(CancellationTokenSource source) : DelayTimer
        {
            internal override void Due() => source.Cancel();
        }

        // As above: made by the static constructor (scripts/static-stores).
        static readonly Action<object?> s_cancelLinked = static s => ((CancellationTokenSource)s!).Cancel();

        /// <summary>A source canceled when any of <paramref name="tokens"/> is (or Cancel is called).</summary>
        public static CancellationTokenSource CreateLinkedTokenSource(params CancellationToken[] tokens)
        {
            var linked = new CancellationTokenSource();
            var links = new CancellationTokenRegistration[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
                links[i] = tokens[i].Register(s_cancelLinked, linked);
            linked._links = links;
            return linked;
        }

        /// <summary>A source canceled when <paramref name="token1"/> or <paramref name="token2"/> is.</summary>
        public static CancellationTokenSource CreateLinkedTokenSource(CancellationToken token1, CancellationToken token2)
            => CreateLinkedTokenSource([token1, token2]);

        /// <summary>A source canceled when <paramref name="token"/> is.</summary>
        public static CancellationTokenSource CreateLinkedTokenSource(CancellationToken token)
            => CreateLinkedTokenSource([token]);

        internal Registration? Register(Action? action, Action<object?>? withState, object? state)
        {
            if (_canceled)
            {
                if (action != null)
                    action();
                else
                    withState!(state);
                return null;
            }
            var r = new Registration(this, action, withState, state);
            Link(r);
            return r;
        }

        // Puts a kept registration (not on a list) on this source's: false,
        // having run it, if the source is canceled already.
        internal bool Attach(Registration r)
        {
            if (_canceled)
            {
                r.Invoke();
                return false;
            }
            r.Source = this;
            Link(r);
            return true;
        }

        void Link(Registration r)
        {
            r.Removed = false;
            r.Previous = null;
            r.Next = _callbacks;
            if (_callbacks != null)
                _callbacks.Previous = r;
            _callbacks = r;
        }

        internal void Unlink(Registration r)
        {
            if (r.Previous != null)
                r.Previous.Next = r.Next;
            else
                _callbacks = r.Next;
            if (r.Next != null)
                r.Next.Previous = r.Previous;
            r.Next = r.Previous = null;
            r.Removed = true;
        }
    }

    // A callback on a source's list. Timers and channels keep one each and
    // attach it again for every wait (CancellationToken.Attach), so waiting
    // with a token allocates nothing.
    internal sealed class Registration(CancellationTokenSource? source, Action? action, Action<object?>? withState, object? state)
    {
        internal CancellationTokenSource? Source = source;
        internal Registration? Next, Previous;
        internal bool Removed = true;

        internal void Invoke()
        {
            if (action != null)
                action();
            else
                withState!(state);
        }
    }

    /// <summary>Asks code to stop early: look at IsCancellationRequested, or Register a callback.</summary>
    public readonly struct CancellationToken : IEquatable<CancellationToken>
    {
        readonly CancellationTokenSource? _source;

        internal CancellationToken(CancellationTokenSource source) { _source = source; }

        /// <summary>A token that is canceled already (true), or never will be (false).</summary>
        public CancellationToken(bool canceled) { _source = canceled ? CancellationTokenSource.s_canceled : null; }

        /// <summary>A token that is never canceled.</summary>
        public static CancellationToken None => default;

        public bool IsCancellationRequested => _source != null && _source.IsCancellationRequested;

        public bool CanBeCanceled => _source != null;

        /// <summary>
        /// Runs <paramref name="callback"/> when the token is canceled, at once if
        /// it is already. Dispose the result to remove it.
        /// </summary>
        public CancellationTokenRegistration Register(Action callback)
            => new CancellationTokenRegistration(_source?.Register(callback, null, null));

        /// <summary>Register, with a state for the callback (no closure needed).</summary>
        public CancellationTokenRegistration Register(Action<object?> callback, object? state)
            => new CancellationTokenRegistration(_source?.Register(null, callback, state));

        // Attaches a kept registration (see Registration): what Dispose removes.
        internal CancellationTokenRegistration Attach(Registration registration)
            => _source != null && _source.Attach(registration) ? new CancellationTokenRegistration(registration) : default;

        /// <summary>Ends the program, with OperationCanceledException's message, if the token is canceled.</summary>
        public void ThrowIfCancellationRequested()
        {
            if (IsCancellationRequested)
                throw new OperationCanceledException(this);
        }

        public bool Equals(CancellationToken other) => _source == other._source;
        public override bool Equals(object? obj) => obj is CancellationToken other && Equals(other);
        public override int GetHashCode() => _source == null ? 0 : _source.GetHashCode();
        public static bool operator ==(CancellationToken left, CancellationToken right) => left.Equals(right);
        public static bool operator !=(CancellationToken left, CancellationToken right) => !left.Equals(right);
    }

    /// <summary>A callback registered on a token: Dispose removes it.</summary>
    public readonly struct CancellationTokenRegistration : IDisposable
    {
        readonly Registration? _registration;

        internal CancellationTokenRegistration(Registration? registration) { _registration = registration; }

        /// <summary>The token the callback was registered on.</summary>
        public CancellationToken Token => _registration?.Source == null ? default : new CancellationToken(_registration.Source);

        /// <summary>Removes the callback, if it hasn't run.</summary>
        public void Dispose() => Unregister();

        /// <summary>Removes the callback: false if it had already run or been removed.</summary>
        public bool Unregister()
        {
            if (_registration == null || _registration.Removed)
                return false;
            _registration.Source!.Unlink(_registration);
            return true;
        }
    }
}
