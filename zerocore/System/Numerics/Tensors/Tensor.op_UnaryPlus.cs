// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Numerics.Tensors
{
    public static partial class Tensor
    {
        extension(ReadOnlyTensorSpan<float>)
        {
            public static ReadOnlyTensorSpan<float> operator +(in ReadOnlyTensorSpan<float> tensor) => tensor;
        }

        extension(Tensor<float>)
        {
            public static Tensor<float> operator +(Tensor<float> tensor) => tensor;
        }

        extension(TensorSpan<float>)
        {
            public static TensorSpan<float> operator +(in TensorSpan<float> tensor) => tensor;
        }
    }
}
