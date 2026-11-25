// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace System.Numerics.Tensors
{
    public interface ITensor : IReadOnlyTensor
    {
        new object? this[params scoped ReadOnlySpan<nint> indexes] { get; set; }

        new object? this[params scoped ReadOnlySpan<NIndex> indexes] { get; set; }

        bool IsReadOnly { get; }

        void Clear();

        void Fill(object value);
    }
}
