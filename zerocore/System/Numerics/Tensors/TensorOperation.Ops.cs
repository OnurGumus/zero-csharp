using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Numerics.Tensors
{
    // The operations (.NET's TensorOperation.cs), for float: each calls the
    // TensorPrimitives function on runs of contiguous elements, and float's own
    // function (ScalarF) on single elements of strided tensors.
    internal static partial class TensorOperation
    {
        public readonly struct Equals
            : IBinaryOperation_Tensor_Scalar<float, bool>,
              IBinaryOperation_Tensor_Tensor<float, bool>
        {
            public static void Invoke(ref readonly float left, float right, ref bool destination)
            {
                destination = (left == right);
            }

            public static void Invoke(ref readonly float left, ref readonly float right, ref bool destination)
            {
                destination = (left == right);
            }

            public static void Invoke(ReadOnlySpan<float> left, float right, Span<bool> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (left[i] == right);
                }
            }

            public static void Invoke(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<bool> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (left[i] == right[i]);
                }
            }
        }

        public readonly struct EqualsAny
            : IBinaryOperation_Tensor_Scalar<float, bool>,
              IBinaryOperation_Tensor_Tensor<float, bool>
        {
            // The main loop early exits at the first false condition, so we
            // check x != y and returns false on first equal. The consumer will
            // then negate whatever the main loop returns as `true` means none
            // are equal.

            public static void Invoke(ref readonly float left, float right, ref bool destination)
            {
                destination = (left != right);
            }

            public static void Invoke(ref readonly float left, ref readonly float right, ref bool destination)
            {
                destination = (left != right);
            }

            public static void Invoke(ReadOnlySpan<float> left, float right, Span<bool> destination)
            {
                Debug.Assert(destination.Length == 1);
                bool result = true;

                for (int i = 0; i < left.Length; i++)
                {
                    result = (left[i] != right);

                    if (!result)
                    {
                        break;
                    }
                }

                destination[0] = result;
            }

            public static void Invoke(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<bool> destination)
            {
                Debug.Assert(destination.Length == 1);
                bool result = true;

                for (int i = 0; i < left.Length; i++)
                {
                    result = (left[i] != right[i]);

                    if (!result)
                    {
                        break;
                    }
                }

                destination[0] = result;
            }
        }

        public readonly struct Abs
            : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Abs(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Abs(x, destination);
            }
        }

        public readonly struct Acos
            : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Acos(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Acos(x, destination);
            }
        }

        public readonly struct Acosh
            : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Acosh(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Acosh(x, destination);
            }
        }

        public readonly struct AcosPi
            : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.AcosPi(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.AcosPi(x, destination);
            }
        }

        public readonly struct Add
            : IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = x + y;
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.Add(x, y, destination);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = x + y;
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Add(x, y, destination);
            }
        }

        public readonly struct Asin
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Asin(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Asin(x, destination);
            }
        }

        public readonly struct Asinh
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Asinh(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Asinh(x, destination);
            }
        }

        public readonly struct AsinPi
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.AsinPi(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.AsinPi(x, destination);
            }
        }

        public readonly struct Atan
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Atan(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Atan(x, destination);
            }
        }

        public readonly struct Atan2
            : IBinaryOperation_Scalar_Tensor<float, float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.Atan2(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = ScalarF.Atan2(x[i], y);
                }
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Atan2(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Atan2(x, y, destination);
            }

            public static void Invoke(float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Atan2(x, y);
            }

            public static void Invoke(float x, ReadOnlySpan<float> y, Span<float> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = ScalarF.Atan2(x, y[i]);
                }
            }
        }

        public readonly struct Atan2Pi
            : IBinaryOperation_Scalar_Tensor<float, float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.Atan2Pi(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = ScalarF.Atan2Pi(x[i], y);
                }
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Atan2Pi(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Atan2Pi(x, y, destination);
            }

            public static void Invoke(float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Atan2Pi(x, y);
            }

            public static void Invoke(float x, ReadOnlySpan<float> y, Span<float> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = ScalarF.Atan2Pi(x, y[i]);
                }
            }
        }

        public readonly struct Atanh
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Atanh(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Atanh(x, destination);
            }
        }

        public readonly struct AtanPi
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.AtanPi(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.AtanPi(x, destination);
            }
        }

        public readonly struct Cbrt
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Cbrt(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Cbrt(x, destination);
            }
        }

        public readonly struct Ceiling
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Ceiling(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Ceiling(x, destination);
            }
        }

        public readonly struct CopySign
        : IBinaryOperation_Tensor_Scalar<float, float>,
          IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.CopySign(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.CopySign(x, y, destination);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.CopySign(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.CopySign(x, y, destination);
            }
        }

        public readonly struct Cos
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Cos(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Cos(x, destination);
            }
        }

        public readonly struct Cosh
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Cosh(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Cosh(x, destination);
            }
        }

        public struct CosineSimilarity
            : IBinaryOperation_Tensor_Tensor<float, ValueTuple<float, float, float>>
        {

            public static void Invoke(ref readonly float x, ref readonly float y, ref (float, float, float) destination)
            {
                destination.Item1 += (x * y);
                destination.Item2 += (x * x);
                destination.Item3 += (y * y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<(float, float, float)> destination)
            {
                destination[0].Item1 += TensorPrimitives.Dot(x, y);
                destination[0].Item2 += TensorPrimitives.SumOfSquares(x);
                destination[0].Item3 += TensorPrimitives.SumOfSquares(y);
            }
        }

        public readonly struct CosPi
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.CosPi(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.CosPi(x, destination);
            }
        }

        public readonly struct Decrement
            : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                float tmp = x;
                destination = --tmp;
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Decrement(x, destination);
            }
        }

        public readonly struct DegreesToRadians
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.DegreesToRadians(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.DegreesToRadians(x, destination);
            }
        }

        public readonly struct Divide
            : IBinaryOperation_Scalar_Tensor<float, float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = x / y;
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.Divide(x, y, destination);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = x / y;
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Divide(x, y, destination);
            }

            public static void Invoke(float x, ref readonly float y, ref float destination)
            {
                destination = x / y;
            }
            public static void Invoke(float x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Divide(x, y, destination);
            }
        }

        public struct Dot
            : IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination += x * y;
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                destination[0] += TensorPrimitives.Dot(x, y);
            }
        }

        public readonly struct Exp
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Exp(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Exp(x, destination);
            }
        }

        public readonly struct Exp10
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Exp10(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Exp10(x, destination);
            }
        }

        public readonly struct Exp10M1
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Exp10M1(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Exp10M1(x, destination);
            }
        }

        public readonly struct Exp2
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Exp2(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Exp2(x, destination);
            }
        }

        public readonly struct Exp2M1
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Exp2M1(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Exp2M1(x, destination);
            }
        }

        public readonly struct ExpM1
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.ExpM1(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.ExpM1(x, destination);
            }
        }

        public readonly struct Floor
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Floor(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Floor(x, destination);
            }
        }

        public readonly struct Hypot
            : IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Hypot(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Hypot(x, y, destination);
            }
        }

        public readonly struct Ieee754Remainder
            : IBinaryOperation_Scalar_Tensor<float, float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.Ieee754Remainder(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.Ieee754Remainder(x, y, destination);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Ieee754Remainder(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Ieee754Remainder(x, y, destination);
            }

            public static void Invoke(float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Ieee754Remainder(x, y);
            }
            public static void Invoke(float x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Ieee754Remainder(x, y, destination);
            }
        }

        public readonly struct ILogB
        : IUnaryOperation_Tensor<float, int>
        {
            public static void Invoke(ref readonly float x, ref int destination)
            {
                destination = ScalarF.ILogB(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<int> destination)
            {
                TensorPrimitives.ILogB(x, destination);
            }
        }

        public readonly struct Increment
            : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                float tmp = x;
                destination = ++tmp;
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Increment(x, destination);
            }
        }

        public readonly struct Log
        : IUnaryOperation_Tensor<float, float>,
          IBinaryOperation_Tensor_Tensor<float, float>,
          IBinaryOperation_Tensor_Scalar<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Log(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Log(x, destination);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Log(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Log(x, y, destination);
            }

            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.Log(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.Log(x, y, destination);
            }
        }

        public readonly struct Log10
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Log10(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Log10(x, destination);
            }
        }

        public readonly struct Log10P1
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Log10P1(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Log10P1(x, destination);
            }
        }

        public readonly struct Log2
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Log2(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Log2(x, destination);
            }
        }

        public readonly struct Log2P1
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Log2P1(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Log2P1(x, destination);
            }
        }

        public readonly struct LogP1
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.LogP1(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.LogP1(x, destination);
            }
        }

        public struct Max
            : IUnaryReduction_Tensor<float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Max(x, destination);
            }

            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination = TensorPrimitives.Max(x);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Max(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Max(x, y, destination);
            }

            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.Max(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.Max(x, y, destination);
            }
        }

        public struct MaxMagnitude
            : IUnaryReduction_Tensor<float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.MaxMagnitude(x, destination);
            }

            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination = TensorPrimitives.MaxMagnitude(x);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.MaxMagnitude(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.MaxMagnitude(x, y, destination);
            }

            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.MaxMagnitude(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.MaxMagnitude(x, y, destination);
            }
        }

        public struct MaxMagnitudeNumber
            : IUnaryReduction_Tensor<float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.MaxMagnitudeNumber(x, destination);
            }

            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination = TensorPrimitives.MaxMagnitudeNumber(x);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.MaxMagnitudeNumber(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.MaxMagnitudeNumber(x, y, destination);
            }

            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.MaxMagnitudeNumber(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.MaxMagnitudeNumber(x, y, destination);
            }
        }

        public struct MaxNumber
            : IUnaryReduction_Tensor<float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.MaxNumber(x, destination);
            }

            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination = TensorPrimitives.MaxNumber(x);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.MaxNumber(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.MaxNumber(x, y, destination);
            }

            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.MaxNumber(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.MaxNumber(x, y, destination);
            }
        }

        public struct Min
        : IUnaryReduction_Tensor<float, float>,
          IBinaryOperation_Tensor_Scalar<float, float>,
          IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Min(x, destination);
            }

            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination = TensorPrimitives.Min(x);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Min(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Min(x, y, destination);
            }

            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.Min(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.Min(x, y, destination);
            }
        }

        public struct MinMagnitude
            : IUnaryReduction_Tensor<float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.MinMagnitude(x, destination);
            }

            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination = TensorPrimitives.MinMagnitude(x);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.MinMagnitude(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.MinMagnitude(x, y, destination);
            }

            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.MinMagnitude(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.MinMagnitude(x, y, destination);
            }
        }

        public struct MinMagnitudeNumber
            : IUnaryReduction_Tensor<float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.MinMagnitudeNumber(x, destination);
            }

            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination = TensorPrimitives.MinMagnitudeNumber(x);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.MinMagnitudeNumber(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.MinMagnitudeNumber(x, y, destination);
            }

            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.MinMagnitudeNumber(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.MinMagnitudeNumber(x, y, destination);
            }
        }

        public struct MinNumber
            : IUnaryReduction_Tensor<float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.MinNumber(x, destination);
            }

            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination = TensorPrimitives.MinNumber(x);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.MinNumber(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.MinNumber(x, y, destination);
            }

            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.MinNumber(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.MinNumber(x, y, destination);
            }
        }

        public readonly struct Multiply
            : IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = x * y;
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.Multiply(x, y, destination);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = x * y;
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Multiply(x, y, destination);
            }
        }

        public readonly struct Negate
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = -x;
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Negate(x, destination);
            }
        }

        public readonly struct Pow
            : IBinaryOperation_Scalar_Tensor<float, float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = ScalarF.Pow(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.Pow(x, y, destination);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Pow(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Pow(x, y, destination);
            }

            public static void Invoke(float x, ref readonly float y, ref float destination)
            {
                destination = ScalarF.Pow(x, y);
            }

            public static void Invoke(float x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Pow(x, y, destination);
            }
        }

        public struct Product
            : IUnaryReduction_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination *= x;
            }

            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination = TensorPrimitives.Product(x);
            }
        }

        public readonly struct RadiansToDegrees
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.RadiansToDegrees(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.RadiansToDegrees(x, destination);
            }
        }

        public readonly struct Reciprocal
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = 1f / x;
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Reciprocal(x, destination);
            }
        }

        public readonly struct RootN
        : IBinaryOperation_Tensor_Int32<float, float>
        {
            public static void Invoke(ref readonly float x, int y, ref float destination)
            {
                destination = ScalarF.RootN(x, y);
            }

            public static void Invoke(ReadOnlySpan<float> x, int y, Span<float> destination)
            {
                TensorPrimitives.RootN(x, y, destination);
            }
        }

        public readonly struct Round
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Round(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Round(x, destination);
            }

        }

        public readonly struct Sigmoid
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = 1f / (1f + ScalarF.Exp(-x));
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Sigmoid(x, destination);
            }
        }

        public readonly struct Sin
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Sin(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Sin(x, destination);
            }
        }

        public readonly struct Sinh
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Sinh(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Sinh(x, destination);
            }
        }

        public readonly struct SinPi
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.SinPi(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.SinPi(x, destination);
            }
        }

        // SoftMax Helper
        public readonly struct SumExp
        : IUnaryReduction_Tensor<float, (float Shift, float Sum, bool HasValue)>
        {
            public static void Invoke(ref readonly float x, ref (float Shift, float Sum, bool HasValue) destination)
            {
                if (!destination.HasValue)
                {
                    destination = (x, 1f, true);
                    return;
                }

                if (ScalarF.IsNegativeInfinity(x))
                {
                    return;
                }

                float term = ScalarF.Exp(x - destination.Shift);
                if (ScalarF.IsInfinity(term))
                {
                    destination.Sum = (destination.Sum * ScalarF.Exp(destination.Shift - x)) + 1f;
                    destination.Shift = x;
                }
                else if (ScalarF.IsInfinity(destination.Sum + term))
                {
                    float previousShift = destination.Shift;
                    float step = 1f;
                    float nextShift = previousShift + step;
                    while (nextShift.Equals(previousShift))
                    {
                        step += step;
                        nextShift = previousShift + step;
                    }
                    float scale = ScalarF.Exp(previousShift - nextShift);
                    destination.Shift = nextShift;
                    destination.Sum = (destination.Sum * scale) + (term * scale);
                }
                else
                {
                    destination.Sum += term;
                }
            }

            public static void Invoke(ReadOnlySpan<float> x, ref (float Shift, float Sum, bool HasValue) destination)
            {
                for (int i = 0; i < x.Length; i++)
                {
                    Invoke(in x[i], ref destination);
                }
            }
        }

        public readonly struct SoftMax
        : IBinaryOperation_Tensor_Scalar<float, (float Shift, float Sum), float>
        {
            public static void Invoke(ref readonly float x, (float Shift, float Sum) y, ref float destination)
            {
                destination = ScalarF.Exp(x - y.Shift) / y.Sum;
            }

            public static void Invoke(ReadOnlySpan<float> x, (float Shift, float Sum) y, Span<float> destination)
            {
                for (int i = 0; i < x.Length; i++)
                {
                    destination[i] = ScalarF.Exp(x[i] - y.Shift) / y.Sum;
                }
            }
        }

        public readonly struct Sqrt
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Sqrt(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Sqrt(x, destination);
            }
        }

        public readonly struct Subtract
            : IBinaryOperation_Scalar_Tensor<float, float, float>,
              IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination = x - y;
            }

            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                TensorPrimitives.Subtract(x, y, destination);
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination = x - y;
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Subtract(x, y, destination);
            }

            public static void Invoke(float x, ref readonly float y, ref float destination)
            {
                destination = x - y;
            }

            public static void Invoke(float x, ReadOnlySpan<float> y, Span<float> destination)
            {
                TensorPrimitives.Subtract(x, y, destination);
            }
        }

        public struct Sum
            : IUnaryReduction_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination += x;
            }

            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination += TensorPrimitives.Sum(x);
            }
        }

        public struct SumOfSquaredDifferences
            : IBinaryOperation_Tensor_Scalar<float, float>,
              IBinaryOperation_Tensor_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                destination += (x - y) * (x - y);
            }
            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                for (int i = 0; i < x.Length; i++)
                {
                    destination[i] = (x[i] - y) * (x[i] - y);
                }
            }

            public static void Invoke(ref readonly float x, ref readonly float y, ref float destination)
            {
                destination += (x - y) * (x - y);
            }

            public static void Invoke(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> destination)
            {
                for (int i = 0; i < x.Length; i++)
                {
                    destination[i] = (x[i] - y[i]) * (x[i] - y[i]);
                }
            }
        }

        public readonly struct SumOfSquaredAbsoluteDifferences
            : IBinaryOperation_Tensor_Scalar<float, float>
        {
            public static void Invoke(ref readonly float x, float y, ref float destination)
            {
                // Absolute value is needed before squaring to support complex numbers
                float diff = ScalarF.Abs(x - y);
                destination += diff * diff;
            }
            public static void Invoke(ReadOnlySpan<float> x, float y, Span<float> destination)
            {
                for (int i = 0; i < x.Length; i++)
                {
                    // Absolute value is needed before squaring to support complex numbers
                    float diff = ScalarF.Abs(x[i] - y);
                    destination[i] = diff * diff;
                }
            }
        }

        public readonly struct SumOfSquares
        : IUnaryReduction_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination += x * x;
            }
            public static void Invoke(ReadOnlySpan<float> x, ref float destination)
            {
                destination += TensorPrimitives.SumOfSquares(x);
            }
        }

        public readonly struct Tan
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Tan(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Tan(x, destination);
            }
        }

        public readonly struct Tanh
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Tanh(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Tanh(x, destination);
            }
        }

        public readonly struct TanPi
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.TanPi(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.TanPi(x, destination);
            }
        }

        public readonly struct Truncate
        : IUnaryOperation_Tensor<float, float>
        {
            public static void Invoke(ref readonly float x, ref float destination)
            {
                destination = ScalarF.Truncate(x);
            }

            public static void Invoke(ReadOnlySpan<float> x, Span<float> destination)
            {
                TensorPrimitives.Truncate(x, destination);
            }
        }

        public readonly struct FilteredUpdate<T>
            : IBinaryOperation_Tensor_Scalar<bool, T, T>,
              IBinaryOperation_Tensor_Tensor<bool, T, T>
        {
            public static void Invoke(ref readonly bool x, ref readonly T y, ref T destination)
            {
                if (x)
                {
                    destination = y;
                }
            }
            public static void Invoke(ReadOnlySpan<bool> x, ReadOnlySpan<T> y, Span<T> destination)
            {
                for (int i = 0; i < x.Length; i++)
                {
                    if (x[i])
                    {
                        destination[i] = y[i];
                    }
                }
            }

            public static void Invoke(ref readonly bool x, T y, ref T destination)
            {
                if (x)
                {
                    destination = y;
                }
            }

            public static void Invoke(ReadOnlySpan<bool> x, T y, Span<T> destination)
            {
                for (int i = 0; i < x.Length; i++)
                {
                    if (x[i])
                    {
                        destination[i] = y;
                    }
                }
            }
        }

        public readonly struct GreaterThan
            : IBinaryOperation_Tensor_Scalar<float, bool>,
              IBinaryOperation_Tensor_Tensor<float, bool>
        {
            public static void Invoke(ref readonly float left, float right, ref bool destination)
            {
                destination = (left > right);
            }

            public static void Invoke(ref readonly float left, ref readonly float right, ref bool destination)
            {
                destination = (left > right);
            }

            public static void Invoke(ReadOnlySpan<float> left, float right, Span<bool> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (left[i] > right);
                }
            }

            public static void Invoke(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<bool> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (left[i] > right[i]);
                }
            }
        }

        public readonly struct GreaterThanAny
            : IBinaryOperation_Tensor_Scalar<float, bool>,
              IBinaryOperation_Tensor_Tensor<float, bool>
        {
            // The main loop early exits at the first false condition, so we
            // check !(x > y) and returns false on first not greater. The consumer will
            // then negate whatever the main loop returns as `true` means none
            // are greater.

            public static void Invoke(ref readonly float left, float right, ref bool destination)
            {
                destination = !(left > right);
            }

            public static void Invoke(ref readonly float left, ref readonly float right, ref bool destination)
            {
                destination = !(left > right);
            }

            public static void Invoke(ReadOnlySpan<float> left, float right, Span<bool> destination)
            {
                Debug.Assert(destination.Length == 1);
                bool result = true;

                for (int i = 0; i < left.Length; i++)
                {
                    result = !(left[i] > right);

                    if (!result)
                    {
                        break;
                    }
                }

                destination[0] = result;
            }

            public static void Invoke(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<bool> destination)
            {
                Debug.Assert(destination.Length == 1);
                bool result = true;

                for (int i = 0; i < left.Length; i++)
                {
                    result = !(left[i] > right[i]);

                    if (!result)
                    {
                        break;
                    }
                }

                destination[0] = result;
            }
        }

        public readonly struct GreaterThanOrEqual
            : IBinaryOperation_Tensor_Scalar<float, bool>,
              IBinaryOperation_Tensor_Tensor<float, bool>
        {
            public static void Invoke(ref readonly float left, float right, ref bool destination)
            {
                destination = (left >= right);
            }

            public static void Invoke(ref readonly float left, ref readonly float right, ref bool destination)
            {
                destination = (left >= right);
            }

            public static void Invoke(ReadOnlySpan<float> left, float right, Span<bool> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (left[i] >= right);
                }
            }

            public static void Invoke(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<bool> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (left[i] >= right[i]);
                }
            }
        }

        public readonly struct GreaterThanOrEqualAny
            : IBinaryOperation_Tensor_Scalar<float, bool>,
              IBinaryOperation_Tensor_Tensor<float, bool>
        {
            // The main loop early exits at the first false condition, so we
            // check !(x >= y) and returns false on first not greater or equal.
            // The consumer will then negate whatever the main loop returns as
            // `true` means none are greater or equal.

            public static void Invoke(ref readonly float left, float right, ref bool destination)
            {
                destination = !(left >= right);
            }

            public static void Invoke(ref readonly float left, ref readonly float right, ref bool destination)
            {
                destination = !(left >= right);
            }

            public static void Invoke(ReadOnlySpan<float> left, float right, Span<bool> destination)
            {
                Debug.Assert(destination.Length == 1);
                bool result = true;

                for (int i = 0; i < left.Length; i++)
                {
                    result = !(left[i] >= right);

                    if (!result)
                    {
                        break;
                    }
                }

                destination[0] = result;
            }

            public static void Invoke(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<bool> destination)
            {
                Debug.Assert(destination.Length == 1);
                bool result = true;

                for (int i = 0; i < left.Length; i++)
                {
                    result = !(left[i] >= right[i]);

                    if (!result)
                    {
                        break;
                    }
                }

                destination[0] = result;
            }
        }

        public readonly struct LessThan
            : IBinaryOperation_Tensor_Scalar<float, bool>,
              IBinaryOperation_Tensor_Tensor<float, bool>
        {
            public static void Invoke(ref readonly float left, float right, ref bool destination)
            {
                destination = (left < right);
            }

            public static void Invoke(ref readonly float left, ref readonly float right, ref bool destination)
            {
                destination = (left < right);
            }

            public static void Invoke(ReadOnlySpan<float> left, float right, Span<bool> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (left[i] < right);
                }
            }

            public static void Invoke(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<bool> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (left[i] < right[i]);
                }
            }
        }

        public readonly struct LessThanAny
            : IBinaryOperation_Tensor_Scalar<float, bool>,
              IBinaryOperation_Tensor_Tensor<float, bool>
        {
            // The main loop early exits at the first false condition, so we
            // check !(x < y) and returns false on first not lesser. The consumer will
            // then negate whatever the main loop returns as `true` means none
            // are lesser.

            public static void Invoke(ref readonly float left, float right, ref bool destination)
            {
                destination = !(left < right);
            }

            public static void Invoke(ref readonly float left, ref readonly float right, ref bool destination)
            {
                destination = !(left < right);
            }

            public static void Invoke(ReadOnlySpan<float> left, float right, Span<bool> destination)
            {
                Debug.Assert(destination.Length == 1);
                bool result = true;

                for (int i = 0; i < left.Length; i++)
                {
                    result = !(left[i] < right);

                    if (!result)
                    {
                        break;
                    }
                }

                destination[0] = result;
            }

            public static void Invoke(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<bool> destination)
            {
                Debug.Assert(destination.Length == 1);
                bool result = true;

                for (int i = 0; i < left.Length; i++)
                {
                    result = !(left[i] < right[i]);

                    if (!result)
                    {
                        break;
                    }
                }

                destination[0] = result;
            }
        }

        public readonly struct LessThanOrEqual
            : IBinaryOperation_Tensor_Scalar<float, bool>,
              IBinaryOperation_Tensor_Tensor<float, bool>
        {
            public static void Invoke(ref readonly float left, float right, ref bool destination)
            {
                destination = (left <= right);
            }

            public static void Invoke(ref readonly float left, ref readonly float right, ref bool destination)
            {
                destination = (left <= right);
            }

            public static void Invoke(ReadOnlySpan<float> left, float right, Span<bool> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (left[i] <= right);
                }
            }

            public static void Invoke(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<bool> destination)
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = (left[i] <= right[i]);
                }
            }
        }

        public readonly struct LessThanOrEqualAny
            : IBinaryOperation_Tensor_Scalar<float, bool>,
              IBinaryOperation_Tensor_Tensor<float, bool>
        {
            // The main loop early exits at the first false condition, so we
            // check !(x <= y) and returns false on first not lesser or equal.
            // The consumer will then negate whatever the main loop returns as
            // `true` means none are lesser or equal.

            public static void Invoke(ref readonly float left, float right, ref bool destination)
            {
                destination = !(left <= right);
            }

            public static void Invoke(ref readonly float left, ref readonly float right, ref bool destination)
            {
                destination = !(left <= right);
            }

            public static void Invoke(ReadOnlySpan<float> left, float right, Span<bool> destination)
            {
                Debug.Assert(destination.Length == 1);
                bool result = true;

                for (int i = 0; i < left.Length; i++)
                {
                    result = !(left[i] <= right);

                    if (!result)
                    {
                        break;
                    }
                }

                destination[0] = result;
            }

            public static void Invoke(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<bool> destination)
            {
                Debug.Assert(destination.Length == 1);
                bool result = true;

                for (int i = 0; i < left.Length; i++)
                {
                    result = !(left[i] <= right[i]);

                    if (!result)
                    {
                        break;
                    }
                }

                destination[0] = result;
            }
        }
    }
}
