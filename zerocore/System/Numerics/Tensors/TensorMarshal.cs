// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Numerics.Tensors;
using System.Runtime.CompilerServices;

namespace System.Runtime.InteropServices
{
    public static class TensorMarshal
    {
        public static TensorSpan<T> CreateTensorSpan<T>(scoped ref T data, nint dataLength, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned)
        {
            return new TensorSpan<T>(ref Unsafe.AsRef(ref data), dataLength, lengths, strides, pinned);
        }

        public static ReadOnlyTensorSpan<T> CreateReadOnlyTensorSpan<T>(scoped ref readonly T data, nint dataLength, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned)
        {
            return new ReadOnlyTensorSpan<T>(in Unsafe.AsRef(in data), dataLength, lengths, strides, pinned);
        }

        public static ref T GetReference<T>(in TensorSpan<T> tensorSpan)
        {
            return ref tensorSpan._reference;
        }

        public static ref readonly T GetReference<T>(in ReadOnlyTensorSpan<T> tensorSpan)
        {
            return ref tensorSpan._reference;
        }
    }
}
