namespace Conveyor.Web.Options;

public sealed class OperatorKpiOptions
{
    public decimal ChartCadenceYellowFrom { get; set; } = 2000m;
    public decimal ChartCadenceGreenAbove { get; set; } = 3000m;
    public decimal CadenceGreenAbove { get; set; } = 1500m;
    public decimal CadenceRedThrough { get; set; } = 1000m;
    public decimal SortedGreenFrom { get; set; } = 97m;
    public decimal SortedRedBelow { get; set; } = 95m;
    public decimal RateGreenBelow { get; set; } = 1.5m;
    public decimal RateYellowThrough { get; set; } = 3.5m;

    public string CadenceTone(double? rate) => rate is null || !double.IsFinite(rate.Value) ? ""
        : rate <= (double)CadenceRedThrough ? "critical" : rate > (double)CadenceGreenAbove ? "success" : "warning";
    public string SortedTone(long count, long total) => total <= 0 ? ""
        : count * 100m / total < SortedRedBelow ? "critical" : count * 100m / total >= SortedGreenFrom ? "success" : "warning";
    public string RateTone(long count, long total)
    {
        if (total <= 0) return "";
        var rate = count * 100m / total;
        return rate < RateGreenBelow ? "success" : rate <= RateYellowThrough ? "warning" : "critical";
    }
    public string? ValidationError() => ChartCadenceYellowFrom < 0 || ChartCadenceGreenAbove <= ChartCadenceYellowFrom
        ? "Seuils du graphique de cadence invalides : le seuil jaune doit être positif ou nul et le seuil vert strictement supérieur au seuil jaune."
        : CadenceRedThrough < 0 || CadenceGreenAbove <= CadenceRedThrough ||
        SortedRedBelow < 0 || SortedGreenFrom < SortedRedBelow || SortedGreenFrom > 100 ||
        RateGreenBelow is < 0 or > 100 || RateYellowThrough < RateGreenBelow || RateYellowThrough > 100
        ? "Seuils KPI invalides : cadences positives ou nulles avec seuil vert supérieur au seuil rouge ; pourcentages entre 0 et 100, seuil vert des bien triés au moins égal au seuil rouge et limite jaune des erreurs au moins égale au seuil vert." : null;
}
