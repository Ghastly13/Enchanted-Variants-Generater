using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace EnchantedVariantsGenerator
{
    public enum JewelSlot { Ring, Necklace, Circlet }

    // Enchanted jewelry (rings, necklaces, circlets). The plain pieces mods add, found in loot (the Creation Club's Nordic
    // Jewelry, ...), get the enchantments the game's enchanted jewelry lists carry on items of their slot, from the game
    // itself, Thaumaturgy and Summermyst (setting "Jewelry: also enchantments from"), as a mod's armor gets those of its
    // kind. The game's own pieces get none: vanilla puts each enchantment and tier on a piece of its choosing, and that is
    // left as it is. ItemDiscovery finds the pieces (a plugin with more than "Jewelry: most pieces per plugin" is left out:
    // Immersive Jewelry has some 5,000), JewelryKind gives the enchantments.
    // Each variant goes beside the items carrying its enchantment: in <their list>_EVG, added to that list, at the level the
    // list gives them (Summermyst's lists give each enchantment its own level).
    // The game's enchanted jewelry lists: LItemEnchRingAll, ...NecklaceAll, ...CircletAll, ...JewelryAll (and their 15/25/75
    // copies) and every list under them, mods' lists placed there included (Summermyst's).
    public partial class LootDistribution
    {
        private static readonly Regex GameJewelryTop = new(@"^(DLC\d)?LItemEnch(Ring|Necklace|Circlet|Jewelry)All\d*$", RegexOptions.IgnoreCase);
        // The game's own plugins: its pieces (which get no variants), and its enchantments
        private static readonly string[] GameMasters = { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
        // The noun an enchanted jewelry item's name is built around when it does not hold its piece's name ("Ring of Minor
        // Destruction", "Jester's Ring")
        private static readonly Regex JewelNoun = new(@"\b(Ring|Necklace|Amulet|Circlet|Pendant|Torc|Choker|Diadem|Tiara|Band|Medallion|Locket|Charm|Talisman)\b", RegexOptions.IgnoreCase);

        // An enchanted jewelry item in a list: its piece (template), slot, enchantment and lowest level there
        private sealed record JewelItem(IArmorGetter Record, IArmorGetter Piece, JewelSlot Slot, FormKey Enchantment, short Level);

        // A list holding enchanted jewelry items directly
        private sealed class JewelList
        {
            public FormKey FormKey;
            public string EditorID = "";
            public readonly Dictionary<FormKey, JewelItem> Items = new();
            public Leaf? AsLeaf;
        }

        private readonly List<JewelList> _jewelLists = new();
        private readonly Dictionary<FormKey, JewelSlot?> _jewelSlots = new();
        private readonly Dictionary<JewelSlot, DetectedKind?> _jewelKinds = new();
        private HashSet<string> _jewelEnchantmentPlugins = new(StringComparer.OrdinalIgnoreCase);

        private static string Plural(JewelSlot slot) => slot switch { JewelSlot.Ring => "rings", JewelSlot.Necklace => "necklaces", _ => "circlets" };

        // Whether the piece is one of the game's own (those get no variants)
        public static bool IsGamePiece(IArmorGetter piece) => GameMasters.Contains(piece.FormKey.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase);

        // A jewelry piece's slot (plain or enchanted): ring, necklace or circlet, by its Clothing... keyword, else its body
        // slot; null for anything else (earrings, crowns, clothing). Jewelry carries ArmorJewelry or VendorItemJewelry.
        public JewelSlot? JewelSlotOf(IArmorGetter armor)
        {
            if (_jewelSlots.TryGetValue(armor.FormKey, out var known)) return known;
            JewelSlot? slot = null;
            var keywords = (armor.Keywords ?? Array.Empty<IFormLinkGetter<IKeywordGetter>>()).Select(k => KeywordID(k.FormKey)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool jewelry = keywords.Contains("ArmorJewelry") || keywords.Contains("VendorItemJewelry");
            if (jewelry && armor.BodyTemplate?.ArmorType is ArmorType.Clothing or null)
            {
                if (keywords.Contains("ClothingRing")) slot = JewelSlot.Ring;
                else if (keywords.Contains("ClothingNecklace")) slot = JewelSlot.Necklace;
                else if (keywords.Contains("ClothingCirclet")) slot = JewelSlot.Circlet;
                else if (armor.BodyTemplate?.FirstPersonFlags is BipedObjectFlag flags)
                {
                    if (flags == BipedObjectFlag.Ring) slot = JewelSlot.Ring;
                    else if (flags == BipedObjectFlag.Amulet) slot = JewelSlot.Necklace;
                    else if (flags == BipedObjectFlag.Circlet) slot = JewelSlot.Circlet;
                }
            }
            return _jewelSlots[armor.FormKey] = slot;
        }

        // An enchanted jewelry item: enchanted, built on a jewelry piece
        private (IArmorGetter Record, IArmorGetter Piece, JewelSlot Slot)? EnchantedJewel(FormKey item)
        {
            if (!_linkCache.TryResolve<IArmorGetter>(item, out var armor) || armor.ObjectEffect.IsNull || armor.TemplateArmor.IsNull) return null;
            if (!_linkCache.TryResolve<IArmorGetter>(armor.TemplateArmor.FormKey, out var piece)) return null;
            return JewelSlotOf(piece) is JewelSlot slot ? (armor, piece, slot) : null;
        }

        // The game's enchanted jewelry lists (see the top of this file): every list under its tops holding enchanted jewelry
        private void FindJewelry(Config config)
        {
            _jewelEnchantmentPlugins = new HashSet<string>(GameMasters.Concat((config.JewelryEnchantmentPlugins ?? new()).Select(p => p.Trim()).Where(p => p.Length > 0)), StringComparer.OrdinalIgnoreCase);
            var tops = _lists.Entries.Keys.Where(fk => _lists.OriginalEditorID(fk) is string id && GameJewelryTop.IsMatch(id)
                && GameMasters.Contains(fk.ModKey.FileName.String, StringComparer.OrdinalIgnoreCase)).ToList();
            var queue = new Queue<FormKey>(tops);
            var seen = new HashSet<FormKey>(tops);
            while (queue.Count > 0)
            {
                var fk = queue.Dequeue();
                JewelList? list = null;
                foreach (var entry in _lists.Entries[fk])
                {
                    if (_lists.IsList(entry.Reference))
                    {
                        if (seen.Add(entry.Reference)) queue.Enqueue(entry.Reference);
                        continue;
                    }
                    if (EnchantedJewel(entry.Reference) is not var (record, piece, slot)) continue;
                    list ??= new JewelList { FormKey = fk, EditorID = ListName(fk) };
                    list.Items[record.FormKey] = list.Items.TryGetValue(record.FormKey, out var have) && have.Level <= entry.Level
                        ? have
                        : new JewelItem(record, piece, slot, record.ObjectEffect.FormKey, entry.Level);
                }
                if (list != null) _jewelLists.Add(list);
            }
            Console.WriteLine("Jewelry: " + _jewelLists.Count + " of the game's enchanted jewelry lists hold " + _jewelLists.SelectMany(l => l.Items.Keys).Distinct().Count()
                + " enchanted items; variants take the enchantments of " + string.Join(", ", _jewelEnchantmentPlugins.Except(GameMasters, StringComparer.OrdinalIgnoreCase).Prepend("the game")));
        }

        // Prefix/suffix an enchanted jewelry item's name puts around its piece's name, else around its noun ("Ring of the
        // Forge" -> " of the Forge", "Jester's Ring" -> "Jester's "); null for a name with neither
        private static (string Prefix, string Suffix)? JewelAffix(JewelItem item)
        {
            string? name = item.Record.Name?.String;
            if (ItemNames.AffixOf(item.Piece.FormKey, item.Piece.Name?.String, name) is { } around) return around;
            if (string.IsNullOrEmpty(name) || JewelNoun.Match(name) is not { Success: true } m) return null;
            return (name.Substring(0, m.Index), name.Substring(m.Index + m.Length));
        }

        // The item's tier by its EditorID in the plugin that defines it (EnchRingDestruction01, SUM_...Quake03, MAG_...04A)
        private int? JewelTierByName(JewelItem item) => TierOf(OriginalItemEditorID(item.Record.FormKey, false) ?? item.Record.EditorID);

        // The enchantments the game's enchanted jewelry lists carry on items of this slot, from the game and the plugins of
        // "Jewelry: also enchantments from", each with its items' tier and the name they put around their piece's ("Ring of
        // Minor Destruction" around a Silver Ruby Ring: " of Minor Destruction"). Null when there are none.
        private DetectedKind? JewelryKind(JewelSlot slot)
        {
            if (_jewelKinds.TryGetValue(slot, out var cached)) return cached;
            var enchantments = new List<EnchantmentInfo>();
            var items = _jewelLists.SelectMany(l => l.Items.Values)
                .Where(i => i.Slot == slot && _jewelEnchantmentPlugins.Contains(i.Enchantment.ModKey.FileName.String))
                .GroupBy(i => i.Record.FormKey).Select(g => g.First());
            foreach (var g in items.GroupBy(i => i.Enchantment))
            {
                if (!_linkCache.TryResolve<IObjectEffectGetter>(g.Key, out var enchantment)) continue;
                string editorID = OriginalEnchantmentEditorID(g.Key) ?? enchantment.EditorID ?? g.Key.ToString();
                int? tier = g.Select(JewelTierByName).OfType<int>().GroupBy(t => t).OrderByDescending(t => t.Count()).ThenBy(t => t.Key).Select(t => (int?)t.Key).FirstOrDefault();
                var affix = g.Select(JewelAffix).OfType<(string Prefix, string Suffix)>().GroupBy(a => a).OrderByDescending(a => a.Count())
                    .ThenBy(a => a.Key.Prefix.Length).ThenBy(a => a.Key.Prefix + "|" + a.Key.Suffix, StringComparer.Ordinal)
                    .Select(a => ((string Prefix, string Suffix)?)a.Key).FirstOrDefault();
                // A variant whose name would not say its enchantment is left out
                if (affix == null && !ItemNames.HasLoadOrderName(g.Key)) continue;
                enchantments.Add(new EnchantmentInfo(editorID, enchantment) { Tier = tier, Prefix = affix?.Prefix, Suffix = affix?.Suffix });
            }
            var kind = enchantments.Count == 0 ? null
                : new DetectedKind("Jewelry|" + slot, "Jewelry: " + Plural(slot) + " (the game's enchanted jewelry lists)",
                    enchantments.OrderBy(e => e.Tier ?? 0).ThenBy(e => e.EditorID, StringComparer.OrdinalIgnoreCase).ToList());
            return _jewelKinds[slot] = kind;
        }

        // Where a jewelry variant with this enchantment goes: beside the list holding most of the slot's items with it (the
        // game's own list first), at the level it gives them
        private (Leaf Leaf, short Level)? JewelryTarget(JewelSlot slot, FormKey enchantment)
        {
            var best = _jewelLists
                .Select(l => (List: l, Items: l.Items.Values.Where(i => i.Slot == slot && i.Enchantment == enchantment).ToList()))
                .Where(x => x.Items.Count > 0)
                .OrderByDescending(x => IsBaseGame(x.List.FormKey.ModKey)).ThenByDescending(x => x.Items.Count)
                .ThenBy(x => x.List.EditorID, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (best.List == null) return null;
            best.List.AsLeaf ??= new Leaf { FormKey = best.List.FormKey, EditorID = best.List.EditorID, Armor = true, Class = "Jewelry " + slot };
            return (best.List.AsLeaf, best.Items.Min(i => i.Level));
        }
    }
}
