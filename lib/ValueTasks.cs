// ValueTask, ValueTask<T> and async streams (IAsyncEnumerable), as in .NET:
// a ValueTask holds either a result, when there was no waiting, or the
// Task to wait for. So a call that finishes at once allocates nothing.
// `async ValueTask` methods work too (AsyncValueTaskMethodBuilder), and so
// does `await foreach` over an IAsyncEnumerable<T> (written by hand: no
// async iterators, `yield return` in an async method).
//
// A timer's or a channel's ValueTask may be backed by a task it reuses
// (Task.Reusable, in place of .NET's IValueTaskSource), so a loop awaiting
// one allocates nothing. As in .NET, such a ValueTask can be awaited once:
// awaiting it again, after its operation was reused, ends the program.
// AsTask() gives a Task to keep. A ValueTask over an ordinary Task (from an
// async method, or made from one) can be awaited any number of times.
//
// Not on WebAssembly, which has no fibers.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Threading.Tasks
{
    /// <summary>An operation that completed at once, or a Task to wait for.</summary>
    [AsyncMethodBuilder(typeof(AsyncValueTaskMethodBuilder))]
    public readonly struct ValueTask : IEquatable<ValueTask>
    {
        internal readonly Task? _task; // null: completed
        internal readonly int _version;

        public ValueTask(Task task)
        {
            _task = task;
            _version = task.Version;
        }

        /// <summary>A ValueTask that has completed.</summary>
        public static ValueTask CompletedTask => default;

        public static ValueTask<TResult> FromResult<TResult>(TResult result) => new ValueTask<TResult>(result);

        public static ValueTask FromCanceled(CancellationToken cancellationToken) => new ValueTask(Task.FromCanceled(cancellationToken));

        public static ValueTask<TResult> FromCanceled<TResult>(CancellationToken cancellationToken) => new ValueTask<TResult>(Task.FromCanceled<TResult>(cancellationToken));

        public bool IsCompleted => _task == null || _task.IsCompleted;
        public bool IsCompletedSuccessfully => _task == null || _task.IsCompletedSuccessfully;
        public bool IsCanceled => _task != null && _task.IsCanceled;
        public bool IsFaulted => _task != null && _task.IsFaulted;

        /// <summary>A Task for it: the one waited for, or Task.CompletedTask (a timer's or channel's reused task gets a copy).</summary>
        public Task AsTask()
        {
            if (_task == null)
                return Task.CompletedTask;
            if (!_task.Reusable)
                return _task;
            _task.CheckVersion(_version);
            if (_task.IsCompleted)
            {
                Task done = _task.IsCanceled ? Task.FromCanceled(default) : Task.CompletedTask;
                _task.Release();
                return done;
            }
            var copy = new Task();
            _task.AddObserver(new CopyOnCompletion<VoidResult>(copy));
            return copy;
        }

        /// <summary>A ValueTask that can be awaited more than once: over AsTask().</summary>
        public ValueTask Preserve() => _task == null || !_task.Reusable ? this : new ValueTask(AsTask());

        public ValueTaskAwaiter GetAwaiter() => new ValueTaskAwaiter(_task, _version);

        /// <summary>Returns something to await. The argument changes nothing.</summary>
        public ConfiguredValueTaskAwaitable ConfigureAwait(bool continueOnCapturedContext) => new ConfiguredValueTaskAwaitable(this);

        public bool Equals(ValueTask other) => _task == other._task && _version == other._version;
        public override bool Equals(object? obj) => obj is ValueTask other && Equals(other);
        public override int GetHashCode() => _task == null ? 0 : _task.GetHashCode() ^ _version;
        public static bool operator ==(ValueTask left, ValueTask right) => left.Equals(right);
        public static bool operator !=(ValueTask left, ValueTask right) => !left.Equals(right);
    }

    /// <summary>A result there at once, or a Task&lt;T&gt; to wait for.</summary>
    [AsyncMethodBuilder(typeof(AsyncValueTaskMethodBuilder<>))]
    public readonly struct ValueTask<TResult> : IEquatable<ValueTask<TResult>>
    {
        internal readonly Task<TResult>? _task; // null: _result is it
        internal readonly TResult _result;
        internal readonly int _version;

        public ValueTask(TResult result)
        {
            _task = null;
            _result = result;
            _version = 0;
        }

        public ValueTask(Task<TResult> task)
        {
            _task = task;
            _result = default!;
            _version = task.Version;
        }

        public bool IsCompleted => _task == null || _task.IsCompleted;
        public bool IsCompletedSuccessfully => _task == null || _task.IsCompletedSuccessfully;
        public bool IsCanceled => _task != null && _task.IsCanceled;
        public bool IsFaulted => _task != null && _task.IsFaulted;

        /// <summary>The result, waiting for it first as Task.Result does. Ends the program if canceled.</summary>
        public TResult Result
        {
            get
            {
                if (_task == null)
                    return _result;
                if (!_task.Reusable)
                    return _task.Result;
                _task.CheckVersion(_version);
                TResult result = _task.Result;
                _task.Release();
                return result;
            }
        }

        /// <summary>A Task for it: the one waited for, or a new one with the result (a timer's or channel's reused task gets a copy).</summary>
        public Task<TResult> AsTask()
        {
            if (_task == null)
                return Task.FromResult(_result);
            if (!_task.Reusable)
                return _task;
            _task.CheckVersion(_version);
            if (_task.IsCompleted)
            {
                Task<TResult> done = _task.IsCanceled ? Task.FromCanceled<TResult>(default) : Task.FromResult(_task._result);
                _task.Release();
                return done;
            }
            var copy = new Task<TResult>();
            _task.AddObserver(new CopyOnCompletion<TResult>(copy));
            return copy;
        }

        /// <summary>A ValueTask that can be awaited more than once: over AsTask().</summary>
        public ValueTask<TResult> Preserve() => _task == null || !_task.Reusable ? this : new ValueTask<TResult>(AsTask());

        public ValueTaskAwaiter<TResult> GetAwaiter() => new ValueTaskAwaiter<TResult>(this);

        /// <summary>Returns something to await. The argument changes nothing.</summary>
        public ConfiguredValueTaskAwaitable<TResult> ConfigureAwait(bool continueOnCapturedContext) => new ConfiguredValueTaskAwaitable<TResult>(this);

        public bool Equals(ValueTask<TResult> other) => _task == other._task && _version == other._version && (_task != null || EqualityComparer<TResult>.Default.Equals(_result, other._result));
        public override bool Equals(object? obj) => obj is ValueTask<TResult> other && Equals(other);
        public override int GetHashCode() => _task != null ? _task.GetHashCode() ^ _version : _result == null ? 0 : _result.GetHashCode();
        public static bool operator ==(ValueTask<TResult> left, ValueTask<TResult> right) => left.Equals(right);
        public static bool operator !=(ValueTask<TResult> left, ValueTask<TResult> right) => !left.Equals(right);
        public override string? ToString() => IsCompletedSuccessfully ? Result?.ToString() : "";
    }

    // AsTask of a reused task still running: a copy, completed with it, and
    // the reused task freed. VoidResult: a plain Task.
    sealed class CopyOnCompletion<TResult>(Task copy) : Observer
    {
        internal override void Completed(Task task)
        {
            if (task.IsCanceled)
                copy.Complete(true);
            else if (task is Task<TResult> withResult && copy is Task<TResult> copyWithResult)
                copyWithResult.TrySetResult(withResult._result);
            else
                copy.Complete();
            task.Release();
        }
    }
}

