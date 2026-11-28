using System.Text;

namespace System.Globalization
{
    // A culture's casing: one char (or surrogate pair) to one, by the simple
    // Unicode mapping, as .NET does. The invariant culture's is .NET's
    // invariant mode; a named culture's also maps İ down to i and ı up to I,
    // and Turkish and Azerbaijani map i up to İ and I down to ı, as .NET's
    // ICU casing does. ToTitleCase has .NET's rules (after dotnet/runtime's
    // TextInfo.cs, MIT): words in capitals stay so, an apostrophe doesn't
    // start a word, and Dutch cultures write "IJ" for "ij" at a word's start.
    public sealed class TextInfo
    {
        private static TextInfo s_invariant;

        private bool _isReadOnly;
        private readonly string _cultureName = "", _listSeparator = ",";
        private readonly int _lcid = CultureInfo.LocaleInvariant, _ansi = 1252, _oem = 437, _mac = 10000, _ebcdic = 37;
        private readonly bool _rightToLeft, _dutch;
        internal readonly int _mode; // TextCasing.Invariant, Culture or Turkish

        private TextInfo(bool isReadOnly) => _isReadOnly = isReadOnly;

        internal TextInfo(string cultureName, string twoLetterName, int lcid, string listSeparator, int ansi, int oem, int mac, int ebcdic, bool rightToLeft, bool isReadOnly)
        {
            _cultureName = cultureName;
            _lcid = lcid;
            _listSeparator = listSeparator;
            _ansi = ansi;
            _oem = oem;
            _mac = mac;
            _ebcdic = ebcdic;
            _rightToLeft = rightToLeft;
            _isReadOnly = isReadOnly;
            _mode = twoLetterName == "tr" || twoLetterName == "az" ? TextCasing.Turkish : TextCasing.Culture;
            _dutch = cultureName.Length > 3 && (cultureName[0] | 0x20) == 'n' && (cultureName[1] | 0x20) == 'l' && cultureName[2] == '-';
        }

        private TextInfo(TextInfo other, bool isReadOnly)
        {
            _cultureName = other._cultureName;
            _lcid = other._lcid;
            _listSeparator = other._listSeparator;
            _ansi = other._ansi;
            _oem = other._oem;
            _mac = other._mac;
            _ebcdic = other._ebcdic;
            _rightToLeft = other._rightToLeft;
            _mode = other._mode;
            _dutch = other._dutch;
            _isReadOnly = isReadOnly;
        }

        internal static TextInfo Invariant => s_invariant ??= new TextInfo(true);

        public string CultureName => _cultureName;
        public int LCID => _lcid;
        public int ANSICodePage => _ansi;
        public int OEMCodePage => _oem;
        public int MacCodePage => _mac;
        public int EBCDICCodePage => _ebcdic;
        public bool IsRightToLeft => _rightToLeft;
        public bool IsReadOnly => _isReadOnly;
        public string ListSeparator => _listSeparator;

        public object Clone() => new TextInfo(this, false);

        public static TextInfo ReadOnly(TextInfo textInfo)
        {
            if (textInfo == null)
                throw new ArgumentNullException("textInfo");
            return textInfo._isReadOnly ? textInfo : new TextInfo(textInfo, true);
        }

        public override bool Equals(object obj) => obj is TextInfo other && other._cultureName == _cultureName;
        public override int GetHashCode() => _cultureName.GetHashCode();
        public override string ToString() => "TextInfo - " + _cultureName;

        public char ToUpper(char c) => _mode == TextCasing.Invariant ? char.ToUpperInvariant(c) : (char)TextCasing.Change(c, true, _mode);
        public char ToLower(char c) => _mode == TextCasing.Invariant ? char.ToLowerInvariant(c) : (char)TextCasing.Change(c, false, _mode);

        public string ToUpper(string str) => TextCasing.Change(str ?? throw new ArgumentNullException("str"), true, _mode);
        public string ToLower(string str) => TextCasing.Change(str ?? throw new ArgumentNullException("str"), false, _mode);

