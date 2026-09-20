using System.Security.Cryptography;
using System.Text;

namespace Conveyor.Web.Services;

public static class ConfigurationAccess
{
    private static readonly byte[] ExpectedPinHash = Convert.FromHexString(
        "FB937217A0D04DA5939AE57522CC743B370857CD9E617C378A538A4A5A24DA74");

    public static bool VerifyPin(string? pin)
    {
        if (string.IsNullOrEmpty(pin)) return false;
        var submittedHash = SHA256.HashData(Encoding.UTF8.GetBytes(pin));
        return CryptographicOperations.FixedTimeEquals(submittedHash, ExpectedPinHash);
    }
}
