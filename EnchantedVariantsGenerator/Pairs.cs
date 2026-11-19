using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace EnchantedVariantsGenerator
{
    // Weapons a mod hands out in pairs: a "use all" list of a weapon and its left-hand counterpart (Animated Armoury's
    // LItemIronClaws: Iron Claws and Iron Claws Left). Their variants go out in pairs too, as Animated Armoury's own
    // enchanted claws (Talons of Woe) do: each with its other hand's variant of the same enchantment, in a "use all" list
    // (<variant>_Pair). The pair then takes one place among its twins' list's other variants, in a list of its own
    // (<variant>_PairDiluted) beside setting PairDilution of its twins, the items of the twins' list with the same
    // enchantment (Iron Dagger of Fire for Iron Claws of Fire): with 4, a pair comes once in 5 there.
    public partial class LootDistribution
    {
        private static readonly FormKey LeftHand = FormKey.Factory("013F43:Skyrim.esm");

        // Each weapon handed out in pairs: its other hand and the list packaging them
        private Dictionary<FormKey, (FormKey Partner, FormKey List)>? _pairs;

        private record PairHalf(GeneratedVariant Variant, NewList Target, short Level, int Tier);

        private static bool IsLeftHanded(IMajorRecordGetter item) => item is IWeaponGetter w && w.EquipmentType.FormKey == LeftHand;

        private Dictionary<FormKey, (FormKey Partner, FormKey List)> Pairs()
        {
            if (_pairs != null) return _pairs;
            _pairs = new();
            foreach (var (list, entries) in _lists.Entries)
            {
                if (entries.Count != 2 || !_lists.Record(list).Flags.HasFlag(LeveledItem.Flag.UseAll)) continue;
                var weapons = entries.Select(e => _linkCache.TryResolve<IWeaponGetter>(e.Reference, out var w) ? w : null).OfType<IWeaponGetter>().ToList();
                if (weapons.Count != 2 || weapons.Count(IsLeftHanded) != 1) continue;
                var left = weapons.First(IsLeftHanded);
                var right = weapons.First(w => !IsLeftHanded(w));
                _pairs.TryAdd(right.FormKey, (left.FormKey, list));
                _pairs.TryAdd(left.FormKey, (right.FormKey, list));
            }
            return _pairs;
        }

        // The other hand of a weapon a mod hands out in pairs
        public FormKey? PairPartner(FormKey weapon) => Pairs().TryGetValue(weapon, out var pair) ? pair.Partner : null;

        // Puts each pair of variants in its right hand's new list, at the level it would have had. A half whose other hand
        // has no variant with its enchantment is left out. Returns the lists made and the variants placed.
        private (int Lists, int Placed) PlacePairs(Config config, List<PairHalf> halves, Action<string, GeneratedVariant> skip, List<string> report)
        {
            var byHand = new Dictionary<(FormKey Base, FormKey Enchantment), PairHalf>();
            foreach (var h in halves) byHand.TryAdd((h.Variant.BaseItem.FormKey, h.Variant.Enchantment.Enchantment.FormKey), h);
            int dilution = Math.Clamp(config.PairDilution, 0, MaxEntriesPerList - 1);
            var counts = new SortedDictionary<string, (int Pairs, int Diluted)>(StringComparer.OrdinalIgnoreCase);
            int lists = 0, placed = 0;
            foreach (var h in halves)
            {
                var baseItem = h.Variant.BaseItem;
                var enchantment = h.Variant.Enchantment.Enchantment.FormKey;
                var (partner, packagedIn) = Pairs()[baseItem.FormKey];
                if (byHand[(baseItem.FormKey, enchantment)] != h) { skip("a second variant of its base item with this enchantment", h.Variant); continue; }
                if (!byHand.TryGetValue((partner, enchantment), out var other))
                {
                    skip("handed out in pairs, and its other hand has no variant with this enchantment", h.Variant);
                    continue;
                }
                // Made with its right hand
                if (IsLeftHanded(baseItem)) continue;

                string id = VariantEditorID(h.Variant);
                var pair = NewRecord(config, id + "_Pair", LeveledItem.Flag.UseAll);
                pair.Entries!.Add(Program.CreateLeveledItemEntry(1, h.Variant.FormKey));
                pair.Entries!.Add(Program.CreateLeveledItemEntry(1, other.Variant.FormKey));
                _state.PatchMod.LeveledItems.Set(pair);
                lists++;

                FormKey entry = pair.FormKey;
                var twins = Twins(h.Target.Target, enchantment, h.Tier);
                bool diluted = dilution > 0 && twins.Count > 0;
                if (diluted)
                {
                    var flags = _lists.IsList(h.Target.Target.FormKey) ? _lists.Record(h.Target.Target.FormKey).Flags & ~LeveledItem.Flag.UseAll : default;
                    var mixed = NewRecord(config, id + "_PairDiluted", flags);
                    mixed.Entries!.Add(Program.CreateLeveledItemEntry(1, pair.FormKey));
                    for (int i = 0; i < dilution; i++) mixed.Entries!.Add(Program.CreateLeveledItemEntry(1, twins[i % twins.Count]));
                    _state.PatchMod.LeveledItems.Set(mixed);
                    lists++;
                    entry = mixed.FormKey;
                }
                h.Target.Items[entry] = h.Target.Items.TryGetValue(entry, out var had) ? Math.Min(had, h.Level) : h.Level;
                placed += 2;

                string label = baseItem.EditorID + " + " + other.Variant.BaseItem.EditorID + " (as " + ListName(packagedIn) + ")";
                var (pairs, mixedPairs) = counts.GetValueOrDefault(label);
                counts[label] = (pairs + 1, mixedPairs + (diluted ? 1 : 0));
            }
            foreach (var (label, (pairs, mixedPairs)) in counts)
            {
                report.Add("  " + label + ": " + pairs + " pairs" + (mixedPairs == pairs ? "" : ", " + (pairs - mixedPairs) + " of them with no twin to go beside"));
            }
            return (lists, placed);
        }

        // The items of the twins' list with this enchantment, else (an enchantment it has on no item) its items of this tier
        private static List<FormKey> Twins(Leaf leaf, FormKey enchantment, int tier)
        {
            var twins = leaf.Items.Where(i => i.Enchantment == enchantment).Select(i => i.Item).Distinct().ToList();
            return twins.Count > 0 ? twins : leaf.Items.Where(i => i.Tier == tier).Select(i => i.Item).Distinct().ToList();
        }

        private string VariantEditorID(GeneratedVariant v) =>
            (_state.PatchMod.Weapons.ContainsKey(v.FormKey) ? _state.PatchMod.Weapons[v.FormKey].EditorID
                : _linkCache.TryResolve<IWeaponGetter>(v.FormKey, out var existing) ? existing.EditorID : null)
            ?? "Ench_" + v.BaseItem.EditorID + "_" + v.Enchantment.EditorID;
    }
}
