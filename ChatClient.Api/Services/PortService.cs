using System.Net.NetworkInformation;

namespace ChatClient.Api.Services;

public static class PortService
{
    public static int FindAvailablePort(int startingPort)
    {
        var port = startingPort;
        var isPortAvailable = false;

        while (!isPortAvailable)
        {
            if (IsPortAvailable(port))
            {
                isPortAvailable = true;
            }
            else
            {
                port++;
            }
        }

        return port;
    }

    public static bool IsPortAvailable(int port)
    {
        // Check if port is used by TCP listeners
        var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
        var tcpConnInfoArray = ipGlobalProperties.GetActiveTcpListeners();

        if (tcpConnInfoArray.Any(endPoint => endPoint.Port == port))
        {
            return false;
        }

        // Check if port is used by active TCP connections
        var tcpConnectionsArray = ipGlobalProperties.GetActiveTcpConnections();
        if (tcpConnectionsArray.Any(conn => conn.LocalEndPoint.Port == port))
        {
            return false;
        }

        // Also check UDP listeners
        var udpConnInfoArray = ipGlobalProperties.GetActiveUdpListeners();
        if (udpConnInfoArray.Any(endPoint => endPoint.Port == port))
        {
            return false;
        }

        return true;
    }
}
