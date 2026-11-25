// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace System.Numerics.Tensors
{
    public interface ITensor<TSelf, T> : ITensor, IReadOnlyTensor<TSelf, T>
        where TSelf : ITensor<TSelf, T>, allows ref struct
    {
        // TODO: Determine if we can implement `IEqualityOperators<TSelf, T, bool>`.
        // It looks like C#/.NET currently hits limitations here as it believes TSelf and T could be the same type
        // Ideally we could annotate it such that they cannot be the same type and no conflicts would exist

        static abstract TSelf CreateFromShape(scoped ReadOnlySpan<nint> lengths, bool pinned = false);

        static abstract TSelf CreateFromShape(scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned = false);

        static abstract TSelf CreateFromShapeUninitialized(scoped ReadOnlySpan<nint> lengths, bool pinned = false);

        static abstract TSelf CreateFromShapeUninitialized(scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned = false);

        new ref T this[params scoped ReadOnlySpan<nint> indexes] { get; }

        new ref T this[params scoped ReadOnlySpan<NIndex> indexes] { get; }

        new TSelf this[params scoped ReadOnlySpan<NRange> ranges] { get; set; }

        TensorSpan<T> AsTensorSpan();

        TensorSpan<T> AsTensorSpan(params scoped ReadOnlySpan<nint> startIndexes);

        TensorSpan<T> AsTensorSpan(params scoped ReadOnlySpan<NIndex> startIndexes);

        TensorSpan<T> AsTensorSpan(params scoped ReadOnlySpan<NRange> ranges);

        void Fill(T value);

        new TensorDimensionSpan<T> GetDimensionSpan(int dimension);

        new ref T GetPinnableReference();

        new Span<T> GetSpan(scoped ReadOnlySpan<nint> startIndexes, int length);

        new Span<T> GetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length);

        bool TryGetSpan(scoped ReadOnlySpan<nint> startIndexes, int length, out Span<T> span);

        bool TryGetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length, out Span<T> span);
    }
}
