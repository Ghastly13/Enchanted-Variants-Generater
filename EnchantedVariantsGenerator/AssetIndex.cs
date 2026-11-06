using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace EnchantedVariantsGenerator
{
    // Which mesh files the game can load: loose files in the data folder (under MO2, its virtual folder) and files inside
    // the archives there. Base items whose model file is missing are left out: the game shows them as nothing (or an
    // error marker), which usually means an unused or unfinished record, e.g. Heavy Armory's "Heavy Ebony Glaive".
    public static class AssetIndex
    {
        private static string _dataPath = "";
        private static HashSet<string> _archived = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> _missing = new(StringComparer.OrdinalIgnoreCase);

        public static void Build(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            _missing.Clear();
            _dataPath = state.DataFolderPath.Path;
            var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int archives = 0;
            foreach (var path in Directory.EnumerateFiles(_dataPath, "*.bsa", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var reader = Archive.CreateReader(state.GameRelease, path);
                    foreach (var file in reader.Files)
                    {
                        string p = Normalize(file.Path);
                        if (p.StartsWith("meshes\\")) archived.Add(p);
                    }
                    archives++;
                }
                catch (Exception e)
                {
                    Console.WriteLine("Model check: could not read " + Path.GetFileName(path) + " (" + e.Message + "); its meshes count as missing");
                }
            }
            _archived = archived;
            Console.WriteLine("Model check: " + archived.Count + " meshes in " + archives + " archives, plus loose files");
        }

        private static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

        // A model path as records store it ("PrvtI\ebony\x.nif", relative to meshes) to its Data path
        public static bool MeshPresent(string? modelFile)
        {
            if (string.IsNullOrWhiteSpace(modelFile)) return false;
            string p = Normalize(modelFile);
            if (!p.StartsWith("meshes\\")) p = "meshes\\" + p;
            return _archived.Contains(p) || File.Exists(Path.Combine(_dataPath, p));
        }

        // False (and noted for the summary) when the weapon's model file is missing
        public static bool HasModel(IWeaponGetter weapon)
        {
            if (MeshPresent(weapon.Model?.File)) return true;
            _missing.TryAdd(weapon.EditorID ?? weapon.FormKey.ToString(), weapon.Model?.File ?? "(no model)");
            return false;
        }

        // An armor counts as having a model when either of its world models' files exists
        public static bool HasModel(IArmorGetter armor)
        {
            var male = armor.WorldModel?.Male?.Model?.File;
            var female = armor.WorldModel?.Female?.Model?.File;
            if (MeshPresent(male) || MeshPresent(female)) return true;
            _missing.TryAdd(armor.EditorID ?? armor.FormKey.ToString(), male ?? female ?? "(no model)");
            return false;
        }

        public static void Report()
        {
            if (_missing.IsEmpty) return;
            Console.WriteLine("Model check: skipped " + _missing.Count + " base items whose model file is missing: "
                + string.Join(", ", _missing.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).Select(m => m.Key + " (" + m.Value + ")")));
        }
    }
}
