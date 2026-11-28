// Test: sorting with the ideograph orders left out (built with
// --feature Zero.Globalization.IdeographOrders=false; in a project,
// <ZeroIdeographOrders>false</ZeroIdeographOrders>): Chinese, Japanese and
// Korean then sort ideographs as every other culture does, by radical and
// strokes (.NET's root order, as en-US gives it), and the program is about
// 300 KB smaller. Everything else sorts as before.
// Exit code = number of failed checks.
// Build: ./build.sh SortingRoot

using System;
using System.Globalization;

namespace SortingRootSample;

public static class Program
{
    static int s_failures;

    static void Is(string name, string actual, string expected)
    {
        if (actual != expected)
        {
            s_failures++;
            Console.WriteLine($"  FAIL: {name}: \"{actual}\", expected \"{expected}\"");
        }
    }

    // The words in the culture's order, joined by spaces (a sorted copy).
    static string Sorted(string culture, params string[] words)
    {
        var compare = CultureInfo.GetCultureInfo(culture).CompareInfo;
        string[] sorted = new string[words.Length];
        for (int i = 0; i < words.Length; i++)
            sorted[i] = words[i];
        new Span<string>(sorted).Sort((a, b) => compare.Compare(a, b));
        string joined = sorted[0];
        for (int i = 1; i < sorted.Length; i++)
            joined = joined + " " + sorted[i];
        return joined;
    }

    public static int Main()
    {
        string[] cities = { "中国", "北京", "上海", "广州", "深圳" };
        string root = Sorted("en-US", cities);
        Is("the root order", root, "上海 中国 北京 广州 深圳");
        Is("Chinese: no pinyin", Sorted("zh-CN", cities), root);
        Is("Chinese (Taiwan): no strokes", Sorted("zh-TW", cities), root);
        Is("Japanese", Sorted("ja-JP", "東京", "大阪", "京都"), "京都 大阪 東京");
        Is("Turkish as before", Sorted("tr-TR", "ılık", "ilaç", "çay", "cami", "ısı", "İzmir"), "cami çay ılık ısı ilaç İzmir");
        Console.WriteLine(s_failures == 0 ? "All checks passed." : $"{s_failures} checks failed.");
        return s_failures;
    }
}
