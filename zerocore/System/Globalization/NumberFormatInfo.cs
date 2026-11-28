namespace System.Globalization
{
    // The symbols numbers are written and read with, as .NET's: the invariant
    // culture's (InvariantInfo, read-only), or your own. `new
    // NumberFormatInfo()` starts as a writable copy of the invariant one:
    // set NumberDecimalSeparator to "," and Parse and ToString given it (or
    // CurrentCulture, once a culture with it is current) use ",".
    //
    // Programs that never make a writable one keep the invariant formatting
    // and nothing else: making one is what links the code that reads these
    // symbols (Number.Culture.cs).
    public sealed class NumberFormatInfo : IFormatProvider
    {
        private static NumberFormatInfo s_invariant;

        private bool _isReadOnly;
        internal string _negativeSign = "-", _positiveSign = "+", _nanSymbol = "NaN",
            _positiveInfinitySymbol = "Infinity", _negativeInfinitySymbol = "-Infinity";
        internal string _numberDecimalSeparator = ".", _numberGroupSeparator = ",";
        internal string _currencySymbol = "\u00A4", _currencyDecimalSeparator = ".", _currencyGroupSeparator = ",";
        internal string _percentSymbol = "%", _perMilleSymbol = "\u2030", _percentDecimalSeparator = ".", _percentGroupSeparator = ",";
        internal int _numberDecimalDigits = 2, _currencyDecimalDigits = 2, _percentDecimalDigits = 2;
        internal int _numberNegativePattern = 1, _currencyPositivePattern, _currencyNegativePattern,
            _percentPositivePattern, _percentNegativePattern;
        internal int[] _numberGroupSizes = { 3 }, _currencyGroupSizes = { 3 }, _percentGroupSizes = { 3 };
        private DigitShapes _digitSubstitution = DigitShapes.None;

        // The invariant one: read-only, and the formatting code knows its
        // symbols without reading them.
        private NumberFormatInfo(bool invariant) => _isReadOnly = invariant;

        public NumberFormatInfo()
        {
            Number.UseCultures();
        }

        public static NumberFormatInfo InvariantInfo => s_invariant ??= new NumberFormatInfo(true);

        public static NumberFormatInfo CurrentInfo => CultureInfo.CurrentCulture.NumberFormat;

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
        {
            if (nfi == null)
                throw new ArgumentNullException("nfi");
            if (nfi._isReadOnly)
                return nfi;
            NumberFormatInfo copy = (NumberFormatInfo)nfi.Clone();
            copy._isReadOnly = true;
            return copy;
        }

        // A writable copy.
        public object Clone()
        {
            NumberFormatInfo copy = new NumberFormatInfo();
            copy._negativeSign = _negativeSign;
            copy._positiveSign = _positiveSign;
            copy._nanSymbol = _nanSymbol;
            copy._positiveInfinitySymbol = _positiveInfinitySymbol;
            copy._negativeInfinitySymbol = _negativeInfinitySymbol;
            copy._numberDecimalSeparator = _numberDecimalSeparator;
            copy._numberGroupSeparator = _numberGroupSeparator;
            copy._currencySymbol = _currencySymbol;
            copy._currencyDecimalSeparator = _currencyDecimalSeparator;
            copy._currencyGroupSeparator = _currencyGroupSeparator;
            copy._percentSymbol = _percentSymbol;
            copy._perMilleSymbol = _perMilleSymbol;
            copy._percentDecimalSeparator = _percentDecimalSeparator;
            copy._percentGroupSeparator = _percentGroupSeparator;
            copy._numberDecimalDigits = _numberDecimalDigits;
            copy._currencyDecimalDigits = _currencyDecimalDigits;
            copy._percentDecimalDigits = _percentDecimalDigits;
            copy._numberNegativePattern = _numberNegativePattern;
            copy._currencyPositivePattern = _currencyPositivePattern;
            copy._currencyNegativePattern = _currencyNegativePattern;
            copy._percentPositivePattern = _percentPositivePattern;
            copy._percentNegativePattern = _percentNegativePattern;
            copy._numberGroupSizes = _numberGroupSizes;
            copy._currencyGroupSizes = _currencyGroupSizes;
            copy._percentGroupSizes = _percentGroupSizes;
            copy._digitSubstitution = _digitSubstitution;
            return copy;
        }

        public object GetFormat(Type formatType) => formatType == typeof(NumberFormatInfo) ? this : null;

        public bool IsReadOnly => _isReadOnly;

        private void VerifyWritable()
        {
            if (_isReadOnly)
                throw new InvalidOperationException("Instance is read-only.");
        }

        private string Symbol(string value)
        {
            VerifyWritable();
            return value ?? throw new ArgumentNullException("value");
        }

        private string DecimalSeparator(string value)
        {
            VerifyWritable();
            if (value == null)
                throw new ArgumentNullException("value");
            if (value.Length == 0)
                throw new ArgumentException("The value cannot be an empty string.", "value");
            return value;
        }

        private int Range(int value, int max)
        {
            VerifyWritable();
            if (value < 0 || value > max)
                throw new ArgumentOutOfRangeException("value", value, "Valid values are between 0 and " + max.ToString() + ", inclusive.");
            return value;
        }

        // 1 to 9 each, except a last 0 (no more grouping); a copy is kept.
        private int[] GroupSizes(int[] value, string propertyName)
        {
            VerifyWritable();
            if (value == null)
                throw new ArgumentNullException(propertyName);
            int[] copy = new int[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                int size = value[i];
                if (size < 1 ? !(size == 0 && i == value.Length - 1) : size > 9)
                    throw new ArgumentException("Every element in the value array should be between one and nine, except for the last element, which can be zero.", propertyName);
                copy[i] = size;
            }
            return copy;
        }

        private static int[] Copy(int[] sizes)
        {
            int[] copy = new int[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
                copy[i] = sizes[i];
            return copy;
        }

        public string NegativeSign { get => _negativeSign; set => _negativeSign = Symbol(value); }
        public string PositiveSign { get => _positiveSign; set => _positiveSign = Symbol(value); }
        public string NaNSymbol { get => _nanSymbol; set => _nanSymbol = Symbol(value); }
        public string PositiveInfinitySymbol { get => _positiveInfinitySymbol; set => _positiveInfinitySymbol = Symbol(value); }
        public string NegativeInfinitySymbol { get => _negativeInfinitySymbol; set => _negativeInfinitySymbol = Symbol(value); }

        public string NumberDecimalSeparator { get => _numberDecimalSeparator; set => _numberDecimalSeparator = DecimalSeparator(value); }
        public string NumberGroupSeparator { get => _numberGroupSeparator; set => _numberGroupSeparator = Symbol(value); }
        public int NumberDecimalDigits { get => _numberDecimalDigits; set => _numberDecimalDigits = Range(value, 99); }
        public int NumberNegativePattern { get => _numberNegativePattern; set => _numberNegativePattern = Range(value, 4); }
        public int[] NumberGroupSizes { get => Copy(_numberGroupSizes); set => _numberGroupSizes = GroupSizes(value, "value"); }

        public string CurrencySymbol { get => _currencySymbol; set => _currencySymbol = Symbol(value); }
        public string CurrencyDecimalSeparator { get => _currencyDecimalSeparator; set => _currencyDecimalSeparator = DecimalSeparator(value); }
        public string CurrencyGroupSeparator { get => _currencyGroupSeparator; set => _currencyGroupSeparator = Symbol(value); }
        public int CurrencyDecimalDigits { get => _currencyDecimalDigits; set => _currencyDecimalDigits = Range(value, 99); }
        public int CurrencyPositivePattern { get => _currencyPositivePattern; set => _currencyPositivePattern = Range(value, 3); }
        public int CurrencyNegativePattern { get => _currencyNegativePattern; set => _currencyNegativePattern = Range(value, 16); }
        public int[] CurrencyGroupSizes { get => Copy(_currencyGroupSizes); set => _currencyGroupSizes = GroupSizes(value, "value"); }

        public string PercentSymbol { get => _percentSymbol; set => _percentSymbol = Symbol(value); }
        public string PerMilleSymbol { get => _perMilleSymbol; set => _perMilleSymbol = Symbol(value); }
        public string PercentDecimalSeparator { get => _percentDecimalSeparator; set => _percentDecimalSeparator = DecimalSeparator(value); }
        public string PercentGroupSeparator { get => _percentGroupSeparator; set => _percentGroupSeparator = Symbol(value); }
        public int PercentDecimalDigits { get => _percentDecimalDigits; set => _percentDecimalDigits = Range(value, 99); }
        public int PercentPositivePattern { get => _percentPositivePattern; set => _percentPositivePattern = Range(value, 3); }
        public int PercentNegativePattern { get => _percentNegativePattern; set => _percentNegativePattern = Range(value, 11); }
        public int[] PercentGroupSizes { get => Copy(_percentGroupSizes); set => _percentGroupSizes = GroupSizes(value, "value"); }

        public DigitShapes DigitSubstitution
        {
            get => _digitSubstitution;
            set
            {
                VerifyWritable();
                if ((uint)value > (uint)DigitShapes.NativeNational)
                    throw new ArgumentException("The DigitSubstitution property must be of a valid member of the DigitShapes enumeration. Valid entries include Context, NativeNational or None.", "value");
                _digitSubstitution = value;
            }
        }

        public string[] NativeDigits => new string[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };

        // .NET also takes '-' for a negative sign that is another dash.
        internal bool AllowHyphenDuringParsing
        {
            get
            {
                string sign = _negativeSign;
                if (sign.Length != 1)
                    return false;
                switch (sign[0])
                {
                    case '\u2012': case '\u207B': case '\u208B': case '\u2212': case '\u2796': case '\uFE63': case '\uFF0D':
                        return true;
                    default:
                        return false;
                }
            }
        }
    }

    public enum DigitShapes
    {
        Context = 0,
        None = 1,
        NativeNational = 2,
    }
}
