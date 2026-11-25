// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#pragma warning disable CS0809 // Obsolete member overrides non-obsolete member

namespace System.Numerics.Tensors
{
    public readonly ref struct ReadOnlyTensorSpan<T> : IReadOnlyTensor<ReadOnlyTensorSpan<T>, T>
    {
        public static ReadOnlyTensorSpan<T> Empty => default;

        internal readonly TensorShape _shape;
        internal readonly ref T _reference;

        public ReadOnlyTensorSpan(T[]? array)
        {
            _shape = TensorShape.Create(array);
            _reference = ref (array is not null)
                       ? ref MemoryMarshal.GetArrayDataReference(array)
                       : ref Unsafe.NullRef<T>();
        }

        public ReadOnlyTensorSpan(T[]? array, scoped ReadOnlySpan<nint> lengths)
            : this(array, lengths, strides: []) { }

        public ReadOnlyTensorSpan(T[]? array, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
        {
            _shape = TensorShape.Create(array, lengths, strides);
            _reference = ref (array is not null)
                       ? ref MemoryMarshal.GetArrayDataReference(array)
                       : ref Unsafe.NullRef<T>();
        }

        public ReadOnlyTensorSpan(T[]? array, int start, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
        {
            _shape = TensorShape.Create(array, start, lengths, strides);
            _reference = ref (array is not null)
                       ? ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), (nint)(uint)start)
                       : ref Unsafe.NullRef<T>();
        }

        public ReadOnlyTensorSpan(ReadOnlySpan<T> span)
        {
            ref T reference = ref MemoryMarshal.GetReference(span);
            _shape = TensorShape.Create(ref reference, span.Length, pinned: false);
            _reference = ref reference;
        }

        public ReadOnlyTensorSpan(ReadOnlySpan<T> span, scoped ReadOnlySpan<nint> lengths)
            : this(span, lengths, strides: []) { }

        public ReadOnlyTensorSpan(ReadOnlySpan<T> span, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
        {
            ref T reference = ref MemoryMarshal.GetReference(span);
            _shape = TensorShape.Create(ref reference, span.Length, lengths, strides, pinned: false);
            _reference = ref reference;
        }


        public unsafe ReadOnlyTensorSpan(T* data, nint dataLength)
        {
            _shape = TensorShape.Create(data, dataLength);
            _reference = ref Unsafe.AsRef<T>(data);
        }

        public unsafe ReadOnlyTensorSpan(T* data, nint dataLength, scoped ReadOnlySpan<nint> lengths)
            : this(data, dataLength, lengths, strides: []) { }

        public unsafe ReadOnlyTensorSpan(T* data, nint dataLength, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
        {
            _shape = TensorShape.Create(data, dataLength, lengths, strides);
            _reference = ref Unsafe.AsRef<T>(data);
        }

        internal ReadOnlyTensorSpan(ref readonly T data, nint dataLength, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned)
        {
            _shape = TensorShape.CreateForView(dataLength, lengths, strides, pinned);
            _reference = ref Unsafe.AsRef(in data);
        }

        internal ReadOnlyTensorSpan(ref readonly T reference, scoped in TensorShape shape)
        {
            _reference = ref Unsafe.AsRef(in reference);
            _shape = shape;
        }

        public ref readonly T this[params scoped ReadOnlySpan<nint> indexes]
        {
            get => ref Unsafe.Add(ref _reference, _shape.GetLinearOffset<TensorShape.GetOffsetAndLengthForNInt, nint>(indexes));
        }

        public ref readonly T this[params scoped ReadOnlySpan<NIndex> indexes]
        {
            get => ref Unsafe.Add(ref _reference, _shape.GetLinearOffset<TensorShape.GetOffsetAndLengthForNIndex, NIndex>(indexes));
        }

        public ReadOnlyTensorSpan<T> this[params scoped ReadOnlySpan<NRange> ranges]
        {
            get => Slice(ranges);
        }

        public nint FlattenedLength => _shape.FlattenedLength;

        public bool HasAnyDenseDimensions => _shape.HasAnyDenseDimensions;

        public bool IsDense => _shape.IsDense;

        public bool IsEmpty => _shape.IsEmpty;

        public bool IsPinned => _shape.IsPinned;

        [UnscopedRef]
        public ReadOnlySpan<nint> Lengths => _shape.Lengths;

        public int Rank => Lengths.Length;

        [UnscopedRef]
        public ReadOnlySpan<nint> Strides => _shape.Strides;

        public static bool operator ==(in ReadOnlyTensorSpan<T> left, in ReadOnlyTensorSpan<T> right)
            => Unsafe.AreSame(ref left._reference, ref right._reference)
            && left._shape == right._shape;

        public static bool operator !=(in ReadOnlyTensorSpan<T> left, in ReadOnlyTensorSpan<T> right) => !(left == right);

        public static implicit operator ReadOnlyTensorSpan<T>(T[]? array) => new ReadOnlyTensorSpan<T>(array);

        public static ReadOnlyTensorSpan<T> CastUp<TDerived>(in ReadOnlyTensorSpan<TDerived> items)
            where TDerived : class?, T
        {
            return new ReadOnlyTensorSpan<T>(
                ref Unsafe.As<TDerived, T>(ref items._reference),
                items._shape
            );
        }

        public void CopyTo(scoped in TensorSpan<T> destination)
        {
            if (!TryCopyTo(destination))
            {
                ThrowHelper.ThrowArgument_DestinationTooShort();
            }
        }

        [Obsolete("Equals() on ReadOnlyTensorSpan will always throw an exception. Use the equality operator instead.")]
        public override bool Equals(object? obj) =>
            throw new NotSupportedException(SR.NotSupported_CannotCallEqualsOnSpan);

        public void FlattenTo(scoped Span<T> destination)
        {
            if (!TryFlattenTo(destination))
            {
                ThrowHelper.ThrowArgument_DestinationTooShort();
            }
        }

        public ReadOnlyTensorDimensionSpan<T> GetDimensionSpan(int dimension) => new ReadOnlyTensorDimensionSpan<T>(this, dimension);

        public Enumerator GetEnumerator() => new Enumerator(this);

        [Obsolete("GetHashCode() on ReadOnlyTensorSpan will always throw an exception.")]
        public override int GetHashCode() =>
            throw new NotSupportedException(SR.NotSupported_CannotCallGetHashCodeOnSpan);

        public ref readonly T GetPinnableReference()
        {
            // Ensure that the native code has just one forward branch that is predicted-not-taken.
            ref T ret = ref Unsafe.NullRef<T>();
            if (_shape.FlattenedLength != 0) ret = ref _reference;
            return ref ret;
        }

        public ReadOnlySpan<T> GetSpan(scoped ReadOnlySpan<nint> startIndexes, int length)
        {
            if (!TryGetSpan(startIndexes, length, out ReadOnlySpan<T> span))
            {
                ThrowHelper.ThrowArgumentOutOfRangeException();
            }
            return span;
        }

        public ReadOnlySpan<T> GetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length)
        {
            if (!TryGetSpan(startIndexes, length, out ReadOnlySpan<T> span))
            {
                ThrowHelper.ThrowArgumentOutOfRangeException();
            }
            return span;
        }

        public ReadOnlyTensorSpan<T> Slice(params scoped ReadOnlySpan<nint> startIndexes)
        {
            TensorShape shape = _shape.Slice<TensorShape.GetOffsetAndLengthForSlice, nint>(startIndexes, out nint linearOffset);
            return new ReadOnlyTensorSpan<T>(
                ref Unsafe.Add(ref _reference, linearOffset),
                shape
            );
        }

        public ReadOnlyTensorSpan<T> Slice(params scoped ReadOnlySpan<NIndex> startIndexes)
        {
            TensorShape shape = _shape.Slice<TensorShape.GetOffsetAndLengthForNIndex, NIndex>(startIndexes, out nint linearOffset);
            return new ReadOnlyTensorSpan<T>(
                ref Unsafe.Add(ref _reference, linearOffset),
                shape
            );
        }

        public ReadOnlyTensorSpan<T> Slice(params scoped ReadOnlySpan<NRange> ranges)
        {
            TensorShape shape = _shape.Slice<TensorShape.GetOffsetAndLengthForNRange, NRange>(ranges, out nint linearOffset);
            return new ReadOnlyTensorSpan<T>(
                ref Unsafe.Add(ref _reference, linearOffset),
                shape
            );
        }

        public override string ToString() => ToString([]);

        public string ToString(params scoped ReadOnlySpan<nint> maximumLengths) => Tensor.ToString(this, maximumLengths, "System.Numerics.Tensors.ReadOnlyTensorSpan");

        public bool TryCopyTo(scoped in TensorSpan<T> destination)
        {
            if ((_shape.FlattenedLength <= destination.FlattenedLength) &&
                TensorShape.AreCompatible(destination._shape, _shape, false))
            {
                TensorOperation.Invoke<TensorOperation.CopyTo<T>, T, T>(this, destination);
                return true;
            }
            return false;
        }

        public bool TryFlattenTo(scoped Span<T> destination)
        {
            if (_shape.FlattenedLength <= destination.Length)
            {
                TensorOperation.Invoke<TensorOperation.CopyTo<T>, T, T>(this, destination);
                return true;
            }
            return false;
        }

        public bool TryGetSpan(scoped ReadOnlySpan<nint> startIndexes, int length, out ReadOnlySpan<T> span)
        {
            // This validates that startIndexes is valid and will throw ArgumentOutOfRangeException or IndexOutOfRangeException if it is not.
            nint longestContiguousLength = _shape.GetLongestContiguousLength<TensorShape.GetOffsetAndLengthForNInt, nint>(startIndexes, out nint linearOffset);

            if ((length < 0) || (length > longestContiguousLength))
            {
                span = default;
                return false;
            }

            span = MemoryMarshal.CreateReadOnlySpan(in Unsafe.Add(ref _reference, linearOffset), length);
            return true;
        }

        public bool TryGetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length, out ReadOnlySpan<T> span)
        {
            // This validates that startIndexes is valid and will throw ArgumentOutOfRangeException or IndexOutOfRangeException if it is not.
            nint longestContiguousLength = _shape.GetLongestContiguousLength<TensorShape.GetOffsetAndLengthForNIndex, NIndex>(startIndexes, out nint linearOffset);

            if ((length < 0) || (length > longestContiguousLength))
            {
                span = default;
                return false;
            }

            span = MemoryMarshal.CreateReadOnlySpan(in Unsafe.Add(ref _reference, linearOffset), length);
            return true;
        }

        //
        // IReadOnlyTensor
        //

        object? IReadOnlyTensor.this[params scoped ReadOnlySpan<NIndex> indexes] => this[indexes];

        object? IReadOnlyTensor.this[params scoped ReadOnlySpan<nint> indexes] => this[indexes];

        //
        // IReadOnlyTensor<TSelf, T>
        //

        ReadOnlyTensorSpan<T> IReadOnlyTensor<ReadOnlyTensorSpan<T>, T>.AsReadOnlyTensorSpan() => this;

        ReadOnlyTensorSpan<T> IReadOnlyTensor<ReadOnlyTensorSpan<T>, T>.AsReadOnlyTensorSpan(params scoped ReadOnlySpan<nint> startIndexes) => Slice(startIndexes);

        ReadOnlyTensorSpan<T> IReadOnlyTensor<ReadOnlyTensorSpan<T>, T>.AsReadOnlyTensorSpan(params scoped ReadOnlySpan<NIndex> startIndexes) => Slice(startIndexes);

        ReadOnlyTensorSpan<T> IReadOnlyTensor<ReadOnlyTensorSpan<T>, T>.AsReadOnlyTensorSpan(params scoped ReadOnlySpan<NRange> ranges) => Slice(ranges);

        ReadOnlyTensorSpan<T> IReadOnlyTensor<ReadOnlyTensorSpan<T>, T>.ToDenseTensor()
        {
            ReadOnlyTensorSpan<T> result = this;

            if (!IsDense)
            {
                Tensor<T> tmp = Tensor.CreateFromShape<T>(Lengths, IsPinned);
                CopyTo(tmp);
                result = tmp;
            }

            return result;
        }

        public ref struct Enumerator : IEnumerator<T>
        {
            private readonly ReadOnlyTensorSpan<T> _span;
            private nint _linearOffset;
            private nint _itemsEnumerated;
            private bool _hasCurrent;

            internal Enumerator(ReadOnlyTensorSpan<T> span)
            {
                _span = span;
                _linearOffset = 0;
                _itemsEnumerated = 0;
                _hasCurrent = false;
            }

            public readonly ref readonly T Current
            {
                get
                {
                    if (!_hasCurrent)
                    {
                        ThrowHelper.ThrowInvalidOperation_EnumerationNotPositioned();
                    }
                    return ref Unsafe.Add(ref _span._reference, _linearOffset);
                }
            }

            public bool MoveNext()
            {
                if (_itemsEnumerated == _span._shape.FlattenedLength)
                {
                    _hasCurrent = false;
                    return false;
                }

                _linearOffset = _span.IsDense
                    ? _itemsEnumerated
                    : _span._shape.GetLinearOffsetForDimension(_itemsEnumerated, _span.Rank);

                _itemsEnumerated++;
                _hasCurrent = true;
                return true;
            }

            public void Reset()
            {
                _linearOffset = 0;
                _itemsEnumerated = 0;
                _hasCurrent = false;
            }

            //
            // IDisposable
            //

            readonly void IDisposable.Dispose() { }

            //
            // IEnumerator
            //

            readonly object? IEnumerator.Current => Current;

            //
            // IEnumerator<T>
            //

            readonly T IEnumerator<T>.Current => Current;
        }
    }
}
