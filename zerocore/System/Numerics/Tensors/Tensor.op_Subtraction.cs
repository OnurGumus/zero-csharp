// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Numerics.Tensors
{
    public static partial class Tensor
    {
        public static Tensor<float> Subtract(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.Subtract, float, float>(x, y, destination);
            return destination;
        }

        public static Tensor<float> Subtract(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Subtract, float, float>(x, y, destination);
            return destination;
        }

        public static Tensor<float> Subtract(float x, in ReadOnlyTensorSpan<float> y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(y.Lengths);
            TensorOperation.Invoke<TensorOperation.Subtract, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Subtract(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.Subtract, float, float>(x, y, destination);
            return ref destination;
        }

        public static ref readonly TensorSpan<float> Subtract(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Subtract, float, float>(x, y, destination);
            return ref destination;
        }

        public static ref readonly TensorSpan<float> Subtract(float x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(y, destination);
            TensorOperation.Invoke<TensorOperation.Subtract, float, float>(x, y, destination);
            return ref destination;
        }

        extension(ReadOnlyTensorSpan<float>)
        {
            public static Tensor<float> operator -(in ReadOnlyTensorSpan<float> left, in ReadOnlyTensorSpan<float> right) => Subtract(left, right);

            public static Tensor<float> operator -(in ReadOnlyTensorSpan<float> left, float right) => Subtract(left, right);

            public static Tensor<float> operator -(float left, in ReadOnlyTensorSpan<float> right) => Subtract(left, right);
        }

        extension(Tensor<float> tensor)
        {
            public static Tensor<float> operator -(Tensor<float> left, Tensor<float> right) => Subtract(left, right);

            public static Tensor<float> operator -(Tensor<float> left, float right) => Subtract(left, right);

            public static Tensor<float> operator -(float left, Tensor<float> right) => Subtract(left, right);

            public void operator -=(in ReadOnlyTensorSpan<float> other) => Subtract(tensor, other, tensor);

            public void operator -=(float other) => Subtract(tensor, other, tensor);
        }

        extension(ref TensorSpan<float> tensor)
        {
            public static Tensor<float> operator -(in TensorSpan<float> left, in TensorSpan<float> right) => Subtract(left, right);

            public static Tensor<float> operator -(in TensorSpan<float> left, float right) => Subtract(left, right);

            public static Tensor<float> operator -(float left, in TensorSpan<float> right) => Subtract(left, right);

            public void operator -=(in ReadOnlyTensorSpan<float> other) => Subtract(tensor, other, tensor);

            public void operator -=(float other) => Subtract(tensor, other, tensor);
        }
    }
}
