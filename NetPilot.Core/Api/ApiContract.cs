namespace NetPilot.Core.Api;

/// <summary>
/// The ten endpoints the Android app calls.
///
/// The phone hard-codes these paths and the bearer-token scheme, so changing one is a breaking
/// protocol change that must land in the phone and every desktop build at the same time. Having
/// them as constants in the shared project means the Windows app, the macOS/Linux daemon and the
/// tests cannot drift apart.
/// </summary>
public static class ApiContract
{
    public const int Version = 1;

    /// <summary>Pairing / control bridge port. The tunnel proxy sits on <see cref="ProxyPort"/>.</summary>
    public const int BridgePort = 8787;

    /// <summary>The HTTP proxy the phone tunnels its traffic through.</summary>
    public const int ProxyPort = 8788;

    public const string Ping    = "GET /api/v1/ping";
    public const string Pair    = "POST /api/v1/pair";
    public const string Unpair  = "DELETE /api/v1/pair";
    public const string Status  = "GET /api/v1/status";
    public const string Hello   = "POST /api/v1/hello";
    public const string Report  = "POST /api/v1/report";
    public const string Share   = "POST /api/v1/share";
    public const string Stop    = "POST /api/v1/stop";
    public const string Backup  = "POST /api/v1/backup";
    public const string Restore = "POST /api/v1/restore";

    /// <summary>Not spoken by the Android app - these are how the daemon is driven on a real
    /// machine (by CI, or by hand) without a GUI. The phone knows nothing about them, so
    /// adding one cannot break pairing.</summary>
    public const string Block      = "POST /api/v1/block";
    public const string Limit      = "POST /api/v1/limit";
    public const string Capability = "POST /api/v1/capability";

    /// <summary>Bearer prefix. The phone stores the pairing token and sends this with every call
    /// except <see cref="Ping"/> and <see cref="Pair"/>.</summary>
    public const string BearerPrefix = "Bearer ";
}