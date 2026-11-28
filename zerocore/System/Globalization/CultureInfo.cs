using System.Runtime;

namespace System.Globalization
{
    // The invariant culture, and the common cultures in CultureData (about 80,
    // from .NET's own ICU data: scripts/gen-cultures), with their names,
    // numbers, dates and casing as .NET gives them. A name outside the table
    // throws CultureNotFoundException. Comparing and sorting stay ordinal for
    // every culture (there is no collation data), as in .NET's invariant
    // mode, and CurrentCulture starts as the invariant culture rather than
    // the system's. Only programs that look a culture up by name or number
    // link the table.
    //
    // CurrentCulture and CurrentUICulture belong to each thread, as in .NET:
    // a thread that hasn't set them gets DefaultThreadCurrentCulture, else
    // the invariant culture. Fibers on one thread share its setting (.NET's
    // follows each async flow).
    public class CultureInfo : IFormatProvider
    {
        internal const int LocaleInvariant = 0x007F;
        private const string InvariantName = "Invariant Language (Invariant Country)";
        private const string NotSupported = "Culture is not supported.";

        private static CultureInfo s_invariant, s_defaultThread, s_defaultThreadUI;

        // Each thread's current cultures, behind a pthread key of their own
        // (made when a thread first sets one) rather than [ThreadStatic],
        // which the Windows build can't compile inside the core library.
        private struct ThreadCultures
        {
            public nint Current, CurrentUI;
        }

        private static nuint s_key;
        private static int s_keyState; // 0 none, 1 being made, 2 ready

        private static unsafe ThreadCultures* Mine(bool create)
        {
            if (System.Threading.Volatile.Read(ref s_keyState) != 2)
            {
                if (!create)
                    return null;
                if (System.Threading.Interlocked.CompareExchange(ref s_keyState, 1, 0) == 0)
                {
                    nuint key = 0;
                    // No destructor: a thread that set a culture leaves its 16
                    // bytes when it ends (a callback from the system isn't
                    // possible in the WebAssembly and DOS builds).
                    if (Platform.pthread_key_create(&key, null) != 0)
                        Environment.FailFast("CultureInfo: no thread key");
                    s_key = key;
                    System.Threading.Volatile.Write(ref s_keyState, 2);
                }
                while (System.Threading.Volatile.Read(ref s_keyState) != 2)
                {
                    // another thread is making it
                }
            }
            ThreadCultures* mine = (ThreadCultures*)Platform.pthread_getspecific(s_key);
            if (mine == null && create)
            {
                mine = (ThreadCultures*)Platform.calloc(1, (nuint)sizeof(ThreadCultures));
                if (mine == null || Platform.pthread_setspecific(s_key, mine) != 0)
                    Environment.FailFast("CultureInfo: can't keep the thread's cultures");
            }
            return mine;
        }

        private static unsafe CultureInfo ThreadCulture(bool ui)
        {
            ThreadCultures* mine = Mine(false);
            if (mine == null)
                return null;
            nint value = ui ? mine->CurrentUI : mine->Current;
            return System.Runtime.CompilerServices.Unsafe.As<nint, CultureInfo>(ref value);
        }

        private static unsafe void SetThreadCulture(CultureInfo culture, bool ui)
        {
            ThreadCultures* mine = Mine(true);
            nint value = System.Runtime.CompilerServices.Unsafe.As<CultureInfo, nint>(ref culture);
            if (ui)
                mine->CurrentUI = value;
            else
                mine->Current = value;
        }

        private readonly bool _isReadOnly;
        private readonly bool _useUserOverride;
        // A named culture's (null for the invariant one), read from the table
        // when it is made, so these getters don't need the table.
        private readonly string _name, _englishName, _nativeName, _twoLetter, _threeLetter, _threeLetterWindows, _ietf, _parentName;
        private readonly int _lcid = LocaleInvariant, _keyboardLayoutId = LocaleInvariant;
        private readonly bool _isNeutral;
        private NumberFormatInfo _numberFormat;
        private DateTimeFormatInfo _dateTimeFormat;
        private TextInfo _textInfo;
        private CompareInfo _compareInfo;

        private static CultureInfo[] s_cultures; // GetCultureInfo's, by table row

        private CultureInfo(bool isReadOnly) => _isReadOnly = isReadOnly;

