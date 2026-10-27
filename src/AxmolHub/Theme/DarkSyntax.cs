using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace AxmolHub;

/// <summary>Applies Hub's light and dark syntax palette to shipped and built-in lexical definitions without
/// replacing their language rules. Definitions are cached separately for each original and backdrop.</summary>
internal static class DarkSyntax
{
    /// <summary>The WCAG AA floor for body text, asked of code as well: a keyword nobody can read is
    /// decoration.</summary>
    internal const double MinimumContrast = 4.5;

    private const string ResourcePrefix = "AvaloniaEdit.Highlighting.Resources.";

    /// <summary>Keep the original definition in the key: Hub and the library both define C++.</summary>
    private static readonly Dictionary<(IHighlightingDefinition Definition, Color Well), IHighlightingDefinition?> Cache = new();

    private static readonly HashSet<IHighlightingDefinition> Produced = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<IHighlightingDefinition, IHighlightingDefinition> Originals = new(ReferenceEqualityComparer.Instance);

    private static readonly XmlReaderSettings StrictReader = new() { XmlResolver = null };

    /// <summary>Return a definition recolored for the current theme, or the original if its source is unavailable.</summary>
    internal static IHighlightingDefinition? ForBackdrop(IHighlightingDefinition? definition)
    {
        if (definition is null) return null;
        var well = Well();

        lock (Cache)
        {
            if (Originals.TryGetValue(definition, out var original)) definition = original;
            if (!Cache.TryGetValue((definition, well), out var adapted))
            {
                adapted = Build(definition, well);
                Cache[(definition, well)] = adapted;
            }

            return adapted ?? definition;
        }
    }

    /// <summary>Whether this definition came out of <see cref="ForBackdrop"/> rather than out of a library
    /// resource. What a check can prove about a code block is which palette it was handed, and this is the only
    /// question that answers it without reaching into brushes.</summary>
    internal static bool IsAdapted(IHighlightingDefinition definition)
    {
        lock (Cache) return Produced.Contains(definition);
    }

    private static IHighlightingDefinition? Build(IHighlightingDefinition original, Color well)
    {
        var xml = CodeSyntax.SourceFor(original) ?? BuiltIns().FirstOrDefault(built
            => string.Equals(built.Name, original.Name, StringComparison.OrdinalIgnoreCase)).Xml;
        if (xml is not { Length: > 0 }) return null;

        using var reader = XmlReader.Create(new StringReader(Transform(xml, well)), StrictReader);
        try
        {
            var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            Produced.Add(definition);
            Originals[definition] = original;
            return definition;
        }
        catch (Exception ex) when (ex is XmlException or HighlightingDefinitionInvalidException)
        {
            // A file the library itself loads cannot fail here, but a definition nobody can read is not worth
            // taking the message flow down for: leave the block to the palette it arrived with.
            return null;
        }
    }

    /// <summary>The built-in syntax files, read once out of the editor assembly.</summary>
    internal static IReadOnlyList<(string Name, string Xml)> BuiltIns() => Files.Value;

    private static readonly Lazy<List<(string Name, string Xml)>> Files = new(() =>
    {
        var assembly = typeof(HighlightingManager).Assembly;
        var list = new List<(string Name, string Xml)>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resource.EndsWith(".xshd", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var text = new StreamReader(stream);
            var xml = text.ReadToEnd();
            list.Add((DefinitionName(xml), xml));
        }

        return list;
    });

