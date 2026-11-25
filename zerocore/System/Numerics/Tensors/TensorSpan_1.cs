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
    public readonly ref struct TensorSpan<T> : ITensor<TensorSpan<T>, T>
    {
        public static TensorSpan<T> Empty => default;

        internal readonly TensorShape _shape;
        internal readonly ref T _reference;

        public TensorSpan(T[]? array)
        {

            _shape = TensorShape.Create(array);
            _reference = ref (array is not null)
                       ? ref MemoryMarshal.GetArrayDataReference(array)
                       : ref Unsafe.NullRef<T>();
        }

        public TensorSpan(T[]? array, scoped ReadOnlySpan<nint> lengths)
            : this(array, lengths, strides: []) { }

        public TensorSpan(T[]? array, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
        {

            _shape = TensorShape.Create(array, lengths, strides);
            _reference = ref (array is not null)
                       ? ref MemoryMarshal.GetArrayDataReference(array)
                       : ref Unsafe.NullRef<T>();
        }

        public TensorSpan(T[]? array, int start, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
        {

            _shape = TensorShape.Create(array, start, lengths, strides);
            _reference = ref (array is not null)
                       ? ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), (nint)(uint)start)
                       : ref Unsafe.NullRef<T>();
        }

        public TensorSpan(Span<T> span)
        {
            ref T reference = ref MemoryMarshal.GetReference(span);
            _shape = TensorShape.Create(ref reference, span.Length, pinned: false);
            _reference = ref reference;
        }

        public TensorSpan(Span<T> span, scoped ReadOnlySpan<nint> lengths)
            : this(span, lengths, strides: []) { }

        public TensorSpan(Span<T> span, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
        {
            ref T reference = ref MemoryMarshal.GetReference(span);
            _shape = TensorShape.Create(ref reference, span.Length, lengths, strides, pinned: false);
            _reference = ref reference;
        }

        public unsafe TensorSpan(T* data, nint dataLength)
        {
            _shape = TensorShape.Create(data, dataLength);
            _reference = ref Unsafe.AsRef<T>(data);
        }

        public unsafe TensorSpan(T* data, nint dataLength, scoped ReadOnlySpan<nint> lengths)
            : this(data, dataLength, lengths, strides: []) { }

        public unsafe TensorSpan(T* data, nint dataLength, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
        {
            _shape = TensorShape.Create(data, dataLength, lengths, strides);
            _reference = ref Unsafe.AsRef<T>(data);
        }

        internal TensorSpan(ref T data, nint dataLength, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned)
        {
            _shape = TensorShape.CreateForView(dataLength, lengths, strides, pinned);
            _reference = ref data;
        }

        internal TensorSpan(ref T reference, scoped in TensorShape shape)
        {
            _reference = ref reference;
            _shape = shape;
        }

        public ref T this[params scoped ReadOnlySpan<nint> indexes]
        {
            get => ref Unsafe.Add(ref _reference, _shape.GetLinearOffset<TensorShape.GetOffsetAndLengthForNInt, nint>(indexes));
        }

        public ref T this[params scoped ReadOnlySpan<NIndex> indexes]
        {
            get => ref Unsafe.Add(ref _reference, _shape.GetLinearOffset<TensorShape.GetOffsetAndLengthForNIndex, NIndex>(indexes));
        }

        public TensorSpan<T> this[params scoped ReadOnlySpan<NRange> ranges]
        {
            get => Slice(ranges);
            set => value.CopyTo(Slice(ranges));
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

        public static bool operator ==(in TensorSpan<T> left, in TensorSpan<T> right)
            => Unsafe.AreSame(ref left._reference, ref right._reference)
            && left._shape == right._shape;

        public static bool operator !=(in TensorSpan<T> left, in TensorSpan<T> right) => !(left == right);

        public static implicit operator TensorSpan<T>(T[]? array) => new TensorSpan<T>(array);

        public static implicit operator ReadOnlyTensorSpan<T>(scoped in TensorSpan<T> tensor) =>
            new ReadOnlyTensorSpan<T>(ref tensor._reference, tensor._shape);

        public ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan() => new ReadOnlyTensorSpan<T>(ref _reference, in _shape);

        public ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan(params scoped ReadOnlySpan<nint> startIndexes) => AsReadOnlyTensorSpan().Slice(startIndexes);

        public ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan(params scoped ReadOnlySpan<NIndex> startIndexes) => AsReadOnlyTensorSpan().Slice(startIndexes);

        public ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan(params scoped ReadOnlySpan<NRange> ranges) => AsReadOnlyTensorSpan().Slice(ranges);

        public void Clear() => TensorOperation.Invoke<TensorOperation.Clear<T>, T>(this);

        public void CopyTo(scoped in TensorSpan<T> destination)
        {
            if (!TryCopyTo(destination))
            {
                ThrowHelper.ThrowArgument_DestinationTooShort();
            }
        }

        [Obsolete("Equals() on TensorSpan will always throw an exception. Use the equality operator instead.")]
        public override bool Equals(object? obj) =>
            throw new NotSupportedException(SR.NotSupported_CannotCallEqualsOnSpan);

        public void Fill(T value) => TensorOperation.Invoke<TensorOperation.Fill<T>, T, T>(this, value);

        public void FlattenTo(scoped Span<T> destination)
        {
            if (!TryFlattenTo(destination))
            {
                ThrowHelper.ThrowArgument_DestinationTooShort();
            }
        }

        public TensorDimensionSpan<T> GetDimensionSpan(int dimension) => new TensorDimensionSpan<T>(this, dimension);

        public Enumerator GetEnumerator() => new Enumerator(this);

        [Obsolete("GetHashCode() on TensorSpan will always throw an exception.")]
        public override int GetHashCode() =>
            throw new NotSupportedException(SR.NotSupported_CannotCallGetHashCodeOnSpan);

        public ref T GetPinnableReference()
        {
            // Ensure that the native code has just one forward branch that is predicted-not-taken.
            ref T ret = ref Unsafe.NullRef<T>();
            if (_shape.FlattenedLength != 0) ret = ref _reference;
            return ref ret;
        }

        public Span<T> GetSpan(scoped ReadOnlySpan<nint> startIndexes, int length)
        {
            if (!TryGetSpan(startIndexes, length, out Span<T> span))
            {
                ThrowHelper.ThrowArgumentOutOfRangeException();
            }
            return span;
        }

        public Span<T> GetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length)
        {
            if (!TryGetSpan(startIndexes, length, out Span<T> span))
            {
                ThrowHelper.ThrowArgumentOutOfRangeException();
            }
            return span;
        }

        public TensorSpan<T> Slice(params scoped ReadOnlySpan<nint> startIndexes)
        {
            TensorShape shape = _shape.Slice<TensorShape.GetOffsetAndLengthForSlice, nint>(startIndexes, out nint linearOffset);
            return new TensorSpan<T>(
                ref Unsafe.Add(ref _reference, linearOffset),
                shape
            );
        }

        public TensorSpan<T> Slice(params scoped ReadOnlySpan<NIndex> startIndexes)
        {
            TensorShape shape = _shape.Slice<TensorShape.GetOffsetAndLengthForNIndex, NIndex>(startIndexes, out nint linearOffset);
            return new TensorSpan<T>(
                ref Unsafe.Add(ref _reference, linearOffset),
                shape
            );
        }

        public TensorSpan<T> Slice(params scoped ReadOnlySpan<NRange> ranges)
        {
            TensorShape shape = _shape.Slice<TensorShape.GetOffsetAndLengthForNRange, NRange>(ranges, out nint linearOffset);
            return new TensorSpan<T>(
                ref Unsafe.Add(ref _reference, linearOffset),
                shape
            );
        }

        public override string ToString() => ToString([]);

        public string ToString(params scoped ReadOnlySpan<nint> maximumLengths) => Tensor.ToString(AsReadOnlyTensorSpan(), maximumLengths, "System.Numerics.Tensors.TensorSpan");

        public bool TryCopyTo(scoped in TensorSpan<T> destination) => AsReadOnlyTensorSpan().TryCopyTo(destination);

        public bool TryFlattenTo(scoped Span<T> destination) => AsReadOnlyTensorSpan().TryFlattenTo(destination);

        public bool TryGetSpan(scoped ReadOnlySpan<nint> startIndexes, int length, out Span<T> span)
        {
            // This validates that startIndexes is valid and will throw ArgumentOutOfRangeException or IndexOutOfRangeException if it is not.
            nint longestContiguousLength = _shape.GetLongestContiguousLength<TensorShape.GetOffsetAndLengthForNInt, nint>(startIndexes, out nint linearOffset);

            if ((length < 0) || (length > longestContiguousLength))
            {
                span = default;
                return false;
            }

            span = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _reference, linearOffset), length);
            return true;
        }

        public bool TryGetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length, out Span<T> span)
        {
            // This validates that startIndexes is valid and will throw ArgumentOutOfRangeException or IndexOutOfRangeException if it is not.
            nint longestContiguousLength = _shape.GetLongestContiguousLength<TensorShape.GetOffsetAndLengthForNIndex, NIndex>(startIndexes, out nint linearOffset);

            if ((length < 0) || (length > longestContiguousLength))
            {
                span = default;
                return false;
            }

            span = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _reference, linearOffset), length);
            return true;
        }

        public bool TryGetSpan(scoped ReadOnlySpan<nint> startIndexes, int length, out ReadOnlySpan<T> span) => AsReadOnlyTensorSpan().TryGetSpan(startIndexes, length, out span);

        public bool TryGetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length, out ReadOnlySpan<T> span) => AsReadOnlyTensorSpan().TryGetSpan(startIndexes, length, out span);

        //
        // IReadOnlyTensor
        //

        object? IReadOnlyTensor.this[params scoped ReadOnlySpan<NIndex> indexes] => this[indexes];

        object? IReadOnlyTensor.this[params scoped ReadOnlySpan<nint> indexes] => this[indexes];

        //
        // IReadOnlyTensor<TSelf, T>
        //

        ref readonly T IReadOnlyTensor<TensorSpan<T>, T>.this[params scoped ReadOnlySpan<NIndex> indexes] => ref this[indexes];

        ref readonly T IReadOnlyTensor<TensorSpan<T>, T>.this[params scoped ReadOnlySpan<nint> indexes] => ref this[indexes];

        ReadOnlyTensorDimensionSpan<T> IReadOnlyTensor<TensorSpan<T>, T>.GetDimensionSpan(int dimension) => GetDimensionSpan(dimension);

        ref readonly T IReadOnlyTensor<TensorSpan<T>, T>.GetPinnableReference() => ref GetPinnableReference();

        ReadOnlySpan<T> IReadOnlyTensor<TensorSpan<T>, T>.GetSpan(scoped ReadOnlySpan<nint> startIndexes, int length) => AsReadOnlyTensorSpan().GetSpan(startIndexes, length);

        ReadOnlySpan<T> IReadOnlyTensor<TensorSpan<T>, T>.GetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length) => AsReadOnlyTensorSpan().GetSpan(startIndexes, length);

        TensorSpan<T> IReadOnlyTensor<TensorSpan<T>, T>.ToDenseTensor()
        {
            TensorSpan<T> result = this;

            if (!IsDense)
            {
                result = Tensor.CreateFromShape<T>(Lengths, IsPinned);
                CopyTo(result);
            }

            return result;
        }

        //
        // ITensor
        //

        bool ITensor.IsReadOnly => false;

        object? ITensor.this[params scoped ReadOnlySpan<NIndex> indexes]
        {
            get => this[indexes];

            set
            {
                this[indexes] = (T)value!;
            }
        }

        object? ITensor.this[params scoped ReadOnlySpan<nint> indexes]
        {
            get => this[indexes];

            set
            {
                this[indexes] = (T)value!;
            }
        }

        void ITensor.Fill(object value) => Fill(value is T t ? t : throw new ArgumentException($"Cannot convert {value} to {typeof(T)}"));

        //
        // ITensor<TSelf, T>
        //

        static TensorSpan<T> ITensor<TensorSpan<T>, T>.CreateFromShape(scoped ReadOnlySpan<nint> lengths, bool pinned) => Tensor.CreateFromShape<T>(lengths, pinned);

        static TensorSpan<T> ITensor<TensorSpan<T>, T>.CreateFromShape(scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned) => Tensor.CreateFromShape<T>(lengths, strides, pinned);

        static TensorSpan<T> ITensor<TensorSpan<T>, T>.CreateFromShapeUninitialized(scoped ReadOnlySpan<nint> lengths, bool pinned) => Tensor.CreateFromShapeUninitialized<T>(lengths, pinned);

        static TensorSpan<T> ITensor<TensorSpan<T>, T>.CreateFromShapeUninitialized(scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned) => Tensor.CreateFromShapeUninitialized<T>(lengths, strides, pinned);

        TensorSpan<T> ITensor<TensorSpan<T>, T>.AsTensorSpan() => this;

        TensorSpan<T> ITensor<TensorSpan<T>, T>.AsTensorSpan(params scoped ReadOnlySpan<nint> startIndexes) => Slice(startIndexes);

        TensorSpan<T> ITensor<TensorSpan<T>, T>.AsTensorSpan(params scoped ReadOnlySpan<NIndex> startIndexes) => Slice(startIndexes);

        TensorSpan<T> ITensor<TensorSpan<T>, T>.AsTensorSpan(params scoped ReadOnlySpan<NRange> ranges) => Slice(ranges);

        public ref struct Enumerator : IEnumerator<T>
        {
            private readonly TensorSpan<T> _span;
            private nint _linearOffset;
            private nint _itemsEnumerated;
            private bool _hasCurrent;

            internal Enumerator(TensorSpan<T> span)
            {
                _span = span;
                _linearOffset = 0;
                _itemsEnumerated = 0;
                _hasCurrent = false;
            }

            public readonly ref T Current
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