        // row: the table's row, or -1 for the invariant culture, which made
        // this way (new CultureInfo("")) is writable, with formats of its own.
        private CultureInfo(int row, bool isReadOnly, bool useUserOverride)
        {
            _isReadOnly = isReadOnly;
            if (row < 0)
            {
                _numberFormat = new NumberFormatInfo();
                _dateTimeFormat = new DateTimeFormatInfo();
                return;
            }
            _useUserOverride = useUserOverride;
            _name = CultureData.Text(row, CultureData.Name);
            _englishName = CultureData.Text(row, CultureData.EnglishName);
            _nativeName = CultureData.Text(row, CultureData.NativeName);
            _twoLetter = CultureData.Text(row, CultureData.TwoLetterISOLanguageName);
            _threeLetter = CultureData.Text(row, CultureData.ThreeLetterISOLanguageName);
            _threeLetterWindows = CultureData.Text(row, CultureData.ThreeLetterWindowsLanguageName);
            _ietf = CultureData.Text(row, CultureData.IetfLanguageTag);
            _parentName = CultureData.Text(row, CultureData.Parent);
            _lcid = CultureData.Int(row, CultureData.LCID);
            _keyboardLayoutId = CultureData.Int(row, CultureData.KeyboardLayoutId);
            _isNeutral = CultureData.Flag(row, CultureData.IsNeutralCulture);
            _numberFormat = CultureData.NumberFormat(row, isReadOnly);
            _dateTimeFormat = CultureData.DateTimeFormat(row, isReadOnly);
            _textInfo = new TextInfo(_name, _twoLetter, _lcid, CultureData.Text(row, CultureData.ListSeparator),
                CultureData.Int(row, CultureData.ANSICodePage), CultureData.Int(row, CultureData.OEMCodePage),
                CultureData.Int(row, CultureData.MacCodePage), CultureData.Int(row, CultureData.EBCDICCodePage),
                CultureData.Flag(row, CultureData.IsRightToLeft), isReadOnly);
            _compareInfo = new CompareInfo(_name, _lcid);
        }

        // A copy (Clone, ReadOnly).
        private CultureInfo(CultureInfo other, bool isReadOnly)
        {
            _isReadOnly = isReadOnly;
            _useUserOverride = other._useUserOverride;
            _name = other._name;
            _englishName = other._englishName;
            _nativeName = other._nativeName;
            _twoLetter = other._twoLetter;
            _threeLetter = other._threeLetter;
            _threeLetterWindows = other._threeLetterWindows;
            _ietf = other._ietf;
            _parentName = other._parentName;
            _lcid = other._lcid;
            _keyboardLayoutId = other._keyboardLayoutId;
            _isNeutral = other._isNeutral;
            _compareInfo = other._compareInfo;
            if (other._textInfo != null)
                _textInfo = isReadOnly ? TextInfo.ReadOnly(other._textInfo) : (TextInfo)other._textInfo.Clone();
            NumberFormatInfo numbers = other.NumberFormat;
            DateTimeFormatInfo dates = other.DateTimeFormat;
            _numberFormat = isReadOnly ? NumberFormatInfo.ReadOnly(numbers) : (NumberFormatInfo)numbers.Clone();
            _dateTimeFormat = isReadOnly ? DateTimeFormatInfo.ReadOnly(dates) : (DateTimeFormatInfo)dates.Clone();
        }

        // "" is the invariant culture; other names and numbers come from the
        // table, as writable copies.
        public CultureInfo(string name) : this(name, true) { }

        public CultureInfo(string name, bool useUserOverride) : this(Row(name, name), false, useUserOverride) { }

        public CultureInfo(int culture) : this(culture, true) { }

        public CultureInfo(int culture, bool useUserOverride) : this(Row(culture), false, useUserOverride) { }

        // The table's row, or -1 for the invariant culture; anything else throws.
        private static int Row(string name, string reported)
        {
            if (name == null)
                throw new ArgumentNullException("name");
            if (name.Length == 0)
                return -1;
            int row = CultureData.Find(name);
            if (row < 0)
                throw new CultureNotFoundException("name", reported, NotSupported);
            return row;
        }

