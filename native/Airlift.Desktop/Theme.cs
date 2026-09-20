using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Airlift.Desktop;

/// <summary>
/// One named colour per job in the interface. Meadow is Airlift's original green; Altitude is its
/// blue sibling, re-hued at matching lightness so every contrast relationship survives the switch.
/// Danger and the close button stay identical in both: they carry meaning, not mood.
/// </summary>
public sealed record Palette
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Summary { get; init; }

    // Surfaces, darkest first.
    public required string Window { get; init; }
    public required string Sidebar { get; init; }
    public required string Code { get; init; }
    public required string Field { get; init; }
    public required string Card { get; init; }
    public required string CardHover { get; init; }
    public required string Pressed { get; init; }
    public required string Hover { get; init; }
    public required string ChromeHover { get; init; }
    public required string ChromePressed { get; init; }
    public required string ClearHover { get; init; }
    public required string ClearPressed { get; init; }
    public required string Raised { get; init; }
    public required string ButtonHover { get; init; }
    public required string Active { get; init; }
    public required string ActiveHover { get; init; }
    public required string CardAccent { get; init; }
    public required string CodeInline { get; init; }

    // Hairlines and borders.
    public required string Line { get; init; }
    public required string LineStrong { get; init; }
    public required string LineButton { get; init; }
    public required string LineButtonHover { get; init; }

    // Type.
    public required string Text { get; init; }
    public required string TextBright { get; init; }
    public required string TextHero { get; init; }
    public required string TextButton { get; init; }
    public required string TextCode { get; init; }
    public required string Prose { get; init; }
    public required string Muted { get; init; }
    public required string Subtle { get; init; }
    public required string Faint { get; init; }

    // Accent and the dark ink that sits on it.
    public required string Accent { get; init; }
    public required string AccentHover { get; init; }
    public required string AccentPressed { get; init; }
    public required string AccentOn { get; init; }
    public required string AccentMuted { get; init; }
    public required string AccentSoft { get; init; }
    public required string AccentStar { get; init; }
    public required string AccentIdle { get; init; }

    // The Discover hero. The Airlift mark itself keeps its own green in both themes.
    public required string HeroFrom { get; init; }
    public required string HeroTo { get; init; }
    public required string HeroOrbit { get; init; }

    // The three collection tiles.
    public required string CollectionOne { get; init; }
    public required string CollectionTwo { get; init; }
    public required string CollectionThree { get; init; }

    // Meaning rather than mood: identical in every theme.
    public string DangerSurface => "#302420";
    public string DangerLine => "#634236";
    public string DangerText => "#EAB5A4";
    public string DangerHoverSurface => "#47302A";
    public string DangerHoverLine => "#896050";
    public string DangerHoverText => "#FFD2C1";
    public string CloseHover => "#C0392B";
    public string ClosePressed => "#9A2C21";
}

public static class Themes
{
    public static readonly Palette Meadow = new()
    {
        Id = "meadow", Name = "Meadow", Summary = "Warm greens. The original Airlift look.",
        Window = "#101211", Sidebar = "#151715", Code = "#121710", Field = "#181C17",
        Card = "#191E17", CardHover = "#1E2419", Pressed = "#20291C", Hover = "#242923",
        ChromeHover = "#252B24", ChromePressed = "#2F362D", ClearHover = "#2A3124", ClearPressed = "#333B2C",
        Raised = "#252D20", ButtonHover = "#303A28", Active = "#293021", ActiveHover = "#313B26",
        CardAccent = "#232E1C", CodeInline = "#293121",
        Line = "#30362C", LineStrong = "#4B593D", LineButton = "#3D4932", LineButtonHover = "#536343",
        Text = "#E5ECDD", TextBright = "#E8EAE5", TextHero = "#EDF4DF", TextButton = "#D1DDC2",
        TextCode = "#D7E3C9", Prose = "#A9B39E", Muted = "#92988D", Subtle = "#9DA399", Faint = "#8E948A",
        Accent = "#D7F59A", AccentHover = "#E5FFB7", AccentPressed = "#BEDD81", AccentOn = "#25311C",
        AccentMuted = "#B8D68F", AccentSoft = "#B6C5A5", AccentStar = "#BAD491", AccentIdle = "#4E5A44",
        HeroFrom = "#252F1E", HeroTo = "#1B2418", HeroOrbit = "#354529",
        CollectionOne = "#28331F", CollectionTwo = "#2D272F", CollectionThree = "#253036",
    };

