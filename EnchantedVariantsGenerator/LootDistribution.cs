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
    // An enchanted variant to hand out: one this run generated, or an existing one standing in for a skipped duplicate
    public record GeneratedVariant(FormKey FormKey, IMajorRecordGetter BaseItem, EnchantmentInfo Enchantment, bool Existing);

    // Hands the enchanted variants out as loot beside their vanilla twins. For each vanilla enchanted sublist a variant
    // belongs in (SublistEnchArmorIronCuirass01, SublistEnchIronSwordFire, ...) the patcher makes <that list>_EVG, holding
    // the variants at the level the vanilla list gives the same tier, and a SkyPatcher INI adds it to the vanilla list once.
    // Whatever draws from the vanilla lists (the game's own loot, Open World Loot's special lists) then hands them out too.
    //   Armor     the base's material, weight class and slot: its tier list for the enchantment's tier, or the nearest
    //             tier the material has. A base with vanilla variants of its own goes where those are.
    //   Weapons   the base's material and type: the list holding an item with this enchantment, else the one for the same
    //             effect (Fire, Frost, ...) as the lists that hold it for other materials.
    //   Armor in a weight class vanilla never enchants for its material (light Daedric, heavy Glass): that material's lists
    //             in the other class are the reference. The new list goes where the reference sits, mirrored into the other
    //             class: added to the counterpart of each heavy/light list holding it (...HeavyBoots... -> ...LightBoots...),
    //             with any list in between (the Daedric wrappers) copied for the new class.
    // The lists are read as the game holds them after SkyPatcher (RuntimeLists), and the INI goes in a folder that sorts
    // after other mods', so its additions land on the finished lists.
    public class LootDistribution
    {
        public const string ListSuffix = "_EVG";
        private const string IniFolder = "zz Enchanted Variants Generator";
        private const string ReportFile = "distributed items.txt";
        private const int MaxEntriesPerList = 255;
        private const int MaxWrapperDepth = 3;

        private static readonly (string Keyword, string Type)[] WeaponTypes =
        {
            ("WeapTypeSword", "Sword"), ("WeapTypeWarAxe", "WarAxe"), ("WeapTypeMace", "Mace"), ("WeapTypeDagger", "Dagger"),
            ("WeapTypeGreatsword", "Greatsword"), ("WeapTypeBattleaxe", "Battleaxe"), ("WeapTypeWarhammer", "Warhammer"),
            ("WeapTypeBow", "Bow"),
        };
        private static readonly string[] ArmorSlots = { "Boots", "Cuirass", "Gauntlets", "Helmet", "Shield" };
        private static readonly ItemDiscovery.Material[] ArmorMaterials = ItemDiscovery.HeavyArmorMaterials.Concat(ItemDiscovery.LightArmorMaterials).ToArray();

        // The effect a weapon list is for: what follows the weapon type in its EditorID (SublistEnchIronSwordFire -> Fire)
        private static readonly Regex ElementName = new(@"(?:Sword|WarAxe|Mace|Dagger|Greatsword|Battleaxe|Warhammer|Crossbow|Bow)([A-Za-z]+)$", RegexOptions.IgnoreCase);
        // The per-item lists older versions of this patcher made (SublistEnchWeapon_IronSword_01): never a target
        private static readonly Regex PerItemList = new(@"SublistEnch(Weapon|Armor)_", RegexOptions.IgnoreCase);
        private static readonly Regex TrailingTier = new(@"(\d{1,2})[A-Za-z]?$");
        // An enchantment's name without its tier: SUM_Weapon_Alt_Ench_Launch_01 -> SUM_Weapon_Alt_Ench_Launch
        private static readonly Regex TierlessName = new(@"^(.*?)_?\d{1,2}[A-Za-z]?$");
        private static readonly string[] BaseGame = { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
        private static readonly Regex CreationClub = new(@"^cc[a-z]+sse\d+", RegexOptions.IgnoreCase);
        private static bool IsBaseGame(ModKey mod) => BaseGame.Contains(mod.FileName.String, StringComparer.OrdinalIgnoreCase) || CreationClub.IsMatch(mod.FileName);

        // A vanilla enchanted sublist: the enchanted items it holds directly
        private class Leaf
        {
            public FormKey FormKey;
            public string EditorID = "";
            public bool Armor;
            public string? Class;
            public string? Element;
            public int Tier;
            public readonly SortedDictionary<int, short> TierLevels = new();
            public readonly HashSet<FormKey> Enchantments = new();
            // Set for a stand-in effect list (EffectList): FormKey is then the list holding it, entered at this level
            public string? PlacedIn;
            public short PlaceLevel;

            // The level the list gives this tier, or the nearest tier it has
            public short LevelFor(int tier) => TierLevels.Count == 0 ? (short)1 : TierLevels.MinBy(t => (Math.Abs(t.Key - tier), t.Key)).Value;
        }

        // A list this run makes: the variants for one vanilla list (or, for the other weight class, one reference list)
        private class NewList
        {
            public string EditorID = "";
            public Leaf Target = null!;
            public string? Weight;     // set for the other weight class: the class the variants are in
            public string? Material;
            public readonly Dictionary<FormKey, short> Items = new();
        }

        private readonly IPatcherState<ISkyrimMod, ISkyrimModGetter> _state;
        private readonly ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> _linkCache;
        private readonly RuntimeLists _lists;
        private readonly List<Leaf> _leaves = new();
        private readonly Dictionary<string, List<Leaf>> _byClass = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<FormKey, List<Leaf>> _byBase = new();
        private readonly Dictionary<FormKey, List<Leaf>> _byEnchantment = new();
        private readonly Dictionary<string, List<Leaf>> _byEnchantmentName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<FormKey, string> _keywordIDs = new();
        private readonly Dictionary<FormKey, string?> _classes = new();

        private LootDistribution(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, RuntimeLists lists)
        {
            _state = state;
            _linkCache = linkCache;
            _lists = lists;
        }

        public static LootDistribution Build(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, RuntimeLists lists)
        {
            var result = new LootDistribution(state, linkCache, lists);
            result.FindLeaves();
            Console.WriteLine("Distribution: " + result._leaves.Count(l => l.Armor) + " vanilla enchanted armor lists and " + result._leaves.Count(l => !l.Armor)
                + " weapon lists, read after " + lists.IniLines + " SkyPatcher leveled-list lines");
            return result;
        }

        // Vanilla enchanted sublists: named SublistEnch..., holding enchanted items of one material and type or slot, and
        // the game's own or placed straight into one of the game's lists (Thaumaturgy's MAG_SublistEnchIronSwordDamageArmor
        // is). A weapon list also names its effect. That leaves out lists a mod keeps for its own items, such as Heavy
        // Armory's ShSublistEnch06Daedric1H (every one-handed Daedric weapon at one tier).
        private void FindLeaves()
        {
            foreach (var (fk, entries) in _lists.Entries)
            {
                string? editorID = _lists.OriginalEditorID(fk);
                if (editorID == null || editorID.IndexOf("SublistEnch", StringComparison.OrdinalIgnoreCase) < 0
                    || editorID.Contains(ListSuffix, StringComparison.OrdinalIgnoreCase) || PerItemList.IsMatch(editorID)) continue;
                if (!IsBaseGame(fk.ModKey) && !_lists.Parents(fk).Any(p => IsBaseGame(p.Parent.ModKey))) continue;

                var leaf = new Leaf { FormKey = fk, EditorID = editorID };
                var classes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var tiers = new Dictionary<int, int>();
                var bases = new HashSet<FormKey>();
                int armor = 0, weapon = 0;
                foreach (var entry in entries)
                {
                    IMajorRecordGetter? baseItem;
                    FormKey enchantment;
                    string? itemEditorID;
                    bool isWeapon;
                    if (_linkCache.TryResolve<IWeaponGetter>(entry.Reference, out var w) && !w.Template.IsNull && !w.ObjectEffect.IsNull)
                    {
                        baseItem = _linkCache.TryResolve<IWeaponGetter>(w.Template.FormKey, out var b) ? b : null;
                        enchantment = w.ObjectEffect.FormKey;
                        itemEditorID = w.EditorID;
                        isWeapon = true;
                        weapon++;
                    }
                    else if (_linkCache.TryResolve<IArmorGetter>(entry.Reference, out var a) && !a.TemplateArmor.IsNull && !a.ObjectEffect.IsNull)
                    {
                        baseItem = _linkCache.TryResolve<IArmorGetter>(a.TemplateArmor.FormKey, out var b) ? b : null;
                        enchantment = a.ObjectEffect.FormKey;
                        itemEditorID = a.EditorID;
                        isWeapon = false;
                        armor++;
                    }
                    else continue;

                    // The defining plugin's name carries the tier: Thaumaturgy renames and renumbers vanilla items
                    // (SublistEnchArmorDaedricBoots05 holds its "...04A")
                    itemEditorID = OriginalItemEditorID(entry.Reference, isWeapon) ?? itemEditorID;
                    leaf.Enchantments.Add(enchantment);
                    if (baseItem != null)
                    {
                        bases.Add(baseItem.FormKey);
                        if (ClassOf(baseItem) is string c) classes[c] = classes.GetValueOrDefault(c) + 1;
                    }
                    if (TierOf(itemEditorID) is int tier)
                    {
                        tiers[tier] = tiers.GetValueOrDefault(tier) + 1;
                        leaf.TierLevels[tier] = leaf.TierLevels.TryGetValue(tier, out var level) ? Math.Min(level, entry.Level) : entry.Level;
                    }
                }
                if (armor + weapon == 0) continue;

                leaf.Armor = armor > weapon;
                leaf.Class = classes.Count > 0 ? classes.MaxBy(c => c.Value).Key : null;
                // One material and type: two in three of the items whose base has a class share the list's (a stray
                // item a mod put in the wrong list is tolerated; a mod's list for many types is not)
                if (leaf.Class != null && classes[leaf.Class] * 3 < classes.Values.Sum() * 2) continue;
                // An armor list's own name gives its tier (SublistEnchArmorIronCuirass01); else the tier most of its items have
                int? itemsTier = tiers.Count > 0 ? tiers.MaxBy(t => (t.Value, -t.Key)).Key : null;
                leaf.Tier = (leaf.Armor ? TierOf(editorID) ?? itemsTier : itemsTier ?? TierOf(editorID)) ?? 0;
                if (!leaf.Armor)
                {
                    if (ElementName.Match(editorID) is not { Success: true } m) continue;
                    leaf.Element = m.Groups[1].Value;
                }

                _leaves.Add(leaf);
                if (leaf.Class != null) Add(_byClass, leaf.Class, leaf);
                foreach (var b in bases) Add(_byBase, b, leaf);
                foreach (var e in leaf.Enchantments)
                {
                    Add(_byEnchantment, e, leaf);
                    if (EnchantmentName(e) is string name) Add(_byEnchantmentName, name, leaf);
                }
            }
        }

        // The item's EditorID in the plugin that defines it
        private string? OriginalItemEditorID(FormKey item, bool weapon)
        {
            if (!_state.LoadOrder.ContainsKey(item.ModKey) || _state.LoadOrder[item.ModKey].Mod is not { } mod) return null;
            return weapon
                ? mod.Weapons.TryGetValue(item, out var w) ? w.EditorID : null
                : mod.Armors.TryGetValue(item, out var a) ? a.EditorID : null;
        }

        private readonly Dictionary<FormKey, string?> _enchantmentNames = new();

        // The enchantment's EditorID without its tier
        private string? EnchantmentName(FormKey enchantment)
        {
            if (_enchantmentNames.TryGetValue(enchantment, out var name)) return name;
            string? id = _linkCache.TryResolve<IObjectEffectGetter>(enchantment, out var e) ? e.EditorID : null;
            return _enchantmentNames[enchantment] = id == null ? null : TierlessName.Match(id) is { Success: true } m ? m.Groups[1].Value : id;
        }

        private static void Add<TKey>(Dictionary<TKey, List<Leaf>> index, TKey key, Leaf leaf) where TKey : notnull
        {
            if (!index.TryGetValue(key, out var list)) index[key] = list = new();
            list.Add(leaf);
        }

        private static int? TierOf(string? editorID)
        {
            var m = TrailingTier.Match(editorID ?? "");
            return m.Success && int.TryParse(m.Groups[1].Value, out int t) && t > 0 ? t : null;
        }

        private string KeywordID(FormKey fk)
        {
            if (_keywordIDs.TryGetValue(fk, out var id)) return id;
            id = _linkCache.TryResolve<IKeywordGetter>(fk, out var kw) ? kw.EditorID ?? "" : "";
            return _keywordIDs[fk] = id;
        }

        // "Iron Sword", "Dwarven Crossbow", "Daedric Heavy Boots"; null when the keywords don't say
        private string? ClassOf(IMajorRecordGetter item)
        {
            if (_classes.TryGetValue(item.FormKey, out var cached)) return cached;
            string? result = null;
            if (item is IWeaponGetter weapon)
            {
                var keywords = (weapon.Keywords ?? Array.Empty<IFormLinkGetter<IKeywordGetter>>()).Select(k => KeywordID(k.FormKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string? material = ItemDiscovery.MaterialNameOf(keywords, weapon.Name?.String, ItemDiscovery.WeaponMaterials);
                string? type = weapon.Data?.AnimationType == WeaponAnimationType.Crossbow ? "Crossbow" : WeaponTypes.FirstOrDefault(t => keywords.Contains(t.Keyword)).Type;
                if (material != null && type != null) result = material + " " + type;
            }
            else if (item is IArmorGetter armor)
            {
                var keywords = (armor.Keywords ?? Array.Empty<IFormLinkGetter<IKeywordGetter>>()).Select(k => KeywordID(k.FormKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool heavy = keywords.Contains("ArmorHeavy") || (!keywords.Contains("ArmorLight") && armor.BodyTemplate?.ArmorType == ArmorType.HeavyArmor);
                bool light = keywords.Contains("ArmorLight") || (!keywords.Contains("ArmorHeavy") && armor.BodyTemplate?.ArmorType == ArmorType.LightArmor);
                string? slot = ArmorSlots.FirstOrDefault(s => keywords.Contains("Armor" + s));
                string? material = ItemDiscovery.MaterialNameOf(keywords, armor.Name?.String, ArmorMaterials);
                if (heavy != light && slot != null && material != null) result = material + (heavy ? " Heavy " : " Light ") + slot;
            }
            return _classes[item.FormKey] = result;
        }

        // The vanilla lists for the base's own kind: where its own vanilla variants are, else by material and type or slot.
        // A crossbow with no crossbow lists for its material uses the bow lists.
        private List<Leaf> Family(IMajorRecordGetter baseItem, string? itemClass)
        {
            bool armor = baseItem is IArmorGetter;
            if (_byBase.TryGetValue(baseItem.FormKey, out var own) && own.Where(l => l.Armor == armor).ToList() is { Count: > 0 } ownFamily) return ownFamily;
            if (itemClass != null && _byClass.TryGetValue(itemClass, out var byClass) && byClass.Where(l => l.Armor == armor).ToList() is { Count: > 0 } classFamily) return classFamily;
            if (!armor && itemClass != null && itemClass.EndsWith(" Crossbow") && _byClass.TryGetValue(itemClass[..^"Crossbow".Length] + "Bow", out var bows)) return bows.Where(l => !l.Armor).ToList();
            return new();
        }

        // For armor with no lists in its own weight class: the same material's lists in the other class, with the classes
        private (List<Leaf> Family, string Material, string Weight, string OtherWeight)? OtherClassFamily(string? itemClass)
        {
            var parts = itemClass?.Split(' ');
            if (parts == null || parts.Length != 3) return null;
            string other = parts[1] == "Heavy" ? "Light" : "Heavy";
            if (!_byClass.TryGetValue(parts[0] + " " + other + " " + parts[2], out var family)) return null;
            var armorFamily = family.Where(l => l.Armor).ToList();
            return armorFamily.Count > 0 ? (armorFamily, parts[0], parts[1], other) : null;
        }

        private static Leaf ByTier(List<Leaf> family, int tier) =>
            family.OrderBy(l => Math.Abs(l.Tier - tier)).ThenBy(l => l.Tier).ThenBy(l => l.EditorID, StringComparer.OrdinalIgnoreCase).First();

        // For item discovery: null when the item's variants would reach loot, else why not
        public string? Problem(IMajorRecordGetter item)
        {
            string? itemClass = ClassOf(item);
            if (Family(item, itemClass).Count > 0) return null;
            if (item is IArmorGetter && OtherClassFamily(itemClass) != null) return null;
            if (itemClass == null) return "no vanilla enchanted list holds its variants, and its keywords give no material and " + (item is IArmorGetter ? "weight class and slot" : "weapon type");
            return "no vanilla enchanted list for " + itemClass + (item is IArmorGetter ? " in either weight class" : "");
        }

        // Tier of a generated variant's enchantment: the digits of its first non-NPC leveled-list suffix in the input file
        // ("_04" -> 4), else the trailing digits of its EditorID
        private static int? EnchantmentTier(EnchantmentInfo info)
        {
            var suffix = info.LeveledLists?.Where(l => !l.LeveledListSuffix.Contains("NPC")).Select(l => l.LeveledListSuffix).FirstOrDefault();
            return TierOf(suffix) ?? TierOf(info.EditorID);
        }

        // distribution: what already reaches the world, so an existing variant standing in for a duplicate is filed only
        // when nothing hands it out yet
        public void Run(Config config, string dataPath, List<GeneratedVariant> variants, Distribution distribution)
        {
            string iniFolder = Path.Combine(_state.DataFolderPath.Path, "SKSE", "Plugins", "SkyPatcher", "leveledList", IniFolder);
            string iniPath = Path.Combine(iniFolder, _state.PatchMod.ModKey.FileName + ".ini");
            string reportPath = Path.Combine(dataPath, ReportFile);
            // Earlier versions filed weapons into Open World Loot's lists, through INIs that now point at lists OWL no longer has
            foreach (var legacy in new[] { "EnchantedVariantsGenerator", "EnchantedVariantsGenerater" })
            {
                Program.DeleteLegacyOutput(Path.Combine(_state.DataFolderPath.Path, "SKSE", "Plugins", "SkyPatcher", "leveledList", legacy, "Open World Loot.esp.ini"));
            }
            if (!config.DistributeVariants)
            {
                DeleteIfPresent(iniPath);
                DeleteIfPresent(reportPath);
                return;
            }

            var newLists = new Dictionary<string, NewList>(StringComparer.OrdinalIgnoreCase);
            var skipped = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            int placed = 0, alreadyListed = 0;
            // Counted by base item, or by enchantment where the enchantment is the problem
            void Skip(string reason, GeneratedVariant v, bool byEnchantment = false)
            {
                if (!skipped.TryGetValue(reason, out var labels)) skipped[reason] = labels = new(StringComparer.OrdinalIgnoreCase);
                string label = byEnchantment
                    ? (v.Enchantment.Enchantment.EditorID ?? v.Enchantment.EditorID) + " (" + v.Enchantment.Enchantment.FormKey.ModKey.FileName + ")"
                    : v.BaseItem.EditorID ?? v.BaseItem.FormKey.ToString();
                labels[label] = labels.GetValueOrDefault(label) + 1;
            }

            foreach (var v in variants)
            {
                // An existing variant that already reaches the world needs no second place
                if (v.Existing && distribution.IsDistributed(v.FormKey)) { alreadyListed++; continue; }

                if (EnchantmentTier(v.Enchantment) is not int tier) { Skip("enchantment tier unknown", v); continue; }
                string? itemClass = ClassOf(v.BaseItem);
                var family = Family(v.BaseItem, itemClass);
                bool armor = v.BaseItem is IArmorGetter;

                NewList? target = null;
                if (family.Count > 0)
                {
                    Leaf? leaf = armor ? ByTier(family, tier) : WeaponList(family, v.Enchantment.Enchantment);
                    if (leaf == null)
                    {
                        Skip(_byEnchantment.ContainsKey(v.Enchantment.Enchantment.FormKey)
                            ? "weapon enchantment whose effect has no list for the base's material and type"
                            : "weapon enchantment no vanilla enchanted list holds", v, true);
                        continue;
                    }
                    string id = leaf.EditorID + ListSuffix;
                    if (!newLists.TryGetValue(id, out target)) newLists[id] = target = new NewList { EditorID = id, Target = leaf };
                }
                else if (armor && OtherClassFamily(itemClass) is { } other)
                {
                    var reference = ByTier(other.Family, tier);
                    string id = OtherClassName(reference.EditorID, other.Material, other.OtherWeight, other.Weight) + ListSuffix;
                    if (!newLists.TryGetValue(id, out target)) newLists[id] = target = new NewList { EditorID = id, Target = reference, Weight = other.Weight, Material = other.Material };
                }
                else
                {
                    Skip(itemClass == null ? "keywords give no material and " + (armor ? "weight class and slot" : "weapon type") : "no vanilla enchanted list for " + itemClass + (armor ? " in either weight class" : ""), v);
                    continue;
                }
                short level = target.Target.LevelFor(tier);
                target.Items[v.FormKey] = target.Items.TryGetValue(v.FormKey, out var l) ? Math.Min(l, level) : level;
                placed++;
            }

            // Records and INI lines
            var ini = new StringBuilder();
            ini.AppendLine("; Generated by Enchanted Variants Generator on every Synthesis run. Do not edit: the next run replaces this file.");
            ini.AppendLine("; Adds the enchanted variants in " + _state.PatchMod.ModKey.FileName + " to the vanilla enchanted lists, each through a list of its own (" + ListSuffix + ").");
            var report = new StringBuilder();
            var otherClassReport = new List<string>();
            var iniAdds = new HashSet<(FormKey Target, FormKey Added, short Level)>();
            void IniAdd(FormKey target, FormKey added, short level, string comment)
            {
                if (!iniAdds.Add((target, added, level))) return;
                ini.AppendLine("; " + comment);
                ini.AppendLine("filterByLLs=" + target.ModKey.FileName + "|" + target.ID.ToString("X6") + ":addOnceToLLs=" + _state.PatchMod.ModKey.FileName + "|" + added.ID.ToString("X6") + "~" + level + "~1");
            }

            int listCount = 0;
            var otherClass = new Dictionary<string, Dictionary<FormKey, (FormKey New, string Material)>>();
            foreach (var list in newLists.Values.OrderBy(n => n.EditorID, StringComparer.OrdinalIgnoreCase))
            {
                var (record, made) = WriteList(config, list.EditorID, list.Items.Select(i => (i.Key, i.Value)).ToList(), _lists.Record(list.Target.FormKey).Flags);
                listCount += made;
                short minLevel = list.Items.Values.Min();
                if (list.Weight == null)
                {
                    // A stand-in effect list goes beside the material's other effect lists, in the list holding them
                    bool standIn = list.Target.PlacedIn != null;
                    string into = list.Target.PlacedIn ?? list.Target.EditorID;
                    IniAdd(list.Target.FormKey, record.FormKey, standIn ? Math.Max(list.Target.PlaceLevel, minLevel) : minLevel, into + " <- " + list.EditorID + " (" + list.Items.Count + ")");
                    report.AppendLine("  " + list.EditorID + " (" + list.Items.Count + ") -> " + into + (standIn ? " (no " + list.Target.EditorID + " in the load order: added beside the other effect lists)" : ""));
                }
                else
                {
                    if (!otherClass.TryGetValue(list.Weight, out var mapping)) otherClass[list.Weight] = mapping = new();
                    mapping[list.Target.FormKey] = (record.FormKey, list.Material!);
                    otherClassReport.Add("  " + list.EditorID + " (" + list.Items.Count + "), modelled on " + list.Target.EditorID);
                }
            }

            // The other weight class: from each reference list up to the heavy/light lists holding it
            foreach (var (weight, mapping) in otherClass)
            {
                string from = weight == "Heavy" ? "Light" : "Heavy";
                var frontier = mapping.Keys.ToList();
                for (int depth = 0; depth <= MaxWrapperDepth && frontier.Count > 0; depth++)
                {
                    var copy = new List<FormKey>();
                    foreach (var child in frontier)
                    {
                        foreach (var (parent, level) in _lists.Parents(child).Distinct())
                        {
                            string parentID = _lists.OriginalEditorID(parent) ?? parent.ToString();
                            if (parentID.Contains(from))
                            {
                                if (Counterpart(parent, from, weight) is FormKey counterpart)
                                {
                                    IniAdd(counterpart, mapping[child].New, level, (_lists.OriginalEditorID(counterpart) ?? counterpart.ToString()) + " <- " + weight.ToLowerInvariant() + " counterpart of " + parentID + "'s entry");
                                    otherClassReport.Add("    in " + (_lists.OriginalEditorID(counterpart) ?? counterpart.ToString()) + " at level " + level + ", as " + (_lists.OriginalEditorID(child) ?? child.ToString()) + " is in " + parentID);
                                }
                                else otherClassReport.Add("    NOT placed in a " + weight.ToLowerInvariant() + " counterpart of " + parentID + ": no list named " + parentID.Replace(from, weight));
                            }
                            else if (!mapping.ContainsKey(parent) && !copy.Contains(parent) && depth < MaxWrapperDepth)
                            {
                                copy.Add(parent);
                            }
                        }
                    }
                    // Lists in between (no weight class in the name): copied for the new class, the reference lists in them
                    // swapped for the new ones and heavy/light lists for their counterparts
                    foreach (var parent in copy)
                    {
                        string parentID = _lists.OriginalEditorID(parent) ?? parent.ToString();
                        string material = _lists.Entries[parent].Select(e => mapping.TryGetValue(e.Reference, out var m) ? m.Material : null).OfType<string>().First();
                        var entries = _lists.Entries[parent].Select(e =>
                        {
                            if (mapping.TryGetValue(e.Reference, out var m)) return (m.New, e.Level, e.Count);
                            if (_lists.IsList(e.Reference) && (_lists.OriginalEditorID(e.Reference) ?? "").Contains(from) && Counterpart(e.Reference, from, weight) is FormKey c) return (c, e.Level, e.Count);
                            return (e.Reference, e.Level, e.Count);
                        }).ToList();
                        var source = _lists.Record(parent);
                        string id = OtherClassName(parentID, material, from, weight) + ListSuffix;
                        var record = NewRecord(config, id, source.Flags);
                        record.ChanceNone = source.ChanceNone;
                        foreach (var (reference, level, count) in entries)
                        {
                            var entry = Program.CreateLeveledItemEntry(level, reference);
                            entry.Data!.Count = count;
                            record.Entries!.Add(entry);
                        }
                        _state.PatchMod.LeveledItems.Set(record);
                        listCount++;
                        mapping[parent] = (record.FormKey, material);
                        otherClassReport.Add("  " + id + ", a copy of " + parentID);
                    }
                    frontier = copy;
                }
            }

            Directory.CreateDirectory(iniFolder);
            File.WriteAllText(iniPath, ini.ToString());

            var text = new StringBuilder();
            text.AppendLine("Enchanted Variants Generator - distributed items, rewritten on every run.");
            text.AppendLine("Each list below holds enchanted variants and is added once to the vanilla list after the arrow, by");
            text.AppendLine(iniPath);
            text.AppendLine();
            text.AppendLine("== Beside their vanilla twins (" + newLists.Values.Count(n => n.Weight == null) + " lists)");
            text.Append(report);
            text.AppendLine();
            text.AppendLine("== Other weight class (" + newLists.Values.Count(n => n.Weight != null) + " lists)");
            foreach (var line in otherClassReport) text.AppendLine(line);
            text.AppendLine();
            text.AppendLine("== Left out (" + skipped.Values.Sum(b => b.Values.Sum()) + " variants), by reason and base item or enchantment");
            foreach (var (reason, bases) in skipped)
            {
                text.AppendLine(reason + ":");
                foreach (var (label, count) in bases) text.AppendLine("  " + label + " (" + count + ")");
            }
            File.WriteAllText(reportPath, text.ToString());

            Console.WriteLine("Distribution: " + placed + " variants in " + listCount + " leveled lists, added to " + iniAdds.Count + " lists by " + iniPath
                + (alreadyListed > 0 ? "; " + alreadyListed + " existing variants that already reach the world left where they are" : ""));
            foreach (var (reason, bases) in skipped) Console.WriteLine("Distribution: left out " + bases.Values.Sum() + " (" + reason + ")");
            Console.WriteLine("Distribution: details in " + reportPath);
        }

        // The material and type's list for this enchantment: one holding an item with it, else the one for the same effect
        // as the lists holding it for other materials (most common first). An enchantment no list holds goes by its other
        // tiers (Summermyst's Launch_01 where only Launch_02 and _03 are on items). When the material and type have no list
        // for that effect at all, a stand-in (see EffectList) for the list vanilla would have.
        private Leaf? WeaponList(List<Leaf> family, IObjectEffectGetter enchantment)
        {
            var own = family.Where(l => l.Enchantments.Contains(enchantment.FormKey)).OrderBy(l => l.EditorID, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (own != null) return own;
            if (!_byEnchantment.TryGetValue(enchantment.FormKey, out var holders)
                && !(EnchantmentName(enchantment.FormKey) is string name && _byEnchantmentName.TryGetValue(name, out holders))) return null;
            var elements = holders.Where(l => !l.Armor && l.Element != null).GroupBy(l => l.Element!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Key).ToList();
            foreach (var element in elements)
            {
                var match = family.Where(l => element.Equals(l.Element, StringComparison.OrdinalIgnoreCase)).OrderBy(l => l.EditorID, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (match != null) return match;
            }
            return elements.Count > 0 ? EffectList(family, elements[0]) : null;
        }

        private readonly Dictionary<string, Leaf?> _effectLists = new(StringComparer.OrdinalIgnoreCase);

        // A stand-in for the effect list a material and type lack (Iron bows have no Clumsy list): named as vanilla would
        // name it (SublistEnchIronBowClumsy), with the tier levels the material's other effect lists share, and placed in
        // the list that holds those (LItemEnchIronBow), at the level they have there. Its FormKey is that holding list.
        private Leaf? EffectList(List<Leaf> family, string element)
        {
            // Named as most of the family's lists are (vanilla misspells a few: DLC2SublistEnchStahlrim..., SublistENch...)
            string Prefix(Leaf l) => l.EditorID.Substring(0, ElementName.Match(l.EditorID).Groups[1].Index);
            var model = family.Where(l => IsBaseGame(l.FormKey.ModKey)).DefaultIfEmpty(family[0]).GroupBy(Prefix, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).First().First();
            string id = Prefix(model) + element;
            if (_effectLists.TryGetValue(id, out var known)) return known;

            // The list holding most of the family's effect lists, and the level it gives them
            var holder = family.SelectMany(l => _lists.Parents(l.FormKey).Select(p => (Leaf: l, p.Parent, p.Level)))
                .GroupBy(p => p.Parent).OrderByDescending(g => g.Select(p => p.Leaf).Distinct().Count()).FirstOrDefault();
            if (holder == null || holder.Select(p => p.Leaf).Distinct().Count() < 2) return _effectLists[id] = null;
            var leaf = new Leaf
            {
                FormKey = holder.Key,
                EditorID = id,
                Armor = false,
                Class = model.Class,
                Element = element,
                Tier = model.Tier,
                PlacedIn = _lists.OriginalEditorID(holder.Key) ?? holder.Key.ToString(),
                PlaceLevel = holder.GroupBy(p => p.Level).OrderByDescending(g => g.Count()).First().Key,
            };
            foreach (var (tier, level) in family.SelectMany(l => l.TierLevels))
            {
                leaf.TierLevels[tier] = leaf.TierLevels.TryGetValue(tier, out var have) ? Math.Min(have, level) : level;
            }
            return _effectLists[id] = leaf;
        }

        // The list named like this one with the other weight class (OWL_Special_LItemEnchArmorHeavyBootsHigh -> ...Light...)
        private FormKey? Counterpart(FormKey list, string from, string to)
        {
            foreach (var id in new[] { _lists.OriginalEditorID(list), _lists.Record(list).EditorID })
            {
                if (id != null && id.Contains(from) && _lists.ListByEditorID(id.Replace(from, to)) is FormKey found) return found;
            }
            return null;
        }

        // A reference list's name for the other weight class: SublistEnchArmorDaedricBoots04 -> SublistEnchArmorDaedricLightBoots04,
        // SublistEnchArmorBoots04Daedric05 -> SublistEnchArmorBoots04DaedricLight05
        private static string OtherClassName(string editorID, string material, string from, string to)
        {
            if (editorID.Contains(from)) return editorID.Replace(from, to);
            int at = editorID.IndexOf(material, StringComparison.OrdinalIgnoreCase);
            return at >= 0 ? editorID.Insert(at + material.Length, to) : editorID + to;
        }

        // The list, holding the items at their levels; past 255 items they are dealt round-robin into equal lists
        // <id>_1, _2, ... (each with every level's share), entered at their lowest level. Returns the top list and the
        // number of lists made.
        private (LeveledItem Record, int Made) WriteList(Config config, string id, List<(FormKey Item, short Level)> items, LeveledItem.Flag flags)
        {
            var sorted = items.OrderBy(i => i.Level).ThenBy(i => i.Item.ToString(), StringComparer.Ordinal).ToList();
            var top = NewRecord(config, id, flags);
            int made = 1;
            if (sorted.Count <= MaxEntriesPerList)
            {
                foreach (var (item, level) in sorted) top.Entries!.Add(Program.CreateLeveledItemEntry(level, item));
            }
            else
            {
                int count = (sorted.Count + MaxEntriesPerList - 1) / MaxEntriesPerList;
                for (int c = 0; c < count; c++)
                {
                    var bucket = sorted.Where((_, i) => i % count == c).ToList();
                    var child = NewRecord(config, id + "_" + (c + 1), flags);
                    foreach (var (item, level) in bucket) child.Entries!.Add(Program.CreateLeveledItemEntry(level, item));
                    _state.PatchMod.LeveledItems.Set(child);
                    made++;
                    top.Entries!.Add(Program.CreateLeveledItemEntry(bucket.Min(b => b.Level), child.FormKey));
                }
            }
            _state.PatchMod.LeveledItems.Set(top);
            return (top, made);
        }

        // A fresh list, reusing the record from an earlier run when "Reuse records from earlier runs" finds one, so FormIDs
        // stay put. The vanilla list's calculation flags; no chance of nothing (the vanilla list already has its own).
        private LeveledItem NewRecord(Config config, string editorID, LeveledItem.Flag flags)
        {
            LeveledItem list = config.CheckExistingGenerated && _linkCache.TryResolve<ILeveledItemGetter>(editorID, out var existing)
                ? existing.DeepCopy()
                : new LeveledItem(_state.PatchMod, editorID);
            list.Flags = flags & (LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer | LeveledItem.Flag.CalculateForEachItemInCount | LeveledItem.Flag.UseAll);
            list.ChanceNone = default;
            list.Entries = new Noggog.ExtendedList<LeveledItemEntry>();
            return list;
        }

        private static void DeleteIfPresent(string path)
        {
            if (!File.Exists(path)) return;
            File.Delete(path);
            Console.WriteLine("Removed " + path);
        }
    }
}
