namespace System
{
    // The System.Numerics.Tensors package's messages (its Strings.resx).
    internal static class SR
    {
        internal static string Format(string format, object arg0) => string.Format(format, arg0);
        internal static string Format(string format, object arg0, object arg1) => string.Format(format, arg0, arg1);

        internal const string Argument_DestinationTooShort = "Destination is too short.";
        internal const string Argument_SpansMustBeNonEmpty = "Input span arguments must not be empty.";
        internal const string Argument_SpansMustHaveSameLength = "Input span arguments must all have the same length.";
        internal const string Argument_InputAndDestinationSpanMustNotOverlap = "The destination span may only overlap with an input span if the two spans start at the same memory location.";
        internal const string Argument_OverlappingTensorLayoutsNotSupported = "The input and destination tensor spans overlap in an unsupported layout.";
        internal const string Argument_DestinationSpansMustNotOverlap = "Destination spans must not overlap with each other.";
        internal const string Overflow_NegateTwosCompNum = "Negating the minimum value of a twos complement number is invalid.";
        internal const string Argument_InvalidEnumValue = "The value '{0}' is not valid for this usage of the type {1}.";
        internal const string InvalidOperation_EnumerationNotPositioned = "Enumeration has not started or has already finished.";
        internal const string Argument_TypeContainsReferences = "The type '{0}' is not supported because it contains references.";
        internal const string DestinationTooShort = "Destination is too short.";
        internal const string NotSupported_CannotCallEqualsOnSpan = "Equals() on TensorSpan and ReadOnlyTensorSpan is not supported. Use operator== instead.";
        internal const string NotSupported_CannotCallGetHashCodeOnSpan = "GetHashCode() on TensorSpan and ReadOnlyTensorSpan is not supported.";
        internal const string ThrowArgument_IndicesLengthMustEqualRank = "Number of Indices must equal the rank of the TensorSpan.";
        internal const string ThrowArgument_LengthsMustEqualArrayLength = "The total length specified by the lengths must equal the length of the array.";
        internal const string ThrowArgument_1DTensorRequired = "Must be a 1d tensor";
        internal const string ThrowArgument_AxisLargerThanRank = "Cannot select an axis greater than the current Rank";
        internal const string ThrowArgument_ConcatenateTooFewTensors = "Must provide at least 2 tensors to Concatenate";
        internal const string ThrowArgument_DimensionsNotSame = "Number of dimensions to slice does not equal the number of dimensions in the span";
        internal const string ThrowArgument_DestinationHasOverlappingElements = "The destination contains overlapping elements and cannot represent distinct output values.";
        internal const string ThrowArgument_FilterTensorMustEqualTensorLength = "The total length of the filter tensor must equal the length of the tensor to be filtered.";
        internal const string ThrowArgument_IncorrectNumberOfFilterItems = "Number of elements provided does not match the number of filters.";
        internal const string ThrowArgument_InPlaceInvalidShape = "In place operations require the same shape for both tensors";
        internal const string ThrowArgument_InvalidDimension = "Invalid dimension provided. Must be greater then or equal to 0 and less than the tensor rank.";
        internal const string ThrowArgument_InvalidConcatenateShape = "The tensors must have the same shape, except in the dimension corresponding to axis.";
        internal const string ThrowArgument_InvalidReshapeDimensions = "Provided dimensions are not valid for reshaping";
        internal const string ThrowArgument_InvalidSqueezeAxis = "Cannot select an axis to squeeze which has size not equal to one";
        internal const string ThrowArgument_OnlyOneWildcard = "Provided dimensions can only include 1 wildcard.";
        internal const string ThrowArgument_PermuteAxisOrder = "Must provide an axis order for each axis";
        internal const string ThrowArgument_SetSliceInvalidShapes = "Provided values must have the same shape as the input tensor.";
        internal const string ThrowArgument_SetSliceNoRange = "When no ranges are specified the values tensor must be equal in size as the input tensor.";
        internal const string ThrowArgument_LengthsNotCompatible = "Lengths are not compatible with each other.";
        internal const string ThrowArgument_SplitNotSplitEvenly = "The number of splits must perfectly divide the dimension.";
        internal const string ThrowArgument_StackTooFewTensors = "Must provide at least 2 tensors to Stack.";
        internal const string ThrowArgument_TransposeTooFewDimensions = "Must provide a tensor with at least 2 dimensions to transpose it.";
        internal const string ThrowArgument_ValueNonNegative = "The provided value must be non-negative.";
        internal const string ThrowArgument_InvalidStridesAndLengths = "The provided lengths and strides would allow you to access elements outside the provided memory.";
        internal const string ThrowArgument_StrideLessThan0 = "Strides cannot be less than 0.";
        internal const string Argument_2DTensorRequired = "Must be a 2d Tensor.";
        internal const string Argument_IncompatibleDimensions = "Incompatible dimensions for provided tensors. left.Lengths[1] == {0} while right.Lengths[1] == {1}.";
        internal const string ThrowArgument_StackShapesNotSame = "All tensors must have the same shape.";
        internal const string Argument_CannotReshapeNonContiguousOrDense = "The Tensor provided is either non-contiguous or non-dense. Reshape only works with contigous and dense memory. You may need to Broadcast or Copy the data to be contigous.";
        internal const string Arithmetic_NaN = "Function does not accept floating point Not-a-Number values.";
        internal const string ThrowArgument_LengthIsNonZeroForNullReference = "The provided length is non zero while the data reference is null.";
    }
}
