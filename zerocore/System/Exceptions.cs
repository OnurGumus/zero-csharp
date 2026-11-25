// Exceptions in zero mode are panics, like Rust's panic = "abort": `throw`
// prints the exception's message to stderr and ends the program. Nothing
// unwinds, so on that path no `catch` block runs and neither do `finally`
// blocks or `using` disposals. RhpThrowEx (Runtime/RuntimeExports.cs) does it.
//
// The C# compiler also needs these types for code it generates:
// - each C# 14 `extension(...)` block gets a hidden type whose method bodies
//   are `throw new NotSupportedException()` (never called);
// - a `switch` on a C# 15 union throws InvalidOperationException for a value
//   that matches no case, such as `default(Result)`.
//
// Without reflection there's no type name to print, so each type here passes
// its own name as the message when none is given.

namespace System
{
    public class Exception
    {
        private readonly string _message;

        public Exception() : this("Exception") { }

        public Exception(string message)
        {
            _message = message;
        }

        public virtual string Message => _message;
    }

    public class InvalidOperationException : Exception
    {
        public InvalidOperationException() : base("InvalidOperationException") { }
        public InvalidOperationException(string message) : base(message) { }
    }

    public class NotSupportedException : Exception
    {
        public NotSupportedException() : base("NotSupportedException") { }
        public NotSupportedException(string message) : base(message) { }
    }

    public class NotImplementedException : Exception
    {
        public NotImplementedException() : base("NotImplementedException") { }
        public NotImplementedException(string message) : base(message) { }
    }

    public class ArgumentException : Exception
    {
        public ArgumentException() : base("ArgumentException") { }
        public ArgumentException(string message) : base(message) { }
        public ArgumentException(string message, string paramName) : base(paramName == null ? message : message + " (Parameter '" + paramName + "')") { }
    }

    public class ArgumentNullException : ArgumentException
    {
        public ArgumentNullException() : base("ArgumentNullException") { }
        public ArgumentNullException(string paramName) : base("ArgumentNullException: " + paramName) { }
    }

    public class ArgumentOutOfRangeException : ArgumentException
    {
        public ArgumentOutOfRangeException() : base("ArgumentOutOfRangeException") { }
        public ArgumentOutOfRangeException(string paramName) : base("ArgumentOutOfRangeException: " + paramName) { }
        public ArgumentOutOfRangeException(string paramName, string message) : base(message, paramName) { }

        // .NET's checks (generic over INumberBase there), for the integer types.
        public static void ThrowIfNegative(int value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string paramName = null)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(paramName, paramName + " ('" + value.ToString() + "') must be a non-negative value.");
        }

        public static void ThrowIfNegative(nint value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string paramName = null)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(paramName, paramName + " ('" + value.ToString() + "') must be a non-negative value.");
        }

        public static void ThrowIfNegativeOrZero(int value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string paramName = null)
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(paramName, paramName + " ('" + value.ToString() + "') must be a non-negative and non-zero value.");
        }

        public static void ThrowIfNotEqual(int value, int other, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string paramName = null)
        {
            if (value != other) throw new ArgumentOutOfRangeException(paramName, paramName + " ('" + value.ToString() + "') must be equal to '" + other.ToString() + "'.");
        }

        public static void ThrowIfLessThan(int value, int other, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string paramName = null)
        {
            if (value < other) throw new ArgumentOutOfRangeException(paramName, paramName + " ('" + value.ToString() + "') must be greater than or equal to '" + other.ToString() + "'.");
        }

        public static void ThrowIfLessThan(nint value, nint other, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string paramName = null)
        {
            if (value < other) throw new ArgumentOutOfRangeException(paramName, paramName + " ('" + value.ToString() + "') must be greater than or equal to '" + other.ToString() + "'.");
        }
    }

    public class IndexOutOfRangeException : Exception
    {
        public IndexOutOfRangeException() : base("Index was outside the bounds of the array.") { }
        public IndexOutOfRangeException(string message) : base(message) { }
    }

    public class ArrayTypeMismatchException : Exception
    {
        public ArrayTypeMismatchException() : base("Attempted to access an element as a type incompatible with the array.") { }
        public ArrayTypeMismatchException(string message) : base(message) { }
    }

    // decimal's (Decimal.cs).
    public class ArithmeticException : Exception
    {
        public ArithmeticException() : base("ArithmeticException") { }
        public ArithmeticException(string message) : base(message) { }
    }

    public class OverflowException : ArithmeticException
    {
        public OverflowException() : base("OverflowException") { }
        public OverflowException(string message) : base(message) { }
    }

    public class DivideByZeroException : ArithmeticException
    {
        public DivideByZeroException() : base("DivideByZeroException") { }
        public DivideByZeroException(string message) : base(message) { }
    }

    public class FormatException : Exception
    {
        public FormatException() : base("FormatException") { }
        public FormatException(string message) : base(message) { }
    }
}
