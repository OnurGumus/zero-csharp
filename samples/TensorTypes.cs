// Tensor<T>, TensorSpan<T> and the Tensor class, as in .NET's
// System.Numerics.Tensors package: n-dimensional arrays with lengths and
// strides, views that share memory (slices, transposes, broadcasts), and
// element-wise math that broadcasts. A matrix is a tensor of rank 2.
//
// Tensor<T> holds any T; the math (Tensor.Add, Tensor.Sum, ...) is for float.
// A Tensor<T> is an object with an array, so creating one allocates, for good
// in zero mode; the methods that take a destination TensorSpan<T> allocate
// nothing.
using System;
using System.Buffers;
using System.Numerics.Tensors;

// docs:create
Tensor<float> a = Tensor.Create(new float[] { 1, 2, 3, 4, 5, 6 }, [2, 3]);
Console.WriteLine(a.ToString([2, 3]));
Console.WriteLine("a[1, 2] = " + a[1, 2].ToString());
// docs:end

// docs:broadcast
Tensor<float> row = Tensor.Create(new float[] { 10, 20, 30 }, [3]);
Tensor<float> sum = a + row; // [3] broadcasts over both rows of [2, 3]
Console.WriteLine(sum.ToString([2, 3]));
Tensor<float> scaled = Tensor.Multiply(a, 0.5f);
Console.WriteLine(scaled.ToString([2, 3]));
// docs:end

// docs:views
// A column is a view with stride 3: no copy, and writes go to a.
TensorSpan<float> column = a.AsTensorSpan().Slice([NRange.All, 1..2]);
Console.WriteLine("column sum = " + Tensor.Sum<float>(column).ToString());
column.Fill(0);
Console.WriteLine(a.ToString([2, 3]));
// Transpose is a view too, with the strides swapped: [3, 2].
Tensor<float> t = Tensor.Transpose(a);
Console.WriteLine(t.ToString([3, 2]));
Tensor<float> flat = Tensor.Reshape(a, [6]); // a view of the same 6 floats
Console.WriteLine(flat.ToString([6]));
// docs:end

// docs:matmul
// A matrix product from rows and columns:
// c[i, j] = Dot(row i of x, column j of y).
Tensor<float> x = Tensor.Create(new float[] { 1, 2, 3, 4, 5, 6 }, [2, 3]);
Tensor<float> y = Tensor.Create(new float[] { 1, 0, 0, 1, 2, 2 }, [3, 2]);
Tensor<float> c = MatMul(x, y);
Console.WriteLine(c.ToString([2, 2]));
// docs:end

// docs:relu
// ReLU in place: everything below 0 becomes 0.
float[] values = { -2, -0.5f, 0, 0.5f, 2, -1 };
Tensor<float> h = Tensor.Create(values, [2, 3]);
Tensor.FilteredUpdate<float>(h, Tensor.LessThan<float>(h, 0f), 0f);
Console.WriteLine(h.ToString([2, 3]));
// docs:end

// docs:softmax
// SoftMax of each row, written over the row (a destination: no allocation).
Tensor<float> logits = Tensor.Create(new float[] { 1, 2, 3, 0, 0, 0 }, [2, 3]);
TensorDimensionSpan<float> rows = logits.AsTensorSpan().GetDimensionSpan(0);
for (int i = 0; i < rows.Length; i++)
{
    TensorSpan<float> r = rows[i];
    Tensor.SoftMax(r, r);
}
// docs:end
for (int i = 0; i < 2; i++)
{
    Console.Write("row " + i.ToString() + ":");
    for (int j = 0; j < 3; j++)
        Console.Write(" " + logits[i, j].ToString("F4"));
    Console.WriteLine("");
}

// docs:stack
Tensor<float> stacked = Tensor.Stack<float>([row, row]);
Console.WriteLine(stacked.ToString([2, 3]));
Tensor<float> joined = Tensor.Concatenate<float>([a, stacked]);
Console.WriteLine(joined.ToString([4, 3]));
float max = Tensor.Max<float>(joined);
nint at = Tensor.IndexOfMax<float>(joined);
float mean = Tensor.Average<float>(joined);
Console.WriteLine("max " + max.ToString() + " at " + at.ToString()
    + ", mean " + mean.ToString());
// docs:end

// docs:arena
// Tensors made over and over go in an arena's object scope, which frees
// them when the block ends; nothing is left on the ordinary heap.
Arena frame = Arena.Create(64 * 1024);
long before = GC.GetTotalAllocatedBytes();
float total = 0;
for (int step = 0; step < 1000; step++)
{
    Arena.ObjectScope objects;
    // SAFETY: nothing created inside is used after the block.
    unsafe { objects = frame.OpenObjectScope(); }
    using (objects)
    {
        Tensor<float> next = Tensor.Multiply(a + row, 0.5f);
        total += Tensor.Sum<float>(next);
    }
}
long leaked = GC.GetTotalAllocatedBytes() - before;
Console.WriteLine("1000 steps: total " + total.ToString()
    + ", heap bytes " + leaked.ToString());
// docs:end

// docs:matmul-function
static Tensor<float> MatMul(Tensor<float> x, Tensor<float> y)
{
    nint n = x.Lengths[0], k = x.Lengths[1], m = y.Lengths[1];
    if (y.Lengths[0] != k)
        throw new ArgumentException("MatMul: inner lengths differ");
    Tensor<float> c = Tensor.CreateFromShape<float>([n, m]);
    ReadOnlyTensorSpan<float> xs = x, ys = y;
    for (nint i = 0; i < n; i++)
    {
        // Row i and column j as tensors of rank 1: views, not copies.
        var xi = xs.Slice([new NRange(i, i + 1), NRange.All]);
        ReadOnlyTensorSpan<float> row = Tensor.Reshape(xi, [k]);
        for (nint j = 0; j < m; j++)
        {
            var yj = ys.Slice([NRange.All, new NRange(j, j + 1)]);
            ReadOnlyTensorSpan<float> column = Tensor.Reshape(yj, [k]);
            c[i, j] = Tensor.Dot(row, column);
        }
    }
    return c;
}
// docs:end
