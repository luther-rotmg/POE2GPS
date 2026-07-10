using System.Text.Json;
using POE2Radar.Overlay.Config;
using Xunit;

namespace POE2Radar.Tests.Config;

/// <summary>
/// THR-XP-SETTINGS: two new fields on SessionHudSettings drive the XP-rate row.
/// ShowXpRate defaults OFF (PMS-6 opt-in policy). XpWindowMinutes is clamped
/// 1..60 in the setter (5 min vs 30 min is a real preference split among grinders,
/// but zero/negative values would break the ring-buffer sizing in the tracker).
/// The two fields must round-trip through JsonSerializer so radar_settings.json
/// persists them across restarts, and must survive the /api/settings JSON POST
/// key mirror (sessionHudShowXpRate + sessionHudXpWindowMinutes).
/// </summary>
public class SessionHudXpSettingsTests
{
    [Fact]
    public void ShowXpRate_defaults_off()
    {
        var s = new SessionHudSettings();
        Assert.False(s.ShowXpRate);
    }

    [Fact]
    public void XpWindowMinutes_defaults_to_five()
    {
        var s = new SessionHudSettings();
        Assert.Equal(5, s.XpWindowMinutes);
    }

    [Fact]
    public void XpWindowMinutes_clamps_below_one_to_one()
    {
        var s = new SessionHudSettings { XpWindowMinutes = 0 };
        Assert.Equal(1, s.XpWindowMinutes);

        s.XpWindowMinutes = -17;
        Assert.Equal(1, s.XpWindowMinutes);
    }

    [Fact]
    public void XpWindowMinutes_clamps_above_sixty_to_sixty()
    {
        var s = new SessionHudSettings { XpWindowMinutes = 120 };
        Assert.Equal(60, s.XpWindowMinutes);

        s.XpWindowMinutes = int.MaxValue;
        Assert.Equal(60, s.XpWindowMinutes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(60)]
    public void XpWindowMinutes_accepts_boundary_and_common_values(int v)
    {
        var s = new SessionHudSettings { XpWindowMinutes = v };
        Assert.Equal(v, s.XpWindowMinutes);
    }

    /// <summary>
    /// Simulates /api/settings POST -> serialize -> deserialize round-trip.
    /// The two new keys must survive radar_settings.json persistence so a grinder
    /// who toggles ShowXpRate on and picks a 30-minute window keeps that after
    /// restarting the overlay.
    /// </summary>
    [Fact]
    public void SessionHud_xp_fields_round_trip_through_json()
    {
        var root = new RadarSettings();
        root.SessionHud.ShowXpRate = true;
        root.SessionHud.XpWindowMinutes = 30;

        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        var json = JsonSerializer.Serialize(root, opts);
        var back = JsonSerializer.Deserialize<RadarSettings>(json, opts);

        Assert.NotNull(back);
        Assert.True(back!.SessionHud.ShowXpRate);
        Assert.Equal(30, back.SessionHud.XpWindowMinutes);
    }

    /// <summary>
    /// Clamp still fires when the on-disk file was edited by hand to an
    /// out-of-range value (or by an older build with no clamp).
    /// </summary>
    [Fact]
    public void Deserialized_XpWindowMinutes_out_of_range_is_clamped()
    {
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = "{\"sessionHud\":{\"xpWindowMinutes\":9999}}";
        var back = JsonSerializer.Deserialize<RadarSettings>(json, opts);
        Assert.NotNull(back);
        Assert.Equal(60, back!.SessionHud.XpWindowMinutes);
    }
}
