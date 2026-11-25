// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Numerics.Tensors
{
    public static partial class Tensor
    {
        public static Tensor<float> Negate(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Negate, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Negate(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Negate, float, float>(x, destination);
            return ref destination;
        }

        extension(ReadOnlyTensorSpan<float>)
        {
            public static Tensor<float> operator -(in ReadOnlyTensorSpan<float> tensor) => Negate(tensor);
        }

        extension(Tensor<float>)
        {
            public static Tensor<float> operator -(Tensor<float> tensor) => Negate(tensor);
        }

        extension(TensorSpan<float>)
        {
            public static Tensor<float> operator -(in TensorSpan<float> tensor) => Negate(tensor);
        }
    }
}
