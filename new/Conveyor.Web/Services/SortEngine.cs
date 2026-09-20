using System.Text.RegularExpressions;
using Conveyor.Web.Domain;
using Conveyor.Web.Options;

namespace Conveyor.Web.Services;

public sealed partial class SortEngine(IConveyorRepository repository, ILogger<SortEngine> logger)
{
    [GeneratedRegex("^[A-Z][0-9][A-Z] ?[0-9][A-Z][0-9]$", RegexOptions.IgnoreCase)]
    private static partial Regex PostalCodeRegex();

    public async Task<SortDecision> DecideAsync(LineOptions line, ParcelContext parcel, CancellationToken token)
    {
        var shiftId = line.ShiftId;
        var code98Enabled = line.ValidateDimensionsAndWeight;
        var missingMeasurements = parcel.Weight <= 0 || parcel.Dimension.Length <= 0 ||
            parcel.Dimension.Width <= 0 || parcel.Dimension.Height <= 0;
        var tokens = parcel.CameraData.ToUpperInvariant().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var postalCodes = tokens.Where(x => PostalCodeRegex().IsMatch(x)).Select(x => x.Replace(" ", "")).Distinct().ToArray();
        var candidates = tokens.Where(x => x.Length > 8 && !PostalCodeRegex().IsMatch(x) && !x.Contains('?')).Distinct().ToArray();
        var chute = line.RejectedChute;
        var reason = "Expédition introuvable";
        var barcode = "";
        var goodBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Shipment? matchedShipment = null;

        if (parcel.CameraData.Contains('?'))
        {
            chute = line.NoReadChute;
            reason = "Lecture caméra invalide";
        }

        foreach (var raw in candidates)
        {
            var candidate = RenameBentley(raw);
            var shipment = await repository.FindShipmentAsync(candidate, token);
            if (shipment is null)
            {
                if (goodBarcodes.Count == 0 && candidate.Length is 11 or 12) barcode = candidate;
                continue;
            }

            barcode = shipment.CustomerId == 129326 ? shipment.ShippingId + "01" : candidate;
            matchedShipment = shipment;
            if (barcode.Length >= 11) barcode = barcode[..11];
            goodBarcodes.Add(barcode);
            reason = "Route de l'expédition non configurée";
            var configuredChute = await repository.FindChuteForRouteAsync(shiftId, shipment.RouteId, token);
            if (configuredChute is > 0)
            {
                chute = configuredChute.Value;
                reason = "Route de l'expédition";
            }

            if (code98Enabled && !missingMeasurements)
            {
                var invalidMeasurements = !parcel.Dimension.IsValid(line.MaximumDimension) ||
                                          parcel.Weight <= 0 || parcel.Weight > line.MaximumWeight;
                if (invalidMeasurements && !shipment.DisableCode98)
                {
                    chute = await repository.ShouldUseExceptionChuteAsync("98", barcode, line.Code86Retry, token)
                        ? 98 : line.RejectedChute;
                    reason = chute == 98 ? "Dimensions ou poids invalides" : "Limite de reprises code 98";
                }
                else if (!invalidMeasurements)
                {
                    await repository.ClearExceptionCodeAsync("98", barcode, token);
                }
            }

            if (line.EnableCode86 && chute != 98)
            {
                chute = await repository.ShouldUseExceptionChuteAsync("86", barcode, line.Code86Retry, token)
                    ? 86 : line.RejectedChute;
                reason = chute == 86 ? "Contrôle code 86" : "Limite de reprises code 86";
            }
            else if (!line.EnableCode86 && !string.IsNullOrEmpty(barcode))
            {
                await repository.ClearExceptionCodeAsync("86", barcode, token);
            }
        }

        if (line.PostalCodeSort && postalCodes.Length == 1 && (chute == line.RejectedChute || chute == 86))
        {
            var postalChute = await repository.FindChuteForPostalCodeAsync(shiftId, postalCodes[0], token);
            if (postalChute is > 0)
            {
                chute = postalChute.Value;
                reason = "Route du code postal";
            }
        }

        if (code98Enabled && missingMeasurements)
        {
            chute = 98;
            reason = "Poids ou dimensions manquants";
        }

        if (goodBarcodes.Count > 1)
        {
            chute = 99;
            reason = "Plusieurs expéditions détectées";
        }

        var plcChute = chute == 99 ? line.RejectedChute : chute;
        logger.LogInformation("Ligne {Line}: {Barcode} -> chute {Chute} ({Reason})", line.Id, barcode, chute, reason);
        return new SortDecision(barcode, postalCodes.FirstOrDefault() ?? "", chute, plcChute, reason,
            parcel.Dimension, parcel.Weight, parcel.CameraTimestamp,
            goodBarcodes.Count == 1 ? matchedShipment?.DestinationPostalCode : null,
            goodBarcodes.Count == 1 ? matchedShipment?.RouteId : null,
            goodBarcodes.Count == 1 ? matchedShipment?.DisableCode98 : null);
    }

    private static string RenameBentley(string value) =>
        value.Length == 12 && value.StartsWith("7000", StringComparison.Ordinal) ? "7" + value[4..] + "010" : value;
}
