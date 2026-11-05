using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace EnchantedVariantsGenerater
{
    // Names for generated items, matched to the load order as the game will show it:
    //   - Item names include SkyPatcher's runtime renames (e.g. WACCF's SkyPatcher edition renaming
    //     "Honed Ancient Nord Sword" to "Ancient Nord Honed Sword"), which no plugin carries.
    //   - An enchantment's prefix/suffix is taken from the enchanted variants that already exist for it, so the names
    //     follow whatever Thaumaturgy, WACCF or another mod calls that enchantment. The input file's text is the
    //     fallback when no variant exists.
    public static class ItemNames
    {
        private static readonly ModKey ThaumaturgyModKey = ModKey.FromFileName("Thaumaturgy.esp");

        // This patcher's own rename INIs. The folder and file both sort after other mods' (WACCF's is
        // "Weapons Armor Clothing and Clutter Fixes\WACCF_Weapons.ini"), so SkyPatcher applies them last and they win.
        private const string OverrideFolder = "zz Enchanted Variants Generater";
        private const string OverrideFile = "zz Enchanted Variants Generater - Names.ini";

        private static bool ThaumaturgyActive(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            return state.LoadOrder.ContainsKey(ThaumaturgyModKey) && state.LoadOrder[ThaumaturgyModKey].Enabled;
        }
        private static Dictionary<FormKey, string> _runtimeNames = new();
        private static Dictionary<FormKey, (string Prefix, string Suffix)> _affixes = new();

        // Runtime renames by source. Paths of Empyrean's SKSE plugin has its own SkyPatcher-syntax reader for
        // SKSE\Plugins\POE\Weapon; nothing says whether it applies before or after SkyPatcher, so where both rename an
        // item the two names are merged (see MergeRenames) and written to both, and the merged name wins either way.
        private static Dictionary<FormKey, string> _poeNames = new();
        private static Dictionary<FormKey, string> _skyPatcherNames = new();
        private static HashSet<FormKey> _merged = new();

        private static string PoeWeaponFolder(IPatcherState<ISkyrimMod, ISkyrimModGetter> state) => Path.Combine(state.DataFolderPath.Path, "SKSE", "Plugins", "POE", "Weapon");

        public static void Build(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, Config config)
        {
            _runtimeNames = new();
            _affixes = new();
            _poeNames = new();
            _skyPatcherNames = new();
            _merged = new();

            if (config.ApplySkyPatcherNames)
            {
                Func<FormKey, bool> weaponExists = fk => linkCache.TryResolve<IWeaponGetter>(fk, out _);
                Func<string, FormKey?> weaponByEditorID = id => linkCache.TryResolve<IWeaponGetter>(id, out var w) ? w.FormKey : null;
                string skyPatcher = Path.Combine(state.DataFolderPath.Path, "SKSE", "Plugins", "SkyPatcher");
                ReadRenames(state, PoeWeaponFolder(state), "Paths of Empyrean weapon", "filterByWeapons", weaponExists, weaponByEditorID, _poeNames);
                ReadRenames(state, Path.Combine(skyPatcher, "weapon"), "SkyPatcher weapon", "filterByWeapons", weaponExists, weaponByEditorID, _skyPatcherNames);
                ReadRenames(state, Path.Combine(skyPatcher, "armor"), "SkyPatcher armor", "filterByArmors", fk => linkCache.TryResolve<IArmorGetter>(fk, out _), id => linkCache.TryResolve<IArmorGetter>(id, out var a) ? a.FormKey : null, _skyPatcherNames);

                foreach (var (item, name) in _poeNames) _runtimeNames[item] = name;
                int unmerged = 0;
                foreach (var (item, skyName) in _skyPatcherNames)
                {
                    if (config.ReconcileRuntimeRenames && _poeNames.TryGetValue(item, out var poeName) && linkCache.TryResolve<IWeaponGetter>(item, out var weapon))
                    {
                        var merged = MergeRenames(weapon.Name?.String, poeName, skyName);
                        if (merged == null) unmerged++;
                        _runtimeNames[item] = merged ?? skyName;
                        _merged.Add(item);
                        continue;
                    }
                    _runtimeNames[item] = skyName;
                }
                if (_merged.Count > 0)
                {
                    Console.WriteLine("Renamed by both Paths of Empyrean and SkyPatcher: " + _merged.Count + " items, merged"
                        + (unmerged > 0 ? " (" + unmerged + " could not be merged; the SkyPatcher name is used)" : ""));
                }
            }

            if (config.MatchEnchantmentNames)
            {
                // With Thaumaturgy, its naming of enchantments wins: affixes are read from the variants' plugin names, which
                // Thaumaturgy (and its patches) set, ignoring runtime renames such as WACCF's SkyPatcher edition. Without it,
                // the runtime names are used, so WACCF's naming applies. Item (base) names keep runtime renames either way.
                bool thaumaturgy = ThaumaturgyActive(state);
                Func<FormKey, string?, string?> affixName = thaumaturgy ? (_, pluginName) => pluginName : NameOf;

                var tally = new Dictionary<FormKey, Dictionary<(string, string), int>>();
                void Count(FormKey enchantment, string? name, string? baseName, string? baseRuntimeName)
                {
                    var split = SplitAffix(name, baseName, baseRuntimeName);
                    if (split == null) return; // a unique name ("Wraith Sword") says nothing about the enchantment's affix
                    var affix = (split.Value.Prefix, split.Value.Suffix);
                    if (!tally.TryGetValue(enchantment, out var counts)) tally[enchantment] = counts = new();
                    counts[affix] = counts.GetValueOrDefault(affix) + 1;
                }

                foreach (var weapon in state.LoadOrder.PriorityOrder.Weapon().WinningOverrides())
                {
                    if (weapon.Template.IsNull || weapon.ObjectEffect.IsNull || weapon.FormKey.ModKey == state.PatchMod.ModKey) continue;
                    if (weapon.Data != null && weapon.Data.Flags.HasFlag(WeaponData.Flag.NonPlayable)) continue;
                    if (!linkCache.TryResolve<IWeaponGetter>(weapon.Template.FormKey, out var template)) continue;
                    Count(weapon.ObjectEffect.FormKey, affixName(weapon.FormKey, weapon.Name?.String), affixName(template.FormKey, template.Name?.String), NameOf(template.FormKey, template.Name?.String));
                }
                foreach (var armor in state.LoadOrder.PriorityOrder.Armor().WinningOverrides())
                {
                    if (armor.TemplateArmor.IsNull || armor.ObjectEffect.IsNull || armor.FormKey.ModKey == state.PatchMod.ModKey) continue;
                    if (armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable)) continue;
                    if (!linkCache.TryResolve<IArmorGetter>(armor.TemplateArmor.FormKey, out var template)) continue;
                    Count(armor.ObjectEffect.FormKey, affixName(armor.FormKey, armor.Name?.String), affixName(template.FormKey, template.Name?.String), NameOf(template.FormKey, template.Name?.String));
                }

                // Most common affix wins; ties go to the suffix-only form, then alphabetical, so the result is stable
                foreach (var (enchantment, counts) in tally)
                {
                    _affixes[enchantment] = counts.OrderByDescending(c => c.Value)
                        .ThenBy(c => c.Key.Item1.Length)
                        .ThenBy(c => c.Key.Item1 + "|" + c.Key.Item2, StringComparer.Ordinal)
                        .First().Key;
                }
                Console.WriteLine("Enchantment names: found the load order's prefix/suffix for " + _affixes.Count + " enchantments"
                    + (thaumaturgy ? " (Thaumaturgy's naming; runtime renames ignored for enchantment names)" : " (including SkyPatcher runtime renames)"));
            }
        }

        // With Thaumaturgy, other mods' SkyPatcher renames (WACCF's) replace its enchantment names on the existing
        // enchanted variants at runtime. Write SkyPatcher INIs, applied after theirs, that put Thaumaturgy's prefix/suffix
        // back while keeping the runtime base name ("Ancient Nord Honed Sword of Frost"). Only variants whose shown name
        // would change get a line. The INIs are rewritten every run and removed when they no longer apply.
        public static void WriteRuntimeNameOverrides(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, Config config)
        {
            string skyPatcher = Path.Combine(state.DataFolderPath.Path, "SKSE", "Plugins", "SkyPatcher");
            string weaponIni = Path.Combine(skyPatcher, "weapon", OverrideFolder, OverrideFile);
            string armorIni = Path.Combine(skyPatcher, "armor", OverrideFolder, OverrideFile);
            string poeIni = Path.Combine(PoeWeaponFolder(state), OverrideFile);

            bool restoreEnchantmentNames = config.OverrideRuntimeEnchantmentNames && config.MatchEnchantmentNames && config.ApplySkyPatcherNames && ThaumaturgyActive(state);
            bool reconcile = config.ReconcileRuntimeRenames && config.ApplySkyPatcherNames;
            if (!restoreEnchantmentNames && !reconcile)
            {
                DeleteIfPresent(weaponIni);
                DeleteIfPresent(armorIni);
                DeleteIfPresent(poeIni);
                return;
            }

            // Thaumaturgy's affix from a variant's plugin name, around the base's shown (runtime) name; null if unchanged
            string? Restored(FormKey variant, string? pluginName, FormKey baseItem, string? basePluginName)
            {
                var affix = SplitAffix(pluginName, basePluginName, NameOf(baseItem, basePluginName));
                if (affix == null) return null;
                string wanted = affix.Value.Prefix + NameOf(baseItem, basePluginName) + affix.Value.Suffix;
                return wanted == NameOf(variant, pluginName) ? null : wanted;
            }

            // Weapon names to set: merged names for items both PoE and SkyPatcher rename, and restored enchantment names
            var weaponNames = new Dictionary<FormKey, string>();
            int merges = 0, restoredWeapons = 0;
            if (reconcile)
            {
                foreach (var item in _merged)
                {
                    weaponNames[item] = _runtimeNames[item];
                    merges++;
                }
            }
            if (restoreEnchantmentNames)
            {
                foreach (var weapon in state.LoadOrder.PriorityOrder.Weapon().WinningOverrides())
                {
                    if (weapon.Template.IsNull || weapon.ObjectEffect.IsNull || weapon.FormKey.ModKey == state.PatchMod.ModKey) continue;
                    if (!linkCache.TryResolve<IWeaponGetter>(weapon.Template.FormKey, out var template)) continue;
                    var wanted = Restored(weapon.FormKey, weapon.Name?.String, template.FormKey, template.Name?.String);
                    if (wanted != null) { weaponNames[weapon.FormKey] = wanted; restoredWeapons++; }
                }
            }

            // Every name goes to SkyPatcher. Names of items Paths of Empyrean also renames go to its folder too, addressed
            // by EditorID as its own INIs are, so the name wins whichever of the two applies last.
            var weaponLines = new List<string>();
            var poeLines = new List<string>();
            foreach (var (item, name) in weaponNames)
            {
                weaponLines.Add("filterByWeapons=" + FormString(item) + ":fullName=~" + name + "~");
                if (_poeNames.ContainsKey(item) && linkCache.TryResolve<IWeaponGetter>(item, out var weapon) && !string.IsNullOrEmpty(weapon.EditorID))
                {
                    poeLines.Add("filterByWeapons=" + weapon.EditorID + ":fullName=~" + name + "~");
                }
            }

            var armorLines = new List<string>();
            if (restoreEnchantmentNames)
            {
                foreach (var armor in state.LoadOrder.PriorityOrder.Armor().WinningOverrides())
                {
                    if (armor.TemplateArmor.IsNull || armor.ObjectEffect.IsNull || armor.FormKey.ModKey == state.PatchMod.ModKey) continue;
                    if (!linkCache.TryResolve<IArmorGetter>(armor.TemplateArmor.FormKey, out var template)) continue;
                    var wanted = Restored(armor.FormKey, armor.Name?.String, template.FormKey, template.Name?.String);
                    if (wanted != null) armorLines.Add("filterByArmors=" + FormString(armor.FormKey) + ":fullName=~" + wanted + "~");
                }
            }

            WriteIni(weaponIni, weaponLines);
            WriteIni(armorIni, armorLines);
            WriteIni(poeIni, poeLines);
            Console.WriteLine("Runtime names: " + merges + " merged item names; restored Thaumaturgy's enchantment names on " + restoredWeapons + " weapons and "
                + armorLines.Count + " armors; " + poeLines.Count + " lines mirrored for Paths of Empyrean");
        }

        private static string FormString(FormKey formKey) => formKey.ModKey.FileName + "|" + formKey.ID.ToString("X6");

        // One item renamed by two mods: carry Paths of Empyrean's word swaps (against the plugin name) over to the
        // SkyPatcher name. Plugin "Supple Ancient Nord Bow", PoE "Supple Ancient Nord Shortbow" (Bow -> Shortbow),
        // WACCF "Ancient Nord Supple Bow" -> "Ancient Nord Supple Shortbow". Null when the swaps cannot be carried over.
        private static string? MergeRenames(string? pluginName, string poeName, string skyName)
        {
            if (string.IsNullOrEmpty(pluginName)) return null;
            if (poeName == pluginName) return skyName;
            if (skyName == pluginName) return poeName;
            var plugin = pluginName.Split(' ');
            var poe = poeName.Split(' ');
            if (plugin.Length != poe.Length) return null;
            var result = skyName.Split(' ');
            var used = new bool[result.Length];
            for (int i = 0; i < plugin.Length; i++)
            {
                if (plugin[i] == poe[i]) continue;
                int at = Array.FindIndex(result, w => w == plugin[i]);
                while (at >= 0 && used[at]) at = Array.FindIndex(result, at + 1, w => w == plugin[i]);
                if (at < 0) return null;
                result[at] = poe[i];
                used[at] = true;
            }
            return string.Join(' ', result);
        }

        // Prefix/suffix of an enchanted item's name around its base item's name, trying each spelling of the base
        // (plugin name, runtime name): Thaumaturgy's WACCF patch writes "Ancient Nord Honed Sword of Frost" while
        // the base's plugin name is still "Honed Ancient Nord Sword". Null when no spelling is found in the name.
        private static (string Prefix, string Suffix)? SplitAffix(string? name, params string?[] baseNames)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var baseName in baseNames)
            {
                if (string.IsNullOrEmpty(baseName)) continue;
                int i = name.IndexOf(baseName, StringComparison.Ordinal);
                if (i >= 0) return (name.Substring(0, i), name.Substring(i + baseName.Length));
            }
            return null;
        }

        private static void WriteIni(string path, List<string> lines)
        {
            if (lines.Count == 0) { DeleteIfPresent(path); return; }
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            var text = new StringBuilder();
            text.AppendLine("; Generated by Enchanted Variants Generater on every Synthesis run. Do not edit: the next run replaces this file.");
            text.AppendLine("; Sets item names agreed between mods that rename items at runtime (WACCF's SkyPatcher edition, Paths of Empyrean),");
            text.AppendLine("; with Thaumaturgy's enchantment names on enchanted items. Sorts after the other INIs in its folder, so it applies last.");
            foreach (var line in lines) text.AppendLine(line);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.ToString());
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Console.WriteLine("Removed " + path);
            }
        }

        // Name for an enchanted variant of the item, e.g. "Daedric Sword of the Inferno"
        public static string Compose(FormKey item, string? itemName, EnchantmentInfo enchantment)
        {
            string name = NameOf(item, itemName) ?? "";
            if (_affixes.TryGetValue(enchantment.Enchantment.FormKey, out var affix))
            {
                return affix.Prefix + name + affix.Suffix;
            }
            return enchantment.Prefix + name + enchantment.Suffix;
        }

        // An item's name as the game shows it: SkyPatcher's runtime rename if there is one, else the plugin's
        private static string? NameOf(FormKey item, string? pluginName)
        {
            return _runtimeNames.TryGetValue(item, out var renamed) ? renamed : pluginName;
        }

        // Collects fullName= from one folder of SkyPatcher-syntax INIs (SkyPatcher's own, or Paths of Empyrean's built-in
        // reader, which uses the same syntax), the way SkyPatcher loads them: files read in filename order (a later file
        // wins), a file named after a plugin only when that plugin is active, and hasPlugins / hasPluginsOr gates honoured.
        // Only lines that pick records by explicit form are applied; lines that pick them by keyword or other rules are
        // counted and left out, rather than guessed at. This patcher's own output is skipped.
        private static void ReadRenames(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, string root, string label, string formFilter, Func<FormKey, bool> exists, Func<string, FormKey?> byEditorID, Dictionary<FormKey, string> target)
        {
            if (!Directory.Exists(root)) return;

            var active = new HashSet<string>(state.LoadOrder.Where(l => l.Value.Enabled).Select(l => l.Key.FileName.String), StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(root, "*.ini", SearchOption.AllDirectories)
                .Select(f => (Full: f, Relative: Path.GetRelativePath(root, f)))
                .OrderBy(f => f.Relative, StringComparer.OrdinalIgnoreCase);

            int renamed = 0, unsupported = 0, unresolved = 0;
            foreach (var (full, relative) in files)
            {
                // This patcher's own renames from an earlier run are not the load order's names
                if (relative.StartsWith(OverrideFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(full).Equals(OverrideFile, StringComparison.OrdinalIgnoreCase)) continue;

                string gate = Path.GetFileNameWithoutExtension(full);
                if (gate.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || gate.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || gate.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                {
                    if (!active.Contains(gate)) continue;
                }

                foreach (var rawLine in File.ReadLines(full))
                {
                    var segments = SplitSegments(rawLine);
                    if (segments.Count == 0) continue;
                    var parts = segments.Select(s => s.Split('=', 2)).Where(p => p.Length == 2).Select(p => (Key: p[0].Trim(), Value: p[1].Trim())).ToList();
                    var fullName = parts.FirstOrDefault(p => p.Key.Equals("fullName", StringComparison.OrdinalIgnoreCase));
                    if (fullName.Key == null) continue;

                    if (parts.Any(p => p.Key.StartsWith("filterBy", StringComparison.OrdinalIgnoreCase) && !p.Key.Equals(formFilter, StringComparison.OrdinalIgnoreCase)))
                    {
                        unsupported++;
                        continue;
                    }
                    var forms = parts.FirstOrDefault(p => p.Key.Equals(formFilter, StringComparison.OrdinalIgnoreCase));
                    if (forms.Key == null) { unsupported++; continue; }

                    var hasAll = parts.FirstOrDefault(p => p.Key.Equals("hasPlugins", StringComparison.OrdinalIgnoreCase));
                    if (hasAll.Key != null && !hasAll.Value.Split(',').All(p => active.Contains(p.Trim()))) continue;
                    var hasAny = parts.FirstOrDefault(p => p.Key.Equals("hasPluginsOr", StringComparison.OrdinalIgnoreCase));
                    if (hasAny.Key != null && !hasAny.Value.Split(',').Any(p => active.Contains(p.Trim()))) continue;

                    string name = fullName.Value.Trim().Trim('~');
                    foreach (var token in forms.Value.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0))
                    {
                        var formKey = ResolveForm(token, exists, byEditorID);
                        if (formKey == null) { unresolved++; continue; }
                        target[formKey.Value] = name;
                        renamed++;
                    }
                }
            }
            Console.WriteLine(label + " names: " + renamed + " renames applied"
                + (unsupported > 0 ? ", " + unsupported + " rename lines skipped (not filtered by explicit forms)" : "")
                + (unresolved > 0 ? ", " + unresolved + " forms not in the load order" : ""));
        }

        // "Plugin.esp|FormID" (load-order byte ignored, light plugins by their low 12 bits) or an EditorID
        private static FormKey? ResolveForm(string token, Func<FormKey, bool> exists, Func<string, FormKey?> byEditorID)
        {
            int bar = token.IndexOf('|');
            if (bar < 0) return byEditorID(token);
            try
            {
                var modKey = ModKey.FromFileName(token.Substring(0, bar).Trim());
                uint id = Convert.ToUInt32(token.Substring(bar + 1).Trim(), 16);
                var full = new FormKey(modKey, id & 0xFFFFFF);
                if (exists(full)) return full;
                var light = new FormKey(modKey, id & 0xFFF);
                if (exists(light)) return light;
            }
            catch (Exception)
            {
            }
            return null;
        }

        // A patch line's ':'-separated segments, with '~...~' values kept whole and a ';' comment dropped
        private static List<string> SplitSegments(string line)
        {
            var segments = new List<string>();
            var current = new StringBuilder();
            bool inValue = false;
            foreach (char c in line)
            {
                if (c == '~') inValue = !inValue;
                if (!inValue && c == ';') break;
                if (!inValue && c == ':')
                {
                    segments.Add(current.ToString());
                    current.Clear();
                    continue;
                }
                current.Append(c);
            }
            if (current.ToString().Trim().Length > 0) segments.Add(current.ToString());
            return segments.Count == 1 && segments[0].Trim().Length == 0 ? new List<string>() : segments;
        }
    }
}