namespace System.Runtime.CompilerServices
{
    // m_task is its only reference field, as IFiberAwaiter needs.
    public readonly struct ValueTaskAwaiter : ICriticalNotifyCompletion, IFiberAwaiter
    {
        internal readonly Task? m_task;
        readonly int _version;

        internal ValueTaskAwaiter(Task? task, int version)
        {
            m_task = task;
            _version = version;
        }

        public bool IsCompleted => m_task == null || m_task.IsCompleted;

        public void GetResult()
        {
            if (m_task == null)
                return;
            m_task.CheckVersion(_version);
            m_task.Wait();
            m_task.Release();
        }

        public void OnCompleted(Action continuation) => AsyncMethods.OnCompleted(m_task ?? Task.CompletedTask, continuation);
        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
    }

    public readonly struct ValueTaskAwaiter<TResult> : ICriticalNotifyCompletion, IWaitTarget
    {
        readonly ValueTask<TResult> _value;

        internal ValueTaskAwaiter(ValueTask<TResult> value) { _value = value; }

        public bool IsCompleted => _value.IsCompleted;
        public TResult GetResult() => _value.Result;
        public void OnCompleted(Action continuation) => AsyncMethods.OnCompleted(_value._task ?? (Task)Task.CompletedTask, continuation);
        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
        Task IWaitTarget.WaitTarget => _value._task!; // not completed, so there's a task
    }

