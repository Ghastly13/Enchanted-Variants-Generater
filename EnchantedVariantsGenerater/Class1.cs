using System;
using System.Collections.Generic;
using System.Text;

namespace EnchantedVariantsGenerater
{
    public class Config
    {
        public bool CheckExistingGenerated { get; set; } = true;
        public bool VerboseLogging { get; set; } = false;

        // Before generating an enchanted variant, look for one that already exists in the load order:
        // a playable record whose template is the same base item and whose enchantment is the same.
        // If one exists, no new record is generated.
        public bool SkipDuplicates { get; set; } = true;

        // When a duplicate is skipped, put the existing variant into the generated leveled lists in its place,
        // so the lists stay complete. False leaves the duplicate out of the lists entirely.
        public bool AddDuplicatesToLeveledLists { get; set; } = true;

        // When Open World Loot is in the load order, file generated weapon variants into OWL's enchanted lists:
        // one sublist per material and weapon type, wired in by a SkyPatcher INI written next to the output.
        public bool OpenWorldLootIntegration { get; set; } = true;

        // How many times each generated sublist is added to its OWL list. OWL picks evenly between a list's
        // entries, so this sets the generated variants' share against the variants already there.
        public int OpenWorldLootWeight { get; set; } = 1;
    }

    public class InputJSON
    {
        public string[]? RequiredMods { get; set; }
        public ItemJSON[]? Armors { get; set; }
        public ItemJSON[]? Weapons { get; set; }
        public EnchantmentJSON[]? Enchantments { get; set; }
        public int PriorityOrder = 0;
    }

    public class ItemJSON
    {
        public string[]? RequiredMods { get; set; }
        public string[]? BlacklistedMods { get; set; }
        public string? FormKey { get; set; }
        public string? EditorID { get; set; }
        public uint? Value { get; set; }
        public bool SetScripts { get; set; } = true;
    }

    public class EnchantmentJSON
    {
        public string[]? RequiredMods { get; set; }
        public string? FormKey { get; set; }
        public string? EditorID { get; set; }
        public string? Prefix { get; set; }
        public string? Suffix { get; set; }
        public int? EnchantmentAmount { get; set; }
        public LeveledListInfo[]? LeveledLists { get; set; }
        public EnchantmentJSON[]? Overwrites { get; set; }
    }
}
