// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace System.Numerics.Tensors
{
    // .NET's float functions that generic math reaches through T.Foo(x)
    // (System.Private.CoreLib's Single.cs and MathF.cs), for the tensor
    // operations' one-element paths: the same formulas, so a strided tensor
    // gets the results .NET gives it. SinPi, CosPi and TanPi are .NET's,
    // which are based on AMD's aocl-libm-ose: Copyright (C) 2008-2020 Advanced
    // Micro Devices, Inc., under the BSD 3-Clause license, whose text is in
    // ../../../THIRD-PARTY-NOTICES.txt. ILogB is written from float's bit layout.
    internal static class ScalarF
    {
        private const float Pi = MathF.PI;

        private static bool IsOddInteger(int value) => (value & 1) != 0;

        private static bool IsEvenInteger(int value) => (value & 1) == 0;

        public static float Abs(float x) => MathF.Abs(x);
        public static float Acos(float x) => MathF.Acos(x);
        public static float Acosh(float x) => MathF.Acosh(x);
        public static float Asin(float x) => MathF.Asin(x);
        public static float Asinh(float x) => MathF.Asinh(x);
        public static float Atan(float x) => MathF.Atan(x);
        public static float Atanh(float x) => MathF.Atanh(x);
        public static float Atan2(float y, float x) => MathF.Atan2(y, x);
        public static float Cbrt(float x) => MathF.Cbrt(x);
        public static float Ceiling(float x) => MathF.Ceiling(x);
        public static float CopySign(float value, float sign) => MathF.CopySign(value, sign);
        public static float Cos(float x) => MathF.Cos(x);
        public static float Cosh(float x) => MathF.Cosh(x);
        public static float Exp(float x) => MathF.Exp(x);
        public static float Floor(float x) => MathF.Floor(x);
        public static float Log(float x) => MathF.Log(x);
        public static float Log(float x, float newBase) => MathF.Log(x, newBase);
        public static float Log10(float x) => MathF.Log10(x);
        public static float Log2(float x) => MathF.Log2(x);
        public static float Max(float x, float y) => MathF.Max(x, y);
        public static float Min(float x, float y) => MathF.Min(x, y);
        public static float Pow(float x, float y) => MathF.Pow(x, y);
        public static float Round(float x) => MathF.Round(x);
        public static float Sin(float x) => MathF.Sin(x);
        public static float Sinh(float x) => MathF.Sinh(x);
        public static float Sqrt(float x) => MathF.Sqrt(x);
        public static float Tan(float x) => MathF.Tan(x);
        public static float Tanh(float x) => MathF.Tanh(x);
        public static float Truncate(float x) => MathF.Truncate(x);
        public static bool IsInfinity(float x) => float.IsInfinity(x);
        public static bool IsNegativeInfinity(float x) => float.IsNegativeInfinity(x);

        public static float AcosPi(float x) => Acos(x) / Pi;
        public static float AsinPi(float x) => Asin(x) / Pi;
        public static float AtanPi(float x) => Atan(x) / Pi;
        public static float Atan2Pi(float y, float x) => Atan2(y, x) / Pi;

        // double's DegreesToRadiansHead and RadiansToDegreesHead.
        public static float DegreesToRadians(float degrees) => (float)(degrees * 0.017453292519943295);
        public static float RadiansToDegrees(float radians) => (float)(radians * 57.29577951308232);

        public static float Exp10(float x) => MathF.Pow(10, x);
        public static float Exp2(float x) => MathF.Pow(2, x);

        // .NET's float.ExpM1 is Exp(x) - 1, which cancels near 0 (16 ulps off
        // at x = -0.055, 1.2e-7 for 1e-30); these work in double instead, with
        // the series where even double's exp would cancel, so they are within
        // an ulp everywhere.
        public static float ExpM1(float x) => (float)ExpM1Double(x);
        public static float Exp2M1(float x) => (float)ExpM1Double(x * 0.69314718055994530942);
        public static float Exp10M1(float x) => (float)ExpM1Double(x * 2.30258509299404568402);

        private static double ExpM1Double(double x) =>
            Math.Abs(x) < 1e-5 ? x + x * x / 2 + x * x * x / 6 : Math.Exp(x) - 1;
        public static float Log10P1(float x) => MathF.Log10(x + 1);
        public static float Log2P1(float x) => MathF.Log2(x + 1);
        public static float LogP1(float x) => MathF.Log(x + 1);
        public static float Ieee754Remainder(float left, float right) => MathF.IEEERemainder(left, right);

        public static float Hypot(float x, float y)
        {
            if (float.IsFinite(x) && float.IsFinite(y))
            {
                float ax = MathF.Abs(x);
                float ay = MathF.Abs(y);
                if (ax == 0.0f)
                    return ay;
                if (ay == 0.0f)
                    return ax;
                double xx = ax;
                xx *= xx;
                double yy = ay;
                yy *= yy;
                return (float)Math.Sqrt(xx + yy);
            }
            if (float.IsInfinity(x) || float.IsInfinity(y))
                return float.PositiveInfinity;
            return float.NaN;
        }

        public static int ILogB(float x)
        {
            int bits = BitConverter.SingleToInt32Bits(x);
            int biased = (bits >> 23) & 0xFF;
            int significand = bits & 0x7FFFFF;
            if (biased == 0)
            {
                if (significand == 0)
                    return int.MinValue;
                // subnormal: MinExponent - (lzcnt(significand) - 8)
                return -126 - (BitOperations.LeadingZeroCount((uint)significand) - 8);
            }
            if (biased == 0xFF)
                return int.MaxValue;
            return biased - 127;
        }

        public static float MaxMagnitude(float x, float y)
        {
            float ax = MathF.Abs(x);
            float ay = MathF.Abs(y);
            if ((ax > ay) || float.IsNaN(ax))
                return x;
            if (ax == ay)
                return float.IsNegative(x) ? y : x;
            return y;
        }

        public static float MinMagnitude(float x, float y)
        {
            float ax = MathF.Abs(x);
            float ay = MathF.Abs(y);
            if ((ax < ay) || float.IsNaN(ax))
                return x;
            if (ax == ay)
                return float.IsNegative(x) ? x : y;
            return y;
        }

        public static float MaxNumber(float x, float y)
        {
            if (x != y)
                return !float.IsNaN(y) ? (y < x ? x : y) : x;
            return float.IsNegative(y) ? x : y;
        }

        public static float MinNumber(float x, float y)
        {
            if (x != y)
                return !float.IsNaN(y) ? (x < y ? x : y) : x;
            return float.IsNegative(x) ? x : y;
        }

        public static float MaxMagnitudeNumber(float x, float y)
        {
            float ax = MathF.Abs(x);
            float ay = MathF.Abs(y);
            if ((ax > ay) || float.IsNaN(ay))
                return x;
            if (ax == ay)
                return float.IsNegative(x) ? y : x;
            return y;
        }

        public static float MinMagnitudeNumber(float x, float y)
        {
            float ax = MathF.Abs(x);
            float ay = MathF.Abs(y);
            if ((ax < ay) || float.IsNaN(ay))
                return x;
            if (ax == ay)
                return float.IsNegative(x) ? x : y;
            return y;
        }

        public static float CosPi(float x)
        {
            // This code is based on `cospif` from amd/aocl-libm-ose
            // Copyright (C) 2008-2020 Advanced Micro Devices, Inc. All rights reserved.
            //
            // Licensed under the BSD 3-Clause "New" or "Revised" License
            // See THIRD-PARTY-NOTICES.TXT for the full license text

            float result;

            if (float.IsFinite(x))
            {
                float ax = MathF.Abs(x);

                if (ax < 8_388_608.0f)              // |x| < 2^23
                {
                    if (ax > 0.25f)
                    {
                        int integral = (int)ax;

                        float fractional = ax - integral;
                        float sign = IsOddInteger(integral) ? -1.0f : +1.0f;

                        if (fractional <= 0.25f)
                        {
                            if (fractional != 0.00f)
                            {
                                result = sign * CosForIntervalPiBy4(fractional * Pi);
                            }
                            else
                            {
                                result = sign;
                            }
                        }
                        else if (fractional <= 0.50f)
                        {
                            if (fractional != 0.50f)
                            {
                                result = sign * SinForIntervalPiBy4((0.5f - fractional) * Pi);
                            }
                            else
                            {
                                result = 0.0f;
                            }
                        }
                        else if (fractional <= 0.75)
                        {
                            result = -sign * SinForIntervalPiBy4((fractional - 0.5f) * Pi);
                        }
                        else
                        {
                            result = -sign * CosForIntervalPiBy4((1.0f - fractional) * Pi);
                        }
                    }
                    else if (ax >= 7.8125E-3f)      // |x| >= 2^-7
                    {
                        result = CosForIntervalPiBy4(x * Pi);
                    }
                    else if (ax >= 1.22070313E-4f)  // |x| >= 2^-13
                    {
                        float value = x * Pi;
                        result = 1.0f - (value * value * 0.5f);
                    }
                    else
                    {
                        result = 1.0f;
                    }
                }
                else if (ax < 16_777_216.0f)        // |x| < 2^24
                {
                    // x is an integer
                    int bits = BitConverter.SingleToInt32Bits(ax);
                    result = IsOddInteger(bits) ? -1.0f : +1.0f;
                }
                else
                {
                    // x is an even integer
                    result = 1.0f;
                }
            }
            else
            {
                result = float.NaN;
            }

            return result;
        }

        public static float SinPi(float x)
        {
            // This code is based on `sinpif` from amd/aocl-libm-ose
            // Copyright (C) 2008-2020 Advanced Micro Devices, Inc. All rights reserved.
            //
            // Licensed under the BSD 3-Clause "New" or "Revised" License
            // See THIRD-PARTY-NOTICES.TXT for the full license text

            float result;

            if (float.IsFinite(x))
            {
                float ax = MathF.Abs(x);

                if (ax < 8_388_608.0f)              // |x| < 2^23
                {
                    if (ax > 0.25f)
                    {
                        int integral = (int)ax;

                        float fractional = ax - integral;
                        float sign = ((x > 0.0f) ? +1.0f : -1.0f) * (IsOddInteger(integral) ? -1.0f : +1.0f);

                        if (fractional <= 0.25f)
                        {
                            if (fractional != 0.00f)
                            {
                                result = sign * SinForIntervalPiBy4(fractional * Pi);
                            }
                            else
                            {
                                result = x * 0.0f;
                            }
                        }
                        else if (fractional <= 0.50f)
                        {
                            if (fractional != 0.50f)
                            {
                                result = sign * CosForIntervalPiBy4((0.5f - fractional) * Pi);
                            }
                            else
                            {
                                result = sign;
                            }
                        }
                        else if (fractional <= 0.75f)
                        {
                            result = sign * CosForIntervalPiBy4((fractional - 0.5f) * Pi);
                        }
                        else
                        {
                            result = sign * SinForIntervalPiBy4((1.0f - fractional) * Pi);
                        }
                    }
                    else if (ax >= 7.8125E-3f)      // |x| >= 2^-7
                    {
                        result = SinForIntervalPiBy4(x * Pi);
                    }
                    else if (ax >= 1.22070313E-4f)  // |x| >= 2^-13
                    {
                        float value = x * Pi;
                        result = value - (value * value * value * (1.0f / 6.0f));
                    }
                    else
                    {
                        result = x * Pi;
                    }
                }
                else
                {
                    // x is an integer
                    result = x * 0.0f;
                }
            }
            else
            {
                result = float.NaN;
            }

            return result;
        }

        public static float TanPi(float x)
        {
            // This code is based on `tanpif` from amd/aocl-libm-ose
            // Copyright (C) 2008-2020 Advanced Micro Devices, Inc. All rights reserved.
            //
            // Licensed under the BSD 3-Clause "New" or "Revised" License
            // See THIRD-PARTY-NOTICES.TXT for the full license text

            float result;

            if (float.IsFinite(x))
            {
                float ax = MathF.Abs(x);
                float sign = (x > 0.0f) ? +1.0f : -1.0f;

                if (ax < 8_388_608.0f)              // |x| < 2^23
                {
                    if (ax > 0.25f)
                    {
                        int integral = (int)ax;
                        float fractional = ax - integral;

                        if (fractional <= 0.25f)
                        {
                            if (fractional != 0.00f)
                            {
                                result = sign * TanForIntervalPiBy4(fractional * Pi, isReciprocal: false);
                            }
                            else
                            {
                                result = sign * (IsOddInteger(integral) ? -0.0f : +0.0f);
                            }
                        }
                        else if (fractional <= 0.50f)
                        {
                            if (fractional != 0.50f)
                            {
                                result = -sign * TanForIntervalPiBy4((0.5f - fractional) * Pi, isReciprocal: true);
                            }
                            else
                            {
                                result = +sign * (IsOddInteger(integral) ? float.NegativeInfinity : float.PositiveInfinity);
                            }
                        }
                        else if (fractional <= 0.75f)
                        {
                            result = +sign * TanForIntervalPiBy4((fractional - 0.5f) * Pi, isReciprocal: true);
                        }
                        else
                        {
                            result = -sign * TanForIntervalPiBy4((1.0f - fractional) * Pi, isReciprocal: false);
                        }
                    }
                    else if (ax >= 7.8125E-3f)      // |x| >= 2^-7
                    {
                        result = TanForIntervalPiBy4(x * Pi, isReciprocal: false);
                    }
                    else if (ax >= 1.22070313E-4f)  // |x| >= 2^-13
                    {
                        float value = x * Pi;
                        result = value + (value * value * value * (1.0f / 3.0f));
                    }
                    else
                    {
                        result = x * Pi;
                    }
                }
                else if (ax < 16_777_216)           // |x| < 2^24
                {
                    // x is an integer
                    int bits = BitConverter.SingleToInt32Bits(ax);
                    result = sign * (IsOddInteger(bits) ? -0.0f : +0.0f);
                }
                else
                {
                    // x is an even integer
                    result = sign * 0.0f;
                }
            }
            else
            {
                result = float.NaN;
            }

            return result;
        }

        private static float CosForIntervalPiBy4(float x)
        {
            // This code is based on `cos_piby4` from amd/aocl-libm-ose
            // Copyright (C) 2008-2020 Advanced Micro Devices, Inc. All rights reserved.
            //
            // Licensed under the BSD 3-Clause "New" or "Revised" License
            // See THIRD-PARTY-NOTICES.TXT for the full license text

            // Taylor series for cos(x) is: 1 - (x^2 / 2!) + (x^4 / 4!) - (x^6 / 6!) ...
            //
            // Then define f(xx) where xx = (x * x)
            // and f(xx) = 1 - (xx / 2!) + (xx^2 / 4!) - (xx^3 / 6!) ...
            //
            // We use a minimax approximation of (f(xx) - 1 + (xx / 2)) / (xx * xx)
            // because this produces an expansion in even powers of x.

            const double C1 = +0.41666666666666665390037E-1;        // approx: +1 / 4!
            const double C2 = -0.13888888888887398280412E-2;        // approx: -1 / 6!
            const double C3 = +0.248015872987670414957399E-4;       // approx: +1 / 8!
            const double C4 = -0.275573172723441909470836E-6;       // approx: -1 / 10!

            double xx = x * x;
            double result = C4;

            result = (result * xx) + C3;
            result = (result * xx) + C2;
            result = (result * xx) + C1;

            result *= xx * xx;
            result += 1.0 - (0.5 * xx);

            return (float)result;
        }

        private static float SinForIntervalPiBy4(float x)
        {
            // This code is based on `sin_piby4` from amd/aocl-libm-ose
            // Copyright (C) 2008-2020 Advanced Micro Devices, Inc. All rights reserved.
            //
            // Licensed under the BSD 3-Clause "New" or "Revised" License
            // See THIRD-PARTY-NOTICES.TXT for the full license text

            // Taylor series for sin(x) is x - (x^3 / 3!) + (x^5 / 5!) - (x^7 / 7!) ...
            // Which can be expressed as x * (1 - (x^2 / 3!) + (x^4 /5!) - (x^6 /7!) ...)
            //
            // Then define f(xx) where xx = (x * x)
            // and f(xx) = 1 - (xx / 3!) + (xx^2 / 5!) - (xx^3 / 7!) ...
            //
            // We use a minimax approximation of (f(xx) - 1) / xx
            // because this produces an expansion in even powers of x.

            const double C1 = -0.166666666666666646259241729;       // approx: -1 / 3!
            const double C2 = +0.833333333333095043065222816E-2;    // approx: +1 / 5!
            const double C3 = -0.19841269836761125688538679E-3;     // approx: -1 / 7!
            const double C4 = +0.275573161037288022676895908448E-5; // approx: +1 / 9!

            double xx = x * x;
            double result = C4;

            result = (result * xx) + C3;
            result = (result * xx) + C2;
            result = (result * xx) + C1;

            result *= x * xx;
            result += x;

            return (float)result;
        }

        private static float TanForIntervalPiBy4(float x, bool isReciprocal)
        {
            // This code is based on `tan_piby4` from amd/aocl-libm-ose
            // Copyright (C) 2008-2020 Advanced Micro Devices, Inc. All rights reserved.
            //
            // Licensed under the BSD 3-Clause "New" or "Revised" License
            // See THIRD-PARTY-NOTICES.TXT for the full license text

            // Core Remez [1, 2] approximation to tan(x) on the interval [0, pi / 4].

            double xx = x * x;

            double denominator = +0.1844239256901656082986661E-1;
            denominator = -0.51396505478854532132342E+0 + (denominator * xx);
            denominator = +0.115588821434688393452299E+1 + (denominator * xx);

            double numerator = -0.172032480471481694693109E-1;
            numerator = 0.385296071263995406715129E+0 + (numerator * xx);

            double result = x * xx;
            result *= numerator / denominator;
            result += x;

            if (isReciprocal)
            {
                result = -1.0 / result;
            }

            return (float)result;
        }

        public static float RootN(float x, int n)
        {
            float result;

            if (n > 0)
            {
                if (n == 2)
                {
                    result = (x != 0.0f) ? MathF.Sqrt(x) : 0.0f;
                }
                else if (n == 3)
                {
                    result = MathF.Cbrt(x);
                }
                else
                {
                    result = PositiveN(x, n);
                }
            }
            else if (n < 0)
            {
                result = NegativeN(x, n);
            }
            else
            {
                Debug.Assert(n == 0);
                result = float.NaN;
            }

            return result;

            static float PositiveN(float x, int n)
            {
                float result;

                if (float.IsFinite(x))
                {
                    if (x != 0)
                    {
                        if ((x > 0) || IsOddInteger(n))
                        {
                            result = (float)Math.Pow(MathF.Abs(x), 1.0 / n);
                            result = MathF.CopySign(result, x);
                        }
                        else
                        {
                            result = float.NaN;
                        }
                    }
                    else if (IsEvenInteger(n))
                    {
                        result = 0.0f;
                    }
                    else
                    {
                        result = MathF.CopySign(0.0f, x);
                    }
                }
                else if (float.IsNaN(x))
                {
                    result = float.NaN;
                }
                else if (x > 0)
                {
                    Debug.Assert(float.IsPositiveInfinity(x));
                    result = float.PositiveInfinity;
                }
                else
                {
                    Debug.Assert(float.IsNegativeInfinity(x));
                    result = IsOddInteger(n) ? float.NegativeInfinity : float.NaN;
                }

                return result;
            }

            static float NegativeN(float x, int n)
            {
                float result;

                if (float.IsFinite(x))
                {
                    if (x != 0)
                    {
                        if ((x > 0) || IsOddInteger(n))
                        {
                            result = (float)Math.Pow(MathF.Abs(x), 1.0 / n);
                            result = MathF.CopySign(result, x);
                        }
                        else
                        {
                            result = float.NaN;
                        }
                    }
                    else if (IsEvenInteger(n))
                    {
                        result = float.PositiveInfinity;
                    }
                    else
                    {
                        result = MathF.CopySign(float.PositiveInfinity, x);
                    }
                }
                else if (float.IsNaN(x))
                {
                    result = float.NaN;
                }
                else if (x > 0)
                {
                    Debug.Assert(float.IsPositiveInfinity(x));
                    result = 0.0f;
                }
                else
                {
                    Debug.Assert(float.IsNegativeInfinity(x));
                    result = IsOddInteger(n) ? -0.0f : float.NaN;
                }

                return result;
            }
        }
    }
}
