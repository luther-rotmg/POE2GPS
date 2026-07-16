using System;
using POE2Radar.Core;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Tests.Web;

internal static class TestBoot
{
    static int _portCounter = 44000;
    public static int NextPort() => System.Threading.Interlocked.Increment(ref _portCounter);

    public static ApiServer Server(bool webMap, bool webObs, out int port,
                                   Func<RadarState>? stateProvider = null,
                                   Func<(byte[]? Walkable, int Width, int Height, uint AreaHash)>? terrainProvider = null,
                                   string? rulesConfigDir = null)
    {
        port = NextPort();
        var settings = new RadarSettings
        {
            EnableWebMap = webMap,
            EnableWebObs = webObs,
            ApiPort = port,
            AllowLanAccess = false,
        };
        // v0.20.1 T9: callers can inject a custom RadarState for routes that read live state
        // (e.g. /api/paths). Default keeps the pre-T9 behaviour: an empty MakeState() snapshot.
        stateProvider ??= () => SseChannelTests.MakeState();
        var sse = (webMap || webObs) ? new SseChannel() : null;
        var host = (webMap || webObs) ? new AssetHost() : null;

        // NOTE: pass whatever concrete callbacks/deps ApiServer needs; use null! for the ones
        // the routes under test don't invoke.
        var api = new ApiServer(
            state: stateProvider,
            settings: settings,
            navGet: null!,
            navToggle: null!,
            navClear: null!,
            hidden: null!,
            displayRules: null!,
            landmarkStore: null!,
            tilesProvider: null!,
            knownModsProvider: null!,
            objectives: null!,
            seenPoisProvider: null!,
            entityAtlasProvider: null!,
            entityNames: null!,
            gearProvider: null!,
            preloadProvider: null!,
            buffsDiagProvider: null!,
            gearWeights: null!,
            allowLanAccess: false,
            port: port,
            sse: sse,
            assetHost: host,
            terrainProvider: terrainProvider ?? (() => (null, 0, 0, 0u)),
            rulesConfigDir: rulesConfigDir);
        api.Start();
        return api;
    }
}