        private static int Row(int culture)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(culture, "culture");
            if (culture == LocaleInvariant)
                return -1;
            // LOCALE_NEUTRAL, _USER_DEFAULT, _SYSTEM_DEFAULT, _CUSTOM_DEFAULT and
            // _CUSTOM_UNSPECIFIED are never a culture.
            int row = culture is 0x0000 or 0x0400 or 0x0800 or 0x0C00 or 0x1000 ? -1 : CultureData.FindLcid(culture);
            if (row < 0)
                throw new CultureNotFoundException("culture", culture, NotSupported);
            return row;
        }

        public static CultureInfo InvariantCulture => s_invariant ??= new CultureInfo(isReadOnly: true);

        public static CultureInfo CurrentCulture
        {
            get => ThreadCulture(false) ?? s_defaultThread ?? InvariantCulture;
            set
            {
                SetThreadCulture(value ?? throw new ArgumentNullException("value"), false);
                UseCurrentCasing();
            }
        }

        // From now on char.ToUpper(c) and string.ToUpper() ask the current
        // culture (see TextCasing.s_currentMode).
        private static unsafe void UseCurrentCasing() => TextCasing.s_currentMode = &CurrentCasingMode;

        private static int CurrentCasingMode() => CurrentCulture.TextInfo._mode;

        public static CultureInfo CurrentUICulture
        {
            get => ThreadCulture(true) ?? s_defaultThreadUI ?? InvariantCulture;
            set => SetThreadCulture(value ?? throw new ArgumentNullException("value"), true);
        }

        // Null until set, as in .NET.
        public static CultureInfo DefaultThreadCurrentCulture
        {
            get => s_defaultThread;
            set
            {
                s_defaultThread = value;
                UseCurrentCasing();
            }
        }

        public static CultureInfo DefaultThreadCurrentUICulture
        {
            get => s_defaultThreadUI;
            set => s_defaultThreadUI = value;
        }

        public static CultureInfo InstalledUICulture => InvariantCulture;

        // Read-only, and the same object each time for a name.
        public static CultureInfo GetCultureInfo(string name)
        {
            // .NET's cached lookup reports the name in lowercase (ASCII only).
            int row = Row(name, name == null ? null : LowerAscii(name));
            return row < 0 ? new CultureInfo(isReadOnly: true) : Cached(row);
        }

