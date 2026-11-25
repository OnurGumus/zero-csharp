// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace System.Numerics.Tensors
{
    public interface IReadOnlyTensor<TSelf, T> : IReadOnlyTensor
        where TSelf : IReadOnlyTensor<TSelf, T>, allows ref struct
    {
        static abstract TSelf Empty { get; }

        new ref readonly T this[params scoped ReadOnlySpan<nint> indexes] { get; }

        new ref readonly T this[params scoped ReadOnlySpan<NIndex> indexes] { get; }

        TSelf this[params scoped ReadOnlySpan<NRange> ranges] { get; }

        ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan();

        ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan(params scoped ReadOnlySpan<nint> startIndexes);

        ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan(params scoped ReadOnlySpan<NIndex> startIndexes);

        ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan(params scoped ReadOnlySpan<NRange> ranges);

        void CopyTo(scoped in TensorSpan<T> destination);

        void FlattenTo(scoped Span<T> destination);

        ReadOnlyTensorDimensionSpan<T> GetDimensionSpan(int dimension);

        ref readonly T GetPinnableReference();

        ReadOnlySpan<T> GetSpan(scoped ReadOnlySpan<nint> startIndexes, int length);

        ReadOnlySpan<T> GetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length);

        TSelf Slice(params scoped ReadOnlySpan<nint> startIndexes);

        TSelf Slice(params scoped ReadOnlySpan<NIndex> startIndexes);

        TSelf Slice(params scoped ReadOnlySpan<NRange> ranges);

        TSelf ToDenseTensor();

        bool TryCopyTo(scoped in TensorSpan<T> destination);

        bool TryFlattenTo(scoped Span<T> destination);

        bool TryGetSpan(scoped ReadOnlySpan<nint> startIndexes, int length, out ReadOnlySpan<T> span);

        bool TryGetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length, out ReadOnlySpan<T> span);
    }
}
