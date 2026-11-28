// Writes zerocore/System/Globalization/CultureData.cs: what zerocore's
// CultureInfo, NumberFormatInfo, DateTimeFormatInfo and TextInfo need to give
// .NET's answers for a set of common cultures, read from the .NET this tool
// runs on (with ICU: InvariantGlobalization is off); and, with Collation.cs,
// CollationData.cs: how each of them sorts text.
//
//   dotnet run --project scripts/gen-cultures
//
// Each culture is a row of fields, each field an index into a table of
// distinct strings, kept as UTF-8 data (lists joined by U+0001). Cultures
// whose default calendar isn't the Gregorian one are left out: zerocore has
// only that calendar.

using System.Globalization;
using System.Text;

string[] specific =
{
    "en-US", "en-GB", "en-CA", "en-AU", "en-IN", "de-DE", "de-AT", "de-CH", "fr-FR", "fr-CA", "fr-CH", "fr-BE",
    "es-ES", "es-MX", "es-AR", "es-US", "it-IT", "pt-BR", "pt-PT", "nl-NL", "nl-BE", "sv-SE", "nb-NO", "da-DK",
    "fi-FI", "pl-PL", "cs-CZ", "sk-SK", "hu-HU", "ro-RO", "bg-BG", "el-GR", "tr-TR", "az-Latn-AZ", "ru-RU", "uk-UA",
    "he-IL", "ar-EG", "hi-IN", "ja-JP", "ko-KR", "zh-CN", "zh-TW", "zh-HK", "id-ID", "vi-VN", "ms-MY",
};

// The cultures and their parents, up to (not including) the invariant one.
var names = new List<string>();
foreach (string name in specific)
{
    for (var c = CultureInfo.GetCultureInfo(name); c.Name.Length > 0; c = c.Parent)
    {
        if (c.Calendar is not GregorianCalendar)
            throw new Exception($"{c.Name}'s calendar is {c.Calendar.GetType().Name}");
        if (!names.Contains(c.Name))
            names.Add(c.Name);
    }
}
names.Sort(StringComparer.OrdinalIgnoreCase);

string[] fields =
{
    "Name", "EnglishName", "NativeName", "TwoLetterISOLanguageName", "ThreeLetterISOLanguageName", "ThreeLetterWindowsLanguageName",
    "IetfLanguageTag", "LCID", "KeyboardLayoutId", "Parent", "IsNeutralCulture", "IsRightToLeft", "ListSeparator",
    "ANSICodePage", "OEMCodePage", "MacCodePage", "EBCDICCodePage",
    "NegativeSign", "PositiveSign", "NaNSymbol", "PositiveInfinitySymbol", "NegativeInfinitySymbol",
    "NumberDecimalSeparator", "NumberGroupSeparator", "NumberDecimalDigits", "NumberNegativePattern", "NumberGroupSizes",
    "CurrencySymbol", "CurrencyDecimalSeparator", "CurrencyGroupSeparator", "CurrencyDecimalDigits", "CurrencyPositivePattern",
    "CurrencyNegativePattern", "CurrencyGroupSizes",
    "PercentSymbol", "PerMilleSymbol", "PercentDecimalSeparator", "PercentGroupSeparator", "PercentDecimalDigits",
    "PercentPositivePattern", "PercentNegativePattern", "PercentGroupSizes", "DigitSubstitution", "NativeDigits",
    "AMDesignator", "PMDesignator", "DateSeparator", "TimeSeparator", "FirstDayOfWeek", "CalendarWeekRule",
    "FullDateTimePattern", "LongDatePattern", "LongTimePattern", "ShortDatePattern", "ShortTimePattern", "MonthDayPattern",
    "YearMonthPattern", "DayNames", "AbbreviatedDayNames", "ShortestDayNames", "MonthNames", "AbbreviatedMonthNames",
    "MonthGenitiveNames", "AbbreviatedMonthGenitiveNames", "EraName", "AbbreviatedEraName", "NativeCalendarName",
    "ShortDatePatterns", "LongDatePatterns", "ShortTimePatterns", "LongTimePatterns", "YearMonthPatterns",
};

const char ListSeparator = '\u0001';
string Join(IEnumerable<string> items) => string.Join(ListSeparator, items);
string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
string Sizes(int[] sizes) => string.Join(",", sizes.Select(Int));

