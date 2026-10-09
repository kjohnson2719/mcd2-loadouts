using System.Collections.Generic;
using NeoRune;
using UE.CoreUObject;
using UE.Engine;
using UE.GameplayTags;
using UE.InventorySystem;
using UE.SWCoreCommon;
using UE.SWCoreGameplay;

namespace Loadouts;

/// <summary>
/// One piece of a loadout: which item it is, the slot it goes in, and the enchantment it had. Items are found again by
/// their type and the time they were picked up (the game's item ids are only kept for a session).
/// </summary>
public struct SavedItem
{
    // Gameplay tag names, e.g. the slot's and the item type's.
    public string Slot;
    public string Type;
    public long Picked;
    public string Rarity;
    public double Power;
    // The item's name when it was saved, for the loadouts screen.
    public string Name;
    // Its enchantment ("None" for none): the enchantment's tag, its specific tag, and the points put into it.
    public string Enchant;
    public string EnchantSpecific;
    public int Points;
    public string EnchantName;
}

public struct SavedLoadout
{
    public bool Used;
    public List<SavedItem> Items;
    // The name the player gave it ("" for the default, "Loadout 1"...).
    public string Name;
}

/// <summary>A character's loadouts (saved per character).</summary>
public struct SavedLoadouts
{
    public List<SavedLoadout> Loadouts;
}

/// <summary>Reading the game's items.</summary>
public static class Items
{
    public static string TagName(FGameplayTag tag) => $"{tag.TagName}";

    /// <summary>An empty tag's name ("None"), or nothing at all.</summary>
    public static bool IsNone(string tag) => UKismetStringLibrary.Len(tag) == 0 || tag == "None";

    /// <summary>The item type's name as the game shows it, e.g. "Diamond Sword"; else the last part of its tag.</summary>
    public static string Name(UObject context, FGameplayTag type)
    {
        var name = UKismetTextLibrary.Conv_TextToString(UInventoryHelperLibrary.GetTypeTitle(context, type, false));
        return UKismetStringLibrary.Len(name) > 0 ? name : LastPart(TagName(type));
    }

    /// <summary>The last part of a tag's name: "Sharpness" for SW.Enchantment.Sharpness.</summary>
    public static string LastPart(string tag)
    {
        var parts = UKismetStringLibrary.ParseIntoArray(tag, ".", true);
        return parts.Count > 0 ? parts[parts.Count - 1] : tag;
    }

    /// <summary>The enchantment points put into an item's enchantment (0 when it has none).</summary>
    public static int Points(FItemData item)
    {
        if (!UInventoryHelperLibrary.GetEnchantmentEffectAppliedOnItem(item, out var effect)) return 0;
        return effect.EnchantmentPointsInvested;
    }

    public static string Enchantment(FItemData item) => TagName(UInventoryHelperLibrary.GetEnchantmentTagAppliedOnItem(item));

    /// <summary>A piece of gear as it's worn now, to save in a loadout.</summary>
    public static SavedItem Describe(UObject context, FInventoryEntry entry)
    {
        var item = entry.ItemData;
        var enchant = UInventoryHelperLibrary.GetEnchantmentTagAppliedOnItem(item);
        string enchantName = "";
        if (!IsNone(TagName(enchant))) enchantName = Name(context, enchant);
        return new SavedItem
        {
            Slot = TagName(entry.EquippedSlot),
            Type = TagName(item.TypeTag),
            Picked = item.PickupTimestamp,
            Rarity = TagName(item.RarityTag),
            Power = item.GeneratorData.PowerGeneratorValues.ItemPowerOriginal,
            Name = Name(context, item.TypeTag),
            Enchant = TagName(enchant),
            EnchantSpecific = TagName(UInventoryHelperLibrary.GetEnchantmentSpecificTagAppliedOnItem(item)),
            Points = Points(item),
            EnchantName = enchantName,
        };
    }

    /// <summary>Whether an item is the saved one: the same type, picked up at the same time.</summary>
    public static bool Same(FItemData item, SavedItem saved)
    {
        if (TagName(item.TypeTag) != saved.Type) return false;
        if (saved.Picked != 0 || item.PickupTimestamp != 0) return item.PickupTimestamp == saved.Picked;
        // Neither has a pickup time (gear the character started with?): the same rarity and power.
        double power = item.GeneratorData.PowerGeneratorValues.ItemPowerOriginal;
        double gap = power - saved.Power;
        return TagName(item.RarityTag) == saved.Rarity && gap < 0.01 && gap > -0.01;
    }

    /// <summary>The frame colour of an item of a rarity, like the game's slots: unique orange, special blue...</summary>
    public static FLinearColor RarityColour(string rarity)
    {
        if (rarity == "SW.Rarity.Unique") return Ui.Color(0.95f, 0.42f, 0.1f, 1);
        if (rarity == "SW.Rarity.Special") return Ui.Color(0.2f, 0.5f, 0.95f, 1);
        if (rarity == "SW.Rarity.Rare") return Ui.Color(0.3f, 0.8f, 0.35f, 1);
        if (rarity == "SW.Rarity.Common") return Ui.Color(0.55f, 0.55f, 0.55f, 1);
        // Talismans (no rarity): gold.
        return Ui.Color(0.85f, 0.65f, 0.2f, 1);
    }

    /// <summary>An amount of one currency in a bundle (a cost or a refund).</summary>
    public static int Amount(FCurrencyBundle bundle, ECurrency currency)
    {
        if (bundle.Currencies.TryGetValue(currency, out int amount)) return amount;
        return 0;
    }

    /// <summary>A bundle for the log: "SpringStone 3, EnchantmentPoint 1".</summary>
    public static string Describe(FCurrencyBundle bundle)
    {
        string text = "";
        foreach (var pair in bundle.Currencies)
        {
            if (UKismetStringLibrary.Len(text) > 0) text += ", ";
            text += $"{pair.Key} {pair.Value}";
        }
        return UKismetStringLibrary.Len(text) > 0 ? text : "nothing";
    }
}
