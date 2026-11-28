namespace System
{
    // .NET's push notifications. F#'s events are IObservables: fsc turns
    // timer.Elapsed.Add(...) into one (FSharp.Core's RuntimeHelpers.CreateEvent),
    // so F# needs these to use any .NET event.
    public interface IObservable<out T>
    {
        IDisposable Subscribe(IObserver<T> observer);
    }

    public interface IObserver<in T>
    {
        void OnNext(T value);
        void OnError(Exception error);
        void OnCompleted();
    }
}
