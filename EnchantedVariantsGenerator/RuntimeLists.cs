using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace EnchantedVariantsGenerator
{
    // Every leveled item list as the game holds it once SkyPatcher has run: the winning record's entries, then each active
    // leveled-list INI line (clear, removeFromLLs, addToLLs, addOnceToLLs): Paths of Empyrean's (SKSE\Plugins\POE\LeveledList,
    // read by its own SKSE plugin in SkyPatcher's syntax, by path; it applies before SkyPatcher, as its weapon renames do),
    // then SkyPatcher's in the order SkyPatcher applies the files (SkyPatcherOrder: shallowest folders first). Paths of
    // Empyrean places its missing shields' enchanted lists (PoE_SublistEnchArmorLeatherShield01, ...) this way.
    // Open World Loot, for one, clears and rebuilds vanilla lists this way, so where a list really sits is only known after
    // the replay. The patcher's own INIs are left out: this run rewrites them.
    public class RuntimeLists
    {
        public record Entry(FormKey Reference, short Level, short Count);

        private static readonly string[] OwnFolders = { "EnchantedVariantsGenerator", "EnchantedVariantsGenerater", "zz Enchanted Variants Generator", "zz Enchanted Variants Generater" };

        private readonly Dictionary<FormKey, List<Entry>> _entries = new();
        private readonly Dictionary<FormKey, ILeveledItemGetter> _records = new();
        private readonly Dictionary<FormKey, string> _originalEditorIDs = new();
        private readonly Dictionary<string, FormKey> _byEditorID = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<FormKey, List<(FormKey Parent, short Level)>>? _parents;

        public IReadOnlyDictionary<FormKey, List<Entry>> Entries => _entries;
        public int IniLines { get; private set; }

        public ILeveledItemGetter Record(FormKey list) => _records[list];

        // The EditorID the defining plugin gives the list: Thaumaturgy, for one, renames vanilla lists ("MAG_...")
        public string? OriginalEditorID(FormKey list) => _originalEditorIDs.TryGetValue(list, out var id) ? id : _records.TryGetValue(list, out var r) ? r.EditorID : null;

        // By the winning or the original EditorID
        public FormKey? ListByEditorID(string editorID) => _byEditorID.TryGetValue(editorID, out var fk) ? fk : null;

        public bool IsList(FormKey fk) => _records.ContainsKey(fk);

        // Every entry of child in list moved to this level, as the patcher's own INI then does in game
        public void Move(FormKey list, FormKey child, short level)
        {
            var entries = _entries[list];
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Reference == child) entries[i] = entries[i] with { Level = level };
            }
            _parents = null;
        }

        // The lists holding this form, with each entry's level
        public IReadOnlyList<(FormKey Parent, short Level)> Parents(FormKey child)
        {
            if (_parents == null)
            {
                _parents = new();
                foreach (var (list, entries) in _entries)
                {
                    foreach (var entry in entries)
                    {
                        if (!_parents.TryGetValue(entry.Reference, out var p)) _parents[entry.Reference] = p = new();
                        p.Add((list, entry.Level));
                    }
                }
            }
            return _parents.TryGetValue(child, out var parents) ? parents : Array.Empty<(FormKey, short)>();
        }

        public static RuntimeLists Build(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            var result = new RuntimeLists();
            foreach (var list in state.LoadOrder.PriorityOrder.LeveledItem().WinningOverrides())
            {
                if (list.FormKey.ModKey == state.PatchMod.ModKey) continue;
                result._records[list.FormKey] = list;
                result._entries[list.FormKey] = (list.Entries ?? Array.Empty<ILeveledItemEntryGetter>())
                    .Where(e => e.Data != null)
                    .Select(e => new Entry(e.Data!.Reference.FormKey, e.Data.Level, e.Data.Count))
                    .ToList();
                if (list.EditorID != null) result._byEditorID.TryAdd(list.EditorID, list.FormKey);
            }
            // Original EditorIDs, from the plugin that defines each list
            foreach (var listing in state.LoadOrder.PriorityOrder)
            {
                if (listing.Mod == null) continue;
                foreach (var list in listing.Mod.LeveledItems)
                {
                    if (list.FormKey.ModKey != listing.ModKey || list.EditorID == null || !result._records.ContainsKey(list.FormKey)) continue;
                    result._originalEditorIDs[list.FormKey] = list.EditorID;
                    result._byEditorID.TryAdd(list.EditorID, list.FormKey);
                }
            }
            result.ReplaySkyPatcher(state, linkCache);
            return result;
        }

        private void ReplaySkyPatcher(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache)
        {
            string root = Path.Combine(state.DataFolderPath.Path, "SKSE", "Plugins", "SkyPatcher", "leveledList");
            string poeRoot = Path.Combine(state.DataFolderPath.Path, "SKSE", "Plugins", "POE", "LeveledList");
            var active = new HashSet<string>(state.LoadOrder.Where(l => l.Value.Enabled).Select(l => l.Key.FileName.String), StringComparer.OrdinalIgnoreCase);
            // Paths of Empyrean's first, by path; then SkyPatcher's in its order: shallowest folders first, then by path
            // (WACCF's INI runs before OWL\Main\...)
            var poeFiles = Directory.Exists(poeRoot)
                ? Directory.EnumerateFiles(poeRoot, "*.ini", SearchOption.AllDirectories).OrderBy(f => Path.GetRelativePath(poeRoot, f), StringComparer.OrdinalIgnoreCase)
                    .Select(f => (Full: f, Relative: Path.GetRelativePath(poeRoot, f))).ToList()
                : new List<(string Full, string Relative)>();
            var files = poeFiles.Concat(SkyPatcherOrder.InApplyOrder(root)
                .Where(f => !f.Relative.Split(Path.DirectorySeparatorChar).Any(part => OwnFolders.Contains(part, StringComparer.OrdinalIgnoreCase))))
                .ToList();
            if (files.Count == 0) return;

            bool Exists(FormKey fk) => _records.ContainsKey(fk) || linkCache.TryResolve<ISkyrimMajorRecordGetter>(fk, out _);
            FormKey? ByEditorID(string id)
            {
                if (_byEditorID.TryGetValue(id, out var list)) return list;
                if (linkCache.TryResolve<IWeaponGetter>(id, out var w)) return w.FormKey;
                if (linkCache.TryResolve<IArmorGetter>(id, out var a)) return a.FormKey;
                return null;
            }
            FormKey? Resolve(string token) => ItemNames.ResolveForm(token.Trim(), Exists, ByEditorID);

            foreach (var (full, _) in files)
            {
                string gate = Path.GetFileNameWithoutExtension(full);
                if ((gate.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) || gate.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || gate.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
                    && !active.Contains(gate)) continue;

                foreach (var raw in File.ReadLines(full))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";")) continue;
                    var parts = line.Split(':').Select(s => s.Split('=', 2)).Where(p => p.Length == 2)
                        .Select(p => (Key: p[0].Trim(), Value: p[1].Trim())).ToList();
                    string? Value(string key) => parts.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

                    var filter = Value("filterByLLs");
                    if (filter == null) continue;
                    var hasAll = Value("hasPlugins");
                    if (hasAll != null && !hasAll.Split(',').All(p => active.Contains(p.Trim()))) continue;
                    var hasAny = Value("hasPluginsOr");
                    if (hasAny != null && !hasAny.Split(',').Any(p => active.Contains(p.Trim()))) continue;

                    var targets = filter.Split(',').Select(Resolve).OfType<FormKey>().Where(_entries.ContainsKey).ToList();
                    if (targets.Count == 0) continue;
                    IniLines++;

                    bool clear = Value("clear") is string c && (c.Equals("true", StringComparison.OrdinalIgnoreCase) || c.Equals("yes", StringComparison.OrdinalIgnoreCase));
                    var removes = (Value("removeFromLLs") ?? "").Split(',').Where(t => t.Trim().Length > 0).Select(t => t.Split('~')).ToList();
                    var adds = parts.Where(p => p.Key.Equals("addToLLs", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("addOnceToLLs", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(p => p.Value.Split(',').Where(t => t.Trim().Length > 0).Select(t => (Once: p.Key.Equals("addOnceToLLs", StringComparison.OrdinalIgnoreCase), Parts: t.Split('~'))))
                        .ToList();

                    foreach (var target in targets)
                    {
                        var entries = _entries[target];
                        if (clear) entries.Clear();
                        foreach (var remove in removes)
                        {
                            if (Resolve(remove[0]) is not FormKey form) continue;
                            string level = remove.Length > 1 ? remove[1].Trim() : "", count = remove.Length > 2 ? remove[2].Trim() : "";
                            entries.RemoveAll(e => e.Reference == form && Matches(level, e.Level) && Matches(count, e.Count));
                        }
                        foreach (var (once, add) in adds)
                        {
                            if (Resolve(add[0]) is not FormKey form) continue;
                            if (once && entries.Any(e => e.Reference == form)) continue;
                            short level = add.Length > 1 && short.TryParse(add[1].Trim(), out var l) ? l : (short)1;
                            short count = add.Length > 2 && short.TryParse(add[2].Trim(), out var n) ? n : (short)1;
                            entries.Add(new Entry(form, level, count));
                        }
                    }
                }
            }
        }

        // removeFromLLs level/count spec: empty or "none" matches any; else a number with an optional <, >, <=, >=
        private static bool Matches(string spec, short value)
        {
            if (spec.Length == 0 || spec.Equals("none", StringComparison.OrdinalIgnoreCase)) return true;
            string op = spec.StartsWith("<=") || spec.StartsWith(">=") ? spec.Substring(0, 2) : spec.StartsWith("<") || spec.StartsWith(">") ? spec.Substring(0, 1) : "";
            if (!int.TryParse(spec.Substring(op.Length).Trim(), out int n)) return true;
            return op switch { "<" => value < n, ">" => value > n, "<=" => value <= n, ">=" => value >= n, _ => value == n };
        }
    }
}
