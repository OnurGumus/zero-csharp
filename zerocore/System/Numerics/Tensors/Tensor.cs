// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Numerics.Tensors
{
    public static partial class Tensor
    {
        #region AsTensorSpan
        public static ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan<T>(this T[]? array)
            => new ReadOnlyTensorSpan<T>(array);

        public static ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan<T>(this T[]? array, scoped ReadOnlySpan<nint> lengths)
            => new ReadOnlyTensorSpan<T>(array, lengths);

        public static ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan<T>(this T[]? array, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
            => new ReadOnlyTensorSpan<T>(array, lengths, strides);

        public static ReadOnlyTensorSpan<T> AsReadOnlyTensorSpan<T>(this T[]? array, int start, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
            => new ReadOnlyTensorSpan<T>(array, start, lengths, strides);

        public static TensorSpan<T> AsTensorSpan<T>(this T[]? array)
            => new TensorSpan<T>(array);

        public static TensorSpan<T> AsTensorSpan<T>(this T[]? array, scoped ReadOnlySpan<nint> lengths)
            => new TensorSpan<T>(array, lengths);

        public static TensorSpan<T> AsTensorSpan<T>(this T[]? array, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
            => new TensorSpan<T>(array, lengths, strides);

        public static TensorSpan<T> AsTensorSpan<T>(this T[]? array, int start, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides) => new TensorSpan<T>(array, start, lengths, strides);
        #endregion

        #region Broadcast
        public static Tensor<T> Broadcast<T>(scoped in ReadOnlyTensorSpan<T> source, scoped in ReadOnlyTensorSpan<T> lengthsSource)
        {
            return Broadcast(source, lengthsSource.Lengths);
        }

        public static Tensor<T> Broadcast<T>(scoped in ReadOnlyTensorSpan<T> source, scoped ReadOnlySpan<nint> lengths)
        {
            TensorOperation.ValidateCompatibility(source, lengths);
            Tensor<T> destination = CreateFromShapeUninitialized<T>(lengths);
            TensorOperation.Invoke<TensorOperation.CopyTo<T>, T, T>(source, destination);
            return destination;
        }
        #endregion

        #region BroadcastTo
        public static void BroadcastTo<T>(this Tensor<T> source, in TensorSpan<T> destination)
        {
            BroadcastTo(source.AsReadOnlyTensorSpan(), destination);
        }

        public static void BroadcastTo<T>(in this TensorSpan<T> source, in TensorSpan<T> destination)
        {
            BroadcastTo(source.AsReadOnlyTensorSpan(), destination);
        }

        public static void BroadcastTo<T>(in this ReadOnlyTensorSpan<T> source, in TensorSpan<T> destination)
        {
            TensorOperation.ValidateCompatibility(source, destination);
            TensorOperation.Invoke<TensorOperation.CopyTo<T>, T, T>(source, destination);
        }
        #endregion

        #region Concatenate
        public static Tensor<T> Concatenate<T>(params scoped ReadOnlySpan<Tensor<T>> tensors)
        {
            return ConcatenateOnDimension(0, tensors);
        }

        public static Tensor<T> ConcatenateOnDimension<T>(int dimension, params scoped ReadOnlySpan<Tensor<T>> tensors)
        {
            if (tensors.Length < 2)
            {
                ThrowHelper.ThrowArgument_ConcatenateTooFewTensors();
            }

            ref readonly TensorShape shape = ref TensorShape.Normalize(tensors[0]._shape);
            int rank = shape.Rank;
            if (dimension < -1 || dimension >= rank)
            {
                ThrowHelper.ThrowArgument_InvalidDimension();
            }

            Tensor<T> tensor;

            // If axis != -1, make sure all dimensions except the one to concatenate on match.
            if (dimension != -1)
            {
                nint sumOfAxis = GetConcatenatedLength(dimension, tensors, shape);
                tensor = CreateConcatenationResult<T>(shape.Lengths, dimension, sumOfAxis);
            }
            else
            {
                // Calculate total space needed.
                nint totalLength = 0;
                for (int i = 0; i < tensors.Length; i++)
                {
                    checked
                    {
                        totalLength += tensors[i].FlattenedLength;
                    }
                }

                tensor = CreateFromShape<T>([totalLength]);
            }

            CopyConcatenatedValues(dimension, tensors, tensor, rank);
            return tensor;
        }

        public static ref readonly TensorSpan<T> Concatenate<T>(scoped ReadOnlySpan<Tensor<T>> tensors, in TensorSpan<T> destination)
        {
            return ref ConcatenateOnDimension(0, tensors, destination);
        }

        public static ref readonly TensorSpan<T> ConcatenateOnDimension<T>(int dimension, scoped ReadOnlySpan<Tensor<T>> tensors, in TensorSpan<T> destination)
        {
            if (tensors.Length < 2)
            {
                ThrowHelper.ThrowArgument_ConcatenateTooFewTensors();
            }

            ref readonly TensorShape shape = ref TensorShape.Normalize(tensors[0]._shape);
            int rank = shape.Rank;
            nint destinationLength = destination.FlattenedLength;
            if (dimension < -1 || dimension >= rank)
            {
                ThrowHelper.ThrowArgument_InvalidDimension();
            }

            // If axis != -1, make sure all dimensions except the one to concatenate on match.
            if (dimension != -1)
            {
                nint sumOfAxis = GetConcatenatedLength(dimension, tensors, shape);
                ValidateConcatenationDestination(dimension, shape.Lengths, sumOfAxis, destination);
            }
            else
            {
                nint totalLength = 0;
                for (int i = 0; i < tensors.Length; i++)
                {
                    totalLength = checked(totalLength + tensors[i].FlattenedLength);
                }

                if (destinationLength != totalLength)
                {
                    ThrowHelper.ThrowArgument_DimensionsNotSame(nameof(destination));
                }
            }

            ValidateConcatenationStorage(tensors, destination);
            CopyConcatenatedValues(dimension, tensors, destination, rank);
            return ref destination;
        }

        private static void ValidateConcatenationDestination<T>(int dimension, ReadOnlySpan<nint> lengths, nint sumOfAxis,
            in TensorSpan<T> destination)
        {
            ReadOnlySpan<nint> destinationLengths = TensorShape.Normalize(destination._shape).Lengths;
            if (!TensorShape.AreLengthsEquivalentExceptDimension(lengths, destinationLengths, dimension, out nint destinationAxisLength)
                || destinationAxisLength != sumOfAxis)
            {
                ThrowHelper.ThrowArgument_DimensionsNotSame(nameof(destination));
            }
        }

        private static Tensor<T> CreateConcatenationResult<T>(ReadOnlySpan<nint> sourceLengths, int dimension, nint axisLength)
        {
            scoped Span<nint> lengths = TensorOperation.RentedBuffer.CreateUninitialized(sourceLengths.Length,
                out TensorOperation.RentedBuffer<nint> rentedBuffer);
            Tensor<T> result;
            try
            {
                sourceLengths.CopyTo(lengths);
                lengths[dimension] = axisLength;
                result = CreateFromShape<T>(lengths);
            }
            finally
            {
                rentedBuffer.Dispose();
            }

            return result;
        }

        private static void ValidateConcatenationStorage<T>(scoped ReadOnlySpan<Tensor<T>> tensors, in TensorSpan<T> destination)
        {
            if (destination._shape.IsSelfOverlapping)
            {
                ThrowHelper.ThrowArgument_DestinationHasOverlappingElements(nameof(destination));
            }

            if (destination.FlattenedLength == 0)
            {
                return;
            }

            nint destinationLinearLength = destination._shape.LinearLength;
            for (int i = 0; i < tensors.Length; i++)
            {
                ReadOnlyTensorSpan<T> source = tensors[i].AsReadOnlyTensorSpan();
                if (source._shape.Overlaps(in source._reference, ref destination._reference, destinationLinearLength))
                {
                    ThrowHelper.ThrowArgument_OverlappingTensorLayoutsNotSupported();
                }
            }
        }

        private static void CopyConcatenatedValues<T>(int dimension, scoped ReadOnlySpan<Tensor<T>> tensors,
            in TensorSpan<T> destination, int rank)
        {
            nint destinationLength = destination.FlattenedLength;
            if (destinationLength == 0)
            {
                return;
            }

            TensorSpan<T> alignedTarget;
            scoped ref readonly TensorSpan<T> target = ref destination;
            if (dimension != -1 && destination.Rank != rank)
            {
                alignedTarget = new TensorSpan<T>(ref destination._reference, TensorShape.WithRank(destination._shape, rank));
                target = ref alignedTarget;
            }

            if (!target.IsDense || destinationLength > int.MaxValue)
            {
                if (dimension == -1)
                {
                    TensorSpan<T>.Enumerator output = target.GetEnumerator();
                    foreach (Tensor<T> tensor in tensors)
                    {
                        Tensor<T>.Enumerator input = tensor.GetEnumerator();
                        while (input.MoveNext())
                        {
                            bool moved = output.MoveNext();
                            Debug.Assert(moved);
                            output.Current = input.Current;
                        }
                    }
                }
                else
                {
                    int targetRank = target.Rank;
                    Span<NRange> ranges = TensorOperation.RentedBuffer.CreateUninitialized(targetRank, out TensorOperation.RentedBuffer<NRange> rentedBuffer);
                    ranges.Fill(NRange.All);
                    nint offset = 0;
                    foreach (Tensor<T> tensor in tensors)
                    {
                        ReadOnlyTensorSpan<T> source = tensor.AsReadOnlyTensorSpan();
                        if (source.Rank != targetRank)
                        {
                            source = new ReadOnlyTensorSpan<T>(in source._reference, TensorShape.WithRank(source._shape, targetRank));
                        }
                        nint length = source.Lengths[dimension];
                        ranges[dimension] = new NRange(offset, offset + length);
                        TensorOperation.Invoke<TensorOperation.CopyTo<T>, T, T>(source, target.Slice(ranges));
                        offset += length;
                    }
                    rentedBuffer.Dispose();
                }
            }
            else
            {
                Span<T> dstSpan = MemoryMarshal.CreateSpan(ref target._reference, (int)destinationLength);
                ConcatenateOnDimensionToSpan(dimension, tensors, target, dstSpan);
            }
        }

        private static nint GetConcatenatedLength<T>(int dimension, scoped ReadOnlySpan<Tensor<T>> tensors, in TensorShape shape)
        {
            ReadOnlySpan<nint> lengths = shape.Lengths;
            nint sumOfAxis = lengths[dimension];

            for (int i = 1; i < tensors.Length; i++)
            {
                ReadOnlySpan<nint> sourceLengths = TensorShape.Normalize(tensors[i]._shape).Lengths;
                if (!TensorShape.AreLengthsEquivalentExceptDimension(lengths, sourceLengths, dimension, out nint axisLength))
                {
                    ThrowHelper.ThrowArgument_InvalidConcatenateShape();
                }
                sumOfAxis = checked(sumOfAxis + axisLength);
            }

            return sumOfAxis;
        }

        private static void ConcatenateOnDimensionToSpan<T>(int dimension, scoped ReadOnlySpan<Tensor<T>> tensors, in TensorSpan<T> destination, Span<T> dstSpan)
        {
            if (dimension is 0 or -1)
            {
                for (int i = 0; i < tensors.Length; i++)
                {
                    TensorOperation.Invoke<TensorOperation.CopyTo<T>, T, T>(tensors[i], dstSpan);
                    dstSpan = dstSpan.Slice((int)tensors[i].FlattenedLength);
                }
            }
            else
            {
                int rank = destination.Rank;
                Span<NRange> ranges = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<NRange> rentedBuffer);
                for (int i = 0; i < dimension; i++)
                {
                    ranges[i] = 0..1;
                }
                for (int i = dimension; i < rank; i++)
                {
                    ranges[i] = ..;
                }

                bool hasMore = !destination.Lengths[..dimension].Contains(0);
                while (hasMore)
                {
                    for (int i = 0; i < tensors.Length; i++)
                    {
                        ReadOnlyTensorSpan<T> source = tensors[i].AsReadOnlyTensorSpan();
                        if (source.Rank != rank)
                        {
                            source = new ReadOnlyTensorSpan<T>(in source._reference, TensorShape.WithRank(source._shape, rank));
                        }
                        ReadOnlyTensorSpan<T> slice = source.Slice(ranges);
                        TensorOperation.Invoke<TensorOperation.CopyTo<T>, T, T>(slice, dstSpan);
                        dstSpan = dstSpan.Slice((int)slice.FlattenedLength);
                    }
                    // We do dimension - 1 because we want to include the dimension we concatenated on.
                    hasMore = TensorShape.AdjustToNextIndex(ranges, dimension - 1, destination.Lengths);
                }
                rentedBuffer.Dispose();
            }
        }
        #endregion

        #region Create
        public static Tensor<T> Create<T>(T[] array)
            => new Tensor<T>(array);

        public static Tensor<T> Create<T>(T[] array, scoped ReadOnlySpan<nint> lengths)
            => new Tensor<T>(array, lengths, strides: []);

        public static Tensor<T> Create<T>(T[] array, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
            => new Tensor<T>(array, lengths, strides);

        public static Tensor<T> Create<T>(T[] array, int start, scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides)
            => new Tensor<T>(array, start, lengths, strides);
        #endregion

        #region CreateFromShape
        public static Tensor<T> CreateFromShape<T>(scoped ReadOnlySpan<nint> lengths, bool pinned = false)
            => new Tensor<T>(lengths, strides: [], pinned);

        public static Tensor<T> CreateFromShape<T>(scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned = false)
            => new Tensor<T>(lengths, strides, pinned);

        public static Tensor<T> CreateFromShapeUninitialized<T>(scoped ReadOnlySpan<nint> lengths, bool pinned = false)
            => CreateFromShapeUninitialized<T>(lengths, strides: [], pinned);

        public static Tensor<T> CreateFromShapeUninitialized<T>(scoped ReadOnlySpan<nint> lengths, scoped ReadOnlySpan<nint> strides, bool pinned = false)
        {
            TensorShape shape = TensorShape.Create(lengths, strides, pinned);
            T[] values = GC.AllocateUninitializedArray<T>(checked((int)(shape.LinearLength)), pinned);
            return new Tensor<T>(values, in shape);
        }
        #endregion


        #region Equals
        public static Tensor<bool> Equals(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<bool> destination);
            TensorOperation.Invoke<TensorOperation.Equals, float, bool>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<bool> Equals(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<bool> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.Equals, float, bool>(x, y, destination);
            return ref destination;
        }

        public static Tensor<bool> Equals(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<bool> destination = CreateFromShapeUninitialized<bool>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Equals, float, bool>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<bool> Equals(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<bool> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Equals, float, bool>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region EqualsAll
        public static bool EqualsAll(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y);
            return TensorOperation.Invoke<TensorOperation.Equals, float>(x, y);
        }

        public static bool EqualsAll(in ReadOnlyTensorSpan<float> x, float y)
 => TensorOperation.Invoke<TensorOperation.Equals, float>(x, y);
        #endregion

        #region EqualsAny
        public static bool EqualsAny(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            // The main loop early exits at the first false condition, so the TensorOperation
            // checks x != y and returns false on first equal. This means we want to negate
            // whatever the main loop returns as `true` means none are equal.

            TensorOperation.ValidateCompatibility(x, y);
            return !TensorOperation.Invoke<TensorOperation.EqualsAny, float>(x, y);
        }

        public static bool EqualsAny(in ReadOnlyTensorSpan<float> x, float y)
 => !TensorOperation.Invoke<TensorOperation.EqualsAny, float>(x, y);
        #endregion

        #region FilteredUpdate
        public static ref readonly TensorSpan<T> FilteredUpdate<T>(in this TensorSpan<T> tensor, scoped in ReadOnlyTensorSpan<bool> filter, T value)
        {
            TensorOperation.ValidateCompatibility(filter, tensor);
            TensorOperation.Invoke<TensorOperation.FilteredUpdate<T>, bool, T, T>(filter, value, tensor);
            return ref tensor;
        }

        public static ref readonly TensorSpan<T> FilteredUpdate<T>(in this TensorSpan<T> tensor, scoped in ReadOnlyTensorSpan<bool> filter, scoped in ReadOnlyTensorSpan<T> values)
        {
            TensorOperation.ValidateCompatibility(filter, values, tensor);
            TensorOperation.Invoke<TensorOperation.FilteredUpdate<T>, bool, T, T>(filter, values, tensor);
            return ref tensor;
        }
        #endregion

        #region GreaterThan
        public static Tensor<bool> GreaterThan(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<bool> destination);
            TensorOperation.Invoke<TensorOperation.GreaterThan, float, bool>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<bool> GreaterThan(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<bool> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.GreaterThan, float, bool>(x, y, destination);
            return ref destination;
        }

        public static Tensor<bool> GreaterThan(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<bool> destination = CreateFromShape<bool>(x.Lengths, false);
            GreaterThan(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<bool> GreaterThan(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<bool> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.GreaterThan, float, bool>(x, y, destination);
            return ref destination;
        }

        public static Tensor<bool> GreaterThan(float x, in ReadOnlyTensorSpan<float> y)
 => LessThan(y, x);

        public static ref readonly TensorSpan<bool> GreaterThan(float x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<bool> destination)
 => ref LessThan(y, x, destination);
        #endregion

        #region GreaterThanAll
        public static bool GreaterThanAll(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y);
            return TensorOperation.Invoke<TensorOperation.GreaterThan, float>(x, y);
        }

        public static bool GreaterThanAll(in ReadOnlyTensorSpan<float> x, float y)
 => TensorOperation.Invoke<TensorOperation.GreaterThan, float>(x, y);

        public static bool GreaterThanAll(float x, in ReadOnlyTensorSpan<float> y)
 => LessThanAll(y, x);
        #endregion

        #region GreaterThanAny
        public static bool GreaterThanAny(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            // The main loop early exits at the first false condition, so the TensorOperation
            // checks !(x > y) and returns false on first equal. This means we want to negate
            // whatever the main loop returns as `true` means none are equal.

            TensorOperation.ValidateCompatibility(x, y);
            return !TensorOperation.Invoke<TensorOperation.GreaterThanAny, float>(x, y);
        }

        public static bool GreaterThanAny(in ReadOnlyTensorSpan<float> x, float y)
 => !TensorOperation.Invoke<TensorOperation.GreaterThanAny, float>(x, y);

        public static bool GreaterThanAny(float x, in ReadOnlyTensorSpan<float> y)
 => LessThanAny(y, x);
        #endregion

        #region GreaterThanOrEqual
        public static Tensor<bool> GreaterThanOrEqual(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<bool> destination);
            TensorOperation.Invoke<TensorOperation.GreaterThanOrEqual, float, bool>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<bool> GreaterThanOrEqual(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<bool> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.GreaterThanOrEqual, float, bool>(x, y, destination);
            return ref destination;
        }

        public static Tensor<bool> GreaterThanOrEqual(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<bool> destination = CreateFromShape<bool>(x.Lengths, false);
            GreaterThanOrEqual(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<bool> GreaterThanOrEqual(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<bool> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.GreaterThanOrEqual, float, bool>(x, y, destination);
            return ref destination;
        }

        public static Tensor<bool> GreaterThanOrEqual(float x, in ReadOnlyTensorSpan<float> y)
 => LessThanOrEqual(y, x);

        public static ref readonly TensorSpan<bool> GreaterThanOrEqual(float x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<bool> destination)
 => ref LessThanOrEqual(y, x, destination);
        #endregion

        #region GreaterThanOrEqualAll
        public static bool GreaterThanOrEqualAll(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y);
            return TensorOperation.Invoke<TensorOperation.GreaterThanOrEqual, float>(x, y);
        }

        public static bool GreaterThanOrEqualAll(in ReadOnlyTensorSpan<float> x, float y)
 => TensorOperation.Invoke<TensorOperation.GreaterThanOrEqual, float>(x, y);

        public static bool GreaterThanOrEqualAll(float x, in ReadOnlyTensorSpan<float> y)
 => LessThanOrEqualAll(y, x);
        #endregion

        #region GreaterThanOrEqualAny
        public static bool GreaterThanOrEqualAny(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            // The main loop early exits at the first false condition, so the TensorOperation
            // checks !(x >= y) and returns false on first equal. This means we want to negate
            // whatever the main loop returns as `true` means none are equal.

            TensorOperation.ValidateCompatibility(x, y);
            return !TensorOperation.Invoke<TensorOperation.GreaterThanOrEqualAny, float>(x, y);
        }

        public static bool GreaterThanOrEqualAny(in ReadOnlyTensorSpan<float> x, float y)
 => !TensorOperation.Invoke<TensorOperation.GreaterThanOrEqualAny, float>(x, y);

        public static bool GreaterThanOrEqualAny(float x, in ReadOnlyTensorSpan<float> y)
 => LessThanOrEqualAny(y, x);
        #endregion

        #region LessThan
        public static Tensor<bool> LessThan(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<bool> destination);
            TensorOperation.Invoke<TensorOperation.LessThan, float, bool>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<bool> LessThan(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<bool> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.LessThan, float, bool>(x, y, destination);
            return ref destination;
        }

        public static Tensor<bool> LessThan(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<bool> destination = CreateFromShape<bool>(x.Lengths, false);
            LessThan(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<bool> LessThan(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<bool> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.LessThan, float, bool>(x, y, destination);
            return ref destination;
        }

        public static Tensor<bool> LessThan(float x, in ReadOnlyTensorSpan<float> y)
 => GreaterThan(y, x);

        public static ref readonly TensorSpan<bool> LessThan(float x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<bool> destination)
 => ref GreaterThan(y, x, destination);
        #endregion

        #region LessThanAll
        public static bool LessThanAll(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y);
            return TensorOperation.Invoke<TensorOperation.LessThan, float>(x, y);
        }

        public static bool LessThanAll(in ReadOnlyTensorSpan<float> x, float y)
 => TensorOperation.Invoke<TensorOperation.LessThan, float>(x, y);

        public static bool LessThanAll(float x, in ReadOnlyTensorSpan<float> y)
 => GreaterThanAll(y, x);
        #endregion

        #region LessThanAny
        public static bool LessThanAny(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            // The main loop early exits at the first false condition, so the TensorOperation
            // checks !(x < y) and returns false on first equal. This means we want to negate
            // whatever the main loop returns as `true` means none are equal.

            TensorOperation.ValidateCompatibility(x, y);
            return !TensorOperation.Invoke<TensorOperation.LessThanAny, float>(x, y);
        }

        public static bool LessThanAny(in ReadOnlyTensorSpan<float> x, float y)
 => !TensorOperation.Invoke<TensorOperation.LessThanAny, float>(x, y);

        public static bool LessThanAny(float x, in ReadOnlyTensorSpan<float> y)
 => GreaterThanAny(y, x);
        #endregion

        #region LessThanOrEqual
        public static Tensor<bool> LessThanOrEqual(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<bool> destination);
            TensorOperation.Invoke<TensorOperation.LessThanOrEqual, float, bool>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<bool> LessThanOrEqual(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<bool> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.LessThanOrEqual, float, bool>(x, y, destination);
            return ref destination;
        }

        public static Tensor<bool> LessThanOrEqual(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<bool> destination = CreateFromShape<bool>(x.Lengths, false);
            LessThanOrEqual(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<bool> LessThanOrEqual(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<bool> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.LessThanOrEqual, float, bool>(x, y, destination);
            return ref destination;
        }

        public static Tensor<bool> LessThanOrEqual(float x, in ReadOnlyTensorSpan<float> y)
 => GreaterThanOrEqual(y, x);

        public static ref readonly TensorSpan<bool> LessThanOrEqual(float x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<bool> destination)
 => ref GreaterThanOrEqual(y, x, destination);
        #endregion

        #region LessThanOrEqualAll
        public static bool LessThanOrEqualAll(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y);
            return TensorOperation.Invoke<TensorOperation.LessThanOrEqual, float>(x, y);
        }

        public static bool LessThanOrEqualAll(in ReadOnlyTensorSpan<float> x, float y)
 => TensorOperation.Invoke<TensorOperation.LessThanOrEqual, float>(x, y);

        public static bool LessThanOrEqualAll(float x, in ReadOnlyTensorSpan<float> y)
 => GreaterThanOrEqualAll(y, x);
        #endregion

        #region LessThanOrEqualAny
        public static bool LessThanOrEqualAny(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            // The main loop early exits at the first false condition, so the TensorOperation
            // checks !(x <= y) and returns false on first equal. This means we want to negate
            // whatever the main loop returns as `true` means none are equal.

            TensorOperation.ValidateCompatibility(x, y);
            return !TensorOperation.Invoke<TensorOperation.LessThanOrEqualAny, float>(x, y);
        }

        public static bool LessThanOrEqualAny(in ReadOnlyTensorSpan<float> x, float y)
 => !TensorOperation.Invoke<TensorOperation.LessThanOrEqualAny, float>(x, y);

        public static bool LessThanOrEqualAny(float x, in ReadOnlyTensorSpan<float> y)
 => GreaterThanOrEqualAny(y, x);
        #endregion

        #region Permute

        public static Tensor<T> PermuteDimensions<T>(this Tensor<T> tensor, ReadOnlySpan<int> dimensions)
        {
            int rank = tensor.Rank;
            if (rank <= 1 && (dimensions.IsEmpty || (dimensions.Length == 1 && dimensions[0] == 0)))
            {
                return tensor;
            }
            else
            {
                ReadOnlySpan<nint> lengths = tensor.Lengths;
                ReadOnlySpan<nint> strides = tensor.Strides;
                if (!dimensions.IsEmpty && dimensions.Length != rank)
                {
                    ThrowHelper.ThrowArgument_PermuteAxisOrder();
                }

                scoped Span<nint> newLengths = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);
                scoped Span<nint> newStrides = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);
                if (dimensions.IsEmpty)
                {
                    for (int i = 0; i < rank; i++)
                    {
                        newLengths[i] = lengths[rank - 1 - i];
                        newStrides[i] = strides[rank - 1 - i];
                    }
                }
                else
                {
                    scoped Span<ulong> seen = TensorOperation.RentedBuffer.CreateUninitialized(
                        (rank - 1) / 64 + 1, out TensorOperation.RentedBuffer<ulong> seenRentedBuffer);
                    for (int i = 0; i < dimensions.Length; i++)
                    {
                        int dimension = dimensions[i];
                        if ((uint)dimension >= (uint)rank)
                        {
                            ThrowHelper.ThrowArgument_InvalidDimension();
                        }

                        int word = dimension / 64;
                        ulong bit = 1UL << (dimension % 64);
                        if ((seen[word] & bit) != 0)
                        {
                            ThrowHelper.ThrowArgument_PermuteAxisOrder();
                        }

                        seen[word] |= bit;
                        newLengths[i] = lengths[dimension];
                        newStrides[i] = strides[dimension];
                    }
                    seenRentedBuffer.Dispose();
                }

                Tensor<T> result = new Tensor<T>(tensor._values, tensor._start, newLengths, newStrides, tensor.IsPinned);
                stridesRentedBuffer.Dispose();
                lengthsRentedBuffer.Dispose();

                return result;
            }
        }
        #endregion

        #region Reshape
        private static void GetReshapeLengths(ReadOnlySpan<nint> lengths, nint flattenedLength, Span<nint> result)
        {
            if (lengths.IsEmpty)
            {
                if (flattenedLength != 0)
                {
                    ThrowHelper.ThrowArgument_InvalidReshapeDimensions();
                }
                return;
            }

            lengths.CopyTo(result);
            int wildcardIndex = -1;
            // A zero makes the entire product zero, regardless of preceding dimension sizes.
            nint knownProduct = lengths.Contains(0) ? 0 : 1;
            for (int i = 0; i < lengths.Length; i++)
            {
                nint length = lengths[i];
                if (length == -1)
                {
                    if (wildcardIndex >= 0)
                    {
                        ThrowHelper.ThrowArgument_OnlyOneWildcard();
                    }
                    wildcardIndex = i;
                }
                else
                {
                    if (length < 0 || (length != 0 && knownProduct > nint.MaxValue / length))
                    {
                        ThrowHelper.ThrowArgument_InvalidReshapeDimensions();
                    }
                    knownProduct *= length;
                }
            }

            if (wildcardIndex >= 0)
            {
                if (knownProduct == 0 || flattenedLength % knownProduct != 0)
                {
                    ThrowHelper.ThrowArgument_InvalidReshapeDimensions();
                }
                result[wildcardIndex] = flattenedLength / knownProduct;
            }
            else if (knownProduct != flattenedLength)
            {
                ThrowHelper.ThrowArgument_InvalidReshapeDimensions();
            }

        }

        private static void GetReshapeStrides(ReadOnlySpan<nint> sourceLengths, ReadOnlySpan<nint> sourceStrides,
            bool isDense, ReadOnlySpan<nint> lengths, Span<nint> strides)
        {
            if (isDense)
            {
                return;
            }

            if (!sourceStrides.ContainsAnyExcept(0) || sourceLengths.Contains(0))
            {
                return;
            }

            int sourceIndex = 0;
            for (int i = 0; i < lengths.Length; i++)
            {
                while (sourceIndex < sourceLengths.Length && sourceLengths[sourceIndex] == 1 && lengths[i] != 1)
                {
                    sourceIndex++;
                }

                if (sourceIndex < sourceLengths.Length && lengths[i] == sourceLengths[sourceIndex])
                {
                    strides[i] = sourceStrides[sourceIndex++];
                }
                else if (lengths[i] != 1)
                {
                    ThrowHelper.ThrowArgument_InvalidReshapeDimensions();
                }
            }

            while (sourceIndex < sourceLengths.Length && sourceLengths[sourceIndex] == 1)
            {
                sourceIndex++;
            }
            if (sourceIndex != sourceLengths.Length)
            {
                ThrowHelper.ThrowArgument_InvalidReshapeDimensions();
            }
        }

        public static Tensor<T> Reshape<T>(this Tensor<T> tensor, ReadOnlySpan<nint> lengths)
        {
            if (tensor.Lengths.SequenceEqual(lengths))
            {
                return tensor;
            }

            if (tensor.Rank == 0)
            {
                TensorSpan<T> reshaped = tensor.AsTensorSpan().Reshape(lengths);
                return new Tensor<T>(tensor._values, tensor._start, reshaped._shape);
            }

            if (!tensor.IsDense && !tensor.Strides.Contains(0))
            {
                ThrowHelper.ThrowArgument_CannotReshapeNonContiguousOrDense();
            }

            scoped Span<nint> newLengths = TensorOperation.RentedBuffer.CreateUninitialized(lengths.Length, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);
            GetReshapeLengths(lengths, tensor.FlattenedLength, newLengths);
            scoped Span<nint> strides = TensorOperation.RentedBuffer.CreateUninitialized(tensor.IsDense ? 0 : newLengths.Length, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);
            GetReshapeStrides(tensor.Lengths, tensor.Strides, tensor.IsDense, newLengths, strides);

            Tensor<T> result = new Tensor<T>(tensor._values, tensor._start, newLengths, strides, tensor.IsPinned);
            stridesRentedBuffer.Dispose();
            lengthsRentedBuffer.Dispose();

            return result;
        }

        public static TensorSpan<T> Reshape<T>(this scoped in TensorSpan<T> tensor, scoped ReadOnlySpan<nint> lengths)
        {
            if (tensor.Lengths.SequenceEqual(lengths))
            {
                return tensor;
            }

            ref readonly TensorShape shape = ref TensorShape.Normalize(tensor._shape);
            if (!shape.IsDense && !shape.Strides.Contains(0))
            {
                ThrowHelper.ThrowArgument_CannotReshapeNonContiguousOrDense();
            }

            scoped Span<nint> newLengths = TensorOperation.RentedBuffer.CreateUninitialized(lengths.Length, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);
            GetReshapeLengths(lengths, shape.FlattenedLength, newLengths);
            scoped Span<nint> strides = TensorOperation.RentedBuffer.CreateUninitialized(shape.IsDense ? 0 : newLengths.Length, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);
            GetReshapeStrides(shape.Lengths, shape.Strides, shape.IsDense, newLengths, strides);

            TensorSpan<T> result = new TensorSpan<T>(ref tensor._reference, tensor._shape.LinearLength, newLengths, strides, tensor.IsPinned);
            stridesRentedBuffer.Dispose();
            lengthsRentedBuffer.Dispose();

            return result;
        }

        public static ReadOnlyTensorSpan<T> Reshape<T>(this scoped in ReadOnlyTensorSpan<T> tensor, scoped ReadOnlySpan<nint> lengths)
        {
            if (tensor.Lengths.SequenceEqual(lengths))
            {
                return tensor;
            }

            ref readonly TensorShape shape = ref TensorShape.Normalize(tensor._shape);
            if (!shape.IsDense && !shape.Strides.Contains(0))
            {
                ThrowHelper.ThrowArgument_CannotReshapeNonContiguousOrDense();
            }

            scoped Span<nint> newLengths = TensorOperation.RentedBuffer.CreateUninitialized(lengths.Length, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);
            GetReshapeLengths(lengths, shape.FlattenedLength, newLengths);
            scoped Span<nint> strides = TensorOperation.RentedBuffer.CreateUninitialized(shape.IsDense ? 0 : newLengths.Length, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);
            GetReshapeStrides(shape.Lengths, shape.Strides, shape.IsDense, newLengths, strides);

            ReadOnlyTensorSpan<T> result = new ReadOnlyTensorSpan<T>(ref tensor._reference, tensor._shape.LinearLength, newLengths, strides, tensor.IsPinned);
            stridesRentedBuffer.Dispose();
            lengthsRentedBuffer.Dispose();

            return result;
        }
        #endregion

        #region Resize
        public static Tensor<T> Resize<T>(Tensor<T> tensor, ReadOnlySpan<nint> lengths)
        {
            Tensor<T> output = CreateFromShape<T>(lengths, tensor.IsPinned);
            nint newSize = output.FlattenedLength;

            if (tensor.IsDense)
            {
                ReadOnlySpan<T> span = MemoryMarshal.CreateSpan(ref tensor.AsReadOnlyTensorSpan()._reference, (int)tensor.FlattenedLength);
                Span<T> ospan = MemoryMarshal.CreateSpan(ref output.AsTensorSpan()._reference, (int)output.FlattenedLength);
                if (newSize >= span.Length)
                {
                    span.CopyTo(ospan);
                }
                else
                {
                    span.Slice(0, ospan.Length).CopyTo(ospan);
                }
            }
            else
            {
                nint copyLength = Math.Min(tensor.FlattenedLength, newSize);
                Span<T> ospan = MemoryMarshal.CreateSpan(ref output.AsTensorSpan()._reference, (int)output.FlattenedLength);
                ReadOnlyTensorSpan<T> source = tensor.AsReadOnlyTensorSpan();
                int dimension = copyLength >= TensorOperation.MinSlicedOperationLength
                    ? TensorOperation.GetDenseSliceDimension(source._shape, source._shape)
                    : -1;
                if (dimension >= 0)
                {
                    ReadOnlyTensorDimensionSpan<T> slices = source.GetDimensionSpan(dimension);
                    int copied = 0;
                    for (nint i = 0; copied < copyLength; i++)
                    {
                        ReadOnlyTensorSpan<T> slice = slices[i];
                        int count = (int)Math.Min(slice.FlattenedLength, copyLength - copied);
                        MemoryMarshal.CreateReadOnlySpan(in slice._reference, count).CopyTo(ospan.Slice(copied, count));
                        copied += count;
                    }
                }
                else
                {
                    ReadOnlyTensorSpan<T>.Enumerator enumerator = source.GetEnumerator();
                    for (nint i = 0; i < copyLength; i++)
                    {
                        bool moved = enumerator.MoveNext();
                        Debug.Assert(moved);
                        ospan[(int)i] = enumerator.Current;
                    }
                }
            }

            return output;
        }

        public static void ResizeTo<T>(scoped in Tensor<T> tensor, in TensorSpan<T> destination)
        {
            ResizeTo(tensor.AsReadOnlyTensorSpan(), destination);
        }

        public static void ResizeTo<T>(scoped in TensorSpan<T> tensor, in TensorSpan<T> destination)
        {
            ResizeTo(tensor.AsReadOnlyTensorSpan(), destination);
        }

        public static void ResizeTo<T>(scoped in ReadOnlyTensorSpan<T> tensor, in TensorSpan<T> destination)
        {
            if (destination._shape.IsSelfOverlapping)
            {
                ThrowHelper.ThrowArgument_DestinationHasOverlappingElements(nameof(destination));
            }

            nint destinationLength = destination.FlattenedLength;
            nint copyLength = Math.Min(tensor.FlattenedLength, destinationLength);
            if (tensor.IsDense && destination.IsDense)
            {
                if (destinationLength <= int.MaxValue)
                {
                    ReadOnlySpan<T> span = MemoryMarshal.CreateReadOnlySpan(in tensor._reference, (int)copyLength);
                    Span<T> ospan = MemoryMarshal.CreateSpan(ref destination._reference, (int)destinationLength);
                    span.CopyTo(ospan);
                    ospan[(int)copyLength..].Clear();
                }
                else
                {
                    TensorOperation.CopyDense(in tensor._reference, ref destination._reference, copyLength);
                    for (nint offset = copyLength; offset < destinationLength;)
                    {
                        int length = (int)Math.Min(destinationLength - offset, int.MaxValue);
                        MemoryMarshal.CreateSpan(ref Unsafe.Add(ref destination._reference, offset), length).Clear();
                        offset += length;
                    }
                }
            }
            else
            {
                if (tensor._shape.Overlaps(in tensor._reference, ref destination._reference, destination._shape.LinearLength))
                {
                    ThrowHelper.ThrowArgument_OverlappingTensorLayoutsNotSupported();
                }

                ReadOnlyTensorSpan<T>.Enumerator srcEnumerator = tensor.GetEnumerator();
                TensorSpan<T>.Enumerator dstEnumerator = destination.GetEnumerator();

                for (nint i = 0; i < copyLength; i++)
                {
                    bool srcMoved = srcEnumerator.MoveNext();
                    bool dstMoved = dstEnumerator.MoveNext();
                    Debug.Assert(srcMoved && dstMoved);
                    dstEnumerator.Current = srcEnumerator.Current;
                }

                for (nint i = copyLength; i < destinationLength; i++)
                {
                    bool dstMoved = dstEnumerator.MoveNext();
                    Debug.Assert(dstMoved);
                    dstEnumerator.Current = default!;
                }
            }
        }
        #endregion

        #region Reverse
        public static Tensor<T> Reverse<T>(in ReadOnlyTensorSpan<T> tensor)
        {
            Tensor<T> output = CreateFromShape<T>(tensor.Lengths);
            ReverseDimension(tensor, output, -1);

            return output;
        }

        public static Tensor<T> ReverseDimension<T>(in ReadOnlyTensorSpan<T> tensor, int dimension)
        {
            Tensor<T> output = CreateFromShape<T>(tensor.Lengths);
            ReverseDimension(tensor, output, dimension);

            return output;
        }

        public static ref readonly TensorSpan<T> Reverse<T>(scoped in ReadOnlyTensorSpan<T> tensor, in TensorSpan<T> destination)
        {
            return ref ReverseDimension(tensor, destination, -1);
        }

        public static ref readonly TensorSpan<T> ReverseDimension<T>(scoped in ReadOnlyTensorSpan<T> tensor, in TensorSpan<T> destination, int dimension)
        {
            if (dimension < -1 || dimension >= TensorShape.Normalize(tensor._shape).Rank)
            {
                ThrowHelper.ThrowArgument_InvalidDimension();
            }
            TensorOperation.ValidateCompatibility(tensor, destination);
            if (destination.FlattenedLength == 0)
            {
                return ref destination;
            }
            if (tensor._shape.Overlaps(in tensor._reference, ref destination._reference, destination._shape.LinearLength))
            {
                if (Unsafe.AreSame(ref Unsafe.AsRef(in tensor._reference), ref destination._reference)
                    && TensorShape.AreLayoutsTheSame(tensor._shape, destination._shape))
                {
                    if ((tensor._shape.LinearLength == 1) || (dimension >= 0 && tensor.Lengths[dimension] == 1))
                    {
                        return ref destination;
                    }
                    if (tensor.IsDense)
                    {
                        int destinationDimension = dimension == -1 ? -1 : dimension + destination.Rank - tensor.Rank;
                        ReverseDimensionInPlace(destination, destinationDimension);
                        return ref destination;
                    }
                }
                ThrowHelper.ThrowArgument_OverlappingTensorLayoutsNotSupported();
            }
            ReverseDimensionCore(tensor, destination, dimension);

            return ref destination;
        }

        private static void ReverseDimensionInPlace<T>(in TensorSpan<T> tensor, int dimension)
        {
            nint axisLength = dimension == -1 ? tensor.FlattenedLength : tensor.Lengths[dimension];
            nint innerLength = 1;
            if (dimension >= 0)
            {
                for (int i = dimension + 1; i < tensor.Rank; i++)
                {
                    innerLength *= tensor.Lengths[i];
                }
            }

            nint blockLength = axisLength * innerLength;
            for (nint block = 0; block < tensor.FlattenedLength; block += blockLength)
            {
                for (nint left = 0; left < axisLength / 2; left++)
                {
                    nint leftOffset = block + left * innerLength;
                    nint rightOffset = block + (axisLength - 1 - left) * innerLength;
                    for (nint i = 0; i < innerLength; i++)
                    {
                        ref T first = ref Unsafe.Add(ref tensor._reference, leftOffset + i);
                        ref T second = ref Unsafe.Add(ref tensor._reference, rightOffset + i);
                        (first, second) = (second, first);
                    }
                }
            }
        }

        private static void ReverseDimensionCore<T>(scoped in ReadOnlyTensorSpan<T> tensor, in TensorSpan<T> destination, int dimension)
        {
            if (dimension == -1)
            {
                TensorOperation.ReverseInvoke<TensorOperation.CopyTo<T>, T, T>(tensor, destination);
            }
            else if (tensor.Lengths[dimension] == 1)
            {
                TensorOperation.Invoke<TensorOperation.CopyTo<T>, T, T>(tensor, destination);
            }
            else
            {
                int rank = tensor.Rank;
                int destinationRank = destination.Rank;
                Span<NRange> srcIndexes = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<NRange> srcIndexesRentedBuffer);
                Span<NRange> dstIndexes = TensorOperation.RentedBuffer.CreateUninitialized(destinationRank, out TensorOperation.RentedBuffer<NRange> dstIndexesRentedBuffer);

                for (int i = 0; i < srcIndexes.Length; i++)
                {
                    srcIndexes[i] = NRange.All;
                }
                for (int i = 0; i < dstIndexes.Length; i++)
                {
                    dstIndexes[i] = NRange.All;
                }

                int destinationAxis = dimension + destinationRank - rank;
                nint axisLength = destination.Lengths[destinationAxis];
                for (nint i = axisLength; i > 0; i--)
                {
                    nint sourceIndex = i - 1;
                    srcIndexes[dimension] = new NRange(sourceIndex, sourceIndex + 1);
                    dstIndexes[destinationAxis] = new NRange(axisLength - i, axisLength - i + 1);
                    TensorOperation.Invoke<TensorOperation.CopyTo<T>, T, T>(tensor.Slice(srcIndexes), destination.Slice(dstIndexes));
                }
                srcIndexesRentedBuffer.Dispose();
                dstIndexesRentedBuffer.Dispose();
            }

        }
        #endregion

        #region SequenceEqual
        public static bool SequenceEqual<T>(this scoped in TensorSpan<T> tensor, scoped in ReadOnlyTensorSpan<T> other)
            where T : IEquatable<T>
        {
            return ((ReadOnlyTensorSpan<T>)tensor).SequenceEqual(other);
        }

        public static bool SequenceEqual<T>(this scoped in ReadOnlyTensorSpan<T> tensor, scoped in ReadOnlyTensorSpan<T> other)
            where T : IEquatable<T>
        {
            nint flattenedLength = tensor.FlattenedLength;
            if (flattenedLength != other.FlattenedLength
                || !TensorShape.AreLengthsEquivalent(tensor.Lengths, other.Lengths))
            {
                return false;
            }

            if (flattenedLength == 0)
            {
                return true;
            }

            if (tensor.IsDense && other.IsDense && flattenedLength <= int.MaxValue)
            {
                return MemoryMarshal.CreateReadOnlySpan(in tensor.GetPinnableReference(), (int)flattenedLength).ElementsEqual(MemoryMarshal.CreateReadOnlySpan(in other.GetPinnableReference(), (int)flattenedLength));
            }

            ReadOnlyTensorSpan<T> alignedOther = other;
            int rank = tensor.Rank;
            if (rank != other.Rank && flattenedLength >= TensorOperation.MinSlicedOperationLength)
            {
                alignedOther = new ReadOnlyTensorSpan<T>(in other._reference, TensorShape.WithRank(other._shape, rank));
            }

            int dimension = flattenedLength >= TensorOperation.MinSlicedOperationLength
                ? TensorOperation.GetDenseSliceDimension(tensor._shape, alignedOther._shape)
                : -1;
            if (dimension >= 0)
            {
                ReadOnlyTensorDimensionSpan<T> tensorSlices = tensor.GetDimensionSpan(dimension);
                ReadOnlyTensorDimensionSpan<T> otherSlices = alignedOther.GetDimensionSpan(dimension);

                nint sliceCount = tensorSlices.Length;
                for (nint i = 0; i < sliceCount; i++)
                {
                    ReadOnlyTensorSpan<T> tensorSlice = tensorSlices[i];
                    ReadOnlyTensorSpan<T> otherSlice = otherSlices[i];
                    if (!MemoryMarshal.CreateReadOnlySpan(in tensorSlice._reference, (int)tensorSlice.FlattenedLength)
                        .ElementsEqual(MemoryMarshal.CreateReadOnlySpan(in otherSlice._reference, (int)otherSlice.FlattenedLength)))
                    {
                        return false;
                    }
                }
                return true;
            }

            ReadOnlyTensorSpan<T>.Enumerator enumerator1 = tensor.GetEnumerator();
            ReadOnlyTensorSpan<T>.Enumerator enumerator2 = other.GetEnumerator();

            while (enumerator1.MoveNext())
            {
                bool moved = enumerator2.MoveNext();
                Debug.Assert(moved);

                if (!enumerator1.Current.Equals(enumerator2.Current))
                {
                    return false;
                }
            }

            return true;
        }
        #endregion

        #region SetSlice
        public static Tensor<T> SetSlice<T>(this Tensor<T> tensor, in ReadOnlyTensorSpan<T> values, params ReadOnlySpan<NRange> ranges)
        {
            tensor.AsTensorSpan().SetSlice(values, ranges);
            return tensor;
        }

        public static ref readonly TensorSpan<T> SetSlice<T>(this in TensorSpan<T> tensor, scoped in ReadOnlyTensorSpan<T> values, params scoped ReadOnlySpan<NRange> ranges)
        {
            if (ranges.IsEmpty)
            {
                values.CopyTo(tensor);
            }
            else
            {
                values.CopyTo(tensor.Slice(ranges));
            }
            return ref tensor;
        }
        #endregion

        #region Split
        public static Tensor<T>[] Split<T>(scoped in ReadOnlyTensorSpan<T> tensor, int splitCount, nint dimension)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(splitCount);
            ref readonly TensorShape shape = ref TensorShape.Normalize(tensor._shape);
            int rank = shape.Rank;
            ReadOnlySpan<nint> lengths = shape.Lengths;
            if (dimension < 0 || dimension >= rank)
            {
                ThrowHelper.ThrowArgument_AxisLargerThanRank();
            }
            if (lengths[(int)dimension] % splitCount != 0)
            {
                ThrowHelper.ThrowArgument_SplitNotSplitEvenly();
            }

            Tensor<T>[] outputs = new Tensor<T>[splitCount];

            nint totalToCopy = tensor.FlattenedLength / splitCount;

            scoped Span<nint> newLengths = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);
            lengths.CopyTo(newLengths);
            nint splitLength = newLengths[(int)dimension] / splitCount;
            newLengths[(int)dimension] = splitLength;

            scoped Span<NRange> sliceDims = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<NRange> sliceDimsRentedBuffer);
            for (int i = 0; i < sliceDims.Length; i++)
            {
                sliceDims[i] = NRange.All;
            }
            nint start = 0;
            for (int i = 0; i < outputs.Length; i++)
            {
                sliceDims[(int)dimension] = new NRange(start, start + splitLength);
                T[] values = new T[(int)totalToCopy];
                outputs[i] = new Tensor<T>(values, newLengths, strides: []);

                tensor.Slice(sliceDims).CopyTo(outputs[i]);
                start += splitLength;
            }

            sliceDimsRentedBuffer.Dispose();
            lengthsRentedBuffer.Dispose();
            return outputs;
        }
        #endregion

        #region Squeeze
        public static Tensor<T> Squeeze<T>(this Tensor<T> tensor)
        {
            return SqueezeDimension(tensor, -1);
        }

        public static Tensor<T> SqueezeDimension<T>(this Tensor<T> tensor, int dimension)
        {
            int rank = tensor.Rank;

            if (rank == 0 && dimension == -1)
            {
                return tensor;
            }

            if ((dimension >= rank) || (dimension < -1))
            {
                ThrowHelper.ThrowArgument_AxisLargerThanRank();
            }

            scoped Span<nint> lengths = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);
            scoped Span<nint> strides = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);

            if (dimension == -1)
            {
                for (int i = 0, index = 0; i < tensor.Lengths.Length; i++)
                {
                    if (tensor.Lengths[i] != 1)
                    {
                        lengths[index] = tensor.Lengths[i];
                        strides[index] = tensor.Strides[i];
                        index++;
                    }
                    else
                    {
                        rank--;
                    }
                }
            }
            else
            {
                if (tensor.Lengths[dimension] != 1)
                {
                    ThrowHelper.ThrowArgument_InvalidSqueezeAxis();
                }

                rank--;

                tensor.Lengths[..dimension].CopyTo(lengths);
                tensor.Lengths[(dimension + 1)..].CopyTo(lengths[dimension..]);

                tensor.Strides[..dimension].CopyTo(strides);
                tensor.Strides[(dimension + 1)..].CopyTo(strides[dimension..]);
            }

            if (rank == 0)
            {
                lengths[0] = 1;
                strides[0] = 0;
                rank = 1;
            }

            Tensor<T> output = new Tensor<T>(tensor._values, tensor._start, lengths[..rank], strides[..rank], tensor.IsPinned);

            lengthsRentedBuffer.Dispose();
            stridesRentedBuffer.Dispose();

            return output;
        }

        public static TensorSpan<T> Squeeze<T>(this scoped in TensorSpan<T> tensor)
        {
            return SqueezeDimension(tensor, -1);
        }

        public static TensorSpan<T> SqueezeDimension<T>(this scoped in TensorSpan<T> tensor, int dimension)
        {
            int rank = tensor.Rank;

            if (rank == 0 && dimension == -1)
            {
                return tensor;
            }

            if ((dimension >= rank) || (dimension < -1))
            {
                ThrowHelper.ThrowArgument_AxisLargerThanRank();
            }

            scoped Span<nint> lengths = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);
            scoped Span<nint> strides = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);

            if (dimension == -1)
            {
                for (int i = 0, index = 0; i < tensor.Lengths.Length; i++)
                {
                    if (tensor.Lengths[i] != 1)
                    {
                        lengths[index] = tensor.Lengths[i];
                        strides[index] = tensor.Strides[i];
                        index++;
                    }
                    else
                    {
                        rank--;
                    }
                }
            }
            else
            {
                if (tensor.Lengths[dimension] != 1)
                {
                    ThrowHelper.ThrowArgument_InvalidSqueezeAxis();
                }

                rank--;

                tensor.Lengths[..dimension].CopyTo(lengths);
                tensor.Lengths[(dimension + 1)..].CopyTo(lengths[dimension..]);

                tensor.Strides[..dimension].CopyTo(strides);
                tensor.Strides[(dimension + 1)..].CopyTo(strides[dimension..]);
            }

            if (rank == 0)
            {
                lengths[0] = 1;
                strides[0] = 0;
                rank = 1;
            }

            TensorSpan<T> output = new TensorSpan<T>(ref tensor._reference, tensor._shape.LinearLength, lengths[..rank], strides[..rank], tensor.IsPinned);

            lengthsRentedBuffer.Dispose();
            stridesRentedBuffer.Dispose();

            return output;
        }

        public static ReadOnlyTensorSpan<T> Squeeze<T>(this scoped in ReadOnlyTensorSpan<T> tensor)
        {
            return SqueezeDimension(tensor, -1);
        }

        public static ReadOnlyTensorSpan<T> SqueezeDimension<T>(this scoped in ReadOnlyTensorSpan<T> tensor, int dimension)
        {
            int rank = tensor.Rank;

            if (rank == 0 && dimension == -1)
            {
                return tensor;
            }

            if ((dimension >= rank) || (dimension < -1))
            {
                ThrowHelper.ThrowArgument_AxisLargerThanRank();
            }

            scoped Span<nint> lengths = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);
            scoped Span<nint> strides = TensorOperation.RentedBuffer.CreateUninitialized(rank, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);

            if (dimension == -1)
            {
                for (int i = 0, index = 0; i < tensor.Lengths.Length; i++)
                {
                    if (tensor.Lengths[i] != 1)
                    {
                        lengths[index] = tensor.Lengths[i];
                        strides[index] = tensor.Strides[i];
                        index++;
                    }
                    else
                    {
                        rank--;
                    }
                }
            }
            else
            {
                if (tensor.Lengths[dimension] != 1)
                {
                    ThrowHelper.ThrowArgument_InvalidSqueezeAxis();
                }

                rank--;

                tensor.Lengths[..dimension].CopyTo(lengths);
                tensor.Lengths[(dimension + 1)..].CopyTo(lengths[dimension..]);

                tensor.Strides[..dimension].CopyTo(strides);
                tensor.Strides[(dimension + 1)..].CopyTo(strides[dimension..]);
            }

            if (rank == 0)
            {
                lengths[0] = 1;
                strides[0] = 0;
                rank = 1;
            }

            ReadOnlyTensorSpan<T> output = new ReadOnlyTensorSpan<T>(ref tensor._reference, tensor._shape.LinearLength, lengths[..rank], strides[..rank], tensor.IsPinned);

            lengthsRentedBuffer.Dispose();
            stridesRentedBuffer.Dispose();

            return output;
        }
        #endregion

        #region Stack
        public static Tensor<T> Stack<T>(params ReadOnlySpan<Tensor<T>> tensors)
        {
            return StackAlongDimension(0, tensors);
        }

        public static Tensor<T> StackAlongDimension<T>(int dimension, params ReadOnlySpan<Tensor<T>> tensors)
        {
            Tensor<T>[] outputs = UnsqueezeForStack(dimension, tensors);
            ReadOnlySpan<nint> lengths = outputs[0].Lengths;
            Tensor<T> result = CreateConcatenationResult<T>(lengths, dimension, outputs.Length);
            CopyConcatenatedValues(dimension, outputs, result, lengths.Length);
            return result;
        }

        public static ref readonly TensorSpan<T> Stack<T>(scoped in ReadOnlySpan<Tensor<T>> tensors, in TensorSpan<T> destination)
        {
            return ref StackAlongDimension(tensors, destination, 0);
        }

        public static ref readonly TensorSpan<T> StackAlongDimension<T>(scoped ReadOnlySpan<Tensor<T>> tensors, in TensorSpan<T> destination, int dimension)
        {
            Tensor<T>[] outputs = UnsqueezeForStack(dimension, tensors);
            ReadOnlySpan<nint> lengths = outputs[0].Lengths;
            ValidateConcatenationDestination(dimension, lengths, outputs.Length, destination);
            ValidateConcatenationStorage<T>(outputs, destination);
            CopyConcatenatedValues<T>(dimension, outputs, destination, lengths.Length);
            return ref destination;
        }

        private static Tensor<T>[] UnsqueezeForStack<T>(int dimension, scoped ReadOnlySpan<Tensor<T>> tensors)
        {
            if (tensors.Length < 2)
            {
                ThrowHelper.ThrowArgument_StackTooFewTensors();
            }

            ref readonly TensorShape shape = ref TensorShape.Normalize(tensors[0]._shape);
            int rank = shape.Rank;
            ReadOnlySpan<nint> lengths = shape.Lengths;
            for (int i = 1; i < tensors.Length; i++)
            {
                if (!TensorShape.AreLengthsEquivalent(lengths, tensors[i].Lengths))
                {
                    ThrowHelper.ThrowArgument_StackShapesNotSame();
                }
            }

            // Insertion also accepts the position immediately after the last axis.
            if (dimension < 0 || dimension > rank)
            {
                ThrowHelper.ThrowArgument_AxisLargerThanRank();
            }

            Tensor<T>[] outputs = new Tensor<T>[tensors.Length];
            for (int i = 0; i < tensors.Length; i++)
            {
                Tensor<T> tensor = tensors[i];
                if (tensor.Rank != rank)
                {
                    tensor = new Tensor<T>(tensor._values, tensor._start, TensorShape.WithRank(tensor._shape, rank));
                }
                outputs[i] = Unsqueeze(tensor, dimension);
            }

            return outputs;
        }
        #endregion

        #region ToString
        // typeof(T).Name, for the types a tensor usually holds: zerocore's
        // Type has no names.
        private static string TypeName<T>()
        {
            Type t = typeof(T);
            if (t == typeof(float)) return "Single";
            if (t == typeof(double)) return "Double";
            if (t == typeof(Half)) return "Half";
            if (t == typeof(int)) return "Int32";
            if (t == typeof(long)) return "Int64";
            if (t == typeof(short)) return "Int16";
            if (t == typeof(sbyte)) return "SByte";
            if (t == typeof(byte)) return "Byte";
            if (t == typeof(ushort)) return "UInt16";
            if (t == typeof(uint)) return "UInt32";
            if (t == typeof(ulong)) return "UInt64";
            if (t == typeof(bool)) return "Boolean";
            if (t == typeof(char)) return "Char";
            if (t == typeof(nint)) return "IntPtr";
            if (t == typeof(nuint)) return "UIntPtr";
            if (t == typeof(decimal)) return "Decimal";
            return "T";
        }

        internal static string ToString<T>(in ReadOnlyTensorSpan<T> tensor, ReadOnlySpan<nint> maximumLengths, string typeName)
        {
            if (!maximumLengths.IsEmpty)
            {
                ArgumentOutOfRangeException.ThrowIfNotEqual(maximumLengths.Length, tensor.Rank);
                foreach (nint maximumLength in maximumLengths)
                {
                    ArgumentOutOfRangeException.ThrowIfNegative(maximumLength, nameof(maximumLengths));
                }
            }

            var sb = new StringBuilder(typeName);

            sb.Append('<');
            sb.Append(TypeName<T>());
            sb.Append('>');

            sb.Append('[');
            sb.AppendJoin(", ", tensor.Lengths);
            sb.Append(']');

            if (!maximumLengths.IsEmpty)
            {
                sb.AppendLine(" {");

                if (tensor.Rank == 1)
                {
                    nint length = nint.Min(tensor.Lengths[0], maximumLengths[0]);
                    ToString(tensor, length, sb, indentLevel: 1);
                    sb.AppendLine();
                }
                else
                {
                    ToString(tensor, maximumLengths, sb);
                }

                sb.Append('}');
            }
            return sb.ToString();
        }

        private static void ToString<T>(in ReadOnlyTensorSpan<T> tensor, ReadOnlySpan<nint> maximumLengths, StringBuilder sb, int indentLevel = 0)
        {
            int rank = tensor.Rank;
            ReadOnlySpan<nint> lengths = tensor.Lengths;
            nint firstDimensionLength = lengths[0];
            nint length = nint.Min(firstDimensionLength, maximumLengths[0]);

            if (indentLevel != 0)
            {
                if (rank != 1)
                {
                    sb.Append(' ', indentLevel * 2);
                    sb.AppendLine("[");
                }
                else
                {
                    ToString(tensor, length, sb, indentLevel);
                    return;
                }
            }

            if (length != 0)
            {
                TensorShape tmpShape = TensorShape.Create(lengths[1..], tensor.Strides[1..], tensor.IsPinned);

                ReadOnlyTensorSpan<T> tmpTensor = new ReadOnlyTensorSpan<T>(ref tensor._reference, tmpShape);
                ToString(tmpTensor, maximumLengths[1..], sb, indentLevel + 1);

                for (nint i = 1; i < length; i++)
                {
                    sb.AppendLine(",");
                    nint linearOffset = tensor._shape.GetLinearOffsetForDimension(i, 1);
                    tmpTensor = new ReadOnlyTensorSpan<T>(ref Unsafe.Add(ref tensor._reference, linearOffset), tmpShape);
                    ToString(tmpTensor, maximumLengths[1..], sb, indentLevel + 1);
                }

                if (length != firstDimensionLength)
                {
                    sb.AppendLine(",");
                    sb.Append(' ', (indentLevel + 1) * 2);
                    sb.Append("..");
                }
                sb.AppendLine();
            }
            else
            {
                sb.Append(' ', (indentLevel + 1) * 2);
                sb.AppendLine("..");
            }
            sb.Append(' ', indentLevel * 2);

            if (indentLevel != 0)
            {
                sb.Append(']');
            }
        }

        private static void ToString<T>(in ReadOnlyTensorSpan<T> tensor, nint length, StringBuilder sb, int indentLevel)
        {
            sb.Append(' ', indentLevel * 2);
            sb.Append('[');

            if (length != 0)
            {
                sb.Append(tensor._reference);

                for (nint i = 1; i < length; i++)
                {
                    sb.Append(", ");
                    sb.Append(Unsafe.Add(ref tensor._reference, i * tensor.Strides[0]));
                }

                if (length != tensor.Lengths[0])
                {
                    sb.Append(", ..");
                }
            }
            else
            {
                sb.Append("..");
            }

            sb.Append(']');
        }

        private static StringBuilder AppendJoin<T>(this StringBuilder sb, string separator, ReadOnlySpan<T> values)
        {
            if (values.IsEmpty)
            {
                return sb;
            }

            if (values[0] is not null)
            {
                sb.Append(values[0]);
            }

            for (int i = 1; i < values.Length; i++)
            {
                sb.Append(separator);

                if (values[i] is not null)
                {
                    sb.Append(values[i]);
                }
            }
            return sb;
        }
        #endregion

        #region Transpose
        public static Tensor<T> Transpose<T>(Tensor<T> tensor)
        {
            if (tensor.Lengths.Length < 2)
                ThrowHelper.ThrowArgument_TransposeTooFewDimensions();

            scoped Span<nint> lengths = TensorOperation.RentedBuffer.CreateUninitialized(tensor.Rank, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);
            scoped Span<nint> strides = TensorOperation.RentedBuffer.CreateUninitialized(tensor.Rank, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);

            tensor.Lengths.CopyTo(lengths);
            tensor.Strides.CopyTo(strides);

            nint temp = lengths[^1];
            lengths[^1] = lengths[^2];
            lengths[^2] = temp;

            temp = strides[^1];
            strides[^1] = strides[^2];
            strides[^2] = temp;

            Tensor<T> output = new Tensor<T>(tensor._values, tensor._start, lengths, strides, tensor.IsPinned);

            lengthsRentedBuffer.Dispose();
            stridesRentedBuffer.Dispose();

            return output;
        }
        #endregion

        #region TryBroadcastTo
        public static bool TryBroadcastTo<T>(this Tensor<T> tensor, in TensorSpan<T> destination)
        {
            return tensor.AsReadOnlyTensorSpan().TryBroadcastTo(destination);
        }

        public static bool TryBroadcastTo<T>(in this TensorSpan<T> tensor, in TensorSpan<T> destination)
        {
            return tensor.AsReadOnlyTensorSpan().TryBroadcastTo(destination);
        }

        public static bool TryBroadcastTo<T>(in this ReadOnlyTensorSpan<T> tensor, in TensorSpan<T> destination)
        {
            if (!TensorShape.AreCompatible(destination._shape, tensor._shape, false))
            {
                return false;
            }

            BroadcastTo(tensor, destination);
            return true;
        }
        #endregion

        #region Unsqueeze
        public static Tensor<T> Unsqueeze<T>(this Tensor<T> tensor, int dimension)
        {
            if (tensor.Rank == 0)
            {
                TensorSpan<T> unsqueezed = tensor.AsTensorSpan().Unsqueeze(dimension);
                return new Tensor<T>(tensor._values, tensor._start, unsqueezed._shape);
            }

            int rank = tensor.Rank;
            ReadOnlySpan<nint> lengths = tensor.Lengths;
            ReadOnlySpan<nint> strides = tensor.Strides;
            if (dimension > rank)
            {
                ThrowHelper.ThrowArgument_AxisLargerThanRank();
            }
            if (dimension < 0)
            {
                dimension = rank - dimension;
            }

            scoped Span<nint> newLengths = TensorOperation.RentedBuffer.CreateUninitialized(rank + 1, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);

            lengths.Slice(0, dimension).CopyTo(newLengths);
            lengths.Slice(dimension).CopyTo(newLengths.Slice(dimension + 1));
            newLengths[dimension] = 1;

            Span<nint> newStrides = TensorOperation.RentedBuffer.CreateUninitialized(rank + 1, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);
            if (dimension == rank)
            {
                strides.CopyTo(newStrides);
                newStrides[dimension] = 0;
            }
            else
            {
                strides.Slice(0, dimension).CopyTo(newStrides);
                strides.Slice(dimension).CopyTo(newStrides.Slice(dimension + 1));
                newStrides[dimension] = 0;
            }

            Tensor<T> output = new Tensor<T>(tensor._values, tensor._start, newLengths, newStrides, tensor.IsPinned);
            lengthsRentedBuffer.Dispose();
            stridesRentedBuffer.Dispose();
            return output;
        }

        public static TensorSpan<T> Unsqueeze<T>(this scoped in TensorSpan<T> tensor, int dimension)
        {
            ref readonly TensorShape shape = ref TensorShape.Normalize(tensor._shape);
            int rank = shape.Rank;
            ReadOnlySpan<nint> lengths = shape.Lengths;
            ReadOnlySpan<nint> strides = shape.Strides;
            if (dimension > rank)
            {
                ThrowHelper.ThrowArgument_AxisLargerThanRank();
            }
            if (dimension < 0)
            {
                dimension = rank - dimension;
            }

            scoped Span<nint> newLengths = TensorOperation.RentedBuffer.CreateUninitialized(rank + 1, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);

            lengths.Slice(0, dimension).CopyTo(newLengths);
            lengths.Slice(dimension).CopyTo(newLengths.Slice(dimension + 1));
            newLengths[dimension] = 1;

            Span<nint> newStrides = TensorOperation.RentedBuffer.CreateUninitialized(rank + 1, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);
            if (dimension == rank)
            {
                strides.CopyTo(newStrides);
                newStrides[dimension] = 0;
            }
            else
            {
                strides.Slice(0, dimension).CopyTo(newStrides);
                strides.Slice(dimension).CopyTo(newStrides.Slice(dimension + 1));
                newStrides[dimension] = 0;
            }

            TensorSpan<T> output = new TensorSpan<T>(ref tensor._reference, tensor._shape.LinearLength, newLengths, newStrides, tensor.IsPinned);
            lengthsRentedBuffer.Dispose();
            stridesRentedBuffer.Dispose();
            return output;
        }

        public static ReadOnlyTensorSpan<T> Unsqueeze<T>(this scoped in ReadOnlyTensorSpan<T> tensor, int dimension)
        {
            ref readonly TensorShape shape = ref TensorShape.Normalize(tensor._shape);
            int rank = shape.Rank;
            ReadOnlySpan<nint> lengths = shape.Lengths;
            ReadOnlySpan<nint> strides = shape.Strides;
            if (dimension > rank)
            {
                ThrowHelper.ThrowArgument_AxisLargerThanRank();
            }
            if (dimension < 0)
            {
                dimension = rank - dimension;
            }

            scoped Span<nint> newLengths = TensorOperation.RentedBuffer.CreateUninitialized(rank + 1, out TensorOperation.RentedBuffer<nint> lengthsRentedBuffer);

            lengths.Slice(0, dimension).CopyTo(newLengths);
            lengths.Slice(dimension).CopyTo(newLengths.Slice(dimension + 1));
            newLengths[dimension] = 1;

            Span<nint> newStrides = TensorOperation.RentedBuffer.CreateUninitialized(rank + 1, out TensorOperation.RentedBuffer<nint> stridesRentedBuffer);
            if (dimension == rank)
            {
                strides.CopyTo(newStrides);
                newStrides[dimension] = 0;
            }
            else
            {
                strides.Slice(0, dimension).CopyTo(newStrides);
                strides.Slice(dimension).CopyTo(newStrides.Slice(dimension + 1));
                newStrides[dimension] = 0;
            }

            ReadOnlyTensorSpan<T> output = new ReadOnlyTensorSpan<T>(ref tensor._reference, tensor._shape.LinearLength, newLengths, newStrides, tensor.IsPinned);
            lengthsRentedBuffer.Dispose();
            stridesRentedBuffer.Dispose();
            return output;
        }
        #endregion

        #region Abs
        public static Tensor<float> Abs(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Abs, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Abs(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Abs, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Acos
        public static Tensor<float> Acos(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Acos, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Acos(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Acos, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Acosh
        public static Tensor<float> Acosh(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Acosh, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Acosh(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Acosh, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region AcosPi
        public static Tensor<float> AcosPi(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.AcosPi, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> AcosPi(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.AcosPi, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Asin
        public static Tensor<float> Asin(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Asin, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Asin(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Asin, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Asinh
        public static Tensor<float> Asinh(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Asinh, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Asinh(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Asinh, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region AsinPi
        public static Tensor<float> AsinPi(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.AsinPi, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> AsinPi(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.AsinPi, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Atan
        public static Tensor<float> Atan(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Atan, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Atan(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Atan, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Atan2
        public static Tensor<float> Atan2(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.Atan2, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Atan2(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.Atan2, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Atan2(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Atan2, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Atan2(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Atan2, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Atan2(float x, in ReadOnlyTensorSpan<float> y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(y.Lengths);
            TensorOperation.Invoke<TensorOperation.Atan2, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Atan2(float x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(y, destination);
            TensorOperation.Invoke<TensorOperation.Atan2, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region Atan2Pi
        public static Tensor<float> Atan2Pi(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.Atan2Pi, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Atan2Pi(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.Atan2Pi, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Atan2Pi(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Atan2Pi, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Atan2Pi(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Atan2Pi, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Atan2Pi(float x, in ReadOnlyTensorSpan<float> y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(y.Lengths);
            TensorOperation.Invoke<TensorOperation.Atan2Pi, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Atan2Pi(float x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(y, destination);
            TensorOperation.Invoke<TensorOperation.Atan2Pi, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region Atanh
        public static Tensor<float> Atanh(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Atanh, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Atanh(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Atanh, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region AtanPi
        public static Tensor<float> AtanPi(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.AtanPi, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> AtanPi(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.AtanPi, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Average
        public static float Average(scoped in ReadOnlyTensorSpan<float> x)
        {
            // Get the flattenedLength first so we don't spend time computing if we'll fail due to overflow

            float flattenedLength = (float)(x.FlattenedLength);
            float sum = Sum(x);
            return sum / flattenedLength;
        }
        #endregion

        #region CubeRoot
        public static Tensor<float> Cbrt(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Cbrt, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Cbrt(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Cbrt, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Ceiling
        public static Tensor<float> Ceiling(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Ceiling, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Ceiling(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Ceiling, float, float>(x, destination);
            return ref destination;
        }
        #endregion




        #region CopySign
        public static Tensor<float> CopySign(in ReadOnlyTensorSpan<float> x, float sign)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.CopySign, float, float>(x, sign, destination);
            return destination;
        }

        public static Tensor<float> CopySign(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> sign)
        {
            TensorOperation.ValidateCompatibility(x, sign, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.CopySign, float, float>(x, sign, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> CopySign(scoped in ReadOnlyTensorSpan<float> x, float sign, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.CopySign, float, float>(x, sign, destination);
            return ref destination;
        }

        public static ref readonly TensorSpan<float> CopySign(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> sign, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, sign, destination);
            TensorOperation.Invoke<TensorOperation.CopySign, float, float>(x, sign, destination);
            return ref destination;
        }
        #endregion

        #region Cos
        public static Tensor<float> Cos(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Cos, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Cos(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Cos, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Cosh
        public static Tensor<float> Cosh(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Cosh, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Cosh(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Cosh, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region CosineSimilarity
        public static float CosineSimilarity(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y);
            ValueTuple<float, float, float> result = (0f, 0f, 0f);
            TensorOperation.Invoke<TensorOperation.CosineSimilarity, float, ValueTuple<float, float, float>>(x, y, ref result);
            return result.Item1 / (ScalarF.Sqrt(result.Item2) * ScalarF.Sqrt(result.Item3));
        }
        #endregion

        #region CosPi
        public static Tensor<float> CosPi(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.CosPi, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> CosPi(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.CosPi, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region DegreesToRadians
        public static Tensor<float> DegreesToRadians(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.DegreesToRadians, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> DegreesToRadians(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.DegreesToRadians, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Distance
        public static float Distance(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y);
            float result = 0f;
            TensorOperation.Invoke<TensorOperation.SumOfSquaredDifferences, float, float>(x, y, ref result);
            return ScalarF.Sqrt(result);
        }
        #endregion

        #region Dot
        public static float Dot(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y);
            float result = 0f;
            TensorOperation.Invoke<TensorOperation.Dot, float, float>(x, y, ref result);
            return result;
        }
        #endregion

        #region Exp
        public static Tensor<float> Exp(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Exp, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Exp(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Exp, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Exp10
        public static Tensor<float> Exp10(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Exp10, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Exp10(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Exp10, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Exp10M1
        public static Tensor<float> Exp10M1(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Exp10M1, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Exp10M1(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Exp10M1, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Exp2
        public static Tensor<float> Exp2(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Exp2, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Exp2(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Exp2, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Exp2M1
        public static Tensor<float> Exp2M1(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Exp2M1, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Exp2M1(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Exp2M1, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region ExpM1
        public static Tensor<float> ExpM1(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.ExpM1, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> ExpM1(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.ExpM1, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Floor
        public static Tensor<float> Floor(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Floor, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Floor(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Floor, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Hypotenuse
        public static Tensor<float> Hypot(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.Hypot, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Hypot(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.Hypot, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region Ieee754Remainder
        public static Tensor<float> Ieee754Remainder(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.Ieee754Remainder, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Ieee754Remainder(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.Ieee754Remainder, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Ieee754Remainder(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Ieee754Remainder, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Ieee754Remainder(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Ieee754Remainder, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Ieee754Remainder(float x, in ReadOnlyTensorSpan<float> y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(y.Lengths);
            TensorOperation.Invoke<TensorOperation.Ieee754Remainder, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Ieee754Remainder(float x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(y, destination);
            TensorOperation.Invoke<TensorOperation.Ieee754Remainder, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region ILogB
        public static Tensor<int> ILogB(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<int> destination = CreateFromShapeUninitialized<int>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.ILogB, float, int>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<int> ILogB(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<int> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.ILogB, float, int>(x, destination);
            return ref destination;
        }
        #endregion

        #region IndexOfMax
        public static nint IndexOfMax(scoped in ReadOnlyTensorSpan<float> x)
        {
            return TensorPrimitives.IndexOf(x, 0);
        }

        #endregion

        #region IndexOfMaxMagnitude
        public static nint IndexOfMaxMagnitude(scoped in ReadOnlyTensorSpan<float> x)
        {
            return TensorPrimitives.IndexOf(x, 1);
        }
        #endregion

        #region IndexOfMin
        public static nint IndexOfMin(scoped in ReadOnlyTensorSpan<float> x)
        {
            return TensorPrimitives.IndexOf(x, 2);
        }
        #endregion

        #region IndexOfMinMagnitude
        public static nint IndexOfMinMagnitude(scoped in ReadOnlyTensorSpan<float> x)
        {
            return TensorPrimitives.IndexOf(x, 3);
        }
        #endregion


        #region Log
        public static Tensor<float> Log(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Log, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Log(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Log, float, float>(x, destination);
            return ref destination;
        }

        public static Tensor<float> Log(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.Log, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Log(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.Log, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Log(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Log, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Log(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Log, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region Log10
        public static Tensor<float> Log10(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Log10, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Log10(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Log10, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Log10P1
        public static Tensor<float> Log10P1(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Log10P1, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Log10P1(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Log10P1, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Log2
        public static Tensor<float> Log2(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Log2, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Log2(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Log2, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Log2P1
        public static Tensor<float> Log2P1(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Log2P1, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Log2P1(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Log2P1, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region LogP1
        public static Tensor<float> LogP1(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.LogP1, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> LogP1(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.LogP1, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Max
        public static float Max(scoped in ReadOnlyTensorSpan<float> x)
        {
            if (x.IsEmpty)
            {
                ThrowHelper.ThrowArgument_SpansMustBeNonEmpty();
            }

            float result = x._reference;
            TensorOperation.Invoke<TensorOperation.Max, float, float>(x, ref result);
            return result;
        }

        public static Tensor<float> Max(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(in x, in y, out Tensor<float> output);
            TensorOperation.Invoke<TensorOperation.Max, float, float>(x, y, output);
            return output;
        }

        public static ref readonly TensorSpan<float> Max(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in y, in destination);
            TensorOperation.Invoke<TensorOperation.Max, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Max(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Max, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Max(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in destination);
            TensorOperation.Invoke<TensorOperation.Max, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region MaxMagnitude
        public static float MaxMagnitude(scoped in ReadOnlyTensorSpan<float> x)
        {
            if (x.IsEmpty)
            {
                ThrowHelper.ThrowArgument_SpansMustBeNonEmpty();
            }

            float result = x._reference;
            TensorOperation.Invoke<TensorOperation.MaxMagnitude, float, float>(x, ref result);
            return result;
        }

        public static Tensor<float> MaxMagnitude(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(in x, in y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.MaxMagnitude, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MaxMagnitude(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in y, in destination);
            TensorOperation.Invoke<TensorOperation.MaxMagnitude, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> MaxMagnitude(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.MaxMagnitude, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MaxMagnitude(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in destination);
            TensorOperation.Invoke<TensorOperation.MaxMagnitude, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region MaxMagnitudeNumber
        public static float MaxMagnitudeNumber(scoped in ReadOnlyTensorSpan<float> x)
        {
            if (x.IsEmpty)
            {
                ThrowHelper.ThrowArgument_SpansMustBeNonEmpty();
            }

            float result = x._reference;
            TensorOperation.Invoke<TensorOperation.MaxMagnitudeNumber, float, float>(x, ref result);
            return result;
        }

        public static Tensor<float> MaxMagnitudeNumber(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(in x, in y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.MaxMagnitudeNumber, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MaxMagnitudeNumber(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in y, in destination);
            TensorOperation.Invoke<TensorOperation.MaxMagnitudeNumber, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> MaxMagnitudeNumber(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.MaxMagnitudeNumber, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MaxMagnitudeNumber(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in destination);
            TensorOperation.Invoke<TensorOperation.MaxMagnitudeNumber, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region MaxNumber
        public static float MaxNumber(scoped in ReadOnlyTensorSpan<float> x)
        {
            if (x.IsEmpty)
            {
                ThrowHelper.ThrowArgument_SpansMustBeNonEmpty();
            }

            float result = x._reference;
            TensorOperation.Invoke<TensorOperation.MaxNumber, float, float>(x, ref result);
            return result;
        }

        public static Tensor<float> MaxNumber(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(in x, in y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.MaxNumber, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MaxNumber(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in y, in destination);
            TensorOperation.Invoke<TensorOperation.MaxNumber, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> MaxNumber(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.MaxNumber, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MaxNumber(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in destination);
            TensorOperation.Invoke<TensorOperation.MaxNumber, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region Min
        public static float Min(scoped in ReadOnlyTensorSpan<float> x)
        {
            if (x.IsEmpty)
            {
                ThrowHelper.ThrowArgument_SpansMustBeNonEmpty();
            }

            float result = x._reference;
            TensorOperation.Invoke<TensorOperation.Min, float, float>(x, ref result);
            return result;
        }

        public static Tensor<float> Min(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(in x, in y, out Tensor<float> output);
            TensorOperation.Invoke<TensorOperation.Min, float, float>(x, y, output);
            return output;
        }

        public static ref readonly TensorSpan<float> Min(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in y, in destination);
            TensorOperation.Invoke<TensorOperation.Min, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Min(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Min, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Min(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in destination);
            TensorOperation.Invoke<TensorOperation.Min, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region MinMagnitude
        public static float MinMagnitude(scoped in ReadOnlyTensorSpan<float> x)
        {
            if (x.IsEmpty)
            {
                ThrowHelper.ThrowArgument_SpansMustBeNonEmpty();
            }

            float result = x._reference;
            TensorOperation.Invoke<TensorOperation.MinMagnitude, float, float>(x, ref result);
            return result;
        }

        public static Tensor<float> MinMagnitude(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(in x, in y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.MinMagnitude, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MinMagnitude(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in y, in destination);
            TensorOperation.Invoke<TensorOperation.MinMagnitude, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> MinMagnitude(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.MinMagnitude, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MinMagnitude(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in destination);
            TensorOperation.Invoke<TensorOperation.MinMagnitude, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region MinMagnitudeNumber
        public static float MinMagnitudeNumber(scoped in ReadOnlyTensorSpan<float> x)
        {
            if (x.IsEmpty)
            {
                ThrowHelper.ThrowArgument_SpansMustBeNonEmpty();
            }

            float result = x._reference;
            TensorOperation.Invoke<TensorOperation.MinMagnitudeNumber, float, float>(x, ref result);
            return result;
        }

        public static Tensor<float> MinMagnitudeNumber(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(in x, in y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.MinMagnitudeNumber, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MinMagnitudeNumber(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in y, in destination);
            TensorOperation.Invoke<TensorOperation.MinMagnitudeNumber, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> MinMagnitudeNumber(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.MinMagnitudeNumber, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MinMagnitudeNumber(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in destination);
            TensorOperation.Invoke<TensorOperation.MinMagnitudeNumber, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region MinNumber
        public static float MinNumber(scoped in ReadOnlyTensorSpan<float> x)
        {
            if (x.IsEmpty)
            {
                ThrowHelper.ThrowArgument_SpansMustBeNonEmpty();
            }

            float result = x._reference;
            TensorOperation.Invoke<TensorOperation.MinNumber, float, float>(x, ref result);
            return result;
        }

        public static Tensor<float> MinNumber(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(in x, in y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.MinNumber, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MinNumber(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in y, in destination);
            TensorOperation.Invoke<TensorOperation.MinNumber, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> MinNumber(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.MinNumber, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> MinNumber(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(in x, in destination);
            TensorOperation.Invoke<TensorOperation.MinNumber, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region Norm
        public static float Norm(scoped in ReadOnlyTensorSpan<float> x)
        {
            float result = 0f;
            TensorOperation.Invoke<TensorOperation.SumOfSquares, float, float>(x, ref result);
            return ScalarF.Sqrt(result);
        }
        #endregion


        #region Pow
        public static Tensor<float> Pow(in ReadOnlyTensorSpan<float> x, in ReadOnlyTensorSpan<float> y)
        {
            TensorOperation.ValidateCompatibility(x, y, out Tensor<float> destination);
            TensorOperation.Invoke<TensorOperation.Pow, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Pow(scoped in ReadOnlyTensorSpan<float> x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, y, destination);
            TensorOperation.Invoke<TensorOperation.Pow, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Pow(in ReadOnlyTensorSpan<float> x, float y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Pow, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Pow(scoped in ReadOnlyTensorSpan<float> x, float y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Pow, float, float>(x, y, destination);
            return ref destination;
        }

        public static Tensor<float> Pow(float x, in ReadOnlyTensorSpan<float> y)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(y.Lengths);
            TensorOperation.Invoke<TensorOperation.Pow, float, float>(x, y, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Pow(float x, scoped in ReadOnlyTensorSpan<float> y, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(y, destination);
            TensorOperation.Invoke<TensorOperation.Pow, float, float>(x, y, destination);
            return ref destination;
        }
        #endregion

        #region Product
        public static float Product(scoped in ReadOnlyTensorSpan<float> x)
        {
            float destination = 1f;
            TensorOperation.Invoke<TensorOperation.Product, float, float>(x, ref destination);
            return destination;
        }
        #endregion

        #region RadiansToDegrees
        public static Tensor<float> RadiansToDegrees(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.RadiansToDegrees, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> RadiansToDegrees(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.RadiansToDegrees, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Reciprocal
        public static Tensor<float> Reciprocal(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Reciprocal, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Reciprocal(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Reciprocal, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region RootN
        public static Tensor<float> RootN(in ReadOnlyTensorSpan<float> x, int n)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.RootN, float, float>(x, n, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> RootN(scoped in ReadOnlyTensorSpan<float> x, int n, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.RootN, float, float>(x, n, destination);
            return ref destination;
        }
        #endregion



        #region Round
        public static Tensor<float> Round(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Round, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Round(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Round, float, float>(x, destination);
            return ref destination;
        }

        #endregion

        #region Sigmoid
        public static Tensor<float> Sigmoid(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Sigmoid, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Sigmoid(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Sigmoid, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Sin
        public static Tensor<float> Sin(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Sin, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Sin(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Sin, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Sinh
        public static Tensor<float> Sinh(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Sinh, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Sinh(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Sinh, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region SinPi
        public static Tensor<float> SinPi(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.SinPi, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> SinPi(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.SinPi, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region SoftMax
        public static Tensor<float> SoftMax(in ReadOnlyTensorSpan<float> x)
        {
            (float Shift, float Sum, bool HasValue) sumExp = default;
            TensorOperation.Invoke<TensorOperation.SumExp, float, (float Shift, float Sum, bool HasValue)>(x, ref sumExp);

            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.SoftMax, float, (float Shift, float Sum), float>(x, (sumExp.Shift, sumExp.Sum), destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> SoftMax(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            (float Shift, float Sum, bool HasValue) sumExp = default;
            TensorOperation.Invoke<TensorOperation.SumExp, float, (float Shift, float Sum, bool HasValue)>(x, ref sumExp);
            TensorOperation.Invoke<TensorOperation.SoftMax, float, (float Shift, float Sum), float>(x, (sumExp.Shift, sumExp.Sum), destination);
            return ref destination;
        }
        #endregion

        #region Sqrt
        public static Tensor<float> Sqrt(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Sqrt, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Sqrt(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Sqrt, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region StdDev
        public static float StdDev(in ReadOnlyTensorSpan<float> x)
        {
            float mean = Average(x);
            float result = 0f;
            TensorOperation.Invoke<TensorOperation.SumOfSquaredAbsoluteDifferences, float, float>(x, mean, ref result);
            float variance = result / (float)(x.FlattenedLength);
            return ScalarF.Sqrt(variance);
        }
        #endregion

        #region Sum
        public static float Sum(scoped in ReadOnlyTensorSpan<float> x)
        {
            float destination = 0f;
            TensorOperation.Invoke<TensorOperation.Sum, float, float>(x, ref destination);
            return destination;
        }
        #endregion

        #region SumOfSquares
        internal static float SumOfSquares(scoped in ReadOnlyTensorSpan<float> x)
        {
            float result = 0f;
            TensorOperation.Invoke<TensorOperation.SumOfSquares, float, float>(x, ref result);
            return result;
        }
        #endregion

        #region Tan
        public static Tensor<float> Tan(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Tan, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Tan(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Tan, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region Tanh
        public static Tensor<float> Tanh(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Tanh, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Tanh(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Tanh, float, float>(x, destination);
            return ref destination;
        }
        #endregion

        #region TanPi
        public static Tensor<float> TanPi(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.TanPi, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> TanPi(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.TanPi, float, float>(x, destination);
            return ref destination;
        }
        #endregion


        #region Truncate
        public static Tensor<float> Truncate(in ReadOnlyTensorSpan<float> x)
        {
            Tensor<float> destination = CreateFromShapeUninitialized<float>(x.Lengths);
            TensorOperation.Invoke<TensorOperation.Truncate, float, float>(x, destination);
            return destination;
        }

        public static ref readonly TensorSpan<float> Truncate(scoped in ReadOnlyTensorSpan<float> x, in TensorSpan<float> destination)
        {
            TensorOperation.ValidateCompatibility(x, destination);
            TensorOperation.Invoke<TensorOperation.Truncate, float, float>(x, destination);
            return ref destination;
        }
        #endregion
    }
}
