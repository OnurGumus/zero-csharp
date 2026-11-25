// Test: System.Numerics in zero mode: the Math functions built on the C
// math library's (Asin, Sinh, Cbrt, ...), Complex, Vector2/3/4, Matrix3x2,
// Matrix4x4, Quaternion and Plane. Each target has its own math library
// (glibc, musl on UEFI, the 387 on DOS, ...), so the checks allow for a
// few units in the last place. scripts/numerics-fuzz compares the same code
// with .NET's on millions of values.
// Exit code = number of failed checks.
// Build: ./build.sh Numerics

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace NumericsSample;

public static class Program
{
    static int s_failures;
    static int s_sectionChecks;
    static int s_sectionFailures;

    static void End(string section)
    {
        Console.Write(s_sectionFailures == 0 ? "PASS " : "FAIL ");
        Console.Write(section);
        Console.Write(" (");
        Console.Write(s_sectionChecks);
        Console.WriteLine(" checks)");
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
            Console.Write("  failed: ");
            Console.WriteLine(name);
        }
    }

    // Hide a value from the optimizer, which would otherwise do the
    // arithmetic at build time. (Always false: see SmallIntegers.cs.)
    static bool s_never;
    [MethodImpl(MethodImplOptions.NoInlining)] static double D(double value) => s_never ? 0 : value;
    [MethodImpl(MethodImplOptions.NoInlining)] static float F(float value) => s_never ? 0 : value;

    // Within a relative error, or an absolute one for values near 0.
    static bool Near(double actual, double expected, double tolerance = 1e-14) =>
        Math.Abs(actual - expected) <= tolerance * Math.Max(1, Math.Abs(expected));

    static bool Near(Complex actual, double re, double im, double tolerance = 1e-14) =>
        Near(actual.Real, re, tolerance) && Near(actual.Imaginary, im, tolerance);

    static bool Near(Vector2 actual, float x, float y) =>
        Near(actual.X, x, 1e-6) && Near(actual.Y, y, 1e-6);

    static bool Near(Vector3 actual, float x, float y, float z) =>
        Near(actual.X, x, 1e-6) && Near(actual.Y, y, 1e-6) && Near(actual.Z, z, 1e-6);

    static bool Near(Matrix4x4 actual, Matrix4x4 expected)
    {
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (!Near(actual[row, column], expected[row, column], 1e-5))
                    return false;
        return true;
    }

    public static int Main()
    {
        s_never = false;
        MathFunctions();
        Complexes();
        Vectors();
        Transforms();

        Console.WriteLine();
        if (s_failures == 0)
            Console.WriteLine("All checks passed.");
        else
        {
            Console.Write(s_failures);
            Console.WriteLine(" checks FAILED.");
        }
        return s_failures;
    }

    static void MathFunctions()
    {
        Check("Asin, Acos, Atan", Near(Math.Asin(D(0.5)), Math.PI / 6) && Near(Math.Acos(D(-0.5)), 2 * Math.PI / 3)
            && Near(Math.Atan(D(1)), Math.PI / 4) && Math.Asin(D(1)) == Math.PI / 2 && double.IsNaN(Math.Asin(D(1.5))));
        Check("Sinh, Cosh, Tanh", Near(Math.Sinh(D(1)), 1.1752011936438014) && Near(Math.Cosh(D(1)), 1.5430806348152437)
            && Near(Math.Tanh(D(0.5)), 0.46211715726000974) && Math.Tanh(D(30)) == 1 && Math.Tanh(D(-30)) == -1);
        // Where exp(x) - exp(-x) would have nothing left but rounding.
        Check("Sinh, Tanh of tiny values", Math.Sinh(D(1e-10)) == 1e-10 && Math.Tanh(D(1e-20)) == 1e-20
            && Near(Math.Sinh(D(1e-5)), 1.0000000000166667e-05));
        Check("Asinh, Acosh, Atanh", Near(Math.Asinh(D(1)), 0.88137358701954302) && Near(Math.Acosh(D(2)), 1.3169578969248167)
            && Near(Math.Atanh(D(0.5)), 0.54930614433405485) && Math.Atanh(D(1)) == double.PositiveInfinity
            && double.IsNaN(Math.Acosh(D(0.5))));
        Check("Cbrt", Math.Cbrt(D(27)) == 3 && Math.Cbrt(D(-8)) == -2 && Near(Math.Cbrt(D(2)), 1.2599210498948732)
            && Near(Math.Cbrt(D(1e-300)), 1e-100));
        Check("Log(a, newBase)", Near(Math.Log(D(8), D(2)), 3) && Near(Math.Log(D(1000), D(10)), 3)
            && double.IsNaN(Math.Log(D(5), D(1))));
        Check("IEEERemainder", Math.IEEERemainder(D(10), D(3)) == 1 && Math.IEEERemainder(D(11), D(3)) == -1
            && Math.IEEERemainder(D(5), D(2)) == 1 && Math.IEEERemainder(D(7), D(2)) == -1);
        Check("Sign, CopySign", Math.Sign(D(-2.5)) == -1 && Math.Sign(D(0)) == 0 && Math.Sign(-7) == -1
            && Math.Sign(long.MinValue) == -1 && Math.CopySign(D(3), D(-0.0)) == -3);
        Check("MathF", Near(MathF.Asin(F(0.5f)), MathF.PI / 6, 1e-6) && Near(MathF.Tan(F(1)), 1.5574077, 1e-6)
            && MathF.Cbrt(F(64)) == 4 && Near(MathF.Atan2(F(1), F(-1)), 3 * MathF.PI / 4, 1e-6));
        End("Math functions");
    }

    static void Complexes()
    {
        // docs:complex
        var a = new Complex(D(1), D(2));
        var b = new Complex(D(3), D(-4));
        Check("+ - * /", a + b == new Complex(4, -2)
            && a * b == new Complex(11, 2) && Near(a / b, -0.2, 0.4));
        Check("Sqrt of a negative", Complex.Sqrt(-4) == new Complex(0, 2));
        // e^(iπ) = -1, up to the rounding of π.
        Check("Euler", Near(Complex.Exp(Complex.ImaginaryOne * Math.PI), -1, 0));
        Check("ToString", a.ToString() == "<1; 2>");
        // docs:end
        Check("-", a - b == new Complex(-2, 6));
        Check("with doubles", a * 2 == new Complex(2, 4) && 1 + a == new Complex(2, 2) && Near(D(1) / b, 0.12, 0.16));
        Check("Magnitude, Phase, Conjugate", new Complex(D(3), D(4)).Magnitude == 5 && Near(Complex.ImaginaryOne.Phase, Math.PI / 2)
            && Complex.Conjugate(a) == new Complex(1, -2));
        Check("FromPolarCoordinates", Near(Complex.FromPolarCoordinates(D(2), Math.PI / 2), 0, 2));
        Check("Sqrt", Complex.Sqrt(D(-4)) == new Complex(0, 2) && Near(Complex.Sqrt(new Complex(D(3), D(4))), 2, 1)
            && Near(Complex.Sqrt(new Complex(D(-3), D(-4))), 1, -2));
        Check("Exp, Log", Near(Complex.Exp(new Complex(0, D(Math.PI))), -1, 0) && Near(Complex.Log(D(-1)), 0, Math.PI)
            && Near(Complex.Log(new Complex(D(1), D(1))), 0.34657359027997265, 0.78539816339744831));
        Check("Pow", Near(Complex.Pow(Complex.ImaginaryOne, D(2)), -1, 0)
            && Near(Complex.Pow(new Complex(D(1), D(1)), new Complex(0.5, -1)), 2.605490112842469, 0.12026481418247156, 1e-13));
        Check("Sin, Cosh, Tan", Near(Complex.Sin(new Complex(D(1), D(2))), 3.1657785132161681, 1.9596010414216059)
            && Near(Complex.Cosh(new Complex(D(1), D(2))), -0.64214812471551996, 1.0686074213827783)
            && Near(Complex.Tan(new Complex(D(1), D(1))), 0.27175258531951172, 1.0839233273386945));
        // Next to tan's pole at π/2, where cos 2x + cosh 2y cancels. The real
        // part is proportional to cos x near its zero, which is only as
        // accurate as the target's cos: the 387's is off in the fifth digit.
        Complex nearPole = Complex.Tan(new Complex(D(Math.PI / 2), D(3.285977092239063e-7)));
        Check("Tan near a pole", Near(nearPole.Imaginary, 3043234.8489643199, 1e-9)
            && Near(nearPole.Real, 0.0005670897441190111, 1e-3));
        Check("Asin, Acos, Atan", Near(Complex.Asin(new Complex(D(0.5), D(0.5))), 0.45227844715119068, 0.53063753095251783)
            && Near(Complex.Acos(new Complex(D(0.5), D(0.5))), 1.1185178796437059, -0.53063753095251783)
            && Near(Complex.Atan(new Complex(D(1), D(1))), 1.0172219678978514, 0.40235947810852509));
        // On the branch cuts, .NET's choices.
        Check("branch cuts", Near(Complex.Asin(D(2)), Math.PI / 2, 1.3169578969248167)
            && Near(Complex.Acos(D(2)), 0, 1.3169578969248167) && Near(Complex.Acos(D(-2)), Math.PI, 1.3169578969248167));
        Check("NaN and infinity", Complex.IsNaN(Complex.NaN) && Complex.IsInfinity(Complex.Infinity)
            && Complex.IsFinite(a) && Complex.NaN.Equals(Complex.NaN) && Complex.NaN != Complex.NaN);
        Check("ToString of fractions", new Complex(1.5, -2).ToString() == "<1.5; -2>");
        End("Complex");
    }

    static void Vectors()
    {
        Vector3 x = Vector3.UnitX, y = Vector3.UnitY;
        Vector3 v = new Vector3(F(3), F(4), F(0));
        Check("Cross, Dot", Vector3.Cross(x, y) == Vector3.UnitZ && Vector3.Dot(v, x) == 3);
        Check("Length, Normalize", v.Length() == 5 && v.LengthSquared() == 25 && Near(Vector3.Normalize(v), 0.6f, 0.8f, 0));
        Check("Distance", Vector2.Distance(new Vector2(F(1), F(1)), new Vector2(4, 5)) == 5);
        Check("Reflect", Vector3.Reflect(new Vector3(F(1), F(-1), F(0)), y) == new Vector3(1, 1, 0));
        Check("Lerp", Vector4.Lerp(Vector4.Zero, new Vector4(F(2), 4, 6, 8), 0.5f) == new Vector4(1, 2, 3, 4));
        Check("Min, Max, Clamp, Abs", Vector2.Min(new Vector2(F(1), 5), new Vector2(3, 2)) == new Vector2(1, 2)
            && Vector3.Clamp(new Vector3(F(-5), 0.5f, 5), Vector3.Zero, Vector3.One) == new Vector3(0, 0.5f, 1)
            && Vector3.Abs(new Vector3(F(-1), 2, -3)) == new Vector3(1, 2, 3));
        Check("operators", v * 2 == new Vector3(6, 8, 0) && v / 2 == new Vector3(1.5f, 2, 0) && -v == new Vector3(-3, -4, 0)
            && v + v == v * new Vector3(2));
        Vector4 w = new Vector4(new Vector2(F(1), 2), 3, 4);
        w[3] = 5;
        Span<float> copy = stackalloc float[4];
        w.CopyTo(copy);
        Check("indexer, CopyTo", w[0] == 1 && w.W == 5 && copy[2] == 3 && copy[3] == 5);
        Check("Equals, ToString", new Vector2(float.NaN, 1).Equals(new Vector2(float.NaN, 1))
            && new Vector2(float.NaN, 1) != new Vector2(float.NaN, 1) && new Vector3(1, 2.5f, -3).ToString() == "<1, 2.5, -3>");
        End("vectors");
    }

    static void Transforms()
    {
        // Quarter turns get exact sines and cosines in Matrix3x2.
        Matrix3x2 turn = Matrix3x2.CreateRotation(F(MathF.PI / 2));
        Check("Matrix3x2 rotation", Vector2.Transform(new Vector2(F(1), 0), turn) == new Vector2(0, 1));
        Matrix3x2 m2 = Matrix3x2.CreateScale(F(2)) * Matrix3x2.CreateTranslation(3, 4);
        Check("Matrix3x2 scale, translation, Invert", Vector2.Transform(new Vector2(F(1), 1), m2) == new Vector2(5, 6)
            && Matrix3x2.Invert(m2, out Matrix3x2 back) && Vector2.Transform(new Vector2(5, 6), back) == new Vector2(1, 1)
            && !Matrix3x2.Invert(Matrix3x2.CreateScale(F(0)), out _));

        Matrix4x4 world = Matrix4x4.CreateScale(F(2)) * Matrix4x4.CreateRotationZ(F(MathF.PI / 2)) * Matrix4x4.CreateTranslation(10, 0, 0);
        Check("Matrix4x4 transform", Near(Vector3.Transform(Vector3.UnitX, world), 10, 2, 0)
            && Near(Vector3.TransformNormal(Vector3.UnitX, world), 0, 2, 0));
        Check("Matrix4x4 Invert", Matrix4x4.Invert(world, out Matrix4x4 inverse) && Near(world * inverse, Matrix4x4.Identity)
            && Near(world.GetDeterminant(), 8, 1e-6));
        Check("Decompose", Matrix4x4.Decompose(world, out Vector3 scale, out Quaternion rotation, out Vector3 translation)
            && Near(scale, 2, 2, 2) && translation == new Vector3(10, 0, 0)
            && Near(Vector3.Transform(Vector3.UnitX, rotation), 0, 1, 0));
        Check("Transpose, indexer", Matrix4x4.Transpose(world)[3, 0] == 0 && Matrix4x4.Transpose(world)[0, 3] == 10);

        // docs:camera
        // A camera at z = 5 looking at the origin sees it 5 units ahead,
        // which is -Z in a right-handed view space.
        var eye = new Vector3(0, 0, F(5));
        var view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
        Check("CreateLookAt",
            Near(Vector3.Transform(Vector3.Zero, view), 0, 0, -5));

        // A 90° field of view, square, drawing from 1 to 100 units away:
        // the far plane lands at depth 1.
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            F(MathF.PI / 2), 1, 1, 100);
        var clip = Vector4.Transform(new Vector3(0, 0, -100), projection);
        Check("CreatePerspectiveFieldOfView",
            Near(clip.Z / clip.W, 1, 1e-6) && projection.M34 == -1);
        // docs:end

        Quaternion quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, F(MathF.PI / 2));
        Check("Quaternion rotation", Near(Vector3.Transform(Vector3.UnitX, quarter), 0, 1, 0)
            && Near(Vector3.Transform(Vector3.UnitX, Matrix4x4.CreateFromQuaternion(quarter)), 0, 1, 0));
        Quaternion half = Quaternion.Slerp(Quaternion.Identity, quarter, 0.5f);
        Check("Slerp", Near(Vector3.Transform(Vector3.UnitX, half), 0.70710678f, 0.70710678f, 0));
        Check("Quaternion * Inverse", Near((quarter * Quaternion.Inverse(quarter)).W, 1, 1e-6) && Quaternion.Identity.IsIdentity);
        Quaternion ypr = Quaternion.CreateFromYawPitchRoll(F(0.3f), 0.2f, 0.1f);
        Check("CreateFromRotationMatrix", Near(Quaternion.Dot(Quaternion.CreateFromRotationMatrix(Matrix4x4.CreateFromQuaternion(ypr)), ypr), 1, 1e-6));

        Plane floor = Plane.CreateFromVertices(Vector3.Zero, Vector3.UnitX, new Vector3(0, 0, F(-1)));
        Check("Plane", Near(floor.Normal, 0, 1, 0) && floor.D == 0 && Plane.DotCoordinate(floor, new Vector3(5, 3, 2)) == 3);
        Check("CreateReflection", Near(Vector3.Transform(new Vector3(1, F(2), 3), Matrix4x4.CreateReflection(floor)), 1, -2, 3));
        Check("ToString", Quaternion.Identity.ToString() == "{X:0 Y:0 Z:0 W:1}"
            && new Plane(0, 1, 0, F(2)).ToString() == "{Normal:<0, 1, 0> D:2}"
            && Matrix3x2.Identity.ToString() == "{ {M11:1 M12:0} {M21:0 M22:1} {M31:0 M32:0} }");
        End("matrices, quaternions, planes");
    }
}
