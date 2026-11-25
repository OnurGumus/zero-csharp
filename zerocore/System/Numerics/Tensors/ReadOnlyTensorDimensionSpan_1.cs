// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Collections;
using System.Collections.Generic;

namespace System.Numerics.Tensors
{
    public readonly ref struct ReadOnlyTensorDimensionSpan<T>
    {
        private readonly ReadOnlyTensorSpan<T> _tensor;
        private readonly nint _length;
        private readonly int _dimension;
        private readonly TensorShape _sliceShape;

        internal ReadOnlyTensorDimensionSpan(ReadOnlyTensorSpan<T> tensor, int dimension)
        {
            int rank = tensor.Rank;
            if (rank == 0)
            {
                tensor = new ReadOnlyTensorSpan<T>(in tensor._reference, TensorShape.Normalize(tensor._shape));
                rank = tensor.Rank;
            }

            if ((uint)dimension >= rank)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException();
            }
            dimension += 1;

            ReadOnlySpan<nint> lengths = tensor.Lengths;
            _tensor = tensor;
            _length = TensorShape.GetProduct(lengths[..dimension]);
            _dimension = dimension;
            _sliceShape = TensorShape.Create((dimension != rank) ? lengths[dimension..] : [1], tensor.Strides[dimension..], tensor.IsPinned);
        }

        public bool IsDense => _sliceShape.IsDense;

        public nint Length => _length;

        public ReadOnlyTensorSpan<T> this[nint index]
        {
            get
            {
                if ((nuint)index >= (nuint)_length)
                {
                    ThrowHelper.ThrowArgumentOutOfRangeException();
                }

                nint linearOffset = _tensor._shape.GetLinearOffsetForDimension(index, _dimension);
                return new ReadOnlyTensorSpan<T>(ref Unsafe.Add(ref _tensor._reference, linearOffset), _sliceShape);
            }
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        public ref struct Enumerator : IEnumerator<ReadOnlyTensorSpan<T>>
        {
            private readonly ReadOnlyTensorDimensionSpan<T> _span;
            private nint _index;

            internal Enumerator(ReadOnlyTensorDimensionSpan<T> span)
            {
                _span = span;
                _index = -1;
            }

            public readonly ReadOnlyTensorSpan<T> Current => _span[_index];

            public bool MoveNext()
            {
                nint index = _index + 1;

                if (index < _span.Length)
                {
                    _index = index;
                    return true;
                }
                return false;
            }

            public void Reset()
            {
                _index = -1;
            }

            //
            // IDisposable
            //

            void IDisposable.Dispose() { }

            //
            // IEnumerator
            //

            readonly object? IEnumerator.Current => throw new NotSupportedException();
        }
    }
}