        public static CultureInfo GetCultureInfo(int culture)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(culture, "culture");
            int row = Row(culture);
            return row < 0 ? new CultureInfo(isReadOnly: true) : Cached(row);
        }

        private static CultureInfo Cached(int row)
        {
            CultureInfo[] cultures = s_cultures ??= new CultureInfo[CultureData.Count];
            return cultures[row] ??= new CultureInfo(row, true, false);
        }

        // The invariant culture (with the neutral ones) and the table's.
        public static CultureInfo[] GetCultures(CultureTypes types)
        {
            if ((types & ~CultureTypes.AllCultures & ~(CultureTypes.UserCustomCulture | CultureTypes.ReplacementCultures | CultureTypes.WindowsOnlyCultures | CultureTypes.FrameworkCultures)) != 0)
                throw new ArgumentOutOfRangeException("types", "Valid values are between 0 and 127, inclusive.");
            bool neutral = (types & CultureTypes.NeutralCultures) != 0, specific = (types & CultureTypes.SpecificCultures) != 0;
            int count = neutral ? 1 : 0;
            for (int i = 0; i < CultureData.Count; i++)
            {
                if (CultureData.Flag(i, CultureData.IsNeutralCulture) ? neutral : specific)
                    count++;
            }
            CultureInfo[] result = new CultureInfo[count];
            int n = 0;
            if (neutral)
                result[n++] = InvariantCulture;
            for (int i = 0; i < CultureData.Count; i++)
            {
                if (CultureData.Flag(i, CultureData.IsNeutralCulture) ? neutral : specific)
                    result[n++] = Cached(i);
            }
            return result;
        }

        public static CultureInfo ReadOnly(CultureInfo ci)
        {
            if (ci == null)
                throw new ArgumentNullException("ci");
            return ci._isReadOnly ? ci : new CultureInfo(ci, true);
        }

        public string Name => _name ?? "";
        public string IetfLanguageTag => _ietf ?? "";
        public string DisplayName => _englishName ?? InvariantName;
        public string EnglishName => _englishName ?? InvariantName;
        public string NativeName => _nativeName ?? InvariantName;
        public string TwoLetterISOLanguageName => _twoLetter ?? "iv";
        public string ThreeLetterISOLanguageName => _threeLetter ?? "ivl";
        public string ThreeLetterWindowsLanguageName => _threeLetterWindows ?? "IVL";
        public int LCID => _lcid;
        public int KeyboardLayoutId => _keyboardLayoutId;
        public bool IsNeutralCulture => _isNeutral;
        public bool IsReadOnly => _isReadOnly;
        public bool UseUserOverride => _useUserOverride;

        public CultureInfo Parent => _parentName == null || _parentName.Length == 0 ? InvariantCulture : GetCultureInfo(_parentName);

        // A read-only invariant culture's are the invariant ones; a writable
        // or named culture has its own, which Parse and ToString follow once
        // the culture is passed to them or made current.
        public NumberFormatInfo NumberFormat
        {
            get => _numberFormat ?? NumberFormatInfo.InvariantInfo;
            set
            {
                VerifyWritable();
                _numberFormat = value ?? throw new ArgumentNullException("value");
            }
        }

        public DateTimeFormatInfo DateTimeFormat
        {
            get => _dateTimeFormat ?? DateTimeFormatInfo.InvariantInfo;
            set
            {
                VerifyWritable();
                _dateTimeFormat = value ?? throw new ArgumentNullException("value");
            }
        }

        private void VerifyWritable()
        {
            if (_isReadOnly)
                throw new InvalidOperationException("Instance is read-only.");
        }

        // Casing by the culture's rules (Turkish i for tr and az); comparing
        // stays ordinal, as in .NET's invariant mode.
        public TextInfo TextInfo => _textInfo ?? TextInfo.Invariant;
        public CompareInfo CompareInfo => _compareInfo ?? CompareInfo.Invariant;

        public object GetFormat(Type formatType)
            => formatType == typeof(NumberFormatInfo) ? NumberFormat
            : formatType == typeof(DateTimeFormatInfo) ? DateTimeFormat : null;

        // A writable copy, as in .NET.
        public object Clone() => new CultureInfo(this, false);

        public void ClearCachedData() { }

        // Cultures with the same name are equal.
        public override bool Equals(object value) => value is CultureInfo other && other.Name == Name;
        public override int GetHashCode() => _name == null ? LocaleInvariant : _name.GetHashCode();
        public override string ToString() => Name;

        private static unsafe string LowerAscii(string s)
        {
            char[] chars = new char[s.Length];
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                chars[i] = c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
            }
            fixed (char* p = chars)
                return Number.FromChars(p, chars.Length);
        }
    }

    [Flags]
    public enum CultureTypes
    {
        NeutralCultures = 1,
        SpecificCultures = 2,
        InstalledWin32Cultures = 4,
        AllCultures = 7,
        UserCustomCulture = 8,
        ReplacementCultures = 16,
        WindowsOnlyCultures = 32,
        FrameworkCultures = 64,
    }

    // A culture name or number that isn't in the table.
    public class CultureNotFoundException : ArgumentException
    {
        private const string NotSupported = "Culture is not supported.";

        public CultureNotFoundException() : base(NotSupported) { }
        public CultureNotFoundException(string message) : base(message) { }
        public CultureNotFoundException(string paramName, string message) : base(message, paramName) { }

        public CultureNotFoundException(string paramName, string invalidCultureName, string message)
            : base(WithName(message, paramName, invalidCultureName == null ? null : invalidCultureName + " is an invalid culture identifier."))
        {
            InvalidCultureName = invalidCultureName;
        }

        public CultureNotFoundException(string paramName, int invalidCultureId, string message)
            : base(WithName(message, paramName, invalidCultureId.ToString() + " (0x" + invalidCultureId.ToString("x4") + ") is an invalid culture identifier."))
        {
            InvalidCultureId = invalidCultureId;
        }

        public string InvalidCultureName { get; }
        public int? InvalidCultureId { get; }

        private static string WithName(string message, string paramName, string invalid)
        {
            message ??= NotSupported;
            if (paramName != null)
                message = message + " (Parameter '" + paramName + "')";
            return invalid == null ? message : message + "\n" + invalid;
        }
    }
}