        public string ToTitleCase(string str)
        {
            if (str == null)
                throw new ArgumentNullException("str");
            if (str.Length == 0)
                return str;
            StringBuilder result = new StringBuilder(str.Length);
            string lowercase = null;
            for (int i = 0; i < str.Length; i++)
            {
                UnicodeCategory category = Category(str, i, out int length);
                if (IsLetter(category))
                {
                    // Dutch writes "IJ" for a word's "ij"; otherwise the word's
                    // first letter in title case.
                    if (_dutch && i < str.Length - 1 && (str[i] == 'i' || str[i] == 'I') && (str[i + 1] == 'j' || str[i + 1] == 'J'))
                    {
                        result.Append("IJ");
                        i += 2;
                    }
                    else
                    {
                        i = AddTitleCase(result, str, i, length) + 1;
                    }
                    // The rest of the word in lower case, unless it has no
                    // lower case letters (an acronym such as "USA").
                    int lowercaseStart = i;
                    bool hasLowerCase = category == UnicodeCategory.LowercaseLetter;
                    while (i < str.Length)
                    {
                        category = Category(str, i, out length);
                        if (IsLetter(category))
                        {
                            if (category == UnicodeCategory.LowercaseLetter)
                                hasLowerCase = true;
                            i += length;
                        }
                        else if (str[i] == '\'')
                        {
                            i++;
                            if (hasLowerCase)
                            {
                                lowercase ??= ToLower(str);
                                result.Append(lowercase, lowercaseStart, i - lowercaseStart);
                            }
                            else
                            {
                                result.Append(str, lowercaseStart, i - lowercaseStart);
                            }
                            lowercaseStart = i;
                            hasLowerCase = true;
                        }
                        else if (!IsWordSeparator(category))
                        {
                            i += length;
                        }
                        else
                        {
                            break;
                        }
                    }
                    int count = i - lowercaseStart;
                    if (count > 0)
                    {
                        if (hasLowerCase)
                        {
                            lowercase ??= ToLower(str);
                            result.Append(lowercase, lowercaseStart, count);
                        }
                        else
                        {
                            result.Append(str, lowercaseStart, count);
                        }
                    }
                    if (i < str.Length)
                        i = AddNonLetter(result, str, i, length);
                }
                else
                {
                    i = AddNonLetter(result, str, i, length);
                }
            }
            return result.ToString();
        }

        private static UnicodeCategory Category(string s, int index, out int length)
        {
            char c = s[index];
            length = char.IsHighSurrogate(c) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]) ? 2 : 1;
            return CharUnicodeInfo.GetUnicodeCategory(s, index);
        }

        private static bool IsLetter(UnicodeCategory category) => category <= UnicodeCategory.OtherLetter;

        // Spaces, controls, punctuation and symbols end a word; marks,
        // digits and the rest belong to it.
        private static bool IsWordSeparator(UnicodeCategory category) => (WordSeparators & (1 << (int)category)) != 0;

        private const int WordSeparators =
            (1 << (int)UnicodeCategory.SpaceSeparator) | (1 << (int)UnicodeCategory.LineSeparator)
            | (1 << (int)UnicodeCategory.ParagraphSeparator) | (1 << (int)UnicodeCategory.Control)
            | (1 << (int)UnicodeCategory.Format) | (1 << (int)UnicodeCategory.ConnectorPunctuation)
            | (1 << (int)UnicodeCategory.DashPunctuation) | (1 << (int)UnicodeCategory.OpenPunctuation)
            | (1 << (int)UnicodeCategory.ClosePunctuation) | (1 << (int)UnicodeCategory.InitialQuotePunctuation)
            | (1 << (int)UnicodeCategory.FinalQuotePunctuation) | (1 << (int)UnicodeCategory.OtherPunctuation)
            | (1 << (int)UnicodeCategory.MathSymbol) | (1 << (int)UnicodeCategory.CurrencySymbol)
            | (1 << (int)UnicodeCategory.ModifierSymbol) | (1 << (int)UnicodeCategory.OtherSymbol);

        private static int AddNonLetter(StringBuilder result, string input, int index, int length)
        {
            if (length == 2)
                result.Append(input[index++]);
            result.Append(input[index]);
            return index;
        }

        private int AddTitleCase(StringBuilder result, string input, int index, int length)
        {
            if (length == 2)
            {
                int cp = TextCasing.Change(char.ConvertToUtf32(input[index], input[index + 1]), true, _mode) - 0x10000;
                result.Append((char)(0xD800 + (cp >> 10)));
                result.Append((char)(0xDC00 + (cp & 0x3FF)));
                return index + 1;
            }
            switch (input[index])
            {
                // The title case letters .NET uses for the digraphs.
                case (char)0x01C4: case (char)0x01C5: case (char)0x01C6: result.Append((char)0x01C5); break;
                case (char)0x01C7: case (char)0x01C8: case (char)0x01C9: result.Append((char)0x01C8); break;
                case (char)0x01CA: case (char)0x01CB: case (char)0x01CC: result.Append((char)0x01CB); break;
                case (char)0x01F1: case (char)0x01F2: case (char)0x01F3: result.Append((char)0x01F2); break;
                default: result.Append(ToUpper(input[index])); break;
            }
            return index;
        }
    }
}
