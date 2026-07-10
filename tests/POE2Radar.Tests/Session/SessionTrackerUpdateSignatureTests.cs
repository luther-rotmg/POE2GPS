using System.Reflection;
using POE2Radar.Core.Session;

namespace POE2Radar.Tests.Session;

/// <summary>
/// Threshold — regression guard for the 9-arg <see cref="SessionTracker.Update"/> overload
/// consumed by the render-thread caller in <c>RadarApp.WorldTick</c>. A future signature-
/// change refactor (arg reorder, arg-type widen, additional arg) would silently break the
/// XP-ring feed path without this reflection lock — the render callsite binds by static
/// overload resolution, so a compile-time swap that keeps the method name compiles at both
/// callsite AND definition even though the wire behaviour is destroyed. Looking the method
/// up by exact <c>Type[]</c> match forces the failure to surface at test time instead of
/// six weeks later when the HUD chip flat-lines in the field.
/// </summary>
public class SessionTrackerUpdateSignatureTests
{
    [Fact]
    public void SessionTracker_Update_9Arg_Overload_MatchesCanonicalSignature()
    {
        var argTypes = new[]
        {
            typeof(uint),   // areaHash
            typeof(string), // areaCode
            typeof(int),    // areaLevel
            typeof(int),    // playerLevel
            typeof(float),  // hpPct
            typeof(long),   // nowTicks
            typeof(bool),   // excludeTowns
            typeof(bool),   // isTown
            typeof(long),   // currentXp
        };

        var method = typeof(SessionTracker).GetMethod(
            "Update",
            BindingFlags.Public | BindingFlags.Instance,
            binder: null,
            types: argTypes,
            modifiers: null);

        Assert.NotNull(method);
        Assert.Equal(typeof(SessionStats), method!.ReturnType);
    }
}
