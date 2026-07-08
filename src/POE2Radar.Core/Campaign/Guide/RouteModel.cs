// Ported from ExileCampaigns2 by syrairc under TODO(syrairc-license) — upstream commit TODO(syrairc-hash)
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Core.Campaign.Guide;

// what completes a step's objective. see AdvanceEngine for how each is evaluated.
public enum ObjectiveType { Kill, Interact, Talk, Loot, Proximity, QuestFlag, EnterArea, ActivateWaypoint, Manual }

// when a step with several objectives advances.
public enum CompleteWhen { All, Any }

// how many path lines an objective's guidance draws. Nearest = one to the closest target (default),
// All = a line to every resolved target (e.g. all 3 Ancient Seals).
public enum PathMode { Nearest, All }

// how an entity matcher matches a live entity: by render name or by metadata path.
public enum MatchKind { Name, Path }

// what a guidance child points at. Tile = Radar ClusterTarget pattern / .tdt; Room = AreaGraph room-name
// filter; Entity = live entity by metadata Path or RenderName.
public enum TargetKind { Tile, Entity, Room }

// every value matched against game state. literal (default, case-insensitive contains) or a regex.
public sealed record Pattern(string Value, bool Regex = false);

// a world target (boss by Name, no-name object like NailStake by Path).
public sealed record EntityMatcher(Pattern Match, MatchKind MatchBy = MatchKind.Name);

// a Loot target: an inventory item plus how many must be held.
public sealed record ItemMatcher(Pattern Match, int Count = 1);

// a guidance target shared by Path / Indicator / MinimapIcon. MatchBy + LivingOnly apply only to Entity.
// LivingOnly: only resolve a live entity that's actually alive (has Life, CurrentHP > 0), so an arrow/icon
// skips a corpse or lifeless prop sharing the name. honored by Indicators + MinimapIcons, not the Paths channel.
public sealed record Target(TargetKind Kind, Pattern Match, MatchKind MatchBy = MatchKind.Name, bool LivingOnly = false);

// one ground/minimap route line. an Entity target that matches several live entities draws one line each.
public sealed record GuidePath(Target Target);

// one on-screen arrow/marker. Entity targets draw the arrow; Tile/Room are accepted but not drawn yet.
public sealed record Indicator(Target Target);

// single per-objective minimap icon. IconKey = SpriteIcon enum name; Tint = packed ARGB (default gold);
// Size = per-icon pixel size, null = use the global MinimapIcons.IconSize default.
public sealed record MinimapIcon(string IconKey, Target? Target = null, uint Tint = 0xFFFFC83Cu, float? Size = null)
{
    public const uint GoldDefault = 0xFFFFC83Cu;   // gold, matches the interaction arrow
}

// one objective on a step. only the fields relevant to Type are used.
public sealed record Objective(
    ObjectiveType Type,
    IReadOnlyList<EntityMatcher>? Entities = null,   // Kill/Interact/Talk/Proximity (priority order)
    IReadOnlyList<ItemMatcher>? Items = null,        // Loot
    int Count = 1,                                   // Kill/Interact/Talk (Loot uses ItemMatcher.Count)
    float Distance = 0f,                             // Proximity (units; engine applies a default when 0)
    Pattern? Flag = null,                            // QuestFlag
    Pattern? AreaTarget = null,                      // EnterArea (area id)
    IReadOnlyList<Pattern>? ProgressFlags = null,    // optional per-target flags (multi-activate drop-each)
    string? Label = null,
    string? Note = null,
    IReadOnlyList<GuidePath>? Paths = null,          // guidance: ground/minimap route lines (independent of Type)
    IReadOnlyList<Indicator>? Indicators = null,     // guidance: on-screen arrows (independent of Type)
    IReadOnlyList<MinimapIcon>? MinimapIcons = null, // guidance: large-map icons (independent of Type)
    PathMode Mode = PathMode.Nearest);               // path line count: Nearest = one (default), All = per target

// one route step. self-describing: explicit act + area, plus its objectives. Id is the stable identity.
public sealed record RouteStep(
    string Id,
    int Act,
    string AreaId,
    string AreaName,
    string Text,
    string Note,
    bool Optional,
    CompleteWhen CompleteWhen,
    IReadOnlyList<Objective> Objectives,
    string? ImportFp);   // fnv1a of the upstream text, null = user-created

// v0.21 wire-shape for a single campaign-step instruction pushed to the client. declared here alongside
// RouteModel; populated by RouteCursor in a later task from RouteStep + current advance state.
public readonly record struct CampaignStepInstruction(
    string StepId,
    string Text,
    string AreaId,
    int Act,
    int Ordinal,
    int TotalSteps,
    bool Optional,
    bool Stalled,
    bool Available,
    string? DegradationReason);

