namespace System.Globalization
{
    // The invariant culture, the only one there is, as in .NET with
    // InvariantGlobalization set: every culture-aware call here formats,
    // parses and compares as the invariant culture does, so code written for
    // .NET compiles, and asking for any other culture throws what .NET throws
    // in that mode.
    //
    // CurrentCulture and CurrentUICulture belong to each thread, as in .NET:
    // a thread that hasn't set them gets DefaultThreadCurrentCulture, else
    // the invariant culture. Fibers on one thread share its setting (.NET's
    // follows each async flow).
    public class CultureInfo : IFormatProvider
    {
        internal const int LocaleInvariant = 0x007F;
        private const string InvariantName = "Invariant Language (Invariant Country)";
        private const string InvariantOnly = "Only the invariant culture is supported in globalization-invariant mode. See https://aka.ms/GlobalizationInvariantMode for more information.";

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
                    if (System.Runtime.Platform.pthread_key_create(&key, null) != 0)
                        Environment.FailFast("CultureInfo: no thread key");
                    s_key = key;
                    System.Threading.Volatile.Write(ref s_keyState, 2);
                }
                while (System.Threading.Volatile.Read(ref s_keyState) != 2)
                {
                    // another thread is making it
                }
            }
            ThreadCultures* mine = (ThreadCultures*)System.Runtime.Platform.pthread_getspecific(s_key);
            if (mine == null && create)
            {
                mine = (ThreadCultures*)System.Runtime.Platform.calloc(1, (nuint)sizeof(ThreadCultures));
                if (mine == null || System.Runtime.Platform.pthread_setspecific(s_key, mine) != 0)
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
        // A writable culture's own formats, made when first asked for.
        private NumberFormatInfo _numberFormat;
        private DateTimeFormatInfo _dateTimeFormat;

        private CultureInfo(bool isReadOnly) => _isReadOnly = isReadOnly;

        // Only "" (the invariant culture) exists; any other name throws
        // CultureNotFoundException, as .NET does in invariant mode. The
        // invariant culture has no user settings to override, so
        // useUserOverride makes no difference.
        public CultureInfo(string name) : this(name, true) { }

        public CultureInfo(string name, bool useUserOverride)
        {
            CheckName(name, name);
            MakeWritableFormats();
        }

        // A writable culture's formats are made with it, not when first asked
        // for: so only programs that make one link the code for formats of
        // their own (see Number.Culture.cs).
        private void MakeWritableFormats()
        {
            _numberFormat = new NumberFormatInfo();
            _dateTimeFormat = new DateTimeFormatInfo();
        }

        // Only 127 (LOCALE_INVARIANT) exists.
        public CultureInfo(int culture) : this(culture, true) { }

        public CultureInfo(int culture, bool useUserOverride)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(culture, "culture");
            switch (culture)
            {
                case LocaleInvariant:
                    MakeWritableFormats();
                    return;
                // LOCALE_NEUTRAL, _USER_DEFAULT, _SYSTEM_DEFAULT, _CUSTOM_DEFAULT
                // and _CUSTOM_UNSPECIFIED: never a culture, in any mode.
                case 0x0000:
                case 0x0400:
                case 0x0800:
                case 0x0C00:
                case 0x1000:
                    throw new CultureNotFoundException("culture", culture, "Culture is not supported.");
                default:
                    throw new CultureNotFoundException("culture", culture, InvariantOnly);
            }
        }

        public static CultureInfo InvariantCulture => s_invariant ??= new CultureInfo(isReadOnly: true);

        public static CultureInfo CurrentCulture
        {
            get => ThreadCulture(false) ?? s_defaultThread ?? InvariantCulture;
            set => SetThreadCulture(value ?? throw new ArgumentNullException("value"), false);
        }

        public static CultureInfo CurrentUICulture
        {
            get => ThreadCulture(true) ?? s_defaultThreadUI ?? InvariantCulture;
            set => SetThreadCulture(value ?? throw new ArgumentNullException("value"), true);
        }

        // Null until set, as in .NET.
        public static CultureInfo DefaultThreadCurrentCulture
        {
            get => s_defaultThread;
            set => s_defaultThread = value;
        }

        public static CultureInfo DefaultThreadCurrentUICulture
        {
            get => s_defaultThreadUI;
            set => s_defaultThreadUI = value;
        }

        public static CultureInfo InstalledUICulture => InvariantCulture;

        public static CultureInfo GetCultureInfo(string name)
        {
            // .NET's cached lookup reports the name in lowercase (ASCII only).
            CheckName(name, name == null ? null : LowerAscii(name));
            return new CultureInfo(isReadOnly: true);
        }

        // Checks the number differently from the constructor, as .NET does.
        public static CultureInfo GetCultureInfo(int culture)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(culture, "culture");
            if (culture != LocaleInvariant)
                throw new CultureNotFoundException("culture", culture, InvariantOnly);
            return new CultureInfo(isReadOnly: true);
        }

        public static CultureInfo ReadOnly(CultureInfo ci)
        {
            if (ci == null)
                throw new ArgumentNullException("ci");
            if (ci._isReadOnly)
                return ci;
            CultureInfo copy = new CultureInfo(isReadOnly: true);
            if (ci._numberFormat != null)
                copy._numberFormat = NumberFormatInfo.ReadOnly(ci._numberFormat);
            if (ci._dateTimeFormat != null)
                copy._dateTimeFormat = DateTimeFormatInfo.ReadOnly(ci._dateTimeFormat);
            return copy;
        }

        public string Name => "";
        public string IetfLanguageTag => "";
        public string DisplayName => InvariantName;
        public string EnglishName => InvariantName;
        public string NativeName => InvariantName;
        public string TwoLetterISOLanguageName => "iv";
        public string ThreeLetterISOLanguageName => "ivl";
        public string ThreeLetterWindowsLanguageName => "IVL";
        public int LCID => LocaleInvariant;
        public int KeyboardLayoutId => LocaleInvariant;
        public bool IsNeutralCulture => false;
        public bool IsReadOnly => _isReadOnly;
        public bool UseUserOverride => false;
        public CultureInfo Parent => InvariantCulture;

        // A read-only culture's are the invariant ones; a writable culture
        // (new CultureInfo(""), or a Clone) has writable copies of its own,
        // which Parse and ToString follow once the culture is passed to them
        // or made current.
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

        // Casing and comparing, the invariant way (ordinal, as in .NET's
        // invariant mode).
        public TextInfo TextInfo => TextInfo.Invariant;
        public CompareInfo CompareInfo => CompareInfo.Invariant;

        public object GetFormat(Type formatType)
            => formatType == typeof(NumberFormatInfo) ? NumberFormat
            : formatType == typeof(DateTimeFormatInfo) ? DateTimeFormat : null;

        // A writable copy, as in .NET.
        public object Clone()
        {
            CultureInfo copy = new CultureInfo(isReadOnly: false);
            copy._numberFormat = (NumberFormatInfo)NumberFormat.Clone();
            copy._dateTimeFormat = (DateTimeFormatInfo)DateTimeFormat.Clone();
            return copy;
        }

        public void ClearCachedData() { }

        // Every culture is the invariant one, so all are equal.
        public override bool Equals(object value) => value is CultureInfo;
        public override int GetHashCode() => LocaleInvariant;
        public override string ToString() => "";

        private static void CheckName(string name, string reported)
        {
            if (name == null)
                throw new ArgumentNullException("name");
            if (name.Length != 0)
                throw new CultureNotFoundException("name", reported, InvariantOnly);
        }

        private static unsafe string LowerAscii(string s)
        {
            string result = string.Allocate(s.Length);
            fixed (char* from = s)
            fixed (char* to = result)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = from[i];
                    to[i] = c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
                }
            }
            return result;
        }
    }

    // A culture name or number that doesn't exist: here, any but the
    // invariant culture's.
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
