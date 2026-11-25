using System.Runtime;
using System.Runtime.CompilerServices;

// x != x is the test for NaN, the one value that isn't equal to itself.
#pragma warning disable CS1718

namespace System
{
    // Math on double. Where the processor has an instruction for an operation
    // (square root everywhere; rounding on arm64 and newer x64) the compiler
    // uses it; otherwise the call goes to the C math library, which is why
    // these have real bodies instead of calling themselves.
    public static class Math
    {
        public const double PI = 3.14159265358979323846;
        public const double E = 2.7182818284590452354;

        // What 32-bit targets (WebAssembly, DOS) call for a checked 64-bit
        // multiplication, which has no instruction there: `checked` code on
        // long, and the size of a `stackalloc` of nint. The compiler finds
        // them by name.
        private static ulong MultiplyChecked(ulong i, ulong j)
        {
            if (i != 0 && j > 0xFFFFFFFFFFFFFFFF / i)
                Environment.FailFast("OverflowException");
            return unchecked(i * j);
        }

        // Checked conversions (`checked((int)x)`) where the compiler calls a
        // helper instead of inlining the test: NativeAOT-LLVM (WebAssembly,
        // DOS), for the tensor code's checked((int)length). The ranges are
        // .NET's; NaN fails both comparisons.
        private static int ConvertToInt32Checked(double value)
        {
            if (value > -2147483649.0 && value < 2147483648.0)
                return (int)value;
            Environment.FailFast("OverflowException");
            return 0;
        }

        private static uint ConvertToUInt32Checked(double value)
        {
            if (value > -1.0 && value < 4294967296.0)
                return (uint)value;
            Environment.FailFast("OverflowException");
            return 0;
        }

        private static long ConvertToInt64Checked(double value)
        {
            if (value > -9223372036854777856.0 && value < 9223372036854775808.0)
                return (long)value;
            Environment.FailFast("OverflowException");
            return 0;
        }

        private static ulong ConvertToUInt64Checked(double value)
        {
            if (value > -1.0 && value < 18446744073709551616.0)
                return (ulong)value;
            Environment.FailFast("OverflowException");
            return 0;
        }

        private static long MultiplyChecked(long i, long j)
        {
            bool negative = (i < 0) != (j < 0);
            ulong a = i < 0 ? unchecked(0 - (ulong)i) : (ulong)i;
            ulong b = j < 0 ? unchecked(0 - (ulong)j) : (ulong)j;
            ulong limit = negative ? 0x8000000000000000 : 0x7FFFFFFFFFFFFFFF;
            if (a != 0 && b > limit / a)
                Environment.FailFast("OverflowException");
            ulong product = unchecked(a * b);
            return negative ? unchecked((long)(0 - product)) : (long)product;
        }

        public static int Abs(int value) => value < 0 ? -value : value;
        public static long Abs(long value) => value < 0 ? -value : value;

        [Intrinsic]
        public static double Abs(double value) => Platform.fabs(value);

