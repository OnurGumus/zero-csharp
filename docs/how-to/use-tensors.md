---
title: Work with tensors and matrices
index: 26
---

# Work with tensors and matrices

`Tensor<T>`, `TensorSpan<T>` and the `Tensor` class are .NET's n-dimensional arrays (the
System.Numerics.Tensors package, version 11), in zerocore: lengths and strides, views that share
memory (slices, transposes, broadcasts), and element-wise math that broadcasts. A matrix is a tensor
of rank 2. The names, overloads, results and error messages are .NET's, so code written for .NET
compiles unchanged; the differences are [at the end](#what-differs-from.net).

The examples come from `samples/TensorTypes.cs`, which runs on every target: Linux, macOS, Windows,
WebAssembly, DOS, UEFI and the microcontrollers.

## Make a tensor

`Tensor.Create` wraps an array in the lengths you give it, without copying it. The last index moves
fastest (row-major), and the indexer takes one index per dimension:

<!-- sample: samples/TensorTypes.cs create -->
```csharp
Tensor<float> a = Tensor.Create(new float[] { 1, 2, 3, 4, 5, 6 }, [2, 3]);
Console.WriteLine(a.ToString([2, 3]));
Console.WriteLine("a[1, 2] = " + a[1, 2].ToString());
```

`ToString` takes the most elements to print per dimension. `Tensor.CreateFromShape<float>([2, 3])`
makes a tensor of zeros instead.

## Broadcast

Element-wise operations line up the two shapes from the last dimension; a dimension of length 1, or
a missing one, repeats to match the other. So a row adds to every row of a matrix:

<!-- sample: samples/TensorTypes.cs broadcast -->
```csharp
Tensor<float> row = Tensor.Create(new float[] { 10, 20, 30 }, [3]);
Tensor<float> sum = a + row; // [3] broadcasts over both rows of [2, 3]
Console.WriteLine(sum.ToString([2, 3]));
Tensor<float> scaled = Tensor.Multiply(a, 0.5f);
Console.WriteLine(scaled.ToString([2, 3]));
```

The operators `+`, `-`, `*` and `/` (and `+=` and the others) work on `Tensor<float>` and
`TensorSpan<float>`, with a tensor or a `float` on either side.

## Take views

`Slice` takes an `NRange` per dimension (`1..2`, or `NRange.All`) and returns a view: the same memory,
with other lengths and strides. Writing through it changes the tensor. `Transpose` and
`PermuteDimensions` are views too, and so is `Reshape` of a dense tensor (it refuses a slice whose
rows aren't next to each other):

<!-- sample: samples/TensorTypes.cs views -->
```csharp
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
```

A view whose elements aren't one block of memory has `IsDense` false. Every operation takes such
tensors: it works on each contiguous run (a row, say) at once and steps through the strides
between them.

## Multiply matrices

.NET's tensor API has no matrix product, but `Tensor.Dot` of a row and a column is one element of it,
and rows and columns are views:

<!-- sample: samples/TensorTypes.cs matmul -->
```csharp
// A matrix product from rows and columns:
// c[i, j] = Dot(row i of x, column j of y).
Tensor<float> x = Tensor.Create(new float[] { 1, 2, 3, 4, 5, 6 }, [2, 3]);
Tensor<float> y = Tensor.Create(new float[] { 1, 0, 0, 1, 2, 2 }, [3, 2]);
Tensor<float> c = MatMul(x, y);
Console.WriteLine(c.ToString([2, 2]));
```

<!-- sample: samples/TensorTypes.cs matmul-function -->
```csharp
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
```

For large matrices, multiply on the GPU instead: `GpuBuffer<T>.AsTensorSpan` makes a GPU buffer a
tensor, so the same code fills and checks the matrices the GPU multiplies
([Run code on the GPU](run-code-on-the-gpu.html#matrices-as-tensors)).

## Work in place

Each operation has a form that returns a new tensor and one that writes to a destination
`TensorSpan<T>`. The destination may be the input, and that form allocates nothing:

<!-- sample: samples/TensorTypes.cs softmax -->
```csharp
// SoftMax of each row, written over the row (a destination: no allocation).
Tensor<float> logits = Tensor.Create(new float[] { 1, 2, 3, 0, 0, 0 }, [2, 3]);
TensorDimensionSpan<float> rows = logits.AsTensorSpan().GetDimensionSpan(0);
for (int i = 0; i < rows.Length; i++)
{
    TensorSpan<float> r = rows[i];
    Tensor.SoftMax(r, r);
}
```

`FilteredUpdate` writes a value wherever a mask (a `Tensor<bool>` from `LessThan`, `GreaterThan`
and the like) is true:

<!-- sample: samples/TensorTypes.cs relu -->
```csharp
// ReLU in place: everything below 0 becomes 0.
float[] values = { -2, -0.5f, 0, 0.5f, 2, -1 };
Tensor<float> h = Tensor.Create(values, [2, 3]);
Tensor.FilteredUpdate<float>(h, Tensor.LessThan<float>(h, 0f), 0f);
Console.WriteLine(h.ToString([2, 3]));
```

A `TensorSpan<T>` can also cover memory you have, like a span from `stackalloc` or an arena:
`new TensorSpan<float>(span, [rows, columns])`.

## Combine and reduce

<!-- sample: samples/TensorTypes.cs stack -->
```csharp
Tensor<float> stacked = Tensor.Stack<float>([row, row]);
Console.WriteLine(stacked.ToString([2, 3]));
Tensor<float> joined = Tensor.Concatenate<float>([a, stacked]);
Console.WriteLine(joined.ToString([4, 3]));
float max = Tensor.Max<float>(joined);
nint at = Tensor.IndexOfMax<float>(joined);
float mean = Tensor.Average<float>(joined);
Console.WriteLine("max " + max.ToString() + " at " + at.ToString()
    + ", mean " + mean.ToString());
```

The reductions (`Sum`, `Average`, `Max`, `IndexOfMax`, `Norm`, `StdDev`, `Dot`, ...) take the whole
tensor; to reduce along one dimension, loop over `GetDimensionSpan`, as the SoftMax example does.

## Free the temporaries

A `Tensor<T>` is an object holding an array, and zero mode never frees those by itself. Tensors
made once (weights, a model) can simply stay. For tensors made over and over (each step's
intermediate results), open an arena's object scope, which frees everything created inside when the
block ends:

<!-- sample: samples/TensorTypes.cs arena -->
```csharp
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
```

Or use the destination forms, which allocate nothing.

## What differs from .NET

- **The math is for `float`.** `Tensor<T>`, `TensorSpan<T>` and the shape operations (`Slice`,
  `Reshape`, `Transpose`, `PermuteDimensions`, `Broadcast`, `Concatenate`, `Stack`, `Split`,
  `Squeeze`, `Unsqueeze`, `Reverse`, `Resize`, `SetSlice`, `FilteredUpdate`, `SequenceEqual`) take
  any `T`. The math (`Add`, `Exp`, `Sum`, the comparisons, the operators) is written for `float`, as
  zerocore's `TensorPrimitives` is. Each has a generic form with .NET's signature, so
  `Tensor.Sum<float>(t)` compiles; any other `T` is refused when the program is built (error
  CS0315: no conversion from `double` to `IFloatTensorElement`, an interface only `float` has),
  as are the operators on a `Tensor<double>`.
- **`Tensor.Sum(t)` works without `<float>`.** .NET needs the type argument, since C# can't infer
  `T` through the conversion from `Tensor<T>` to `ReadOnlyTensorSpan<T>`; zerocore's float methods
  take the conversion directly.
- **Not there:** the integer operations (`BitwiseAnd`, `ShiftLeft`, `PopCount`, ...), the
  conversions (`ConvertChecked` and the others), `FillGaussianNormalDistribution` and
  `FillUniformDistribution` (zerocore has no `Random`), `Round` with digits or a `MidpointRounding`,
  the constructors from multi-dimensional arrays, and `GetPinnedHandle`. Nothing moves in zero
  mode, so every tensor is effectively pinned.
- **The functions are float's own.** `Sin`, `Log`, `CosPi` and the rest give what `MathF` and
  .NET's `float.SinPi`-style methods give, not .NET's vectorized `TensorPrimitives` code, which is
  up to a few ulps off; `Exp`, `Sigmoid`, `Tanh` and `SoftMax` are vectorized, as in
  [TensorPrimitives](../concepts/limits.html#tensorprimitives). `ExpM1`, `Exp2M1` and `Exp10M1`
  work in double, where .NET's `float.ExpM1` (`Exp(x) - 1`) loses digits near 0.
- **`ToString` names the element type** for the primitive types only (`Tensor<Single>`); zerocore's
  `Type` has no names, so other types show `T`.
- **Errors end the program**, with .NET's exception and message, as every `throw` does in zero mode.

`scripts/tensor-type-fuzz` runs zerocore's tensors and .NET's package side by side on random
tensors (dense, sliced, permuted, broadcast) and compares every result and every error.
