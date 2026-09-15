using System.Security.Cryptography;
using System.Text;

namespace AzerothManager.Services;

/// <summary>Chiffre/déchiffre les secrets locaux (mots de passe MySQL et SSH) via DPAPI, utilisateur Windows courant.</summary>
public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AzerothManager.Secrets.v1");

    public static string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        byte[] data = Encoding.UTF8.GetBytes(plainText);
        byte[] enc = ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    public static string Unprotect(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText)) return "";
        try
        {
            byte[] enc = Convert.FromBase64String(cipherText);
            byte[] data = ProtectedData.Unprotect(enc, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch
        {
            // Chiffré par un autre utilisateur Windows, ou donnée corrompue : on repart à vide.
            return "";
        }
    }
}
