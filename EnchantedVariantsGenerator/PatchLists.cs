using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace EnchantedVariantsGenerator
{
    // What Open World Loot's patches (e.g. "Open World Loot - Heavy Armory Patch") already distribute. A patch defines its
    // own OWL_... lists, such as OWL_Weapon_Daedric_GreatSword_Ench_HeavyArmory_PoleArm, and fills them with a weapon mod's
    // existing enchanted variants, through its plugin and through SkyPatcher leveledList INIs. Where a base weapon's
    // variants sit tells where the patch author files that weapon (hatchets under daggers, polearms in their own list),
    // which keywords cannot. Read from the load order and the INIs as SkyPatcher would apply them.
    public class PatchLists
    {
        private static readonly string[] OwnFolders = { "EnchantedVariantsGenerator", "EnchantedVariantsGenerater" };

        // (base weapon, slot) -> patch list EditorID -> variants of that base in it. Slot: General, Fire or Frost<n>.
        private readonly Dictionary<(FormKey Base, string Slot), Dictionary<string, int>> _routes = new();
        private readonly Dictionary<FormKey, string> _anyRoute = new();
        private readonly Dictionary<string, int> _entryCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _patchLists = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<FormKey> _distributedBases = new();
        private readonly Dictionary<ModKey, string> _coveringPatch = new();
        private readonly Dictionary<string, FormKey> _owlLists;
        private readonly HashSet<string> _patchPlugins = new(StringComparer.OrdinalIgnoreCase);
        private int _variants;

        private PatchLists(Dictionary<string, FormKey> owlLists) => _owlLists = owlLists;

        public static PatchLists Discover(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, Dictionary<string, FormKey> owlLists)
        {
            var result = new PatchLists(owlLists);
            var contents = new Dictionary<string, List<FormKey>>(StringComparer.OrdinalIgnoreCase);
            List<FormKey> ContentsOf(string list) => contents.TryGetValue(list, out var c) ? c : contents[list] = new List<FormKey>();

            // Entries the plugins give the lists
            foreach (var (editorID, formKey) in owlLists)
            {
                if (!linkCache.TryResolve<ILeveledItemGetter>(formKey, out var list) || list.Entries == null) continue;
                foreach (var entry in list.Entries)
                {
                    if (entry.Data != null) ContentsOf(editorID).Add(entry.Data.Reference.FormKey);
                }
            }

            // Entries SkyPatcher adds at runtime, read in SkyPatcher's order and with its plugin gates
            var listByForm = owlLists.ToDictionary(l => l.Value, l => l.Key);
            Func<FormKey, bool> weaponExists = fk => linkCache.TryResolve<IWeaponGetter>(fk, out _);
            Func<string, FormKey?> weaponByEditorID = id => linkCache.TryResolve<IWeaponGetter>(id, out var w) ? w.FormKey : null;
            string root = Path.Combine(state.DataFolderPath.Path, "SKSE", "Plugins", "SkyPatcher", "leveledList");
            var active = new HashSet<string>(state.LoadOrder.Where(l => l.Value.Enabled).Select(l => l.Key.FileName.String), StringComparer.OrdinalIgnoreCase);
            var files = Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)
                    .Select(f => (Full: f, Relative: Path.GetRelativePath(root, f)))
                    .Where(f => !OwnFolders.Any(o => f.Relative.StartsWith(o + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : new();
            foreach (var (full, _) in files)
            {
                string gate = Path.GetFileNameWithoutExtension(full);
                if ((gate.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || gate.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || gate.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                    && !active.Contains(gate)) continue;

                foreach (var rawLine in File.ReadLines(full))
                {
                    var parts = ItemNames.SplitSegments(rawLine).Select(s => s.Split('=', 2)).Where(p => p.Length == 2)
                        .Select(p => (Key: p[0].Trim(), Value: p[1].Trim())).ToList();
                    var filter = parts.FirstOrDefault(p => p.Key.Equals("filterByLLs", StringComparison.OrdinalIgnoreCase));
                    if (filter.Key == null) continue;
                    var hasAll = parts.FirstOrDefault(p => p.Key.Equals("hasPlugins", StringComparison.OrdinalIgnoreCase));
                    if (hasAll.Key != null && !hasAll.Value.Split(',').All(p => active.Contains(p.Trim()))) continue;
                    var hasAny = parts.FirstOrDefault(p => p.Key.Equals("hasPluginsOr", StringComparison.OrdinalIgnoreCase));
                    if (hasAny.Key != null && !hasAny.Value.Split(',').Any(p => active.Contains(p.Trim()))) continue;

                    var adds = parts.Where(p => p.Key.Equals("addToLLs", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("addOnceToLLs", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(p => p.Value.Split(',')).Select(t => t.Split('~')[0].Trim()).Where(t => t.Length > 0).ToList();
                    if (adds.Count == 0) continue;

                    foreach (var token in filter.Value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
                    {
                        string? list = token.Contains('|')
                            ? ItemNames.ResolveForm(token, listByForm.ContainsKey, _ => null) is FormKey fk ? listByForm[fk] : null
                            : owlLists.ContainsKey(token) ? token : null;
                        if (list == null) continue;
                        foreach (var add in adds)
                        {
                            // Weapons by FormKey; anything else (sublists) only counts toward the list's size
                            ContentsOf(list).Add(ItemNames.ResolveForm(add, weaponExists, weaponByEditorID) ?? FormKey.Null);
                        }
                    }
                }
            }

            // Plugins OWL itself depends on (the game and DLC) are never "covered" by a patch
            var owlMasters = new HashSet<ModKey>();
            if (state.LoadOrder.ContainsKey(OwlIntegration.OwlModKey) && state.LoadOrder[OwlIntegration.OwlModKey].Mod is { } owl)
            {
                foreach (var master in owl.ModHeader.MasterReferences) owlMasters.Add(master.Master);
            }

            foreach (var (listID, entries) in contents)
            {
                result._entryCounts[listID] = entries.Count;
                var listPlugin = owlLists[listID].ModKey;
                if (listPlugin == OwlIntegration.OwlModKey) continue;
                string? slot = SlotOf(listID);
                foreach (var formKey in entries)
                {
                    if (formKey.IsNull || !linkCache.TryResolve<IWeaponGetter>(formKey, out var weapon)) continue;
                    FormKey baseKey;
                    if (!weapon.Template.IsNull && !weapon.ObjectEffect.IsNull)
                    {
                        baseKey = weapon.Template.FormKey;
                        if (slot != null)
                        {
                            var key = (baseKey, slot);
                            if (!result._routes.TryGetValue(key, out var lists)) result._routes[key] = lists = new(StringComparer.OrdinalIgnoreCase);
                            lists[listID] = lists.GetValueOrDefault(listID) + 1;
                            if (slot == "General" || !result._anyRoute.ContainsKey(baseKey)) result._anyRoute[baseKey] = listID;
                            result._patchLists.Add(listID);
                            result._variants++;
                        }
                    }
                    else
                    {
                        baseKey = weapon.FormKey;
                    }
                    result._distributedBases.Add(baseKey);
                    result._patchPlugins.Add(listPlugin.FileName);
                    if (baseKey.ModKey != OwlIntegration.OwlModKey && !owlMasters.Contains(baseKey.ModKey)) result._coveringPatch.TryAdd(baseKey.ModKey, listPlugin.FileName);
                }
            }
            return result;
        }

        // "..._Ench_Frost_02_..." -> Frost2, "..._Ench_Fire..." -> Fire, other "..._Ench..." -> General; unenchanted lists: null
        private static string? SlotOf(string listID)
        {
            var frost = Regex.Match(listID, @"_Ench_Frost_(\d+)", RegexOptions.IgnoreCase);
            if (frost.Success) return "Frost" + int.Parse(frost.Groups[1].Value);
            if (Regex.IsMatch(listID, @"_Ench_Fire(_|$)", RegexOptions.IgnoreCase)) return "Fire";
            if (Regex.IsMatch(listID, @"_Ench(_|$)", RegexOptions.IgnoreCase)) return "General";
            return null;
        }

        // The patch list holding most of the base's variants for that slot. A Draugr base filed under one frost tier's list
        // goes to the matching list for another tier.
        public string? Route(FormKey baseKey, string slot)
        {
            if (_routes.TryGetValue((baseKey, slot), out var lists)) return lists.MaxBy(l => l.Value).Key;
            if (!slot.StartsWith("Frost")) return null;
            string tier = int.Parse(slot.Substring("Frost".Length)).ToString("00");
            foreach (var ((b, s), other) in _routes)
            {
                if (b != baseKey || !s.StartsWith("Frost")) continue;
                string candidate = Regex.Replace(other.MaxBy(l => l.Value).Key, @"_Frost_\d+", "_Frost_" + tier, RegexOptions.IgnoreCase);
                if (_owlLists.ContainsKey(candidate)) return candidate;
            }
            return null;
        }

        public string? AnyRoute(FormKey baseKey) => _anyRoute.TryGetValue(baseKey, out var list) ? list : null;

        public bool IsPatchList(string listID) => _patchLists.Contains(listID);

        public int EntryCount(string listID) => _entryCounts.GetValueOrDefault(listID);

        // The OWL patch covering the base's mod when that patch never distributes this base; null otherwise
        public string? CoveringPatch(FormKey baseKey)
        {
            if (_distributedBases.Contains(baseKey)) return null;
            return _coveringPatch.TryGetValue(baseKey.ModKey, out var patch) ? patch : null;
        }

        public void Report(Config config)
        {
            if (_patchPlugins.Count == 0) return;
            Console.WriteLine("Open World Loot patches: " + string.Join(", ", _patchPlugins.OrderBy(p => p)) + " list " + _variants + " enchanted variants of "
                + _routes.Keys.Select(k => k.Base).Distinct().Count() + " base weapons in " + _patchLists.Count + " lists"
                + (config.OpenWorldLootUsePatchLists ? "; generated variants of those bases go to the same lists" : " (not used: setting off)"));
            if (config.OpenWorldLootSkipUnpatchedWeapons && _coveringPatch.Count > 0)
            {
                Console.WriteLine("Open World Loot patches cover " + string.Join(", ", _coveringPatch.Keys.Select(k => k.FileName.String).OrderBy(p => p))
                    + ": their weapons no patch distributes get no OWL loot");
            }
        }
    }
}
