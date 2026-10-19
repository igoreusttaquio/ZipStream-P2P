using System.Net;
using System.Net.Sockets;

namespace ZipP2P.App;

public static class NetworkInfo
{
    /// <summary>Retorna o primeiro endereço IPv4 não-loopback da máquina (para exibir o IP na rede local).</summary>
    public static string GetLocalIpAddress()
    {
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip.ToString();
            }
        }
        catch
        {
            // ignora e cai para "localhost"
        }

        return "localhost";
    }
}
