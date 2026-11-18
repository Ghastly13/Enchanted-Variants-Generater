using System;
using System.Collections.Generic;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace EnchantedVariantsGenerator
{
    // The EditorIDs the patch's new records get: the one wanted, else, where a weapon, armor or leveled list in the load
    // order or the patch has it already, or a record made earlier this run took it, the same with _1, _2, ... appended
    // (Ench_IronSword_EnchWeaponFireDamage01_1). No two records then share an EditorID, which SkyPatcher's EditorID lookup
    // and Synthesis's FormID allocation both need. A load-order version of the patch itself (its output from an earlier
    // run) does not count: its records are this patcher's own.
    public static class EditorIDs
    {
        private static HashSet<string> _taken = new(StringComparer.OrdinalIgnoreCase);
        private static int _renamed;

        public static int Renamed => _renamed;

        public static void Build(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            _taken = new(StringComparer.OrdinalIgnoreCase);
            _renamed = 0;
            var patch = state.PatchMod.ModKey;
            foreach (var listing in state.LoadOrder.PriorityOrder)
            {
                if (listing.Mod is not { } mod || listing.ModKey == patch) continue;
                Add(mod.Weapons);
                Add(mod.Armors);
                Add(mod.LeveledItems);
            }
            // What earlier patchers of the same run put in the patch
            Add(state.PatchMod.Weapons);
            Add(state.PatchMod.Armors);
            Add(state.PatchMod.LeveledItems);
        }

        private static void Add(IEnumerable<IMajorRecordGetter> records)
        {
            foreach (var record in records)
            {
                if (!string.IsNullOrEmpty(record.EditorID)) _taken.Add(record.EditorID);
            }
        }

        // The EditorID a new record gets, taken from here on
        public static string Claim(string wanted)
        {
            string id = wanted;
            for (int n = 1; _taken.Contains(id); n++) id = wanted + "_" + n;
            if (id != wanted)
            {
                _renamed++;
                Console.WriteLine("EditorID " + wanted + " is taken: the new record is " + id);
            }
            _taken.Add(id);
            return id;
        }
    }
}
