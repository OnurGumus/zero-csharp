// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Numerics.Tensors
{
    internal static partial class TensorOperation
    {
        // Constructing slices costs more than indexed iteration for tiny tensors.
        internal const int MinSlicedOperationLength = 32;

        // Three indexed traversals make binary slicing pay off at 16 elements rather than 32.
        private const int MinSlicedBinaryOperationLength = 16;

        private static bool ValidateSourceOverlap<TSource, TDestination>(
            in ReadOnlyTensorSpan<TSource> source, in TensorSpan<TDestination> destination, bool isCopy = false)
        {
            if (source.FlattenedLength == 0 || destination.FlattenedLength == 0)
            {
                return false;
            }

            if (!source._shape.Overlaps(in source._reference, ref destination._reference, destination._shape.LinearLength))
            {
                return false;
            }

            if (typeof(TSource) == typeof(TDestination))
            {
                ref TSource destinationReference = ref Unsafe.As<TDestination, TSource>(ref destination._reference);
                if (Unsafe.AreSame(ref Unsafe.AsRef(in source._reference), ref destinationReference)
                    && TensorShape.AreLayoutsTheSame(source._shape, destination._shape))
                {
                    if (isCopy)
                    {
                        return true;
                    }
                    if (!source._shape.IsSelfOverlapping)
                    {
                        return false;
                    }
                }
            }

            throw new ArgumentException(SR.Argument_OverlappingTensorLayoutsNotSupported, nameof(destination));
        }

        private static ReadOnlySpan<T> AsDenseSpan<T>(in ReadOnlyTensorSpan<T> source)
            => MemoryMarshal.CreateReadOnlySpan(in source._reference, checked((int)source.FlattenedLength));

        private static Span<T> AsDenseSpan<T>(in TensorSpan<T> destination)
            => MemoryMarshal.CreateSpan(ref destination._reference, checked((int)destination.FlattenedLength));

        internal static void CopyDense<T>(ref readonly T source, ref T destination, nint length, int maximumChunkLength = int.MaxValue)
        {
            Debug.Assert(length >= 0 && maximumChunkLength > 0);
            // Copy overlapping chunks in memmove order. Each span is bounded by
            // int.MaxValue, while offsets and the total length remain native-sized.
            bool backwards = Unsafe.IsAddressGreaterThan(ref destination, ref Unsafe.AsRef(in source));
            nint remaining = length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(remaining, maximumChunkLength);
                nint offset = backwards ? remaining - count : length - remaining;
                MemoryMarshal.CreateReadOnlySpan(in Unsafe.Add(ref Unsafe.AsRef(in source), offset), count)
                    .CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref destination, offset), count));
                remaining -= count;
            }
        }

        private static ReadOnlyTensorSpan<T> BroadcastSource<T>(in ReadOnlyTensorSpan<T> source, in TensorShape destination)
        {
            if (destination.FlattenedLength == 0)
            {
                return source;
            }

            ReadOnlySpan<nint> sourceLengths = source.Lengths;
            ReadOnlySpan<nint> sourceStrides = source.Strides;
            int destinationRank = destination.Rank;
            scoped Span<nint> strides = RentedBuffer.CreateUninitialized(destinationRank, out RentedBuffer<nint> rentedBuffer);
            int rankDifference = destinationRank - sourceLengths.Length;

            for (int i = 0; i < destinationRank; i++)
            {
                int sourceIndex = i - rankDifference;
                strides[i] = sourceIndex < 0 || sourceLengths[sourceIndex] == 1 ? 0 : sourceStrides[sourceIndex];
            }

            TensorShape shape = TensorShape.Create(in source._reference, source._shape.LinearLength, destination.Lengths, strides, source.IsPinned);
            rentedBuffer.Dispose();
            return new ReadOnlyTensorSpan<T>(in source._reference, in shape);
        }

        internal static int GetDenseSliceDimension(in TensorShape source, in TensorShape destination, in TensorShape other = default,
            int minimumLength = MinSlicedOperationLength)
        {
            if (destination.FlattenedLength < minimumLength)
            {
                return -1;
            }

            ReadOnlySpan<nint> lengths = destination.Lengths;
            bool hasOther = other.Rank != 0;
            Debug.Assert(source.Lengths.SequenceEqual(lengths));
            Debug.Assert(!hasOther || other.Lengths.SequenceEqual(lengths));
            ReadOnlySpan<nint> sourceStrides = source.Strides;
            ReadOnlySpan<nint> destinationStrides = destination.Strides;
            ReadOnlySpan<nint> otherStrides = other.Strides;
            nint contiguousLength = 1;
            int dimension = -1;

            for (int i = lengths.Length - 1; i > 0; i--)
            {
                nint length = lengths[i];
                if (length > 1 &&
                    (sourceStrides[i] != contiguousLength ||
                     destinationStrides[i] != contiguousLength ||
                     (hasOther && otherStrides[i] != contiguousLength)))
                {
                    break;
                }
                if (length > int.MaxValue / contiguousLength)
                {
                    break;
                }

                contiguousLength *= length;
                if (contiguousLength > 1)
                {
                    dimension = i - 1;
                }
            }

            return dimension;
        }

        public static void Invoke<TOperation, T>(in TensorSpan<T> x)
            where TOperation : IOperation<T>
        {
            if (x.IsDense && x.FlattenedLength <= int.MaxValue)
            {
                TOperation.Invoke(AsDenseSpan(x));
                return;
            }

            if (GetDenseSliceDimension(x._shape, x._shape) is int dimension && dimension >= 0)
            {
                TensorDimensionSpan<T> slices = x.GetDimensionSpan(dimension);
                for (nint i = 0; i < slices.Length; i++)
                {
                    TensorSpan<T> slice = slices[i];
                    TOperation.Invoke(AsDenseSpan(slice));
                }
                return;
            }

            scoped Span<nint> indexes = RentedBuffer.Create(x.Rank, x.Strides, out nint linearOffset, out RentedBuffer<nint> rentedBuffer);

            for (nint i = 0; i < x.FlattenedLength; i++)
            {
                linearOffset = x._shape.AdjustToNextIndex(x._shape, linearOffset, indexes);
                TOperation.Invoke(
                    ref Unsafe.Add(ref x._reference, linearOffset)
                );
            }

            rentedBuffer.Dispose();
        }

        public static bool Invoke<TOperation, TArg>(in ReadOnlyTensorSpan<TArg> x, in ReadOnlyTensorSpan<TArg> y)
            where TOperation : IBinaryOperation_Tensor_Tensor<TArg, bool>
        {
            bool result = false;

            TensorShape destinationShape = GetBroadcastShape(x._shape, y._shape);

            scoped Span<nint> xIndexes = RentedBuffer.Create(destinationShape.Rank, x.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);
            scoped Span<nint> yIndexes = RentedBuffer.Create(destinationShape.Rank, y.Strides, out nint yLinearOffset, out RentedBuffer<nint> yRentedBuffer);

            for (nint i = 0; i < destinationShape.FlattenedLength; i++)
            {
                xLinearOffset = x._shape.AdjustToNextIndex(destinationShape, xLinearOffset, xIndexes);
                yLinearOffset = y._shape.AdjustToNextIndex(destinationShape, yLinearOffset, yIndexes);

                 TOperation.Invoke(
                    in Unsafe.Add(ref x._reference, xLinearOffset),
                    in Unsafe.Add(ref y._reference, yLinearOffset),
                    ref result
                );

                if (!result)
                {
                    break;
                }
            }

            xRentedBuffer.Dispose();
            yRentedBuffer.Dispose();

            return (destinationShape.FlattenedLength == 0) || result;
        }

        private static TensorShape GetBroadcastShape(in TensorShape x, in TensorShape y)
        {
            x = ref TensorShape.Normalize(x);
            y = ref TensorShape.Normalize(y);

            if (TensorShape.AreLengthsTheSame(x, y))
            {
                return x;
            }

            int rank = Math.Max(x.Rank, y.Rank);
            scoped Span<nint> lengths = RentedBuffer.CreateUninitialized(rank, out RentedBuffer<nint> rentedBuffer);
            ReadOnlySpan<nint> xLengths = x.Lengths;
            ReadOnlySpan<nint> yLengths = y.Lengths;
            for (int i = 0; i < rank; i++)
            {
                nint xLength = i < rank - x.Rank ? 1 : xLengths[i - (rank - x.Rank)];
                nint yLength = i < rank - y.Rank ? 1 : yLengths[i - (rank - y.Rank)];
                lengths[i] = xLength == 1 ? yLength : xLength;
            }

            TensorShape shape = TensorShape.Create(lengths, [], pinned: false);
            rentedBuffer.Dispose();
            return shape;
        }

        public static bool Invoke<TOperation, TArg>(in ReadOnlyTensorSpan<TArg> x, TArg y)
            where TOperation : IBinaryOperation_Tensor_Scalar<TArg, bool>
        {
            bool result = false;

            scoped Span<nint> xIndexes = RentedBuffer.Create(x.Rank, x.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);

            for (nint i = 0; i < x.FlattenedLength; i++)
            {
                xLinearOffset = x._shape.AdjustToNextIndex(x._shape, xLinearOffset, xIndexes);

                TOperation.Invoke(
                    in Unsafe.Add(ref x._reference, xLinearOffset),
                    y,
                    ref result
                );

                if (!result)
                {
                    break;
                }
            }

            xRentedBuffer.Dispose();
            return (x.FlattenedLength == 0) || result;
        }

        public static void Invoke<TOperation, TArg, TResult>(in TensorSpan<TResult> destination, TArg scalar)
            where TOperation : IUnaryOperation_Scalar<TArg, TResult>
        {
            if (destination.IsDense && destination.FlattenedLength <= int.MaxValue)
            {
                TOperation.Invoke(AsDenseSpan(destination), scalar);
                return;
            }

            if (GetDenseSliceDimension(destination._shape, destination._shape) is int dimension && dimension >= 0)
            {
                TensorDimensionSpan<TResult> slices = destination.GetDimensionSpan(dimension);
                for (nint i = 0; i < slices.Length; i++)
                {
                    TensorSpan<TResult> slice = slices[i];
                    TOperation.Invoke(AsDenseSpan(slice), scalar);
                }
                return;
            }

            scoped Span<nint> indexes = RentedBuffer.Create(destination.Rank, destination.Strides, out nint linearOffset, out RentedBuffer<nint> rentedBuffer);

            for (nint i = 0; i < destination.FlattenedLength; i++)
            {
                linearOffset = destination._shape.AdjustToNextIndex(destination._shape, linearOffset, indexes);
                TOperation.Invoke(
                    ref Unsafe.Add(ref destination._reference, linearOffset),
                    scalar
                );
            }

            rentedBuffer.Dispose();
        }

        public static void Invoke<TOperation, TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, in TensorSpan<TResult> destination)
            where TOperation : IUnaryOperation_Tensor<TArg, TResult>
        {
            nint sourceLength = x.FlattenedLength;
            nint destinationLength = destination.FlattenedLength;
            bool isCopy = typeof(TOperation) == typeof(CopyTo<TArg>) && typeof(TArg) == typeof(TResult);
            if (isCopy)
            {
                ref TArg destinationReference = ref Unsafe.As<TResult, TArg>(ref destination._reference);
                if (x.IsDense && destination.IsDense && sourceLength == destinationLength)
                {
                    if (sourceLength <= int.MaxValue)
                    {
                        MemoryMarshal.CreateReadOnlySpan(in x._reference, (int)sourceLength)
                            .CopyTo(MemoryMarshal.CreateSpan(ref destinationReference, (int)destinationLength));
                    }
                    else
                    {
                        CopyDense(in x._reference, ref destinationReference, sourceLength);
                    }
                    return;
                }
            }

            if (ValidateSourceOverlap(x, destination, isCopy))
            {
                return;
            }
            ReadOnlySpan<nint> destinationLengths = destination.Lengths;
            ReadOnlyTensorSpan<TArg> optimizedSource = x.Lengths.SequenceEqual(destinationLengths) ? x : BroadcastSource(x, destination._shape);
            if (destinationLength != 0)
            {
                if (optimizedSource.IsDense && destination.IsDense && destinationLength <= int.MaxValue)
                {
                    TOperation.Invoke(AsDenseSpan(optimizedSource), AsDenseSpan(destination));
                    return;
                }

                int dimension = GetDenseSliceDimension(optimizedSource._shape, destination._shape);
                if (dimension >= 0)
                {
                    ReadOnlyTensorDimensionSpan<TArg> sourceSlices = optimizedSource.GetDimensionSpan(dimension);
                    TensorDimensionSpan<TResult> destinationSlices = destination.GetDimensionSpan(dimension);
                    nint sliceCount = destinationSlices.Length;
                    for (nint i = 0; i < sliceCount; i++)
                    {
                        ReadOnlyTensorSpan<TArg> sourceSlice = sourceSlices[i];
                        TensorSpan<TResult> destinationSlice = destinationSlices[i];
                        TOperation.Invoke(AsDenseSpan(sourceSlice), AsDenseSpan(destinationSlice));
                    }
                    return;
                }
            }
            {
                ReadOnlyTensorSpan<TArg> source = optimizedSource;
                int destinationRank = destinationLengths.Length;
                scoped Span<nint> xIndexes = RentedBuffer.Create(destinationRank, source.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);
                scoped Span<nint> destinationIndexes = RentedBuffer.Create(destinationRank, destination.Strides, out nint destinationLinearOffset, out RentedBuffer<nint> destinationRentedBuffer);
                for (nint i = 0; i < destinationLength; i++)
                {
                    xLinearOffset = source._shape.AdjustToNextIndex(destination._shape, xLinearOffset, xIndexes);
                    destinationLinearOffset = destination._shape.AdjustToNextIndex(destination._shape, destinationLinearOffset, destinationIndexes);

                    TOperation.Invoke(
                        in Unsafe.Add(ref source._reference, xLinearOffset),
                        ref Unsafe.Add(ref destination._reference, destinationLinearOffset)
                    );
                }
                xRentedBuffer.Dispose();
                destinationRentedBuffer.Dispose();
            }
        }

        public static void ReverseInvoke<TOperation, TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, in TensorSpan<TResult> destination)
            where TOperation : IUnaryOperation_Tensor<TArg, TResult>
        {
            ReadOnlySpan<nint> destinationLengths = destination.Lengths;
            int destinationRank = destinationLengths.Length;
            nint destinationLength = destination.FlattenedLength;
            ReadOnlyTensorSpan<TArg> source = x.Lengths.SequenceEqual(destinationLengths) ? x : BroadcastSource(x, destination._shape);
            scoped Span<nint> xIndexes = RentedBuffer.Create(destinationRank, source.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);
            scoped Span<nint> destinationIndexes = RentedBuffer.Create(destinationRank, destination.Strides, out nint negInnermostStride, out RentedBuffer<nint> destinationRentedBuffer);

            for (int i = 0; i < destinationIndexes.Length - 1; i++)
            {
                destinationIndexes[i] = destinationLengths[i] - 1;
            }
            destinationIndexes[^1] = destinationLengths[^1];
            nint destinationLinearOffset = destination._shape.LinearLength - 1 - negInnermostStride;

            for (nint i = 0; i < destinationLength; i++)
            {
                xLinearOffset = source._shape.AdjustToNextIndex(destination._shape, xLinearOffset, xIndexes);
                destinationLinearOffset = destination._shape.AdjustToPreviousIndex(destination._shape, destinationLinearOffset, destinationIndexes);

                TOperation.Invoke(
                    in Unsafe.Add(ref source._reference, xLinearOffset),
                    ref Unsafe.Add(ref destination._reference, destinationLinearOffset)
                );
            }

            xRentedBuffer.Dispose();
            destinationRentedBuffer.Dispose();
        }

        // For copyto/flattento
        public static void Invoke<TOperation, TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, in Span<TResult> destination)
            where TOperation : IUnaryOperation_Tensor<TArg, TResult>
        {
            Span<TResult> result = destination[..checked((int)x.FlattenedLength)];
            if (typeof(TOperation) == typeof(CopyTo<TArg>) && typeof(TArg) == typeof(TResult))
            {
                ref TArg destinationReference = ref Unsafe.As<TResult, TArg>(ref MemoryMarshal.GetReference(result));
                if (x.IsDense)
                {
                    MemoryMarshal.CreateReadOnlySpan(in x._reference, result.Length)
                        .CopyTo(MemoryMarshal.CreateSpan(ref destinationReference, result.Length));
                    return;
                }

                if (x._shape.Overlaps(in x._reference, ref destinationReference, result.Length))
                {
                    ThrowHelper.ThrowArgument_OverlappingTensorLayoutsNotSupported();
                }

                int dimension = result.Length >= MinSlicedOperationLength
                    ? GetDenseSliceDimension(x._shape, x._shape)
                    : -1;
                if (dimension >= 0)
                {
                    ReadOnlyTensorDimensionSpan<TArg> slices = x.GetDimensionSpan(dimension);
                    int destinationOffset = 0;

                    nint sliceCount = slices.Length;
                    for (nint i = 0; i < sliceCount; i++)
                    {
                        ReadOnlyTensorSpan<TArg> slice = slices[i];
                        ReadOnlySpan<TArg> sourceSpan = AsDenseSpan(slice);
                        TOperation.Invoke(sourceSpan, result.Slice(destinationOffset, sourceSpan.Length));
                        destinationOffset += sourceSpan.Length;
                    }
                    return;
                }
            }

            scoped Span<nint> xIndexes = RentedBuffer.Create(x.Rank, x.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);
            nint destinationIndex = -1;

            for (nint i = 0; i < result.Length; i++)
            {
                xLinearOffset = x._shape.AdjustToNextIndex(x._shape, xLinearOffset, xIndexes);
                destinationIndex++;

                TOperation.Invoke(
                    in Unsafe.Add(ref x._reference, xLinearOffset),
                    ref Unsafe.Add(ref result[0], destinationIndex)
                );
            }

            xRentedBuffer.Dispose();
        }

        public static void Invoke<TOperation, TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, ref TResult destination)
            where TOperation : IUnaryReduction_Tensor<TArg, TResult>
        {
            scoped Span<nint> xIndexes = RentedBuffer.Create(x.Rank, x.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);

            for (nint i = 0; i < x.FlattenedLength; i++)
            {
                xLinearOffset = x._shape.AdjustToNextIndex(x._shape, xLinearOffset, xIndexes);

                TOperation.Invoke(
                    in Unsafe.Add(ref x._reference, xLinearOffset),
                    ref destination
                );
            }

            xRentedBuffer.Dispose();
        }

        public static void Invoke<TOperation, TArg1, TArg2, TResult>(in ReadOnlyTensorSpan<TArg1> x, in ReadOnlyTensorSpan<TArg2> y, in TensorSpan<TResult> destination)
            where TOperation : IBinaryOperation_Tensor_Tensor<TArg1, TArg2, TResult>
        {
            ValidateSourceOverlap(x, destination);
            ValidateSourceOverlap(y, destination);
            ReadOnlySpan<nint> destinationLengths = destination.Lengths;
            nint destinationLength = destination.FlattenedLength;
            ReadOnlyTensorSpan<TArg1> optimizedX = x.Lengths.SequenceEqual(destinationLengths) ? x : BroadcastSource(x, destination._shape);
            ReadOnlyTensorSpan<TArg2> optimizedY = y.Lengths.SequenceEqual(destinationLengths) ? y : BroadcastSource(y, destination._shape);
            if (destinationLength != 0)
            {
                if (optimizedX.IsDense && optimizedY.IsDense && destination.IsDense && destinationLength <= int.MaxValue)
                {
                    TOperation.Invoke(AsDenseSpan(optimizedX), AsDenseSpan(optimizedY), AsDenseSpan(destination));
                    return;
                }

                int dimension = GetDenseSliceDimension(optimizedX._shape, destination._shape, optimizedY._shape, MinSlicedBinaryOperationLength);
                if (dimension >= 0)
                {
                    ReadOnlyTensorDimensionSpan<TArg1> xSlices = optimizedX.GetDimensionSpan(dimension);
                    ReadOnlyTensorDimensionSpan<TArg2> ySlices = optimizedY.GetDimensionSpan(dimension);
                    TensorDimensionSpan<TResult> destinationSlices = destination.GetDimensionSpan(dimension);
                    nint sliceCount = destinationSlices.Length;
                    for (nint i = 0; i < sliceCount; i++)
                    {
                        ReadOnlyTensorSpan<TArg1> xSlice = xSlices[i];
                        ReadOnlyTensorSpan<TArg2> ySlice = ySlices[i];
                        TensorSpan<TResult> destinationSlice = destinationSlices[i];
                        TOperation.Invoke(AsDenseSpan(xSlice), AsDenseSpan(ySlice), AsDenseSpan(destinationSlice));
                    }
                    return;
                }
            }
            {
                ReadOnlyTensorSpan<TArg1> left = optimizedX;
                ReadOnlyTensorSpan<TArg2> right = optimizedY;
                int destinationRank = destinationLengths.Length;
                scoped Span<nint> xIndexes = RentedBuffer.Create(destinationRank, left.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);
                scoped Span<nint> yIndexes = RentedBuffer.Create(destinationRank, right.Strides, out nint yLinearOffset, out RentedBuffer<nint> yRentedBuffer);
                scoped Span<nint> destinationIndexes = RentedBuffer.Create(destinationRank, destination.Strides, out nint destinationLinearOffset, out RentedBuffer<nint> destinationRentedBuffer);
                for (nint i = 0; i < destinationLength; i++)
                {
                    xLinearOffset = left._shape.AdjustToNextIndex(destination._shape, xLinearOffset, xIndexes);
                    yLinearOffset = right._shape.AdjustToNextIndex(destination._shape, yLinearOffset, yIndexes);
                    destinationLinearOffset = destination._shape.AdjustToNextIndex(destination._shape, destinationLinearOffset, destinationIndexes);

                    TOperation.Invoke(
                        in Unsafe.Add(ref left._reference, xLinearOffset),
                        in Unsafe.Add(ref right._reference, yLinearOffset),
                        ref Unsafe.Add(ref destination._reference, destinationLinearOffset)
                    );
                }
                xRentedBuffer.Dispose();
                yRentedBuffer.Dispose();
                destinationRentedBuffer.Dispose();
            }
        }

        public static void Invoke<TOperation, TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, in ReadOnlyTensorSpan<TArg> y, in TensorSpan<TResult> destination)
            where TOperation : IBinaryOperation_Tensor_Tensor<TArg, TResult>
        => Invoke<TOperation, TArg, TArg, TResult>(in x, in y, in destination);

        public static void Invoke<TOperation, TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, in ReadOnlyTensorSpan<TArg> y, ref TResult result)
            where TOperation : IBinaryOperation_Tensor_Tensor<TArg, TResult>
        {
            TensorShape destinationShape = GetBroadcastShape(x._shape, y._shape);

            scoped Span<nint> xIndexes = RentedBuffer.Create(destinationShape.Rank, x.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);
            scoped Span<nint> yIndexes = RentedBuffer.Create(destinationShape.Rank, y.Strides, out nint yLinearOffset, out RentedBuffer<nint> yRentedBuffer);

            for (nint i = 0; i < destinationShape.FlattenedLength; i++)
            {
                xLinearOffset = x._shape.AdjustToNextIndex(destinationShape, xLinearOffset, xIndexes);
                yLinearOffset = y._shape.AdjustToNextIndex(destinationShape, yLinearOffset, yIndexes);

                TOperation.Invoke(
                    in Unsafe.Add(ref x._reference, xLinearOffset),
                    in Unsafe.Add(ref y._reference, yLinearOffset),
                    ref result
                );
            }

            xRentedBuffer.Dispose();
            yRentedBuffer.Dispose();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Invoke<TOperation, TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, TArg y, in TensorSpan<TResult> destination)
            where TOperation : IBinaryOperation_Tensor_Scalar<TArg, TResult> => Invoke<TOperation, TArg, TArg, TResult>(in x, y, in destination);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Invoke<TOperation, TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, int y, in TensorSpan<TResult> destination)
            where TOperation : IBinaryOperation_Tensor_Int32<TArg, TResult> => Invoke<TOperation, TArg, int, TResult>(in x, y, in destination);

        public static void Invoke<TOperation, TArg1, TArg2, TResult>(in ReadOnlyTensorSpan<TArg1> x, TArg2 y, in TensorSpan<TResult> destination)
            where TOperation : IBinaryOperation_Tensor_Scalar<TArg1, TArg2, TResult>
        {
            ValidateSourceOverlap(x, destination);
            ReadOnlySpan<nint> destinationLengths = destination.Lengths;
            nint destinationLength = destination.FlattenedLength;
            ReadOnlyTensorSpan<TArg1> optimizedSource = x.Lengths.SequenceEqual(destinationLengths) ? x : BroadcastSource(x, destination._shape);
            if (destinationLength != 0)
            {
                if (optimizedSource.IsDense && destination.IsDense && destinationLength <= int.MaxValue)
                {
                    TOperation.Invoke(AsDenseSpan(optimizedSource), y, AsDenseSpan(destination));
                    return;
                }

                int dimension = GetDenseSliceDimension(optimizedSource._shape, destination._shape);
                if (dimension >= 0)
                {
                    ReadOnlyTensorDimensionSpan<TArg1> sourceSlices = optimizedSource.GetDimensionSpan(dimension);
                    TensorDimensionSpan<TResult> destinationSlices = destination.GetDimensionSpan(dimension);
                    nint sliceCount = destinationSlices.Length;
                    for (nint i = 0; i < sliceCount; i++)
                    {
                        ReadOnlyTensorSpan<TArg1> sourceSlice = sourceSlices[i];
                        TensorSpan<TResult> destinationSlice = destinationSlices[i];
                        TOperation.Invoke(AsDenseSpan(sourceSlice), y, AsDenseSpan(destinationSlice));
                    }
                    return;
                }
            }
            {
                ReadOnlyTensorSpan<TArg1> source = optimizedSource;
                int destinationRank = destinationLengths.Length;
                scoped Span<nint> xIndexes = RentedBuffer.Create(destinationRank, source.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);
                scoped Span<nint> destinationIndexes = RentedBuffer.Create(destinationRank, destination.Strides, out nint destinationLinearOffset, out RentedBuffer<nint> destinationRentedBuffer);
                for (nint i = 0; i < destinationLength; i++)
                {
                    xLinearOffset = source._shape.AdjustToNextIndex(destination._shape, xLinearOffset, xIndexes);
                    destinationLinearOffset = destination._shape.AdjustToNextIndex(destination._shape, destinationLinearOffset, destinationIndexes);

                    TOperation.Invoke(
                        in Unsafe.Add(ref source._reference, xLinearOffset),
                        y,
                        ref Unsafe.Add(ref destination._reference, destinationLinearOffset)
                    );
                }
                xRentedBuffer.Dispose();
                destinationRentedBuffer.Dispose();
            }
        }

        public static void Invoke<TOperation, TArg, TResult>(TArg x, in ReadOnlyTensorSpan<TArg> y, in TensorSpan<TResult> destination)
            where TOperation : IBinaryOperation_Scalar_Tensor<TArg, TArg, TResult>
        {
            ValidateSourceOverlap(y, destination);
            ReadOnlySpan<nint> destinationLengths = destination.Lengths;
            nint destinationLength = destination.FlattenedLength;
            ReadOnlyTensorSpan<TArg> optimizedSource = y.Lengths.SequenceEqual(destinationLengths) ? y : BroadcastSource(y, destination._shape);
            if (destinationLength != 0)
            {
                if (optimizedSource.IsDense && destination.IsDense && destinationLength <= int.MaxValue)
                {
                    TOperation.Invoke(x, AsDenseSpan(optimizedSource), AsDenseSpan(destination));
                    return;
                }

                int dimension = GetDenseSliceDimension(optimizedSource._shape, destination._shape);
                if (dimension >= 0)
                {
                    ReadOnlyTensorDimensionSpan<TArg> sourceSlices = optimizedSource.GetDimensionSpan(dimension);
                    TensorDimensionSpan<TResult> destinationSlices = destination.GetDimensionSpan(dimension);
                    nint sliceCount = destinationSlices.Length;
                    for (nint i = 0; i < sliceCount; i++)
                    {
                        ReadOnlyTensorSpan<TArg> sourceSlice = sourceSlices[i];
                        TensorSpan<TResult> destinationSlice = destinationSlices[i];
                        TOperation.Invoke(x, AsDenseSpan(sourceSlice), AsDenseSpan(destinationSlice));
                    }
                    return;
                }
            }
            {
                ReadOnlyTensorSpan<TArg> source = optimizedSource;
                int destinationRank = destinationLengths.Length;
                scoped Span<nint> xIndexes = RentedBuffer.Create(destinationRank, source.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);
                scoped Span<nint> destinationIndexes = RentedBuffer.Create(destinationRank, destination.Strides, out nint destinationLinearOffset, out RentedBuffer<nint> destinationRentedBuffer);
                for (nint i = 0; i < destinationLength; i++)
                {
                    xLinearOffset = source._shape.AdjustToNextIndex(destination._shape, xLinearOffset, xIndexes);
                    destinationLinearOffset = destination._shape.AdjustToNextIndex(destination._shape, destinationLinearOffset, destinationIndexes);

                    TOperation.Invoke(
                        x,
                        in Unsafe.Add(ref source._reference, xLinearOffset),
                        ref Unsafe.Add(ref destination._reference, destinationLinearOffset)
                    );
                }
                xRentedBuffer.Dispose();
                destinationRentedBuffer.Dispose();
            }
        }

        public static void Invoke<TOperation, TArg1, TArg2, TResult>(in ReadOnlyTensorSpan<TArg1> x, TArg2 y, ref TResult result)
            where TOperation : IBinaryOperation_Tensor_Scalar<TArg1, TArg2, TResult>
        {
            scoped Span<nint> xIndexes = RentedBuffer.Create(x.Rank, x.Strides, out nint xLinearOffset, out RentedBuffer<nint> xRentedBuffer);

            for (nint i = 0; i < x.FlattenedLength; i++)
            {
                xLinearOffset = x._shape.AdjustToNextIndex(x._shape, xLinearOffset, xIndexes);

                TOperation.Invoke(
                    in Unsafe.Add(ref x._reference, xLinearOffset),
                    y,
                    ref result
                );
            }

            xRentedBuffer.Dispose();
        }

        public static void Invoke<TOperation, TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, TArg y, ref TResult result)
            where TOperation : IBinaryOperation_Tensor_Scalar<TArg, TResult> => Invoke<TOperation, TArg, TArg, TResult>(in x, y, ref result);

        public static void ValidateCompatibility<TArg>(in ReadOnlyTensorSpan<TArg> x, in ReadOnlySpan<nint> lengths)
        {
            // x can be broadcast to destination, not vice verse
            if (!TensorShape.AreCompatible(lengths, x._shape))
                ThrowHelper.ThrowArgument_LengthsNotCompatible();
        }

        public static void ValidateCompatibility<TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, in ReadOnlyTensorSpan<TResult> y)
        {
            // Can be bidirectional validation
            if (!TensorShape.AreCompatible(x._shape, y._shape, true))
                ThrowHelper.ThrowArgument_LengthsNotCompatible();
        }

        public static void ValidateCompatibility<TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, in TensorSpan<TResult> destination)
        {
            // x can be broadcast to destination, not vice verse
            if (!TensorShape.AreCompatible(destination._shape, x._shape, false))
                ThrowHelper.ThrowArgument_LengthsNotCompatible();
        }

        public static void ValidateCompatibility<TArg1, TArg2, TResult>(in ReadOnlyTensorSpan<TArg1> x, in ReadOnlyTensorSpan<TArg2> y, in TensorSpan<TResult> destination)
        {
            // can do bidirectional validation between x and y, that result can then be broadcast to destination
            if (TensorShape.AreCompatible(x._shape, y._shape, true))
            {
                if (TensorShape.AreCompatible(destination._shape, x._shape, false))
                {
                    if (TensorShape.AreCompatible(destination._shape, y._shape, false))
                    {
                        // all three are compatible
                        return;
                    }
                }
            }
            ThrowHelper.ThrowArgument_LengthsNotCompatible();
        }

        public static void ValidateCompatibility<TArg, TResult>(in ReadOnlyTensorSpan<TArg> x, in ReadOnlyTensorSpan<TArg> y, out Tensor<TResult> destination)
        {
            // can do bidirectional validation between x and y, that result can then be broadcast to destination
            if (TensorShape.AreCompatible(x._shape, y._shape, true))
            {
                ref readonly TensorShape xShape = ref TensorShape.Normalize(x._shape);
                ref readonly TensorShape yShape = ref TensorShape.Normalize(y._shape);
                ReadOnlySpan<nint> xLengths = xShape.Lengths;
                ReadOnlySpan<nint> yLengths = yShape.Lengths;
                int maxRank = Math.Max(xLengths.Length, yLengths.Length);

                scoped Span<nint> resultLengths = RentedBuffer.CreateUninitialized(maxRank, out RentedBuffer<nint> resultLengthsRentedBuffer);

                int xOffset = maxRank - xLengths.Length;
                int yOffset = maxRank - yLengths.Length;

                for (int i = 0; i < maxRank; i++)
                {
                    nint xLen = (i >= xOffset) ? xLengths[i - xOffset] : 1;
                    nint yLen = (i >= yOffset) ? yLengths[i - yOffset] : 1;
                    resultLengths[i] = (xLen == 1) ? yLen : xLen;
                }

                destination = Tensor.CreateFromShapeUninitialized<TResult>(resultLengths);

                resultLengthsRentedBuffer.Dispose();

                return;
            }
            destination = default!;
            ThrowHelper.ThrowArgument_LengthsNotCompatible();
        }

        public readonly struct Clear<T>
            : IOperation<T>
        {
            public static void Invoke(ref T destination)
            {
                destination = default!;
            }

            public static void Invoke(Span<T> destination)
            {
                destination.Clear();
            }
        }

        public readonly struct CopyTo<T>
            : IUnaryOperation_Tensor<T, T>
        {
            public static void Invoke(ref readonly T source, ref T destination)
            {
                destination = source;
            }

            public static void Invoke(ReadOnlySpan<T> source, Span<T> destination)
            {
                source.CopyTo(destination);
            }
        }

        public readonly struct Fill<T>
            : IUnaryOperation_Scalar<T, T>
        {
            public static void Invoke(ref T destination, T value)
            {
                destination = value;
            }

            public static void Invoke(Span<T> destination, T value)
            {
                destination.Fill(value);
            }
        }

        public interface IBinaryOperation_Tensor_Scalar<T, TResult>
            : IBinaryOperation_Tensor_Scalar<T, T, TResult>
        {
        }

        public interface IBinaryOperation_Tensor_Int32<T, TResult>
            : IBinaryOperation_Tensor_Scalar<T, int, TResult>
        {
        }

        public interface IBinaryOperation_Tensor_Scalar<T1, T2, TResult>
        {
            static abstract void Invoke(ref readonly T1 x, T2 y, ref TResult destination);
            static abstract void Invoke(ReadOnlySpan<T1> x, T2 y, Span<TResult> destination);
        }

        public interface IBinaryOperation_Scalar_Tensor<T1, T2, TResult>
        {
            static abstract void Invoke(T1 x, ref readonly T2 y, ref TResult destination);
            static abstract void Invoke(T1 x, ReadOnlySpan<T2> y, Span<TResult> destination);
        }

        public interface IBinaryOperation_Tensor_Tensor<T1, T2, TResult>
        {
            static abstract void Invoke(ref readonly T1 x, ref readonly T2 y, ref TResult destination);
            static abstract void Invoke(ReadOnlySpan<T1> x, ReadOnlySpan<T2> y, Span<TResult> destination);
        }

        public interface IBinaryOperation_Tensor_Tensor<T, TResult>
            : IBinaryOperation_Tensor_Tensor<T, T, TResult>
        {
        }

        public interface IOperation<T>
        {
            static abstract void Invoke(ref T destination);
            static abstract void Invoke(Span<T> destination);
        }

        public interface IUnaryOperation_Scalar<T, TResult>
        {
            static abstract void Invoke(ref TResult destination, T x);
            static abstract void Invoke(Span<TResult> destination, T x);
        }

        public interface IUnaryOperation_Tensor<T, TResult>
        {
            static abstract void Invoke(ref readonly T x, ref TResult destination);
            static abstract void Invoke(ReadOnlySpan<T> x, Span<TResult> destination);
        }

        public interface IUnaryReduction_Tensor<T, TResult>
        {
            static abstract void Invoke(ref readonly T x, ref TResult destination);
            static abstract void Invoke(ReadOnlySpan<T> x, ref TResult destination);
        }

        internal readonly struct RentedBuffer
        {
            public static Span<nint> Create(int rank, ReadOnlySpan<nint> strides, out nint linearOffset, [UnscopedRef] out RentedBuffer<nint> rentedBuffer)
            {
                Span<nint> output = RentedBuffer<nint>.Create(rank, out rentedBuffer);
                linearOffset = 0 - (!strides.IsEmpty ? strides[^1] : 0);

                if (!output.IsEmpty)
                {
                    output[^1] = -1;
                }
                return output;
            }

            public static Span<T> CreateUninitialized<T>(int rank, [UnscopedRef] out RentedBuffer<T> rentedBuffer)
                => RentedBuffer<T>.Create(rank, out rentedBuffer);
        }

        internal ref struct RentedBuffer<T> : IDisposable
        {
            private T[]? _array;
            private TensorShape.InlineBuffer<T> _inline;

            public static Span<T> Create(int rank, [UnscopedRef] out RentedBuffer<T> rentedBuffer)
            {
                if (rank > TensorShape.MaxInlineRank)
                {
                    rentedBuffer._array = ArrayPool<T>.Shared.Rent(rank);
                    Unsafe.SkipInit(out rentedBuffer._inline);

                    Span<T> resultBuffer = rentedBuffer._array.AsSpan(0, rank);
                    resultBuffer.Clear();
                    return resultBuffer;
                }
                else
                {
                    rentedBuffer._array = null;
                    rentedBuffer._inline = default;

                    return ((Span<T>)rentedBuffer._inline)[..rank];
                }
            }

            public void Dispose()
            {
                if (_array is not null)
                {
                    ArrayPool<T>.Shared.Return(_array);
                    _array = null;
                }
            }
        }
    }
}
