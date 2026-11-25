// expect: CS0315
// zerocore's tensor math is for float: Sum<double> is refused when the program
// is built, not when the line runs.
using System.Numerics.Tensors;

public static class Program
{
    public static void Main()
    {
        Tensor<double> t = Tensor.Create(new double[] { 1, 2, 3 }, [3]);
        double sum = Tensor.Sum<double>(t);
    }
}
