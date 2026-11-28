// Prints three Chinese cities in zh-CN's order: by pinyin ("北京 上海 中国"),
// or, built with ZeroIdeographOrders=false, by radical and strokes
// ("上海 中国 北京").
using System;
using System.Globalization;

public static class Program
{
    public static int Main()
    {
        var compare = CultureInfo.GetCultureInfo("zh-CN").CompareInfo;
        string[] cities = { "中国", "北京", "上海" };
        new Span<string>(cities).Sort((a, b) => compare.Compare(a, b));
        Out.Line(cities[0] + " " + cities[1] + " " + cities[2]);
        return 0;
    }
}