// the whole route + companion data payloads. Steps list order is the canonical sequence. Companion
// JSONs (overrides / area-objectives / area-transitions / area-targets / xp_curve) are carried as raw
// text so the specific consumers (WorldStateAdapter, RouteCursor) can parse the shapes they need
// without re-reading resources.
public sealed record RouteModel(
    int Version,
    IReadOnlyList<RouteStep> Steps,
    string OverridesJson = "",
    string AreaObjectivesJson = "",
    string AreaTransitionsJson = "",
    string AreaTargetsJson = "",
    string XpCurveJson = "")
{
    public const int CurrentVersion = 2;   // guidance lives in Paths/Indicators/MinimapIcon children
    public static readonly RouteModel Empty = new(CurrentVersion, Array.Empty<RouteStep>());

    // logical embedded-resource names landed by EC2-DATA (Task 1). Order matches FromJson parameters.
    private const string RouteResource            = "POE2Radar.Core.Campaign.Guide.Data.poe2.route.json";
    private const string OverridesResource        = "POE2Radar.Core.Campaign.Guide.Data.poe2.overrides.json";
    private const string AreaObjectivesResource   = "POE2Radar.Core.Campaign.Guide.Data.poe2.area-objectives.json";
    private const string AreaTransitionsResource  = "POE2Radar.Core.Campaign.Guide.Data.poe2.area-transitions.json";
    private const string AreaTargetsResource      = "POE2Radar.Core.Campaign.Guide.Data.poe2.area-targets.json";
    private const string XpCurveResource          = "POE2Radar.Core.Campaign.Guide.Data.poe2.xp_curve.json";

    private static JsonSerializerOptions BuildOpts() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) },
    };

    // reads all 6 embedded resources from the POE2Radar.Core assembly and returns a fully-hydrated model.
    // throws RouteSchemaMismatchException on missing resource, malformed JSON, or schema-version mismatch.
    public static RouteModel LoadEmbedded()
    {
        var asm = typeof(RouteModel).Assembly;
        var routeJson           = ReadResource(asm, RouteResource);
        var overridesJson       = ReadResource(asm, OverridesResource);
        var areaObjectivesJson  = ReadResource(asm, AreaObjectivesResource);
        var areaTransitionsJson = ReadResource(asm, AreaTransitionsResource);
        var areaTargetsJson     = ReadResource(asm, AreaTargetsResource);
        var xpCurveJson         = ReadResource(asm, XpCurveResource);
        return FromJson(routeJson, overridesJson, areaObjectivesJson, areaTransitionsJson, areaTargetsJson, xpCurveJson);
    }

    // deterministic test seam: given the six JSON payloads (in the same order as LoadEmbedded reads them),
    // parse route.json into RouteStep[] and carry the other five as raw strings for the consumers.
    public static RouteModel FromJson(
        string routeJson,
        string overridesJson,
        string areaObjectivesJson,
        string areaTransitionsJson,
        string areaTargetsJson,
        string xpCurveJson)
    {
        if (routeJson is null) throw new ArgumentNullException(nameof(routeJson));
        var opts = BuildOpts();

        RouteDoc? parsed;
        try { parsed = JsonSerializer.Deserialize<RouteDoc>(routeJson, opts); }
        catch (JsonException ex)
        {
            throw new RouteSchemaMismatchException("route.json failed to deserialize", ex);
        }

        if (parsed is null)
            throw new RouteSchemaMismatchException("route.json deserialized to null");
        if (parsed.Version != CurrentVersion)
            throw new RouteSchemaMismatchException(
                $"route.json schemaVersion={parsed.Version}, expected {CurrentVersion}");

        return new RouteModel(
            parsed.Version,
            parsed.Steps ?? Array.Empty<RouteStep>(),
            overridesJson       ?? string.Empty,
            areaObjectivesJson  ?? string.Empty,
            areaTransitionsJson ?? string.Empty,
            areaTargetsJson     ?? string.Empty,
            xpCurveJson         ?? string.Empty);
    }

    private static string ReadResource(Assembly asm, string name)
    {
        using var s = asm.GetManifestResourceStream(name)
            ?? throw new RouteSchemaMismatchException($"embedded resource '{name}' not found");
        using var reader = new StreamReader(s);
        return reader.ReadToEnd();
    }

    // private DTO used only to unpack route.json into the record graph.
    private sealed record RouteDoc(int Version, IReadOnlyList<RouteStep>? Steps);
}

public sealed class RouteSchemaMismatchException : Exception
{
    public RouteSchemaMismatchException(string message) : base(message) { }
    public RouteSchemaMismatchException(string message, Exception inner) : base(message, inner) { }
}