        public static int Min(int a, int b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static uint Min(uint a, uint b) => a < b ? a : b;
        public static uint Max(uint a, uint b) => a > b ? a : b;
        public static long Min(long a, long b) => a < b ? a : b;
        public static long Max(long a, long b) => a > b ? a : b;
        public static ulong Min(ulong a, ulong b) => a < b ? a : b;
        public static ulong Max(ulong a, ulong b) => a > b ? a : b;
        public static nint Min(nint a, nint b) => a < b ? a : b;
        public static nint Max(nint a, nint b) => a > b ? a : b;
        public static nuint Min(nuint a, nuint b) => a < b ? a : b;
        public static nuint Max(nuint a, nuint b) => a > b ? a : b;

        // As in .NET, the result is NaN if either argument is.
        public static double Min(double a, double b) => a != a ? a : b != b ? b : a < b ? a : b;
        public static double Max(double a, double b) => a != a ? a : b != b ? b : a > b ? a : b;

        public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
        public static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;
        public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

        [Intrinsic]
        public static double Sqrt(double value) => Platform.sqrt(value);

        [Intrinsic]
        public static double Floor(double value) => Platform.floor(value);

        [Intrinsic]
        public static double Ceiling(double value) => Platform.ceil(value);

        [Intrinsic]
        public static double Truncate(double value) => Platform.trunc(value);

        /// <summary>Rounds to the nearest whole number; halfway cases go to the even one, as in .NET.</summary>
        [Intrinsic]
        public static double Round(double value) => Platform.rint(value);

        public static double Pow(double x, double y) => Platform.pow(x, y);
        public static double Exp(double value) => Platform.exp(value);
        public static double Log(double value) => Platform.log(value);
        public static double Log2(double value) => Platform.log2(value);
        public static double Log10(double value) => Platform.log10(value);
        public static double Sin(double value) => Platform.sin(value);
        public static double Cos(double value) => Platform.cos(value);
        public static double Tan(double value) => Platform.tan(value);
        public static double Atan2(double y, double x) => Platform.atan2(y, x);

        public const double Tau = 6.283185307179586476925;

        public static float Abs(float value) => MathF.Abs(value);
        public static short Abs(short value) => (short)(value < 0 ? -value : value);
        public static sbyte Abs(sbyte value) => (sbyte)(value < 0 ? -value : value);
        public static float Min(float a, float b) => MathF.Min(a, b);
        public static float Max(float a, float b) => MathF.Max(a, b);
        public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;
        public static uint Clamp(uint value, uint min, uint max) => value < min ? min : value > max ? max : value;
        public static ulong Clamp(ulong value, ulong min, ulong max) => value < min ? min : value > max ? max : value;

        public static int Sign(int value) => (value >> 31) | (int)((uint)-value >> 31);
        public static int Sign(long value) => (int)(value >> 63) | (int)((ulong)-value >> 63);
        public static int Sign(float value) => Sign((double)value);

        // As in .NET, NaN has no sign.
        public static int Sign(double value)
        {
            if (value < 0)
                return -1;
            if (value > 0)
                return 1;
            if (value == 0)
                return 0;
            throw new ArithmeticException("Function does not accept floating point Not-a-Number values.");
        }

        public static unsafe double CopySign(double x, double y)
        {
            ulong bits = (*(ulong*)&x & 0x7FFFFFFFFFFFFFFF) | (*(ulong*)&y & 0x8000000000000000);
            return *(double*)&bits;
        }

        // The rest are built from the functions above rather than imported, so
        // that every target's math library (musl on UEFI, the 387 on DOS,
        // llvm-mos on the C64, ...) is enough as it is.

        public static double Atan(double value) => Platform.atan2(value, 1);

        // sqrt((1 - x)(1 + x)) is cos(asin(x)) without the cancellation of
        // 1 - x*x near 1. Outside [-1, 1] it is NaN, and so is the result.
        public static double Asin(double value) => Platform.atan2(value, Platform.sqrt((1 - value) * (1 + value)));
        public static double Acos(double value) => Platform.atan2(Platform.sqrt((1 - value) * (1 + value)), value);

        public static double Sinh(double value)
        {
            double a = Abs(value);
            double result;
            if (a < 0.5)
                result = SmallSinh(a); // exp(a) - exp(-a) would lose digits to cancellation
            else if (a < 709)
            {
                double e = Platform.exp(a);
                result = (e - 1 / e) / 2;
            }
            else
            {
                // exp(a) overflows before sinh(a) does.
                double e = Platform.exp(a / 2);
                result = e / 2 * e;
            }
            return CopySign(result, value);
        }

        public static double Cosh(double value)
        {
            double a = Abs(value);
            if (a < 709)
            {
                double e = Platform.exp(a);
                return (e + 1 / e) / 2;
            }
            double half = Platform.exp(a / 2);
            return half / 2 * half;
        }

        public static double Tanh(double value)
        {
            double a = Abs(value);
            double result;
            if (a > 22)
                result = 1; // as close to 1 as a double gets
            else if (a < 0.5)
            {
                double sinh = SmallSinh(a);
                result = sinh / Platform.sqrt(1 + sinh * sinh);
            }
            else
            {
                double m = Expm1(2 * a);
                result = m / (m + 2);
            }
            return value != value ? value : CopySign(result, value);
        }

        public static double Asinh(double value)
        {
            double a = Abs(value);
            double result;
            if (a > 1e8)
                result = Platform.log(a) + Ln2;
            else
                result = Log1p(a + a * a / (1 + Platform.sqrt(1 + a * a)));
            return CopySign(result, value);
        }

        public static double Acosh(double value)
        {
            if (value < 1)
                return double.NaN;
            if (value > 1e8)
                return Platform.log(value) + Ln2;
            double t = value - 1;
            return Log1p(t + Platform.sqrt(2 * t + t * t));
        }

        public static double Atanh(double value)
        {
            double a = Abs(value);
            if (a > 1)
                return double.NaN;
            return CopySign(Log1p(2 * a / (1 - a)) / 2, value);
        }

        public static double Cbrt(double value)
        {
            if (value == 0 || !double.IsFinite(value))
                return value;
            double a = Abs(value);
            // Scaled by 2^±300 where y * y * y in the Newton step would
            // underflow or overflow; the cube root is then off by 2^±100.
            double scale = 1;
            if (a < 1e-250)
            {
                a *= 2.037035976334486e90; // 2^300
                scale = 7.888609052210118e-31; // 2^-100
            }
            else if (a > 1e250)
            {
                a *= 4.909093465297727e-91; // 2^-300
                scale = 1.2676506002282294e30; // 2^100
            }
            double y = Platform.pow(a, 1.0 / 3);
            y -= (y * y * y - a) / (3 * y * y); // one Newton step: pow's 1/3 isn't exactly a third
            return CopySign(y * scale, value);
        }

        // x - y * n, n being x / y rounded to the nearest whole number (to
        // the even one at halfway), where % truncates instead. .NET's method.
        public static double IEEERemainder(double x, double y)
        {
            if (x != x)
                return x;
            if (y != y)
                return y;
            double regularMod = x % y;
            if (regularMod != regularMod)
                return double.NaN;
            if (regularMod == 0 && double.IsNegative(x))
                return double.NegativeZero;
            double alternativeResult = regularMod - Abs(y) * Sign(x);
            if (Abs(alternativeResult) == Abs(regularMod))
            {
                double divisionResult = x / y;
                double roundedResult = Round(divisionResult);
                return Abs(roundedResult) > Abs(divisionResult) ? alternativeResult : regularMod;
            }
            return Abs(alternativeResult) < Abs(regularMod) ? alternativeResult : regularMod;
        }

        public static double Log(double a, double newBase)
        {
            if (a != a)
                return a;
            if (newBase != newBase)
                return newBase;
            if (newBase == 1)
                return double.NaN;
            if (a != 1 && (newBase == 0 || newBase == double.PositiveInfinity))
                return double.NaN;
            return Platform.log(a) / Platform.log(newBase);
        }

        private const double Ln2 = 0.693147180559945309417;

        // sinh(x) for 0 <= x < 0.5: x + x^3/3! + x^5/5! + ..., whose terms
        // all have the same sign, added smallest first. Eleven reach below
        // a double's precision.
        private static double SmallSinh(double x)
        {
            double x2 = x * x;
            double sum = 0;
            for (int n = 21; n >= 3; n -= 2)
                sum = (sum + 1) * x2 / (n * (n - 1));
            return x + x * sum;
        }

        // exp(x) - 1, accurate for small x (a series there; 0 <= x < 1 only
        // needs 18 terms for a double).
        private static double Expm1(double x)
        {
            if (x >= 1)
                return Platform.exp(x) - 1;
            double term = x, sum = x;
            for (int n = 2; n < 20; n++)
            {
                term *= x / n;
                sum += term;
            }
            return sum;
        }

        // log(1 + x), accurate for small x: (1 + x) is rounded, and the
        // rounding is undone by the factor x / ((1 + x) - 1).
        private static double Log1p(double x)
        {
            double u = 1 + x;
            if (u == 1)
                return x;
            if (u == double.PositiveInfinity)
                return u;
            return Platform.log(u) * x / (u - 1);
        }
    }

