using OpenForecourt.SiteController.Config;

namespace OpenForecourt.SiteController.Simulation;

/// <summary>The fuel grades this forecourt sells, from configuration.</summary>
/// <remarks>
/// A grade is not decoration: selecting one pushes its unit price into the dispenser firmware, so
/// the metered value the tile shows is computed by the pump at the selected price, not by the web
/// tier. OFP-1 has no grade field (<c>docs/protocol-pump.md §2</c>) — a multi-product dispenser
/// would carry one; here the price change is the whole of the grade's effect on the wire.
/// </remarks>
public sealed class GradeCatalogue
{
    private readonly Dictionary<string, GradeOption> _byCode;

    /// <summary>Builds the catalogue from <see cref="SiteOptions.Grades"/>.</summary>
    /// <exception cref="InvalidOperationException">No grades are configured.</exception>
    public GradeCatalogue(SiteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        All = [.. options.Grades];
        if (All.Count == 0)
        {
            throw new InvalidOperationException("At least one fuel grade must be configured.");
        }

        _byCode = All.ToDictionary(g => g.Code, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every configured grade, in configuration order.</summary>
    public IReadOnlyList<GradeOption> All { get; }

    /// <summary>The grade a pump starts on.</summary>
    public GradeOption Default => All[0];

    /// <summary>Looks up a grade by code, or null.</summary>
    public GradeOption? TryGet(string? code) =>
        code is not null && _byCode.TryGetValue(code, out var grade) ? grade : null;
}
