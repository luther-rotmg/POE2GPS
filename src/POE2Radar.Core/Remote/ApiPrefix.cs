namespace POE2Radar.Core.Remote;

/// <summary>Builds the HttpListener URI prefix for the local API. Loopback by default; binds all
/// interfaces (http://+:port) only when the user opts into LAN access. Pure/testable.</summary>
public static class ApiPrefix
{
    public static string Build(bool lanAccess, int port)
        => lanAccess ? $"http://+:{port}/" : $"http://localhost:{port}/";
}