    // The same on float.
    public static class MathF
    {
        public const float PI = 3.14159265f;
        public const float E = 2.71828183f;

        [Intrinsic]
        public static float Abs(float value) => Platform.fabsf(value);

        public static float Min(float a, float b) => a != a ? a : b != b ? b : a < b ? a : b;
        public static float Max(float a, float b) => a != a ? a : b != b ? b : a > b ? a : b;

        [Intrinsic]
        public static float Sqrt(float value) => Platform.sqrtf(value);

        [Intrinsic]
        public static float Floor(float value) => Platform.floorf(value);

        [Intrinsic]
        public static float Ceiling(float value) => Platform.ceilf(value);

        [Intrinsic]
        public static float Truncate(float value) => Platform.truncf(value);

        [Intrinsic]
        public static float Round(float value) => Platform.rintf(value);

        public static float Pow(float x, float y) => Platform.powf(x, y);
        public static float Exp(float value) => Platform.expf(value);
        public static float Log(float value) => Platform.logf(value);
        public static float Sin(float value) => Platform.sinf(value);
        public static float Cos(float value) => Platform.cosf(value);
        public static float Tanh(float value) => Platform.tanhf(value);

        public const float Tau = 6.28318531f;

        public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

        public static float IEEERemainder(float x, float y)
        {
            if (x != x)
                return x;
            if (y != y)
                return y;
            float regularMod = x % y;
            if (regularMod != regularMod)
                return float.NaN;
            if (regularMod == 0 && float.IsNegative(x))
                return float.NegativeZero;
            float alternativeResult = regularMod - Abs(y) * Sign(x);
            if (Abs(alternativeResult) == Abs(regularMod))
            {
                float divisionResult = x / y;
                float roundedResult = Round(divisionResult);
                return Abs(roundedResult) > Abs(divisionResult) ? alternativeResult : regularMod;
            }
            return Abs(alternativeResult) < Abs(regularMod) ? alternativeResult : regularMod;
        }

        public static int Sign(float value) => Math.Sign((double)value);
        public static float CopySign(float x, float y) => (float)Math.CopySign(x, y);

        // In double, then rounded: as accurate as a float allows.
        public static float Log2(float value) => (float)Math.Log2(value);
        public static float Log10(float value) => (float)Math.Log10(value);
        public static float Log(float a, float newBase) => (float)Math.Log(a, newBase);
        public static float Tan(float value) => (float)Math.Tan(value);
        public static float Asin(float value) => (float)Math.Asin(value);
        public static float Acos(float value) => (float)Math.Acos(value);
        public static float Atan(float value) => (float)Math.Atan(value);
        public static float Atan2(float y, float x) => (float)Math.Atan2(y, x);
        public static float Sinh(float value) => (float)Math.Sinh(value);
        public static float Cosh(float value) => (float)Math.Cosh(value);
        public static float Asinh(float value) => (float)Math.Asinh(value);
        public static float Acosh(float value) => (float)Math.Acosh(value);
        public static float Atanh(float value) => (float)Math.Atanh(value);
        public static float Cbrt(float value) => (float)Math.Cbrt(value);
    }
}
