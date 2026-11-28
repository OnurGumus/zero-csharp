namespace System.Globalization
{
    // The invariant culture's numbers, read-only: the symbols the parsing
    // and formatting code uses, with .NET's names and values. There is no
    // public constructor and there are no setters yet, so a custom decimal
    // separator doesn't compile instead of being silently ignored.
    public sealed class NumberFormatInfo : IFormatProvider
    {
        private static NumberFormatInfo s_invariant;
        private static int[] s_groupSizes;
        private static string[] s_nativeDigits;

        private NumberFormatInfo() { }

        public static NumberFormatInfo InvariantInfo => s_invariant ??= new NumberFormatInfo();

        public static NumberFormatInfo CurrentInfo => InvariantInfo;

        // What .NET's GetInstance finds: a culture's NumberFormat, the
        // NumberFormatInfo itself, or the one GetFormat returns; else the
        // current culture's.
        public static NumberFormatInfo GetInstance(IFormatProvider formatProvider)
        {
            if (formatProvider is CultureInfo culture)
                return culture.NumberFormat;
            if (formatProvider is NumberFormatInfo info)
                return info;
            if (formatProvider != null && formatProvider.GetFormat(typeof(NumberFormatInfo)) is NumberFormatInfo provided)
                return provided;
            return CurrentInfo;
        }

        public static NumberFormatInfo ReadOnly(NumberFormatInfo nfi)
            => nfi ?? throw new ArgumentNullException("nfi");

        public object GetFormat(Type formatType)
            => formatType == typeof(NumberFormatInfo) ? this : null;

        public bool IsReadOnly => true;

        public string NegativeSign => "-";
        public string PositiveSign => "+";
        public string NaNSymbol => "NaN";
        public string PositiveInfinitySymbol => "Infinity";
        public string NegativeInfinitySymbol => "-Infinity";

        public string NumberDecimalSeparator => ".";
        public string NumberGroupSeparator => ",";
        public int NumberDecimalDigits => 2;
        public int NumberNegativePattern => 1;

        public string CurrencySymbol => "\u00A4";
        public string CurrencyDecimalSeparator => ".";
        public string CurrencyGroupSeparator => ",";
        public int CurrencyDecimalDigits => 2;
        public int CurrencyPositivePattern => 0;
        public int CurrencyNegativePattern => 0;

        public string PercentSymbol => "%";
        public string PerMilleSymbol => "\u2030";
        public string PercentDecimalSeparator => ".";
        public string PercentGroupSeparator => ",";
        public int PercentDecimalDigits => 2;
        public int PercentPositivePattern => 0;
        public int PercentNegativePattern => 0;

        public DigitShapes DigitSubstitution => DigitShapes.None;

        // Arrays, as in .NET; each call returns a new copy there, so callers
        // can't change the shared one. Here they share one each: don't
        // change it.
        public int[] NumberGroupSizes => GroupSizes;
        public int[] CurrencyGroupSizes => GroupSizes;
        public int[] PercentGroupSizes => GroupSizes;

        public string[] NativeDigits => s_nativeDigits ??= new string[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };

        private static int[] GroupSizes => s_groupSizes ??= new int[] { 3 };
    }

    public enum DigitShapes
    {
        Context = 0,
        None = 1,
        NativeNational = 2,
    }
}
