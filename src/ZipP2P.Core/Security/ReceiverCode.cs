using System.Security.Cryptography;

namespace ZipP2P.Core.Security;

/// <summary>
/// Código fixo de um computador em receive mode (ex.: <c>K7P-2QX-9MRT-H4WZ</c>). Quem o digita
/// pode parear com esse computador. São 14 caracteres de um alfabeto de 32 (70 bits): como o
/// código não expira, precisa resistir a força bruta mesmo se a sinalização for comprometida.
/// </summary>
public static class ReceiverCode
{
    /// <summary>Sem ambiguidades (sem 0/O, 1/I), como o código de transferência.</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public const int Length = 14;

    private static readonly int[] GroupSizes = [3, 3, 4, 4];

    /// <summary>Gera um código novo, já formatado.</summary>
    public static string Generate()
        => Format(string.Create(Length, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }));

    /// <summary>
    /// Normaliza o código digitado (maiúsculas, sem espaços/hífens). Retorna false se não tiver o
    /// tamanho certo ou tiver caracteres fora do alfabeto.
    /// </summary>
    public static bool TryNormalize(string? input, out string code)
    {
        code = string.Empty;
        if (input is null)
            return false;

        var normalized = new string(input.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        if (normalized.Length != Length || !normalized.All(Alphabet.Contains))
            return false;

        code = normalized;
        return true;
    }

    /// <summary>Agrupa um código normalizado para exibição: <c>XXX-XXX-XXXX-XXXX</c>.</summary>
    public static string Format(string normalizedCode)
    {
        if (normalizedCode.Length != Length)
            throw new ArgumentException($"A receiver code has {Length} characters.", nameof(normalizedCode));

        var groups = new List<string>(GroupSizes.Length);
        var start = 0;
        foreach (var size in GroupSizes)
        {
            groups.Add(normalizedCode.Substring(start, size));
            start += size;
        }

        return string.Join('-', groups);
    }
}
