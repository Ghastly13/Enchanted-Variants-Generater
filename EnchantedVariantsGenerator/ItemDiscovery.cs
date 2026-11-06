using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace EnchantedVariantsGenerator
{
    // Finds base weapons and armor no input file names and files each into the input folder for its type and tier, the
    // way the hand-written files are organised. Built to refuse rather than guess: an item is used only when every
    // check passes, and every candidate's outcome, with the reason, goes to "discovered items.txt" in the data folder.
    //   Candidates   weapons and armor with a material keyword, not enchanted, not a template variant, from a plugin not
    //                excluded, and named by no input file (even a file switched off by RequiredMods)
    //   Checks       playable, named, has an EditorID, enchantable (no MagicDisallowEnchanting / DaedricArtifact),
    //                model file present, every material keyword known and agreeing on one material, a weapon type /
    //                armor slot and weight the input folders cover, the target folder has enchantments, and distributed
    public static class ItemDiscovery
    {
        private const string ReportFile = "discovered items.txt";

        private record Material(string Keyword, string Name, string Tier, bool Specific = false, string[]? Supersedes = null);

        // Weapon tiers follow the hand-written files: folder "weapons<tier>" / "WeaponBows<tier>". A specific keyword
        // (Skyforge, Redguard, ...) names the material even where a generic one (Steel) sits beside it.
        private static readonly Material[] WeaponMaterials =
        {
            new("ShWeapMaterialSilverPolished", "PolishedSilver", "03", true),
            new("WAF_WeapMaterialSkyforge", "Skyforge", "03", true),
            new("WAF_WeapMaterialRedguard", "Redguard", "03", true),
            new("WAF_WeapMaterialBlades", "Akaviri", "03", true),
            new("WAF_WeapMaterialForsworn", "Forsworn", "01", true),
            new("WAF_DLC1WeapMaterialDawnguard", "Dawnguard", "01", true),
            new("ccBGSSSE025_WeapMaterialAmber", "Amber", "04", true),
            new("ccBGSSSE025_WeapMaterialDark", "DarkSeducer", "04", true),
            new("ccBGSSSE025_WeapMaterialGolden", "GoldenSaint", "04", true),
            new("ccBGSSSE025_WeapMaterialMadness", "Madness", "04", true),
            new("WeapMaterialIron", "Iron", "01"),
            new("WeapMaterialSteel", "Steel", "01"),
            new("WeapMaterialImperial", "Imperial", "01"),
            new("WeapMaterialWood", "Wood", "01"),
            new("WeapMaterialSilver", "Silver", "01"),
            new("WAF_MaterialSilver", "Silver", "01"),
            new("WeapMaterialFalmer", "Falmer", "01"),
            new("WeapMaterialFalmerHoned", "FalmerHoned", "03"),
            new("WeapMaterialDraugr", "Draugr", "AncientNord"),
            new("WeapMaterialDraugrHoned", "DraugrHoned", "AncientNord"),
            new("WeapMaterialOrcish", "Orcish", "02"),
            new("WeapMaterialDwarven", "Dwarven", "02"),
            new("WeapMaterialElven", "Elven", "03"),
            new("WeapMaterialGlass", "Glass", "03"),
            new("DLC2WeaponMaterialNordic", "Nordic", "03Nordic"),
            new("WeapMaterialEbony", "Ebony", "04"),
            new("WeapMaterialDaedric", "Daedric", "04"),
            new("DLC2WeaponMaterialStalhrim", "Stalhrim", "04Stalhrim"),
            new("DLC1WeapMaterialDragonbone", "Dragonbone", "04"),
        };

        // Armor tiers follow the hand-written vanilla armor files, separately for heavy and light
        private static readonly Material[] HeavyArmorMaterials =
        {
            new("ArmorMaterialIronBanded", "IronBanded", "01", Supersedes: new[] { "ArmorMaterialIron" }),
            new("ArmorMaterialSteelPlate", "SteelPlate", "02", Supersedes: new[] { "ArmorMaterialSteel" }),
            new("ArmorMaterialIron", "Iron", "01"),
            new("ArmorMaterialSteel", "Steel", "01"),
            new("ArmorMaterialImperialHeavy", "ImperialHeavy", "01"),
            new("DLC2ArmorMaterialBonemoldHeavy", "Bonemold", "01"),
            new("ArmorMaterialDwarven", "Dwarven", "02"),
            new("DLC2ArmorMaterialChitinHeavy", "Chitin", "02"),
            new("ArmorMaterialOrcish", "Orcish", "03"),
            new("DLC2ArmorMaterialNordicHeavy", "Nordic", "03"),
            new("ArmorMaterialEbony", "Ebony", "03"),
            new("DLC2ArmorMaterialStalhrimHeavy", "Stalhrim", "04"),
            new("ArmorMaterialDaedric", "Daedric", "04"),
            new("ArmorMaterialDragonplate", "Dragonplate", "04"),
        };
        private static readonly Material[] LightArmorMaterials =
        {
            new("ArmorMaterialElvenGilded", "ElvenGilded", "03", Supersedes: new[] { "ArmorMaterialElven" }),
            new("ArmorMaterialImperialStudded", "ImperialStudded", "01", Supersedes: new[] { "ArmorMaterialStudded", "ArmorMaterialImperialLight" }),
            new("ArmorMaterialHide", "Hide", "01"),
            new("ArmorMaterialLeather", "Leather", "01"),
            new("ArmorMaterialImperialLight", "ImperialLight", "01"),
            new("ArmorMaterialStudded", "Studded", "01"),
            new("ArmorMaterialElven", "Elven", "02"),
            new("DLC2ArmorMaterialChitinLight", "Chitin", "02"),
            new("ArmorMaterialScaled", "Scaled", "02"),
            new("ArmorMaterialGlass", "Glass", "03"),
            new("DLC2ArmorMaterialStalhrimLight", "Stalhrim", "03"),
            new("ArmorMaterialDragonscale", "Dragonscale", "04"),
        };

        // Keywords with "Material" in the name that sort or tag items rather than name their material
        private static readonly Regex MarkerKeyword = new(
            @"^(WAF_MaterialFaction|WAF_WeapMaterialFaction|WAF_MaterialNordic|WAF_MaterialChitin|WAF_MaterialBonemold|WAF_TreatAs.*|WAF_Ignore.*|WAF_ToolsMaterials|SF_K_.*|xxx.*|.*CraftingMaterial.*)$",
            RegexOptions.IgnoreCase);

        private static readonly Regex CreationClub = new(@"^cc[a-z]+sse\d+", RegexOptions.IgnoreCase);

        // Words in an item's name that name a material, longest first, each with the materials it is consistent with. The
        // name is a second witness to the keywords: some mods tag items with a nearby vanilla material (a Forsworn halberd
        // as Steel, an Ancient Nord helmet as Daedric), so the name must name the keyword's material and nothing else.
        private static readonly (string Phrase, string[] Materials)[] NameWords =
        {
            ("Polished Silver", new[] { "PolishedSilver" }),
            ("Steel Plate", new[] { "SteelPlate" }),
            ("Banded Iron", new[] { "IronBanded" }), ("Iron Banded", new[] { "IronBanded" }),
            ("Nord Hero", new[] { "NordHero" }),
            ("Ancient Nord", new[] { "Draugr", "DraugrHoned" }),
            ("Dark Seducer", new[] { "DarkSeducer" }), ("Golden Saint", new[] { "GoldenSaint" }),
            ("Honed", new[] { "DraugrHoned", "FalmerHoned" }),
            ("Draugr", new[] { "Draugr", "DraugrHoned" }),
            ("Skyforge", new[] { "Skyforge" }), ("Silver", new[] { "Silver" }), ("Dawnguard", new[] { "Dawnguard" }),
            ("Forsworn", new[] { "Forsworn" }), ("Redguard", new[] { "Redguard" }), ("Akaviri", new[] { "Akaviri" }),
            ("Blades", new[] { "Akaviri" }), ("Falmer", new[] { "Falmer", "FalmerHoned" }), ("Amber", new[] { "Amber" }),
            ("Madness", new[] { "Madness" }), ("Dragonbone", new[] { "Dragonbone" }), ("Dragonplate", new[] { "Dragonplate" }),
            ("Dragonscale", new[] { "Dragonscale" }),
            ("Imperial", new[] { "Imperial", "ImperialHeavy", "ImperialLight", "ImperialStudded" }),
            ("Studded", new[] { "Studded", "ImperialStudded" }),
            ("Gilded", new[] { "ElvenGilded" }),
            ("Elven", new[] { "Elven", "ElvenGilded" }),
            ("Iron", new[] { "Iron", "IronBanded" }), ("Steel", new[] { "Steel" }), ("Orcish", new[] { "Orcish" }),
            ("Dwarven", new[] { "Dwarven" }), ("Glass", new[] { "Glass" }), ("Nordic", new[] { "Nordic" }),
            ("Ebony", new[] { "Ebony" }), ("Daedric", new[] { "Daedric" }), ("Stalhrim", new[] { "Stalhrim" }),
            ("Bonemold", new[] { "Bonemold" }), ("Chitin", new[] { "Chitin" }), ("Hide", new[] { "Hide" }), ("Fur", new[] { "Hide" }),
            ("Leather", new[] { "Leather" }), ("Scaled", new[] { "Scaled" }),
            ("Wooden", new[] { "Wood" }), ("Hunting", new[] { "Wood" }),
        };

        // Null when the name names the material and no other, else why not
        private static string? NameDisagrees(string name, string material)
        {
            string rest = " " + name + " ";
            bool named = false;
            foreach (var (phrase, materials) in NameWords)
            {
                var pattern = new Regex(@"\b" + Regex.Escape(phrase) + @"\b", RegexOptions.IgnoreCase);
                if (!pattern.IsMatch(rest)) continue;
                if (!materials.Contains(material, StringComparer.OrdinalIgnoreCase)) return "name says " + phrase + ", keywords say " + material;
                named = true;
                rest = pattern.Replace(rest, " ");
            }
            return named ? null : "name does not say its material (" + material + ")";
        }

        public static void Run(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, Config config, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
            string dataPath, List<InputThing> inputs, HashSet<FormKey> listedItems)
        {
            string reportPath = Path.Combine(dataPath, ReportFile);
            if (!config.DiscoverItems)
            {
                if (File.Exists(reportPath)) File.Delete(reportPath);
                return;
            }

            var folders = inputs.ToDictionary(i => i.Folder, StringComparer.OrdinalIgnoreCase);
            var excludedPlugins = new HashSet<string>(config.DiscoverExcludedPlugins, StringComparer.OrdinalIgnoreCase);
            var excludedMaterials = new HashSet<string>(config.DiscoverExcludedMaterials, StringComparer.OrdinalIgnoreCase);
            var excludedItems = new HashSet<string>(config.DiscoverExcludedItems, StringComparer.OrdinalIgnoreCase);
            var keywordIDs = new Dictionary<FormKey, string>();
            string KeywordID(FormKey fk)
            {
                if (keywordIDs.TryGetValue(fk, out var id)) return id;
                id = linkCache.TryResolve<IKeywordGetter>(fk, out var kw) ? kw.EditorID ?? "" : "";
                return keywordIDs[fk] = id;
            }

            bool PluginAllowed(ModKey mod) =>
                mod != state.PatchMod.ModKey && !excludedPlugins.Contains(mod.FileName) && (config.DiscoverCreationClub || !CreationClub.IsMatch(mod.FileName));

            var distribution = Distribution.Build(state, linkCache, dataPath);
            var owl = OwlIntegration.IsActive(state, config) ? new OwlIntegration.Placement(state, config, linkCache) : null;

            // Items crafted from another weapon or armor are upgrades of it (Masterwork, crossbow modifications), not bases
            var upgrades = new HashSet<FormKey>();
            foreach (var recipe in state.LoadOrder.PriorityOrder.ConstructibleObject().WinningOverrides())
            {
                if (recipe.CreatedObject.IsNull || recipe.Items == null) continue;
                foreach (var component in recipe.Items)
                {
                    var fk = component.Item.Item.FormKey;
                    if (linkCache.TryResolve<IWeaponGetter>(fk, out _) || linkCache.TryResolve<IArmorGetter>(fk, out _))
                    {
                        upgrades.Add(recipe.CreatedObject.FormKey);
                        break;
                    }
                }
            }

            // Whether the folder's enchanted variants of this item would be distributed: weapons through Open World Loot when
            // it is active, otherwise only into leveled lists the input files name after the item, which must already exist
            // (the patcher fills those lists but does not wire new ones into the world)
            string? VariantsUndistributed(InputThing folder, IMajorRecordGetter item)
            {
                if (owl != null && item is IWeaponGetter weapon) return owl.Problem(weapon);
                foreach (var enchantment in folder.Enchantments.Values)
                {
                    foreach (var list in enchantment.LeveledLists ?? new List<LeveledListInfo>())
                    {
                        if (list.Mode == "Add" && linkCache.TryResolve<ILeveledItemGetter>(list.LeveledListPrefix + item.EditorID + list.LeveledListSuffix, out _)) return null;
                    }
                }
                return "its enchanted variants would not be distributed (no Open World Loot list and no existing leveled list for it)";
            }

            var added = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var rejected = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            int addedCount = 0;
            void Reject(IMajorRecordGetter item, string? name, string reason)
            {
                string plugin = item.FormKey.ModKey.FileName;
                if (!rejected.TryGetValue(plugin, out var list)) rejected[plugin] = list = new();
                list.Add(reason + ": " + (item.EditorID ?? item.FormKey.ToString()) + " \"" + name + "\"");
            }
            void Add(InputThing folder, IMajorRecordGetter item, string? name, string material, bool armor)
            {
                var json = new ItemJSON { EditorID = item.EditorID, FormKey = item.FormKey.ToString() };
                var target = armor ? folder.Armors : folder.Weapons;
                if (!target.TryAdd(item.EditorID!, json)) return;
                if (!added.TryGetValue(folder.Folder, out var list)) added[folder.Folder] = list = new();
                list.Add(item.EditorID + " \"" + name + "\" [" + material + "] " + item.FormKey.ModKey.FileName);
                addedCount++;
            }

            // Material by keywords: null and a reason when unknown, conflicting or missing
            (Material? Material, string? Reason) MaterialOf(IEnumerable<string> keywords, Material[] table)
            {
                var materialKeywords = keywords.Where(k => k.Contains("Material", StringComparison.OrdinalIgnoreCase) && !MarkerKeyword.IsMatch(k)).ToList();
                var known = new List<Material>();
                foreach (var k in materialKeywords)
                {
                    var m = table.FirstOrDefault(t => t.Keyword.Equals(k, StringComparison.OrdinalIgnoreCase));
                    if (m == null) return (null, "unknown material keyword " + k);
                    known.Add(m);
                }
                if (known.Count == 0) return (null, "no material keyword");
                var specific = known.Where(m => m.Specific).Select(m => m.Name).Distinct().ToList();
                if (specific.Count > 1) return (null, "conflicting material keywords " + string.Join(", ", specific));
                if (specific.Count == 1) return (known.First(m => m.Specific), null);
                var superseded = known.SelectMany(m => m.Supersedes ?? Array.Empty<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var remaining = known.Where(m => !superseded.Contains(m.Keyword)).Select(m => m.Name).Distinct().ToList();
                if (remaining.Count > 1) return (null, "conflicting material keywords " + string.Join(", ", remaining));
                return (known.First(m => m.Name == remaining[0]), null);
            }

            // Checks shared by weapons and armor; null when they pass
            string? CommonChecks(IMajorRecordGetter item, string? name, IEnumerable<string> keywords, bool playable, bool hasModel)
            {
                if (!playable) return "not playable";
                if (string.IsNullOrWhiteSpace(name)) return "no name";
                if (string.IsNullOrEmpty(item.EditorID)) return "no EditorID";
                if (excludedItems.Contains(item.EditorID)) return "excluded item";
                if (keywords.Any(k => k.Equals("MagicDisallowEnchanting", StringComparison.OrdinalIgnoreCase))) return "cannot be enchanted (MagicDisallowEnchanting)";
                if (keywords.Any(k => k.Equals("DaedricArtifact", StringComparison.OrdinalIgnoreCase))) return "Daedric artifact";
                if (!hasModel) return "model file missing";
                return null;
            }

            string? FolderCheck(string folderName, out InputThing? folder)
            {
                if (!folders.TryGetValue(folderName, out folder)) return "no input folder " + folderName;
                if (folder.Enchantments.Count == 0) return "input folder " + folderName + " has no enchantments for this load order";
                return null;
            }

            foreach (var weapon in state.LoadOrder.PriorityOrder.Weapon().WinningOverrides())
            {
                if (!PluginAllowed(weapon.FormKey.ModKey) || listedItems.Contains(weapon.FormKey)) continue;
                if (!weapon.Template.IsNull || !weapon.ObjectEffect.IsNull) continue;
                var keywords = (weapon.Keywords ?? Array.Empty<IFormLinkGetter<IKeywordGetter>>()).Select(k => KeywordID(k.FormKey)).ToList();
                if (!keywords.Any(k => k.Contains("Material", StringComparison.OrdinalIgnoreCase))) continue;
                string? name = weapon.Name?.String;

                bool playable = weapon.Data == null || !weapon.Data.Flags.HasFlag(WeaponData.Flag.NonPlayable);
                string? reason = CommonChecks(weapon, name, keywords, playable, AssetIndex.MeshPresent(weapon.Model?.File));
                if (reason != null) { Reject(weapon, name, reason); continue; }
                if (upgrades.Contains(weapon.FormKey)) { Reject(weapon, name, "crafted from another item (an upgrade)"); continue; }

                var anim = weapon.Data?.AnimationType;
                bool bow = anim is WeaponAnimationType.Bow or WeaponAnimationType.Crossbow;
                bool melee = anim is WeaponAnimationType.OneHandSword or WeaponAnimationType.OneHandDagger or WeaponAnimationType.OneHandAxe
                    or WeaponAnimationType.OneHandMace or WeaponAnimationType.TwoHandSword or WeaponAnimationType.TwoHandAxe;
                if (!bow && !melee) { Reject(weapon, name, "not an enchantable weapon type (" + anim + ")"); continue; }

                var (material, why) = MaterialOf(keywords, WeaponMaterials);
                if (material == null) { Reject(weapon, name, why!); continue; }
                string materialName = material.Name;
                // Nord Hero weapons carry the honed Draugr keyword; only their name tells them apart
                if (materialName.StartsWith("Draugr") && name!.Contains("Nord Hero", StringComparison.OrdinalIgnoreCase)) materialName = "NordHero";
                if (excludedMaterials.Contains(materialName)) { Reject(weapon, name, "excluded material " + materialName); continue; }
                reason = NameDisagrees(name!, materialName);
                if (reason != null) { Reject(weapon, name, reason); continue; }

                string folderName = material.Tier == "AncientNord"
                    ? (bow ? "" : "weaponsAncientNord")
                    : (bow ? "WeaponBows" : "weapons") + material.Tier;
                if (folderName == "") { Reject(weapon, name, "no input folder for Ancient Nord bows"); continue; }
                reason = FolderCheck(folderName, out var folder);
                if (reason != null) { Reject(weapon, name, reason); continue; }

                if (!distribution.IsDistributed(weapon.FormKey)) { Reject(weapon, name, "not distributed"); continue; }
                reason = VariantsUndistributed(folder!, weapon);
                if (reason != null) { Reject(weapon, name, reason); continue; }
                Add(folder!, weapon, name, materialName, false);
            }

            foreach (var armor in state.LoadOrder.PriorityOrder.Armor().WinningOverrides())
            {
                if (!PluginAllowed(armor.FormKey.ModKey) || listedItems.Contains(armor.FormKey)) continue;
                if (!armor.TemplateArmor.IsNull || !armor.ObjectEffect.IsNull) continue;
                var keywords = (armor.Keywords ?? Array.Empty<IFormLinkGetter<IKeywordGetter>>()).Select(k => KeywordID(k.FormKey)).ToList();
                if (!keywords.Any(k => k.Contains("ArmorMaterial", StringComparison.OrdinalIgnoreCase))) continue;
                string? name = armor.Name?.String;

                bool playable = !armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable);
                bool hasModel = AssetIndex.MeshPresent(armor.WorldModel?.Male?.Model?.File) || AssetIndex.MeshPresent(armor.WorldModel?.Female?.Model?.File);
                string? reason = CommonChecks(armor, name, keywords, playable, hasModel);
                if (reason != null) { Reject(armor, name, reason); continue; }
                if (upgrades.Contains(armor.FormKey)) { Reject(armor, name, "crafted from another item (an upgrade)"); continue; }

                bool heavy = keywords.Contains("ArmorHeavy", StringComparer.OrdinalIgnoreCase);
                bool light = keywords.Contains("ArmorLight", StringComparer.OrdinalIgnoreCase);
                if (heavy == light) { Reject(armor, name, heavy ? "both heavy and light" : "neither heavy nor light armor"); continue; }
                var slots = new[] { "ArmorBoots", "ArmorGauntlets", "ArmorHelmet", "ArmorCuirass", "ArmorShield" }
                    .Where(s => keywords.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
                if (slots.Count != 1) { Reject(armor, name, slots.Count == 0 ? "no armor slot keyword" : "several armor slot keywords"); continue; }

                var (material, why) = MaterialOf(keywords, heavy ? HeavyArmorMaterials : LightArmorMaterials);
                if (material == null) { Reject(armor, name, why! + (heavy ? " (heavy)" : " (light)")); continue; }
                if (excludedMaterials.Contains(material.Name)) { Reject(armor, name, "excluded material " + material.Name); continue; }
                reason = NameDisagrees(name!, material.Name);
                if (reason != null) { Reject(armor, name, reason); continue; }

                string weight = heavy ? "Heavy" : "Light";
                string folderName = slots[0] switch
                {
                    "ArmorShield" => "ArmorShields" + material.Tier,
                    "ArmorBoots" => "ArmorBoots" + weight + material.Tier,
                    "ArmorGauntlets" => "ArmorGauntlets" + weight + material.Tier,
                    "ArmorHelmet" => "ArmorHelmets" + weight + material.Tier,
                    _ => "ArmorsCuirass" + weight + material.Tier,
                };
                reason = FolderCheck(folderName, out var folder);
                if (reason != null) { Reject(armor, name, reason); continue; }

                if (!distribution.IsDistributed(armor.FormKey)) { Reject(armor, name, "not distributed"); continue; }
                reason = VariantsUndistributed(folder!, armor);
                if (reason != null) { Reject(armor, name, reason); continue; }
                Add(folder!, armor, name, material.Name + " " + weight, true);
            }

            var report = new StringBuilder();
            report.AppendLine("Enchanted Variants Generator - discovered items, rewritten on every run.");
            report.AppendLine("Items no input file names, found by keywords and distribution. To keep one out, add its EditorID to");
            report.AppendLine("\"Discover items: excluded items\" in the patcher's settings; an input file naming an item always takes precedence.");
            report.AppendLine();
            report.AppendLine("== Added (" + addedCount + ")");
            foreach (var (folder, items) in added)
            {
                report.AppendLine(folder + ":");
                foreach (var item in items.OrderBy(i => i, StringComparer.OrdinalIgnoreCase)) report.AppendLine("  " + item);
            }
            report.AppendLine();
            report.AppendLine("== Left out (" + rejected.Values.Sum(l => l.Count) + "), by plugin");
            foreach (var (plugin, items) in rejected)
            {
                report.AppendLine(plugin + ":");
                foreach (var item in items.OrderBy(i => i, StringComparer.OrdinalIgnoreCase)) report.AppendLine("  " + item);
            }
            File.WriteAllText(reportPath, report.ToString());
            Console.WriteLine("Discover items: added " + addedCount + " items to " + added.Count + " input folders, left out "
                + rejected.Values.Sum(l => l.Count) + "; details in " + reportPath);
        }
    }

    // Which items are handed out as loot: in a leveled list in use, or added by SPID (Data\*_DISTR.ini) or SkyPatcher. A
    // leveled list is in use when the base game or Creation Club defines it (the game's own lists are wired into the world),
    // or an NPC, container, outfit, SPID, SkyPatcher or another list in use names it.
    public class Distribution
    {
        private readonly HashSet<FormKey> _distributed = new();

        public bool IsDistributed(FormKey item) => _distributed.Contains(item);

        private static readonly string[] BaseGame = { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
        private static readonly Regex CreationClub = new(@"^cc[a-z]+sse\d+", RegexOptions.IgnoreCase);
        private static readonly string[] OwnFolders = { "EnchantedVariantsGenerator", "EnchantedVariantsGenerater", "zz Enchanted Variants Generator" };
        private static readonly HashSet<string> SkyPatcherAddKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "addToLLs", "addOnceToLLs", "addToContainers", "addOnceToContainers", "objectsToAdd", "addOnceToInventory",
            "deathItem", "outfitDefault", "outfitSleep", "formsToAdd", "objectsToReplace",
        };

        public static Distribution Build(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, string dataPath)
        {
            var result = new Distribution();
            var lists = new Dictionary<FormKey, ILeveledItemGetter>();
            foreach (var list in state.LoadOrder.PriorityOrder.LeveledItem().WinningOverrides()) lists[list.FormKey] = list;
            var outfits = new Dictionary<FormKey, IOutfitGetter>();
            foreach (var outfit in state.LoadOrder.PriorityOrder.Outfit().WinningOverrides()) outfits[outfit.FormKey] = outfit;

            var live = new HashSet<FormKey>();
            var pending = new Queue<ILeveledItemGetter>();
            // direct: an item named here counts as distributed itself. NPCs, containers and outfits only make the leveled
            // lists they hold count: an item they name directly is usually a placed unique, and test chests (e.g. "QA"
            // containers) name items too.
            void Mark(FormKey fk, bool direct)
            {
                if (fk.IsNull) return;
                if (lists.TryGetValue(fk, out var list))
                {
                    if (live.Add(fk)) pending.Enqueue(list);
                }
                else if (outfits.TryGetValue(fk, out var outfit))
                {
                    foreach (var item in outfit.Items ?? Array.Empty<IFormLinkGetter<IOutfitTargetGetter>>()) Mark(item.FormKey, direct);
                }
                else if (direct)
                {
                    result._distributed.Add(fk);
                }
            }

            foreach (var list in lists.Values)
            {
                string plugin = list.FormKey.ModKey.FileName;
                if (BaseGame.Contains(plugin, StringComparer.OrdinalIgnoreCase) || CreationClub.IsMatch(plugin)) Mark(list.FormKey, true);
            }
            foreach (var npc in state.LoadOrder.PriorityOrder.Npc().WinningOverrides())
            {
                foreach (var entry in npc.Items ?? Array.Empty<IContainerEntryGetter>()) Mark(entry.Item.Item.FormKey, false);
                Mark(npc.DeathItem.FormKey, false);
                Mark(npc.DefaultOutfit.FormKey, false);
                Mark(npc.SleepingOutfit.FormKey, false);
            }
            foreach (var container in state.LoadOrder.PriorityOrder.Container().WinningOverrides())
            {
                foreach (var entry in container.Items ?? Array.Empty<IContainerEntryGetter>()) Mark(entry.Item.Item.FormKey, false);
            }
            foreach (var outfit in outfits.Values) Mark(outfit.FormKey, false);

            // Runtime distribution
            FormKey? ByEditorID(string id)
            {
                if (linkCache.TryResolve<ILeveledItemGetter>(id, out var l)) return l.FormKey;
                if (linkCache.TryResolve<IWeaponGetter>(id, out var w)) return w.FormKey;
                if (linkCache.TryResolve<IArmorGetter>(id, out var a)) return a.FormKey;
                if (linkCache.TryResolve<IOutfitGetter>(id, out var o)) return o.FormKey;
                return null;
            }
            bool Exists(FormKey fk) => linkCache.TryResolve<ISkyrimMajorRecordGetter>(fk, out _);
            int runtime = 0;
            void MarkToken(string token)
            {
                token = token.Trim();
                if (token.Length == 0 || token.Equals("null", StringComparison.OrdinalIgnoreCase)) return;
                FormKey? fk;
                int tilde = token.IndexOf('~');
                if (tilde > 0)
                {
                    // SPID: 0x123~Plugin.esp (the 0x is optional)
                    string id = token.Substring(0, tilde).Trim();
                    if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) id = id.Substring(2);
                    fk = ItemNames.ResolveForm(token.Substring(tilde + 1).Trim() + "|" + id, Exists, ByEditorID);
                }
                else
                {
                    fk = ItemNames.ResolveForm(token, Exists, ByEditorID);
                }
                if (fk != null) { Mark(fk.Value, true); runtime++; }
            }

            // SPID: Item / Outfit / SleepOutfit / Form lines in Data\*_DISTR.ini (not subfolders)
            foreach (var file in Directory.EnumerateFiles(state.DataFolderPath.Path, "*.ini", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileNameWithoutExtension(f).Contains("_DISTR", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var raw in File.ReadLines(file))
                {
                    string line = raw.Split(';')[0];
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string type = line.Substring(0, eq).Trim();
                    if (!(type.Equals("Item", StringComparison.OrdinalIgnoreCase) || type.Equals("Outfit", StringComparison.OrdinalIgnoreCase)
                        || type.Equals("SleepOutfit", StringComparison.OrdinalIgnoreCase) || type.Equals("Form", StringComparison.OrdinalIgnoreCase))) continue;
                    string forms = line.Substring(eq + 1).Split('|')[0];
                    // SPID turns " - " into "~" when the value has no "~"
                    if (!forms.Contains('~')) forms = forms.Replace(" - ", "~");
                    foreach (var token in forms.Split(',')) MarkToken(token);
                }
            }

            // SkyPatcher: keys that add items, lists or outfits anywhere under SKSE\Plugins\SkyPatcher, with its plugin gates
            string skyPatcher = Path.Combine(state.DataFolderPath.Path, "SKSE", "Plugins", "SkyPatcher");
            var active = new HashSet<string>(state.LoadOrder.Where(l => l.Value.Enabled).Select(l => l.Key.FileName.String), StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(skyPatcher))
            {
                foreach (var file in Directory.EnumerateFiles(skyPatcher, "*.ini", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(skyPatcher, file).Split(Path.DirectorySeparatorChar);
                    if (relative.Any(part => OwnFolders.Contains(part, StringComparer.OrdinalIgnoreCase))) continue;
                    string gate = Path.GetFileNameWithoutExtension(file);
                    if ((gate.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || gate.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || gate.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                        && !active.Contains(gate)) continue;

                    foreach (var raw in File.ReadLines(file))
                    {
                        var parts = ItemNames.SplitSegments(raw).Select(s => s.Split('=', 2)).Where(p => p.Length == 2)
                            .Select(p => (Key: p[0].Trim(), Value: p[1].Trim())).ToList();
                        if (parts.Count == 0) continue;
                        var hasAll = parts.FirstOrDefault(p => p.Key.Equals("hasPlugins", StringComparison.OrdinalIgnoreCase));
                        if (hasAll.Key != null && !hasAll.Value.Split(',').All(p => active.Contains(p.Trim()))) continue;
                        var hasAny = parts.FirstOrDefault(p => p.Key.Equals("hasPluginsOr", StringComparison.OrdinalIgnoreCase));
                        if (hasAny.Key != null && !hasAny.Value.Split(',').Any(p => active.Contains(p.Trim()))) continue;

                        foreach (var (key, value) in parts.Where(p => SkyPatcherAddKeys.Contains(p.Key)))
                        {
                            foreach (var entry in value.Split(','))
                            {
                                // form~level~count, form~count, form=count; objectsToReplace=old~new adds the new one
                                string form = key.Equals("objectsToReplace", StringComparison.OrdinalIgnoreCase)
                                    ? entry.Split('~').Last()
                                    : entry.Split('~')[0].Split('=')[0];
                                MarkToken(form);
                            }
                        }
                    }
                }
            }

            while (pending.Count > 0)
            {
                var list = pending.Dequeue();
                foreach (var entry in list.Entries ?? Array.Empty<ILeveledItemEntryGetter>())
                {
                    if (entry.Data != null) Mark(entry.Data.Reference.FormKey, true);
                }
            }
            Console.WriteLine("Distribution: " + result._distributed.Count + " items reach the world through " + live.Count
                + " leveled lists in use, NPCs, containers, outfits and " + runtime + " SPID/SkyPatcher entries");
            return result;
        }
    }
}
