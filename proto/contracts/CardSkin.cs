using System.IO;
using System.Text.RegularExpressions;

namespace Kards.Ui.Contracts;

/// <summary>完整卡面文件的引用工具。不做格式白名单，只把扩展名收敛成安全的小写后缀。</summary>
public static class CardSkinFile
{
    public static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return "";
        var value = extension.TrimStart('.').ToLowerInvariant();
        return value.Length is < 1 or > 8 || !value.All(char.IsAsciiLetterOrDigit) ? "" : "." + value;
    }
    /// <summary>取卡面引用的扩展名；没有合法后缀时沿用 PNG 后缀。</summary>
    public static string ExtensionOf(string? art) =>
        NormalizeExtension(Path.GetExtension(art ?? "")) is { Length: > 0 } ext ? ext : ".png";
}

public sealed record SkinRect(float X, float Y, float Width, float Height)
{
    public void Check()
    {
        if (!float.IsFinite(X) || !float.IsFinite(Y) || !float.IsFinite(Width) || !float.IsFinite(Height)
            || X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 1.001f || Y + Height > 1.001f)
            throw new InvalidDataException("数值区域须在卡面内，宽高须大于零。");
    }
}

/// <summary>Workshop-owned complete printed card and manually placed live numbers. No template layers, no image format/size limits.</summary>
public sealed record CardSkin
{
    public string Art { get; init; } = "";
    public string Ink { get; init; } = "F1E8CD";
    public string Paper { get; init; } = "202C38";
    public SkinRect CostBox { get; init; } = new(.035f, .035f, .13f, .10f);
    public SkinRect AttackBox { get; init; } = new(.055f, .59f, .18f, .10f);
    public SkinRect DefenseBox { get; init; } = new(.765f, .59f, .18f, .10f);
    public SkinRect OpBox { get; init; } = new(.42f, .61f, .16f, .065f);
    public CardSkin Checked()
    {
        // 只挡路径逃逸；扩展名、图片格式、像素尺寸与文件体积都不做限制。
        // 四个数值区域仍受 SkinRect.Check 约束——数值对齐口径不变。
        if (!Regex.IsMatch(Art, @"\A(?:art/)?[a-zA-Z0-9_-]{1,100}\.[a-zA-Z0-9]{1,8}\z")
            || Ink.Length != 6 || !Ink.All(char.IsAsciiHexDigit) || Paper.Length != 6 || !Paper.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("请导入完整卡面，并设置有效的数值颜色。");
        foreach (var box in new[] { CostBox, AttackBox, DefenseBox, OpBox }) box.Check();
        return this;
    }
}
