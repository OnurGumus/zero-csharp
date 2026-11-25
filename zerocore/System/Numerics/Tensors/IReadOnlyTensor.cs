// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace System.Numerics.Tensors
{
    public interface IReadOnlyTensor
    {
        object? this[params scoped ReadOnlySpan<nint> indexes] { get; }

        object? this[params scoped ReadOnlySpan<NIndex> indexes] { get; }

        nint FlattenedLength { get; }

        bool HasAnyDenseDimensions { get; }

        bool IsDense { get; }

        bool IsEmpty { get; }

        bool IsPinned { get; }

        [UnscopedRef]
        ReadOnlySpan<nint> Lengths { get; }

        int Rank { get; }

        [UnscopedRef]
        ReadOnlySpan<nint> Strides { get; }
    }
}