string[] Row(CultureInfo c)
{
    var n = c.NumberFormat;
    var d = c.DateTimeFormat;
    var t = c.TextInfo;
    return
    [
        c.Name, c.EnglishName, c.NativeName, c.TwoLetterISOLanguageName, c.ThreeLetterISOLanguageName, c.ThreeLetterWindowsLanguageName,
        c.IetfLanguageTag, Int(c.LCID), Int(c.KeyboardLayoutId), c.Parent.Name, c.IsNeutralCulture ? "1" : "0", t.IsRightToLeft ? "1" : "0",
        t.ListSeparator, Int(t.ANSICodePage), Int(t.OEMCodePage), Int(t.MacCodePage), Int(t.EBCDICCodePage),
        n.NegativeSign, n.PositiveSign, n.NaNSymbol, n.PositiveInfinitySymbol, n.NegativeInfinitySymbol,
        n.NumberDecimalSeparator, n.NumberGroupSeparator, Int(n.NumberDecimalDigits), Int(n.NumberNegativePattern), Sizes(n.NumberGroupSizes),
        n.CurrencySymbol, n.CurrencyDecimalSeparator, n.CurrencyGroupSeparator, Int(n.CurrencyDecimalDigits), Int(n.CurrencyPositivePattern),
        Int(n.CurrencyNegativePattern), Sizes(n.CurrencyGroupSizes),
        n.PercentSymbol, n.PerMilleSymbol, n.PercentDecimalSeparator, n.PercentGroupSeparator, Int(n.PercentDecimalDigits),
        Int(n.PercentPositivePattern), Int(n.PercentNegativePattern), Sizes(n.PercentGroupSizes), Int((int)n.DigitSubstitution), Join(n.NativeDigits),
        d.AMDesignator, d.PMDesignator, d.DateSeparator, d.TimeSeparator, Int((int)d.FirstDayOfWeek), Int((int)d.CalendarWeekRule),
        d.FullDateTimePattern, d.LongDatePattern, d.LongTimePattern, d.ShortDatePattern, d.ShortTimePattern, d.MonthDayPattern,
        d.YearMonthPattern, Join(d.DayNames), Join(d.AbbreviatedDayNames), Join(d.ShortestDayNames), Join(d.MonthNames), Join(d.AbbreviatedMonthNames),
        Join(d.MonthGenitiveNames), Join(d.AbbreviatedMonthGenitiveNames), d.GetEraName(1), d.GetAbbreviatedEraName(1), d.NativeCalendarName,
        Join(d.GetAllDateTimePatterns('d')), Join(d.GetAllDateTimePatterns('D')), Join(d.GetAllDateTimePatterns('t')),
        Join(d.GetAllDateTimePatterns('T')), Join(d.GetAllDateTimePatterns('y')),
    ];
}

var strings = new List<string>();
var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
int Intern(string s)
{
    if (!indexOf.TryGetValue(s, out int i))
    {
        i = strings.Count;
        strings.Add(s);
        indexOf[s] = i;
    }
    return i;
}

var records = new List<byte>();
foreach (string name in names)
{
    string[] row = Row(CultureInfo.GetCultureInfo(name));
    if (row.Length != fields.Length)
        throw new Exception("field count");
    foreach (string value in row)
    {
        int i = Intern(value);
        if (i > 0xFFFF)
            throw new Exception("too many strings");
        records.AddRange([(byte)i, (byte)(i >> 8)]);
    }
}

var pool = new List<byte>();
var table = new List<byte>();
foreach (string s in strings)
{
    byte[] bytes = Encoding.UTF8.GetBytes(s);
    int start = pool.Count;
    if (bytes.Length > 0xFFFF || start > 0xFFFFFF)
        throw new Exception("pool too large");
    table.AddRange([(byte)start, (byte)(start >> 8), (byte)(start >> 16), (byte)bytes.Length, (byte)(bytes.Length >> 8)]);
    pool.AddRange(bytes);
}

var sb = new StringBuilder();
sb.Append($$"""
// <auto-generated>
// Written by scripts/gen-cultures from .NET {{Environment.Version}}'s culture data (ICU).
// Don't edit; rerun: dotnet run --project scripts/gen-cultures
// </auto-generated>

namespace System.Globalization
{
    internal static partial class CultureData
    {
        // {{names.Count}} cultures, sorted by name, ignoring case:
        // {{string.Join(" ", names)}}
        internal const int Count = {{names.Count}};
        internal const int FieldCount = {{fields.Length}};

""");
for (int i = 0; i < fields.Length; i++)
    sb.Append($"        internal const int {fields[i]} = {i};\n");
sb.Append('\n');
Emit(sb, "Records", records, "Each culture's fields: indices into Strings, 2 bytes each, little-endian.");
Emit(sb, "Strings", table, "Each string: where it starts in Pool (3 bytes) and its length (2 bytes), little-endian.");
Emit(sb, "Pool", pool, "The strings, as UTF-8; lists are joined by U+0001.");
sb.Append("    }\n}\n");

string path = Path.Combine(FindRoot(), "zerocore/System/Globalization/CultureData.cs");
File.WriteAllText(path, sb.ToString());
Console.WriteLine($"{path}: {names.Count} cultures, {strings.Count} strings, {records.Count + table.Count + pool.Count} bytes of tables");

var collationLog = new StringBuilder();
Collation.Write(names, FindRoot(), collationLog);
Console.Write(collationLog);

static void Emit(StringBuilder sb, string name, List<byte> bytes, string comment)
{
    sb.Append($"        // {comment}\n");
    sb.Append($"        internal static ReadOnlySpan<byte> {name} =>\n        [\n");
    for (int i = 0; i < bytes.Count; i += 16)
    {
        sb.Append("            ");
        sb.Append(string.Join(", ", bytes.Skip(i).Take(16).Select(b => $"0x{b:X2}")));
        sb.Append(",\n");
    }
    sb.Append("        ];\n\n");
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "build.sh")) && Directory.Exists(Path.Combine(dir.FullName, "zerocore")))
            return dir.FullName;
    }
    throw new Exception("repository root not found");
}
