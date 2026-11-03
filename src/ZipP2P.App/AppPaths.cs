using System.Security.Cryptography;
using System.Text;

namespace ZipP2P.App;

/// <summary>
/// Onde o app guarda configurações e transferências. Padrão: <c>%LOCALAPPDATA%/ZipStream</c> (ou
/// equivalente). A variável <c>ZIPSTREAM_DATA_DIR</c> aponta para outra pasta: um perfil separado,
/// com a própria instância (ex.: testar dois "computadores" na mesma máquina, prints de demonstração).
/// </summary>
public static class AppPaths
{
    public const string DataDirectoryVariable = "ZIPSTREAM_DATA_DIR";

    private static string? CustomDataDirectory => Environment.GetEnvironmentVariable(DataDirectoryVariable);

    public static string DataDirectory => ResolveDataDirectory(CustomDataDirectory);

    /// <summary>Sufixo para separar a instância única (e o mutex) de um perfil personalizado da instância padrão.</summary>
    public static string InstanceSuffix => InstanceSuffixFor(CustomDataDirectory);

    public static string ResolveDataDirectory(string? customDirectory) => customDirectory is { Length: > 0 }
        ? Path.GetFullPath(customDirectory)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZipStream");

    public static string InstanceSuffixFor(string? customDirectory) => customDirectory is { Length: > 0 }
        ? "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(customDirectory).ToLowerInvariant())))[..12]
        : string.Empty;
}