    public readonly struct ConfiguredValueTaskAwaitable
    {
        readonly ValueTask _value;

        internal ConfiguredValueTaskAwaitable(ValueTask value) { _value = value; }

        public ValueTaskAwaiter GetAwaiter() => _value.GetAwaiter();
    }

    public readonly struct ConfiguredValueTaskAwaitable<TResult>
    {
        readonly ValueTask<TResult> _value;

        internal ConfiguredValueTaskAwaitable(ValueTask<TResult> value) { _value = value; }

        public ValueTaskAwaiter<TResult> GetAwaiter() => new ValueTaskAwaiter<TResult>(_value);
    }

    /// <summary>Runs `async ValueTask` methods (the C# compiler calls it). One that doesn't wait allocates nothing.</summary>
    public struct AsyncValueTaskMethodBuilder
    {
        Task? _task;

        public static AsyncValueTaskMethodBuilder Create() => default;

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            // The first step, on the caller's stack: a throw faults the task.
            Exception? thrown = Catch.RunStep(ref stateMachine);
            if (thrown != null)
                SetException(thrown);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) { }

        public ValueTask Task => _task == null ? default : new ValueTask(_task);

        public void SetResult() => _task?.Complete();

        public void SetException(Exception exception) => (_task ??= new Task()).Fail(exception);

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.Suspend(_task ??= new Task(), ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.Suspend(_task ??= new Task(), ref awaiter, ref stateMachine);
    }

    /// <summary>Runs `async ValueTask&lt;T&gt;` methods (the C# compiler calls it). One that doesn't wait allocates nothing.</summary>
    public struct AsyncValueTaskMethodBuilder<TResult>
    {
        Task<TResult>? _task;
        TResult _result;

        public static AsyncValueTaskMethodBuilder<TResult> Create() => default;

        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine
        {
            // The first step, on the caller's stack: a throw faults the task.
            Exception? thrown = Catch.RunStep(ref stateMachine);
            if (thrown != null)
                SetException(thrown);
        }

        public void SetStateMachine(IAsyncStateMachine stateMachine) { }

        // Read once the method returns: it has either waited (a task) or set its result.
        public ValueTask<TResult> Task => _task == null ? new ValueTask<TResult>(_result) : new ValueTask<TResult>(_task);

        public void SetResult(TResult result)
        {
            if (_task == null)
                _result = result;
            else
                _task.TrySetResult(result);
        }

        public void SetException(Exception exception) => (_task ??= new Task<TResult>()).Fail(exception);

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.Suspend(_task ??= new Task<TResult>(), ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine
            => AsyncMethods.Suspend(_task ??= new Task<TResult>(), ref awaiter, ref stateMachine);
    }
}

namespace System
{
    /// <summary>Something to dispose by awaiting: `await using`.</summary>
    public interface IAsyncDisposable
    {
        ValueTask DisposeAsync();
    }
}

namespace System.Collections.Generic
{
    /// <summary>A sequence read with `await foreach`.</summary>
    public interface IAsyncEnumerable<out T>
    {
        IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default);
    }

    public interface IAsyncEnumerator<out T> : IAsyncDisposable
    {
        T Current { get; }
        ValueTask<bool> MoveNextAsync();
    }
}
