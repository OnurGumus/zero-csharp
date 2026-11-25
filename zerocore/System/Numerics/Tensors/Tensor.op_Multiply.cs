// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Numerics.Tensors
{
    public static partial class Tensor
    {
        public static Tensor<float> Multiply(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.Multiply, float, float>(x, y, destination);
            return destination;
        }

        public static Tensor<float> Multiply(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShape<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Multiply, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Multiply(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.Multiply, float, float>(x, y, destination);
            return ref destination;
        }

        public static ref readonly TensorSpan<float> Multiply(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Multiply, float, float>(x, y, destination);
            return ref destination;
        }

        extension(ReadOnlyTensorSpan<float>)
        {
            public static Tensor<float> operator *(in ReadOnlyTensorSpan<float> left, in ReadOnlyTensorSpan<float> right) => Multiply(left, right);

            public static Tensor<float> operator *(in ReadOnlyTensorSpan<float> left, float right) => Multiply(left, right);

            public static Tensor<float> operator *(float left, in ReadOnlyTensorSpan<float> right) => Multiply(right, left);
        }

        extension(Tensor<float> tensor)
        {
            public static Tensor<float> operator *(Tensor<float> left, Tensor<float> right) => Multiply(left, right);

            public static Tensor<float> operator *(Tensor<float> left, float right) => Multiply(left, right);

            public static Tensor<float> operator *(float left, Tensor<float> right) => Multiply(right, left);

            public void operator *=(in ReadOnlyTensorSpan<float> other) => Multiply(tensor, other, tensor);

            public void operator *=(float other) => Multiply(tensor, other, tensor);
        }

        extension(ref TensorSpan<float> tensor)
        {
            public static Tensor<float> operator *(in TensorSpan<float> left, in TensorSpan<float> right) => Multiply(left, right);

            public static Tensor<float> operator *(in TensorSpan<float> left, float right) => Multiply(left, right);

            public static Tensor<float> operator *(float left, in TensorSpan<float> right) => Multiply(right, left);

            public void operator *=(in ReadOnlyTensorSpan<float> other) => Multiply(tensor, other, tensor);

            public void operator *=(float other) => Multiply(tensor, other, tensor);
        }
    }
}