    public static readonly Palette Altitude = new()
    {
        Id = "altitude", Name = "Altitude", Summary = "Cool blues. High and clear.",
        Window = "#0E1117", Sidebar = "#121620", Code = "#0F141C", Field = "#151A24",
        Card = "#161B26", CardHover = "#1A2130", Pressed = "#19222F", Hover = "#1D2430",
        ChromeHover = "#1E2531", ChromePressed = "#28313F", ClearHover = "#222C3A", ClearPressed = "#2B3648",
        Raised = "#1E2838", ButtonHover = "#26344A", Active = "#1F2C3E", ActiveHover = "#26364C",
        CardAccent = "#1A2A3E", CodeInline = "#202E42",
        Line = "#2A3342", LineStrong = "#3E4E64", LineButton = "#354459", LineButtonHover = "#475B78",
        Text = "#DCE5F2", TextBright = "#E6ECF5", TextHero = "#E4EDF8", TextButton = "#C6D4E8",
        TextCode = "#CBD9EE", Prose = "#A2ADC0", Muted = "#8A94A6", Subtle = "#949EB0", Faint = "#87919F",
        Accent = "#8FC2F2", AccentHover = "#A9D2F8", AccentPressed = "#74ACE0", AccentOn = "#0B1622",
        AccentMuted = "#8FB4DC", AccentSoft = "#A8BCD6", AccentStar = "#9CC3EA", AccentIdle = "#46586E",
        HeroFrom = "#1A2739", HeroTo = "#131B28", HeroOrbit = "#2C405A",
        CollectionOne = "#1F2C3E", CollectionTwo = "#272B3E", CollectionThree = "#1E3040",
    };

    /// <summary>Every theme Airlift offers. Add a palette above and list it here; because each
    /// role is <c>required</c>, a new theme cannot compile until it answers all of them.</summary>
    public static IReadOnlyList<Palette> All { get; } = [Meadow, Altitude];
    public static Palette Default => All[0];
    public static Palette Current { get; private set; } = All[0];
    /// <summary>Raised after <see cref="Apply"/> changes the palette, so open windows can rebuild.</summary>
    public static event Action? Changed;

    public static Palette Find(string? id) => All.FirstOrDefault(p => p.Id == id) ?? Default;

    /// <summary>Publishes the palette as application resources, which the XAML styles bind to dynamically.</summary>
    public static void Apply(string? id, Application? application = null)
    {
        var palette = Find(id);
        var target = application ?? Application.Current;
        var changed = !ReferenceEquals(palette, Current);
        Current = palette;
        if (target != null)
        {
            foreach (var (key, hex) in Entries(palette)) target.Resources["Airlift." + key] = new ImmutableSolidColorBrush(Color.Parse(hex));
            target.Resources["SystemAccentColor"] = Color.Parse(palette.Accent);
            target.Resources["SystemAccentColorLight1"] = Color.Parse(palette.AccentHover);
            target.Resources["SystemAccentColorDark1"] = Color.Parse(palette.AccentPressed);
        }
        if (changed) Changed?.Invoke();
    }

    public static IEnumerable<(string Key, string Hex)> Entries(Palette palette)
    {
        foreach (var property in typeof(Palette).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType != typeof(string)) continue;
            if (property.Name is nameof(Palette.Id) or nameof(Palette.Name) or nameof(Palette.Summary)) continue;
            yield return (property.Name, (string)property.GetValue(palette)!);
        }
    }

    private static readonly Dictionary<string, IBrush> Brushes = [];
    public static string Hex(Func<Palette, string> role) => role(Current);
    public static IBrush Brush(Func<Palette, string> role)
    {
        var hex = role(Current);
        if (!Brushes.TryGetValue(hex, out var brush)) Brushes[hex] = brush = Avalonia.Media.Brush.Parse(hex);
        return brush;
    }
}
