namespace POE2Radar.Core.Campaign;

public enum ClassifyConfidence { High, Medium, Low }

public readonly record struct ClassifyResult(
    ObjectiveTier Tier,
    string SuggestedCategory,
    ClassifyConfidence Confidence);

public static class ObjectiveClassifier
{
    public static ClassifyResult? Classify(
        string? metadata,
        string entityCategory,
        bool poi,
        string rarity)
    {
        var meta = metadata ?? "";
        bool HasMeta(string s) => meta.Contains(s, StringComparison.OrdinalIgnoreCase);
        bool IsCat(string s) => string.Equals(entityCategory, s, StringComparison.OrdinalIgnoreCase);
        bool IsRarity(string s) => string.Equals(rarity, s, StringComparison.OrdinalIgnoreCase);

        // Rules 1-8: league-mechanic metadata fragments (any category)
        if (HasMeta("Breach"))      return new(ObjectiveTier.Bonus, "League", ClassifyConfidence.High);
        if (HasMeta("Ritual"))      return new(ObjectiveTier.Bonus, "League", ClassifyConfidence.High);
        if (HasMeta("Expedition"))  return new(ObjectiveTier.Bonus, "League", ClassifyConfidence.High);
        if (HasMeta("Delirium"))    return new(ObjectiveTier.Bonus, "League", ClassifyConfidence.High);
        if (HasMeta("Abyss"))       return new(ObjectiveTier.Bonus, "League", ClassifyConfidence.High);
        if (HasMeta("Sanctum"))     return new(ObjectiveTier.Bonus, "League", ClassifyConfidence.High);
        if (HasMeta("Heist"))       return new(ObjectiveTier.Bonus, "League", ClassifyConfidence.High);
        if (HasMeta("Affliction"))  return new(ObjectiveTier.Bonus, "League", ClassifyConfidence.High);

        // Rule 9: Boss path + Monster + Unique rarity
        if ((HasMeta("BossArena") || HasMeta("Boss")) && IsCat("Monster") && IsRarity("Unique"))
            return new(ObjectiveTier.SideBoss, "SideBoss", ClassifyConfidence.High);

        // Rule 10: Boss path + Monster + poi (any rarity)
        if (HasMeta("Boss") && IsCat("Monster") && poi)
            return new(ObjectiveTier.SideBoss, "SideBoss", ClassifyConfidence.Medium);

        // Rules 11-14: Transition category
        if (HasMeta("Transition") && IsCat("Transition"))
            return new(ObjectiveTier.Exit, "Transition", ClassifyConfidence.High);
        if (HasMeta("Exit") && IsCat("Transition"))
            return new(ObjectiveTier.Exit, "Transition", ClassifyConfidence.High);
        if ((HasMeta("WorldArea") || HasMeta("AreaTransition")) && IsCat("Transition"))
            return new(ObjectiveTier.Exit, "Transition", ClassifyConfidence.High);
        if (IsCat("Transition"))
            return new(ObjectiveTier.Exit, "Transition", ClassifyConfidence.Medium);

        // Rule 15: Shrine/Altar in Object category
        if ((HasMeta("Shrine") || HasMeta("Altar")) && IsCat("Object"))
            return new(ObjectiveTier.Bonus, "Shrine", ClassifyConfidence.Medium);

        // Rule 16: Chest with "Unique" in the metadata path
        if (HasMeta("Chest") && HasMeta("Unique") && IsCat("Chest"))
            return new(ObjectiveTier.Bonus, "Treasure", ClassifyConfidence.Medium);

        // Rule 17: poi=true + Unique rarity (any category)
        if (poi && IsRarity("Unique"))
            return new(ObjectiveTier.SideBoss, "SideBoss", ClassifyConfidence.Medium);

        // Rule 18: NPC + poi
        if (IsCat("Npc") && poi)
            return new(ObjectiveTier.Bonus, "NPC", ClassifyConfidence.Low);

        // Rule 19: Monster + Unique (fallback after rules 9/10/17)
        if (IsCat("Monster") && IsRarity("Unique"))
            return new(ObjectiveTier.SideBoss, "SideBoss", ClassifyConfidence.Low);

        return null;
    }
}
