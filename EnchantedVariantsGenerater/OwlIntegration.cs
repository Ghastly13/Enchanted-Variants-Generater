using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace EnchantedVariantsGenerater
{
    public record GeneratedWeapon(FormKey FormKey, IWeaponGetter BaseItem, EnchantmentInfo Enchantment);

    // Open World Loot (SkyPatcher Edition) distributes enchanted weapons through one list per material and weapon
    // type, OWL_Weapon_<Material>_<Type>_Ench, which holds the vanilla per-material enchanted list. This files each
    // generated weapon variant into a sublist for its base weapon's material and type, and writes a SkyPatcher INI
    // that adds each sublist to the matching OWL list. Two materials are special in OWL:
    //   - Daedric also has OWL_Weapon_Daedric_<Type>_Ench_Fire (fire-enchanted only); fire variants go there too.
    //   - Draugr has no general enchanted list, only frost: OWL_Weapon_Draugr_<Type>[_Honed]_Ench_Frost_01..03,
    //     one per frost tier. Only frost variants are filed for Draugr; the rest are skipped.
    public static class OwlIntegration
    {
        public static readonly ModKey OwlModKey = ModKey.FromFileName("Open World Loot.esp");
        private const int MaxEntriesPerList = 255;
        private const string SublistSuffix = "_EVG";

        private record MaterialInfo(string Name, int FirstTier, short[] TierLevels);

        // Material keyword (checked in this order) -> OWL material name, the lowest enchantment tier that material
        // gets, and the entry level for its three tiers, taken from the vanilla SublistEnch<Material>... lists.
        private static readonly (string Keyword, MaterialInfo Info)[] Materials =
        {
            ("WeapMaterialDraugrHoned", new MaterialInfo("DraugrHoned", 1, new short[] { 1, 1, 1 })),
            ("WeapMaterialDraugr", new MaterialInfo("Draugr", 1, new short[] { 1, 1, 1 })),
            ("DLC2WeaponMaterialStalhrim", new MaterialInfo("Stalhrim", 4, new short[] { 37, 40, 43 })),
            ("DLC2WeaponMaterialNordic", new MaterialInfo("Nordic", 3, new short[] { 20, 22, 25 })),
            ("WeapMaterialDaedric", new MaterialInfo("Daedric", 4, new short[] { 47, 50, 53 })),
            ("WeapMaterialEbony", new MaterialInfo("Ebony", 4, new short[] { 37, 40, 43 })),
            ("WeapMaterialGlass", new MaterialInfo("Glass", 3, new short[] { 28, 31, 34 })),
            ("WeapMaterialElven", new MaterialInfo("Elven", 3, new short[] { 20, 22, 25 })),
            ("WeapMaterialDwarven", new MaterialInfo("Dwarven", 2, new short[] { 13, 15, 17 })),
            ("WeapMaterialOrcish", new MaterialInfo("Orcish", 2, new short[] { 7, 9, 11 })),
            ("WeapMaterialImperial", new MaterialInfo("Imperial", 1, new short[] { 4, 6, 8 })),
            ("WeapMaterialSteel", new MaterialInfo("Steel", 1, new short[] { 4, 6, 8 })),
            ("WeapMaterialIron", new MaterialInfo("Iron", 1, new short[] { 1, 4, 6 })),
        };

        // Vanilla weapon-type keyword -> OWL type name
        private static readonly (string Keyword, string Type)[] Types =
        {
            ("WeapTypeSword", "Sword"), ("WeapTypeWarAxe", "WarAxe"), ("WeapTypeMace", "Mace"), ("WeapTypeDagger", "Dagger"),
            ("WeapTypeGreatsword", "GreatSword"), ("WeapTypeBattleaxe", "BattleAxe"), ("WeapTypeWarhammer", "Warhammer"),
            ("WeapTypeBow", "Bow"),
        };

        public static void Run(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, Config config, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, List<GeneratedWeapon> generated)
        {
            string iniPath = Path.Combine(state.DataFolderPath.Path, "SKSE", "Plugins", "SkyPatcher", "leveledList", "EnchantedVariantsGenerater", OwlModKey.FileName + ".ini");

            bool owlLoaded = state.LoadOrder.ContainsKey(OwlModKey) && state.LoadOrder[OwlModKey].Enabled;
            if (!config.OpenWorldLootIntegration || !owlLoaded || generated.Count == 0)
            {
                if (config.OpenWorldLootIntegration && !owlLoaded) Console.WriteLine("Open World Loot is not in the load order; skipping OWL integration");
                DeleteIfPresent(iniPath);
                return;
            }

            // OWL's own lists, by EditorID (case-insensitive: OWL's INIs mix "GreatSword" and "Greatsword")
            var owlLists = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
            foreach (var list in state.LoadOrder.PriorityOrder.LeveledItem().WinningOverrides())
            {
                if (list.FormKey.ModKey == OwlModKey && list.EditorID != null) owlLists[list.EditorID] = list.FormKey;
            }

            var enchantments = generated.Select(g => g.Enchantment.Enchantment).DistinctBy(e => e.FormKey).ToList();
            var fire = ElementEnchantments(state, linkCache, enchantments, "MagicDamageFire", "Fire");
            var frost = ElementEnchantments(state, linkCache, enchantments, "MagicDamageFrost", "Frost");
            var materialKeywords = ResolveKeywords(linkCache, Materials.Select(m => m.Keyword));
            var typeKeywords = ResolveKeywords(linkCache, Types.Select(t => t.Keyword));

            // OWL list EditorID -> (tier index 0..2 -> generated weapons)
            var targets = new SortedDictionary<string, SortedDictionary<int, List<FormKey>>>(StringComparer.OrdinalIgnoreCase);
            var skipped = new Dictionary<string, int>();
            void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
            void Add(string target, int tierIndex, FormKey weapon)
            {
                if (!owlLists.ContainsKey(target)) { Skip("no OWL list " + target); return; }
                if (!targets.TryGetValue(target, out var tiers)) targets[target] = tiers = new SortedDictionary<int, List<FormKey>>();
                if (!tiers.TryGetValue(tierIndex, out var list)) tiers[tierIndex] = list = new List<FormKey>();
                list.Add(weapon);
            }

            foreach (var g in generated)
            {
                var keywords = g.BaseItem.Keywords?.Select(k => k.FormKey).ToHashSet() ?? new HashSet<FormKey>();
                var material = Materials.FirstOrDefault(m => materialKeywords.TryGetValue(m.Keyword, out var fk) && keywords.Contains(fk)).Info;
                var type = Types.FirstOrDefault(t => typeKeywords.TryGetValue(t.Keyword, out var fk) && keywords.Contains(fk)).Type;
                if (material == null) { Skip("base weapon has no OWL material"); continue; }
                if (type == null) { Skip("base weapon has no OWL weapon type"); continue; }

                int? tier = TierOf(g.Enchantment);
                if (tier == null) { Skip("enchantment tier unknown"); continue; }
                int tierIndex = Math.Clamp(tier.Value - material.FirstTier, 0, 2);
                var enchantment = g.Enchantment.Enchantment.FormKey;

                if (material.Name.StartsWith("Draugr"))
                {
                    if (!frost.Contains(enchantment)) { Skip("Draugr: OWL only distributes frost enchantments"); continue; }
                    string honed = material.Name == "DraugrHoned" ? "_Honed" : "";
                    Add("OWL_Weapon_Draugr_" + type + honed + "_Ench_Frost_" + (tierIndex + 1).ToString("00"), 0, g.FormKey);
                    continue;
                }

                Add("OWL_Weapon_" + material.Name + "_" + type + "_Ench", tierIndex, g.FormKey);
                if (material.Name == "Daedric" && fire.Contains(enchantment))
                {
                    Add("OWL_Weapon_Daedric_" + type + "_Ench_Fire", tierIndex, g.FormKey);
                }
            }

            // Build one parent sublist per OWL list: one entry per tier (at that material's tier level), each pointing at a
            // child list of that tier's variants, split into several children if a tier holds more than 255.
            bool draugrList(string target) => target.StartsWith("OWL_Weapon_Draugr_", StringComparison.OrdinalIgnoreCase);
            var ini = new StringBuilder();
            ini.AppendLine("; Generated by Enchanted Variants Generater on every Synthesis run. Do not edit: the next run replaces this file.");
            ini.AppendLine("; Adds the generated weapon variants (" + state.PatchMod.ModKey.FileName + ") to Open World Loot's enchanted weapon lists.");
            int listCount = 0, entryCount = 0;
            foreach (var (target, tiers) in targets)
            {
                var parent = NewList(state, linkCache, config, target + SublistSuffix);
                listCount++;
                foreach (var (tierIndex, weapons) in tiers)
                {
                    var material = Materials.First(m => target.StartsWith("OWL_Weapon_" + m.Info.Name + "_", StringComparison.OrdinalIgnoreCase)
                        || (draugrList(target) && m.Info.Name == "Draugr")).Info;
                    short level = draugrList(target) ? (short)1 : material.TierLevels[tierIndex];
                    var chunks = weapons.Chunk(MaxEntriesPerList).ToList();
                    for (int c = 0; c < chunks.Count; c++)
                    {
                        string childId = target + SublistSuffix + "_T" + (tierIndex + 1) + (chunks.Count > 1 ? "_" + (c + 1) : "");
                        var child = NewList(state, linkCache, config, childId);
                        listCount++;
                        foreach (var weapon in chunks[c]) child.Entries!.Add(Program.CreateLeveledItemEntry(1, weapon));
                        entryCount += chunks[c].Length;
                        state.PatchMod.LeveledItems.Set(child);
                        parent.Entries!.Add(Program.CreateLeveledItemEntry(level, child.FormKey));
                    }
                }
                state.PatchMod.LeveledItems.Set(parent);

                var owlList = owlLists[target];
                string entry = state.PatchMod.ModKey.FileName + "|" + parent.FormKey.ID.ToString("X6") + "~1~1";
                string op = config.OpenWorldLootWeight <= 1
                    ? "addOnceToLLs=" + entry
                    : "addToLLs=" + string.Join(", ", Enumerable.Repeat(entry, config.OpenWorldLootWeight));
                ini.AppendLine("; " + target);
                ini.AppendLine("filterByLLs=" + OwlModKey.FileName + "|" + owlList.ID.ToString("X6") + ":" + op);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(iniPath)!);
            File.WriteAllText(iniPath, ini.ToString());
            Console.WriteLine("Open World Loot: filed " + entryCount + " generated weapons into " + listCount + " leveled lists for " + targets.Count + " OWL lists; wrote " + iniPath);
            foreach (var (reason, count) in skipped.OrderByDescending(s => s.Value))
            {
                Console.WriteLine("Open World Loot: skipped " + count + " (" + reason + ")");
            }
        }

        // Tier of an enchantment entry: the digits of its first non-NPC leveled-list suffix ("_04" -> 4),
        // else the trailing digits of its EditorID.
        private static int? TierOf(EnchantmentInfo info)
        {
            var suffix = info.LeveledLists?.Where(l => !l.LeveledListSuffix.Contains("NPC")).Select(l => l.LeveledListSuffix).FirstOrDefault();
            var m = Regex.Match(suffix ?? "", @"(\d+)$");
            if (!m.Success) m = Regex.Match(info.EditorID, @"(\d+)$");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }

        // An enchantment counts as fire (or frost) when one of its effects carries the vanilla damage keyword, or when a
        // weapon with that enchantment already sits in a vanilla-style SublistEnch...Fire (or ...Frost) list, which is how
        // Summermyst files its fire blasts and fire weakness.
        private static HashSet<FormKey> ElementEnchantments(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, List<IObjectEffectGetter> candidates, string keywordEditorID, string sublistSuffix)
        {
            var result = new HashSet<FormKey>();
            if (linkCache.TryResolve<IKeywordGetter>(keywordEditorID, out var keyword))
            {
                foreach (var ench in candidates)
                {
                    foreach (var effect in ench.Effects)
                    {
                        if (effect.BaseEffect.TryResolve(linkCache, out var mgef) && mgef.Keywords != null && mgef.Keywords.Any(k => k.FormKey == keyword.FormKey))
                        {
                            result.Add(ench.FormKey);
                            break;
                        }
                    }
                }
            }
            var sublistName = new Regex("^(DLC\\d)?SublistEnch.*" + sublistSuffix + "$", RegexOptions.IgnoreCase);
            foreach (var list in state.LoadOrder.PriorityOrder.LeveledItem().WinningOverrides())
            {
                if (list.EditorID == null || list.Entries == null || !sublistName.IsMatch(list.EditorID)) continue;
                foreach (var entry in list.Entries)
                {
                    if (entry.Data != null && linkCache.TryResolve<IWeaponGetter>(entry.Data.Reference.FormKey, out var weapon) && !weapon.ObjectEffect.IsNull)
                    {
                        result.Add(weapon.ObjectEffect.FormKey);
                    }
                }
            }
            return result;
        }

        private static Dictionary<string, FormKey> ResolveKeywords(ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, IEnumerable<string> editorIDs)
        {
            var result = new Dictionary<string, FormKey>();
            foreach (var id in editorIDs)
            {
                if (linkCache.TryResolve<IKeywordGetter>(id, out var kw)) result[id] = kw.FormKey;
            }
            return result;
        }

        // A fresh list, reusing the record from an earlier run when CheckExistingGenerated finds one, so FormIDs stay put.
        private static LeveledItem NewList(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, Config config, string editorID)
        {
            LeveledItem list;
            if (config.CheckExistingGenerated && linkCache.TryResolve<ILeveledItemGetter>(editorID, out var existing))
            {
                list = existing.DeepCopy();
            }
            else
            {
                list = new LeveledItem(state.PatchMod, editorID);
            }
            list.Flags = LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer | LeveledItem.Flag.CalculateForEachItemInCount;
            list.Entries = new Noggog.ExtendedList<LeveledItemEntry>();
            return list;
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Console.WriteLine("Removed stale " + path);
            }
        }
    }
}
