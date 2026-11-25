// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Numerics.Tensors
{
    public sealed class Tensor<T> : ITensor<Tensor<T>, T>, IEnumerable<T>
    {
        public static Tensor<T> Empty { get; } = new();

        internal readonly TensorShape _shape;
        internal readonly T[] _values;
        internal readonly int _start;

        internal Tensor(scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned)
        {
            _shape = TensorShape.Create(lengths, strides, pinned);
            _values = GC.AllocateArray<T>(checked((int)(_shape.LinearLength)), pinned);
            _start = 0;
        }

        internal Tensor(T[]? array)
        {
            _shape = TensorShape.Create(array);
            _values = (array is not null) ? array : [];
            _start = 0;
        }

        internal Tensor(T[]? array, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
        {
            _shape = TensorShape.Create(array, lengths, strides);
            _values = (array is not null) ? array : [];
            _start = 0;
        }

        internal Tensor(T[]? array, int start, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned = false)
        {
            _shape = TensorShape.Create(array, start, lengths, strides, pinned);
            _values = (array is not null) ? array : [];
            _start = start;
        }

        internal Tensor(T[] array, in TensorShape shape)
        {

            _shape = shape;
            _values = array;
            _start = 0;
        }

        internal Tensor(T[] array, int start, in TensorShape shape)
        {

            _shape = shape;
            _values = array;
            _start = start;
        }

        private Tensor()
        {
            _shape = default;
            _values = [];
            _start = 0;
        }

        public ref T this[params scoped ReadOnlySpan<nint> indexes]
        {
            get => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_values), _start + _shape.GetLinearOffset<TensorShape.GetOffsetAndLengthForNInt, nint>(indexes));
        }

        public ref T this[params scoped ReadOnlySpan<NIndex> indexes]
        {
            get => ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_values), _start + _shape.GetLinearOffset<TensorShape.GetOffsetAndLengthForNIndex, NIndex>(indexes));
        }

        public Tensor<T> this[params ReadOnlySpan<NRange> ranges]
        {
            get => Slice(ranges);
            set => value.CopyTo(Slice(ranges));
        }

        public nint FlattenedLength => _shape.FlattenedLength;

        public bool HasAnyDenseDimensions => _shape.HasAnyDenseDimensions;

        public bool IsDense => _shape.IsDense;

        public bool IsEmpty => _shape.IsEmpty;

        public bool IsPinned => _shape.IsPinned;

        public ReadOnlySpan<nint> Lengths => _shape.Lengths;

        public int Rank => _shape.Rank;

        public ReadOnlySpan<nint> Strides => _shape.Strides;

        public static implicit operator Tensor<T>(T[] array) => Tensor.Create(array);

        public static implicit operator TensorSpan<T>(Tensor<T> tensor) => tensor.AsTensorSpan();

        public static implicit operator ReadOnlyTensorSpan<T>(Tensor<T> tensor) => tensor.AsReadOnlyTensorSpan();

        public ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan() => new ReadOnlyTensorSpan<T>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_values), _start), in _shape);

        public ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan(params scoped ReadOnlySpan<nint> startIndexes) => AsReadOnlyTensorSpan().Slice(startIndexes);

        public ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan(params scoped ReadOnlySpan<NIndex> startIndexes) => AsReadOnlyTensorSpan().Slice(startIndexes);

        public ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan(params scoped ReadOnlySpan<NRange> ranges) => AsReadOnlyTensorSpan().Slice(ranges);

        public TensorSpan<T> AsTensorSpan() => new TensorSpan<T>(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_values), _start), in _shape);

        public TensorSpan<T> AsTensorSpan(params scoped ReadOnlySpan<nint> startIndexes) => AsTensorSpan().Slice(startIndexes);

        public TensorSpan<T> AsTensorSpan(params scoped ReadOnlySpan<NIndex> startIndexes) => AsTensorSpan().Slice(startIndexes);

        public TensorSpan<T> AsTensorSpan(params scoped ReadOnlySpan<NRange> ranges) => AsTensorSpan().Slice(ranges);

        public unsafe void Clear() => AsTensorSpan().Clear();

        public void CopyTo(scoped in TensorSpan<T> destination)
        {
            if (!TryCopyTo(destination))
            {
                ThrowHelper.ThrowArgument_DestinationTooShort();
            }
        }

        public void Fill(T value) => AsTensorSpan().Fill(value);

        public void FlattenTo(scoped Span<T> destination)
        {
            if (!TryFlattenTo(destination))
            {
                ThrowHelper.ThrowArgument_DestinationTooShort();
            }
        }

        public TensorDimensionSpan<T> GetDimensionSpan(int dimension) => AsTensorSpan().GetDimensionSpan(dimension);

        public Enumerator GetEnumerator() => new Enumerator(this);

        public ref T GetPinnableReference()
        {
            // Ensure that the native code has just one forward branch that is predicted-not-taken.
            ref T ret = ref Unsafe.NullRef<T>();
            if (_shape.FlattenedLength != 0)
            {
                ret = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_values), _start);
            }
            return ref ret;
        }

        public Span<T> GetSpan(scoped ReadOnlySpan<nint> startIndexes, int length) => AsTensorSpan().GetSpan(startIndexes, length);

        public Span<T> GetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length) => AsTensorSpan().GetSpan(startIndexes, length);

        public Tensor<T> Slice(params ReadOnlySpan<nint> startIndexes)
        {
            TensorShape shape = _shape.Slice<TensorShape.GetOffsetAndLengthForSlice, nint>(startIndexes, out nint linearOffset);

            // The source tensor can have no more than int.MaxValue elements so linearOffset will always be in range of int.
            Debug.Assert((int)(linearOffset) == linearOffset);

            return new Tensor<T>(
                _values,
                (int)(_start + linearOffset),
                in shape
            );
        }

        public Tensor<T> Slice(params ReadOnlySpan<NIndex> startIndexes)
        {
            TensorShape shape = _shape.Slice<TensorShape.GetOffsetAndLengthForNIndex, NIndex>(startIndexes, out nint linearOffset);

            // The source tensor can have no more than int.MaxValue elements so linearOffset will always be in range of int.
            Debug.Assert((int)(linearOffset) == linearOffset);

            return new Tensor<T>(
                _values,
                (int)(_start + linearOffset),
                in shape
            );
        }

        public Tensor<T> Slice(params ReadOnlySpan<NRange> ranges)
        {
            TensorShape shape = _shape.Slice<TensorShape.GetOffsetAndLengthForNRange, NRange>(ranges, out nint linearOffset);

            // The source tensor can have no more than int.MaxValue elements so linearOffset will always be in range of int.
            Debug.Assert((int)(linearOffset) == linearOffset);

            return new Tensor<T>(
                _values,
                (int)(_start + linearOffset),
                in shape
            );
        }

        public Tensor<T> ToDenseTensor()
        {
            Tensor<T> result = this;

            if (!IsDense)
            {
                result = Tensor.CreateFromShape<T>(Lengths, IsPinned);
                CopyTo(result);
            }

            return result;
        }

        public bool TryCopyTo(scoped in TensorSpan<T> destination) => AsReadOnlyTensorSpan().TryCopyTo(destination);

        public bool TryFlattenTo(scoped Span<T> destination) => AsReadOnlyTensorSpan().TryFlattenTo(destination);

        public bool TryGetSpan(scoped ReadOnlySpan<nint> startIndexes, int length, out Span<T> span) => AsTensorSpan().TryGetSpan(startIndexes, length, out span);

        public bool TryGetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length, out Span<T> span) => AsTensorSpan().TryGetSpan(startIndexes, length, out span);

        public bool TryGetSpan(scoped ReadOnlySpan<nint> startIndexes, int length, out ReadOnlySpan<T> span) => AsReadOnlyTensorSpan().TryGetSpan(startIndexes, length, out span);

        public bool TryGetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length, out ReadOnlySpan<T> span) => AsReadOnlyTensorSpan().TryGetSpan(startIndexes, length, out span);

        public override string ToString() => ToString([]);

        public string ToString(params scoped ReadOnlySpan<nint> maximumLengths) => Tensor.ToString(AsReadOnlyTensorSpan(), maximumLengths, "System.Numerics.Tensors.Tensor");

        //
        // IEnumerable
        //

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        //
        // IEnumerable<T>
        //

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

        //
        // IReadOnlyTensor
        //

        object? IReadOnlyTensor.this[params scoped ReadOnlySpan<NIndex> indexes] => this[indexes];

        object? IReadOnlyTensor.this[params scoped ReadOnlySpan<nint> indexes] => this[indexes];

        //
        // IReadOnlyTensor<TSelf, T>
        //

        ref readonly T IReadOnlyTensor<Tensor<T>, T>.this[params ReadOnlySpan<nint> indexes] => ref this[indexes];

        ref readonly T IReadOnlyTensor<Tensor<T>, T>.this[params ReadOnlySpan<NIndex> indexes] => ref this[indexes];

        ReadOnlyTensorDimensionSpan<T> IReadOnlyTensor<Tensor<T>, T>.GetDimensionSpan(int dimension) => AsReadOnlyTensorSpan().GetDimensionSpan(dimension);

        ref readonly T IReadOnlyTensor<Tensor<T>, T>.GetPinnableReference() => ref GetPinnableReference();

        ReadOnlySpan<T> IReadOnlyTensor<Tensor<T>, T>.GetSpan(scoped ReadOnlySpan<nint> startIndexes, int length) => AsReadOnlyTensorSpan().GetSpan(startIndexes, length);

        ReadOnlySpan<T> IReadOnlyTensor<Tensor<T>, T>.GetSpan(scoped ReadOnlySpan<NIndex> startIndexes, int length) => AsReadOnlyTensorSpan().GetSpan(startIndexes, length);

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

        static Tensor<T> ITensor<Tensor<T>, T>.CreateFromShape(scoped ReadOnlySpan<nint> lengths, bool pinned) => Tensor.CreateFromShape<T>(lengths, pinned);

        static Tensor<T> ITensor<Tensor<T>, T>.CreateFromShape(scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned) => Tensor.CreateFromShape<T>(lengths, strides, pinned);

        static Tensor<T> ITensor<Tensor<T>, T>.CreateFromShapeUninitialized(scoped ReadOnlySpan<nint> lengths, bool pinned) => Tensor.CreateFromShapeUninitialized<T>(lengths, pinned);

        static Tensor<T> ITensor<Tensor<T>, T>.CreateFromShapeUninitialized(scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned) => Tensor.CreateFromShapeUninitialized<T>(lengths, strides, pinned);

        public struct Enumerator : IEnumerator<T>
        {
            private readonly Tensor<T> _tensor;
            private nint _linearOffset;
            private nint _itemsEnumerated;
            private bool _hasCurrent;

            internal Enumerator(Tensor<T> tensor)
            {
                _tensor = tensor;
                _linearOffset = tensor._start;
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
                    return ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_tensor._values), _linearOffset);
                }
            }

            public bool MoveNext()
            {
                if (_itemsEnumerated == _tensor._shape.FlattenedLength)
                {
                    _hasCurrent = false;
                    return false;
                }

                _linearOffset = _tensor._start + (_tensor.IsDense
                    ? _itemsEnumerated
                    : _tensor._shape.GetLinearOffsetForDimension(_itemsEnumerated, _tensor.Rank));

                _itemsEnumerated++;
                _hasCurrent = true;
                return true;
            }

            public void Reset()
            {
                _linearOffset = _tensor._start;
                _itemsEnumerated = 0;
                _hasCurrent = false;
            }

            //
            // IDisposable
            //

            void IDisposable.Dispose() { }

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