    private static string DefinitionName(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Root?.Attribute("name")?.Value ?? "";
        }
        catch (XmlException)
        {
            return "";
        }
    }

    /// <summary>Rewrites one syntax file against one backdrop. Every colour a rule can be drawn in lives on a
    /// <c>&lt;Color&gt;</c> element or inline on the span that uses it, so walking the attributes covers both,
    /// and a span that names a colour by reference is fixed by the same edit as the definition it points at.</summary>
    internal static string Transform(string xshdXml, Color well)
    {
        var document = XDocument.Parse(xshdXml);
        foreach (var element in document.Descendants())
        {
            element.Attribute("background")?.Remove();
            if (element.Attribute("foreground") is not { } foreground) continue;
            if (!TryResolve(foreground.Value, out var color)) continue;
            foreground.Value = PaletteColor(element, color, IsDark(well));
        }

        return document.ToString(SaveOptions.DisableFormatting);
    }

    // Named roles take precedence; legacy unnamed rules use their original hue as a fallback. This keeps
    // each language's lexical rules while replacing its unrelated vendor palette with Hub's shared colors.
    private static string PaletteColor(XElement element, Color original, bool dark)
    {
        var name = string.Join(" ", element.AncestorsAndSelf().SelectMany(e =>
            new[] { e.Attribute("name")?.Value, e.Attribute("color")?.Value }).Where(s => s is not null)).ToLowerInvariant();
        string role;
        if (name.Contains("comment") || name.Contains("documentation")) role = "comment";
        else if (name.Contains("string") || name.Contains("charliteral") || name.Contains("attributevalue")) role = "string";
        else if (name.Contains("number") || name.Contains("literal") || name.Contains("constant")) role = "number";
        else if (name.Contains("type") || name.Contains("class") || name.Contains("tag")) role = "type";
        else if (name.Contains("keyword") || name.Contains("command") || name.Contains("preprocessor")) role = "keyword";
        else if (name.Contains("attribute") || name.Contains("variable")) role = "variable";
        else
        {
            var (hue, saturation, _) = ToHsl(original);
            role = saturation < 0.18 ? "comment" : hue < 0.13 || hue > 0.92 ? "keyword"
                : hue < 0.42 ? "string" : hue < 0.62 ? "type" : "number";
        }
        var index = role switch { "comment" => 0, "string" => 1, "number" => 2, "type" => 3, "variable" => 4, _ => 5 };
        return dark ? Palette[index].Dark : Palette[index].Light;
    }

    private static readonly (string Dark, string Light)[] Palette =
    [
        ("#A0AAB5", "#52616D"), ("#B4CF9A", "#426B30"), ("#D0B2E3", "#70448C"),
        ("#8FC9D5", "#1C6575"), ("#D7C193", "#76571D"), ("#C0B4E8", "#58428B"),
    ];

    internal static IBrush? BrushForTheme(IBrush? brush, ThemeVariant theme)
    {
        if (brush is not ISolidColorBrush solid) return brush;
        foreach (var (dark, light) in Palette)
            if (solid.Color == Color.Parse(dark) || solid.Color == Color.Parse(light))
                return new SolidColorBrush(Color.Parse(theme == ThemeVariant.Dark ? dark : light));
        return brush;
    }

    /// <summary>The contrast of every foreground a file can paint with, against one backdrop. A value the parser
    /// does not recognise is reported as negative rather than skipped: a check that quietly ignored an
    /// untranslatable colour name would pass by doing nothing, which is the failure this whole file exists to
    /// avoid.</summary>
    internal static List<(string Value, double Contrast)> Foregrounds(string xshdXml, Color well)
    {
        var readings = new List<(string, double)>();
        foreach (var foreground in XDocument.Parse(xshdXml).Descendants().Attributes("foreground"))
        {
            readings.Add((foreground.Value,
                TryResolve(foreground.Value, out var color) ? Contrast(color, well) : double.NegativeInfinity));
        }

        return readings;
    }

    private static bool TryResolve(string value, out Color color) => Color.TryParse(value, out color);

    /// <summary>Relative luminance, then the WCAG ratio: the same two numbers a person's eyes apply.</summary>
    internal static double Contrast(Color foreground, Color background)
    {
        var one = Luminance(foreground);
        var other = Luminance(background);
        var (lighter, darker) = one >= other ? (one, other) : (other, one);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(double value)
            => value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

        return 0.2126 * Channel(color.R / 255.0)
               + 0.7152 * Channel(color.G / 255.0)
               + 0.0722 * Channel(color.B / 255.0);
    }

    private static (double Hue, double Saturation, double Lightness) ToHsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2;
        if (max == min) return (0, 0, lightness);

        var delta = max - min;
        var saturation = lightness > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        double hue;
        if (max == r) hue = (g - b) / delta + (g < b ? 6 : 0);
        else if (max == g) hue = (b - r) / delta + 2;
        else hue = (r - g) / delta + 4;
        return (hue / 6, saturation, lightness);
    }

    /// <summary>Whether this surface is dark enough that a light-page palette fails on it.</summary>
    internal static bool IsDark(Color surface) => Luminance(surface) < 0.5;

    /// <summary>The colour code blocks are actually painted on, read for the theme on screen — or for one named
    /// explicitly, because a check asks about the dark well whether or not the machine it runs on happens to be
    /// dark. An unreadable token answers white, which means "leave the palette alone".</summary>
    internal static Color Well(ThemeVariant? variant = null)
        => Application.Current is { } app
           && app.TryGetResource("Hub.SurfaceSunken", variant ?? app.ActualThemeVariant, out var value)
           && value is ISolidColorBrush brush
            ? brush.Color
            : Colors.White;
}
