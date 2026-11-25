// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Numerics.Tensors
{
    public static partial class Tensor
    {
        public static Tensor<float> Decrement(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Decrement, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Decrement(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Decrement, float, float>(x, destination);
            return ref destination;
        }

        extension(Tensor<float> tensor)
        {
            public void operator --() => Decrement(tensor, tensor);
        }

        extension(ref TensorSpan<float> tensor)
        {
            public void operator --() => Decrement(tensor, tensor);
        }
    }
}
