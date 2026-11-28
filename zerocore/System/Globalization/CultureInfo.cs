namespace System.Globalization
{
    // The invariant culture, the only one there is, as in .NET with
    // InvariantGlobalization set: every culture-aware call here formats,
    // parses and compares as the invariant culture does, so code written for
    // .NET compiles, and asking for any other culture throws what .NET throws
    // in that mode.
    //
    // CurrentCulture and the rest are one setting for the whole program, not
    // one per thread: every culture that can exist behaves the same, so only
    // which object comes back could tell them apart.
    public class CultureInfo : IFormatProvider
    {
        internal const int LocaleInvariant = 0x007F;
        private const string InvariantName = "Invariant Language (Invariant Country)";
        private const string InvariantOnly = "Only the invariant culture is supported in globalization-invariant mode. See https://aka.ms/GlobalizationInvariantMode for more information.";

        private static CultureInfo s_invariant, s_current, s_currentUI, s_defaultThread, s_defaultThreadUI;

        private readonly bool _isReadOnly;

        private CultureInfo(bool isReadOnly) => _isReadOnly = isReadOnly;

        // Only "" (the invariant culture) exists; any other name throws
        // CultureNotFoundException, as .NET does in invariant mode. The
        // invariant culture has no user settings to override, so
        // useUserOverride makes no difference.
        public CultureInfo(string name) : this(name, true) { }

        public CultureInfo(string name, bool useUserOverride) => CheckName(name, name);

        // Only 127 (LOCALE_INVARIANT) exists.
        public CultureInfo(int culture) : this(culture, true) { }

        public CultureInfo(int culture, bool useUserOverride)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(culture, "culture");
            switch (culture)
            {
                case LocaleInvariant:
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
            get => s_current ?? s_defaultThread ?? InvariantCulture;
            set => s_current = value ?? throw new ArgumentNullException("value");
        }

        public static CultureInfo CurrentUICulture
        {
            get => s_currentUI ?? s_defaultThreadUI ?? InvariantCulture;
            set => s_currentUI = value ?? throw new ArgumentNullException("value");
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
            return ci._isReadOnly ? ci : new CultureInfo(isReadOnly: true);
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

        // The invariant numbers, read-only, for every culture: there is no
        // writable NumberFormatInfo yet.
        public NumberFormatInfo NumberFormat => NumberFormatInfo.InvariantInfo;

        public object GetFormat(Type formatType)
            => formatType == typeof(NumberFormatInfo) ? NumberFormat : null;

        // A writable copy, as in .NET.
        public object Clone() => new CultureInfo(isReadOnly: false);

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
