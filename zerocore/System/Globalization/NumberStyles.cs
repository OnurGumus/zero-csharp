namespace System.Globalization
{
    // What Parse and TryParse accept besides digits, numbered as in .NET.
    // There are no cultures: the signs are '+' and '-', the decimal point is
    // '.', the group separator ',' and the currency symbol '¤', as in .NET's
    // invariant culture.
    [Flags]
    public enum NumberStyles
    {
        None = 0,
        AllowLeadingWhite = 0x1,
        AllowTrailingWhite = 0x2,
        AllowLeadingSign = 0x4,
        AllowTrailingSign = 0x8,
        AllowParentheses = 0x10,
        AllowDecimalPoint = 0x20,
        AllowThousands = 0x40,
        AllowExponent = 0x80,
        AllowCurrencySymbol = 0x100,
        AllowHexSpecifier = 0x200,
        AllowBinarySpecifier = 0x400,

        Integer = AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign,
        HexNumber = AllowLeadingWhite | AllowTrailingWhite | AllowHexSpecifier,
        BinaryNumber = AllowLeadingWhite | AllowTrailingWhite | AllowBinarySpecifier,
        Number = AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign | AllowTrailingSign |
                 AllowDecimalPoint | AllowThousands,
        Float = AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign |
                AllowDecimalPoint | AllowExponent,
        Currency = AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign | AllowTrailingSign |
                   AllowParentheses | AllowDecimalPoint | AllowThousands | AllowCurrencySymbol,
        Any = AllowLeadingWhite | AllowTrailingWhite | AllowLeadingSign | AllowTrailingSign |
              AllowParentheses | AllowDecimalPoint | AllowThousands | AllowCurrencySymbol | AllowExponent,
    }
}
