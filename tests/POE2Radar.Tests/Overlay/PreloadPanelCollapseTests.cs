using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Overlay;

/// <summary>
/// Signal — SIG-PRELOAD-COLLAPSE. Locks the setting default (false — preload panel is a "look at
/// this" surface, users should see it on first launch) and the toggle semantics. Direct2D render
/// and click delivery are structural constraints verified by manual smoke; these tests cover the
/// settings contract only.
/// </summary>
public class PreloadPanelCollapseTests
{
    [Fact]
    public void PreloadPanelCollapsed_DefaultsToFalse()
    {
        var s = new RadarSettings();
        Assert.False(s.PreloadPanelCollapsed);
    }

    [Fact]
    public void PreloadPanelCollapsed_ToggleFlipsAndPersists()
    {
        var s = new RadarSettings();
        s.PreloadPanelCollapsed = !s.PreloadPanelCollapsed;
        Assert.True(s.PreloadPanelCollapsed);
        s.PreloadPanelCollapsed = !s.PreloadPanelCollapsed;
        Assert.False(s.PreloadPanelCollapsed);
    }
}
