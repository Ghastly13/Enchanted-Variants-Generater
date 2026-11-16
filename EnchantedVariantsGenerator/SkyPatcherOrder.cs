using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace EnchantedVariantsGenerator
{
    // The order SkyPatcher applies the INIs of one type folder (leveledList, weapon, ...) in, as its log shows: by how
    // deeply the file is nested under the type folder, shallowest first, then by path, ignoring case. So
    // leveledList\WACCF\x.ini runs before leveledList\OWL\Main\y.ini, whatever the names.
    public static class SkyPatcherOrder
    {
        // A form as SkyPatcher INIs address it by FormID: Plugin.esp|FormID, without the FormID's leading zeros
        // (Skyrim.esm|12EB7, not Skyrim.esm|012EB7). The patcher writes forms by EditorID where it can (SkyPatcherRefs).
        public static string FormString(FormKey formKey) => formKey.ModKey.FileName + "|" + formKey.ID.ToString("X");

        private static int Depth(string relative) => relative.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

        // Every INI under the type folder, in the order SkyPatcher applies them
        public static List<(string Full, string Relative)> InApplyOrder(string typeFolder)
        {
            if (!Directory.Exists(typeFolder)) return new();
            return Directory.EnumerateFiles(typeFolder, "*.ini", SearchOption.AllDirectories)
                .Select(f => (Full: f, Relative: Path.GetRelativePath(typeFolder, f)))
                .OrderBy(f => Depth(f.Relative))
                .ThenBy(f => f.Relative, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Where this patcher's INI goes so SkyPatcher applies it after every other INI in the type folder: under its own
        // folder, nested one level deeper than the deepest INI anyone else has there (subfolders named "zz"). Copies of
        // the same file at another depth, from earlier runs, are removed, and so are the "zz" folders they leave empty.
        public static string LastPath(string typeFolder, string ownFolder, string fileName)
        {
            int deepest = InApplyOrder(typeFolder)
                .Where(f => !f.Relative.StartsWith(ownFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Select(f => Depth(f.Relative)).DefaultIfEmpty(0).Max();
            string folder = Path.Combine(new[] { typeFolder, ownFolder }.Concat(Enumerable.Repeat("zz", deepest)).ToArray());
            string path = Path.Combine(folder, fileName);
            RemoveOtherCopies(typeFolder, ownFolder, fileName, path);
            return path;
        }

        // Removes every copy of the file under the patcher's folder (all of them when keep is null)
        public static void RemoveOtherCopies(string typeFolder, string ownFolder, string fileName, string? keep)
        {
            string own = Path.Combine(typeFolder, ownFolder);
            if (!Directory.Exists(own)) return;
            foreach (var file in Directory.EnumerateFiles(own, fileName, SearchOption.AllDirectories).ToList())
            {
                if (keep != null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(file);
                Console.WriteLine("Removed " + file);
            }
            // Empty "zz" folders, deepest first
            foreach (var dir in Directory.EnumerateDirectories(own, "zz", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
        }
    }

    // How this patcher names a form in its SkyPatcher INIs: by EditorID, as Open World Loot's and most mods' INIs do, where
    // SkyPatcher can be given it: made only of letters, digits and _ (nothing SkyPatcher reads as a separator), and had by
    // no other leveled list, weapon or armor in the load order or the patch (SkyPatcher could take either). Else
    // Plugin.esp|FormID without leading zeros. None of the operations the patcher writes (addToLLs, addOnceToLLs,
    // removeFromLLs, fullName) is one of SkyPatcher's FormID-only ones (formsToReplace, objectsToAdd, factionsToAdd).
    public sealed class SkyPatcherRefs
    {
        private static readonly Regex Plain = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);
        private readonly ISkyrimMod _patch;
        private readonly ILinkCache<ISkyrimMod, ISkyrimModGetter> _linkCache;
        // Every EditorID a version of a leveled list, weapon or armor has, with the forms that have it
        private readonly Dictionary<string, HashSet<FormKey>> _holders = new(StringComparer.OrdinalIgnoreCase);
        // The EditorIDs written so far, and the form each stands for
        private readonly Dictionary<string, FormKey> _written = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);

        public SkyPatcherRefs(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            _patch = state.PatchMod;
            _linkCache = linkCache;
            foreach (var listing in state.LoadOrder.PriorityOrder)
            {
                if (listing.Mod is not { } mod) continue;
                AddHolders(mod.LeveledItems);
                AddHolders(mod.Weapons);
                AddHolders(mod.Armors);
            }
            AddHolders(_patch.LeveledItems);
            AddHolders(_patch.Weapons);
            AddHolders(_patch.Armors);
        }

        private void AddHolders(IEnumerable<IMajorRecordGetter> records)
        {
            foreach (var record in records)
            {
                if (string.IsNullOrEmpty(record.EditorID)) continue;
                if (!_holders.TryGetValue(record.EditorID, out var forms)) _holders[record.EditorID] = forms = new();
                forms.Add(record.FormKey);
            }
        }

        public string Ref(FormKey formKey)
        {
            string? editorID = EditorIDOf(formKey);
            if (editorID == null || !Plain.IsMatch(editorID)) return SkyPatcherOrder.FormString(formKey);
            // Any version counts, not only the winning one: a record a mod gave this EditorID and a later mod renamed
            // (Thaumaturgy's Hunting Bow of Weariness, renamed by its WACCF patch) may still answer to it at runtime
            var others = (_holders.TryGetValue(editorID, out var forms) ? forms.Where(f => f != formKey) : Enumerable.Empty<FormKey>()).ToList();
            if (_written.TryGetValue(editorID, out var first) && first != formKey) others.Add(first);
            if (others.Count > 0)
            {
                if (_reported.Add(editorID)) Console.WriteLine("SkyPatcher INIs: " + formKey + " is written by its FormID: its EditorID " + editorID + " is also on " + string.Join(", ", others.Distinct()) + " (in at least one version)");
                return SkyPatcherOrder.FormString(formKey);
            }
            _written[editorID] = formKey;
            return editorID;
        }

        // The form's EditorID as the game ends up with it: the patch's version where it has one, else the winning one
        private string? EditorIDOf(FormKey formKey)
        {
            if (_patch.LeveledItems.TryGetValue(formKey, out var patchList)) return patchList.EditorID;
            if (_patch.Weapons.TryGetValue(formKey, out var patchWeapon)) return patchWeapon.EditorID;
            if (_patch.Armors.TryGetValue(formKey, out var patchArmor)) return patchArmor.EditorID;
            if (_linkCache.TryResolve<ILeveledItemGetter>(formKey, out var list)) return list.EditorID;
            if (_linkCache.TryResolve<IWeaponGetter>(formKey, out var weapon)) return weapon.EditorID;
            if (_linkCache.TryResolve<IArmorGetter>(formKey, out var armor)) return armor.EditorID;
            return null;
        }

        // Once the patch is complete: an EditorID written for one form that a record made after it was written has too
        public void CheckPatch()
        {
            foreach (var record in _patch.LeveledItems.Cast<IMajorRecordGetter>().Concat(_patch.Weapons).Concat(_patch.Armors))
            {
                if (record.EditorID != null && _written.TryGetValue(record.EditorID, out var written) && written != record.FormKey)
                    Console.WriteLine("WARNING: the SkyPatcher INIs name " + written + " by its EditorID " + record.EditorID + ", which " + record.FormKey + " in the patch has too: SkyPatcher may take either");
            }
        }
    }
}
