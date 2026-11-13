using System;
using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Synthesis;

namespace EnchantedVariantsGenerator
{
    // V.A.E.R. - Visual Animated Enchants Reborn (VAEReborn.esp; the Creation Club and the Vanilla edition share the name)
    // gives enchanted weapons animated visuals by overriding records, built against the base game and Creation Club only:
    //   magic effects   its enchant art and enchant shader (and any hit or casting visuals it sets)
    //   enchantments    unique weapons' effects swapped for its own animated copies (Nightingale, Molag Bal, ...)
    //   weapons         unique weapons given its own enchantment (the Rueful Axe, the Crusader's weapons, ...)
    // Mods later in the load order (Thaumaturgy, Summermyst, CACO) override the same magic effects and win, so the visuals
    // are lost; where VAER wins, its copies undo what mods before it changed (USSEP's fixes, ...). For each record VAER
    // overrides, this writes the highest-priority version that is not VAER's with VAER's own changes (against the plugin
    // that defines the record) applied on top. Records where nothing is lost either way are left alone.
    public static class VaerPatch
    {
        private static readonly ModKey Vaer = ModKey.FromNameAndExtension("VAEReborn.esp");

        public static void Run(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, Config config)
        {
            if (!config.PatchVaer) return;
            if (!state.LoadOrder.ContainsKey(Vaer) || !state.LoadOrder[Vaer].Enabled || state.LoadOrder[Vaer].Mod is not { } vaer)
            {
                Console.WriteLine("V.A.E.R.: VAEReborn.esp is not in the load order; nothing to patch");
                return;
            }

            var lines = new List<string>();
            int magicEffects = 0, enchantments = 0, weapons = 0;

            foreach (var own in vaer.MagicEffects.Where(r => r.FormKey.ModKey != Vaer))
            {
                var (baseRecord, original, vaerWins, baseFrom) = Stack<IMagicEffect, IMagicEffectGetter>(linkCache, own.FormKey);
                if (baseRecord == null || original == null) continue;
                // VAER's visual fields that differ from the defining plugin's
                var changes = new List<(string Field, FormKey? Value, Func<IMagicEffectGetter, FormKey?> Get, Action<MagicEffect, FormKey?> Set)>();
                void Field(string name, Func<IMagicEffectGetter, FormKey?> get, Action<MagicEffect, FormKey?> set)
                {
                    if (get(own) != get(original)) changes.Add((name, get(own), get, set));
                }
                Field("EnchantArt", m => m.EnchantArt.FormKeyNullable, (m, v) => m.EnchantArt.SetTo(v));
                Field("EnchantShader", m => m.EnchantShader.FormKeyNullable, (m, v) => m.EnchantShader.SetTo(v));
                Field("HitShader", m => m.HitShader.FormKeyNullable, (m, v) => m.HitShader.SetTo(v));
                Field("HitEffectArt", m => m.HitEffectArt.FormKeyNullable, (m, v) => m.HitEffectArt.SetTo(v));
                Field("CastingArt", m => m.CastingArt.FormKeyNullable, (m, v) => m.CastingArt.SetTo(v));
                if (changes.Count == 0) continue;
                bool lost = changes.Any(c => c.Get(baseRecord) != c.Value);
                if (vaerWins && baseFrom == original.FormKey.ModKey) continue;   // VAER's own copy undoes nothing
                if (!vaerWins && !lost) continue;                               // the winner already has the visuals

                var merged = (MagicEffect)baseRecord.DeepCopy();
                foreach (var c in changes) c.Set(merged, c.Value);
                state.PatchMod.MagicEffects.Set(merged);
                magicEffects++;
                lines.Add(Describe("magic effect", own, vaerWins, baseFrom, string.Join(", ", changes.Select(c => c.Field))));
            }

            foreach (var own in vaer.ObjectEffects.Where(r => r.FormKey.ModKey != Vaer))
            {
                var (baseRecord, original, vaerWins, baseFrom) = Stack<IObjectEffect, IObjectEffectGetter>(linkCache, own.FormKey);
                if (baseRecord == null || original == null) continue;
                // The base effects VAER swapped, position by position against the defining plugin's
                var ownEffects = own.Effects ?? (IReadOnlyList<IEffectGetter>)Array.Empty<IEffectGetter>();
                var originalEffects = original.Effects ?? (IReadOnlyList<IEffectGetter>)Array.Empty<IEffectGetter>();
                if (ownEffects.Count != originalEffects.Count)
                {
                    lines.Add("  skipped enchantment " + own.EditorID + " (" + own.FormKey + "): VAER's has " + ownEffects.Count + " effects, the original " + originalEffects.Count);
                    continue;
                }
                var swaps = new Dictionary<FormKey, FormKey>();
                for (int i = 0; i < ownEffects.Count; i++)
                {
                    var from = originalEffects[i].BaseEffect.FormKeyNullable;
                    var to = ownEffects[i].BaseEffect.FormKeyNullable;
                    if (from != null && to != null && from != to) swaps[from.Value] = to.Value;
                }
                if (swaps.Count == 0) continue;
                var baseEffects = baseRecord.Effects ?? (IReadOnlyList<IEffectGetter>)Array.Empty<IEffectGetter>();
                bool lost = baseEffects.Any(e => e.BaseEffect.FormKeyNullable is FormKey fk && swaps.ContainsKey(fk));
                if (vaerWins && baseFrom == original.FormKey.ModKey) continue;
                if (!vaerWins && !lost) continue;

                var merged = (ObjectEffect)baseRecord.DeepCopy();
                foreach (var effect in merged.Effects)
                {
                    if (effect.BaseEffect.FormKeyNullable is FormKey fk && swaps.TryGetValue(fk, out var to)) effect.BaseEffect.SetTo(to);
                }
                state.PatchMod.ObjectEffects.Set(merged);
                enchantments++;
                lines.Add(Describe("enchantment", own, vaerWins, baseFrom, swaps.Count + " effect(s) swapped for VAER's"));
            }

            foreach (var own in vaer.Weapons.Where(r => r.FormKey.ModKey != Vaer))
            {
                var (baseRecord, original, vaerWins, baseFrom) = Stack<IWeapon, IWeaponGetter>(linkCache, own.FormKey);
                if (baseRecord == null || original == null) continue;
                var enchantment = own.ObjectEffect.FormKeyNullable;
                if (enchantment == original.ObjectEffect.FormKeyNullable) continue;
                bool lost = baseRecord.ObjectEffect.FormKeyNullable != enchantment;
                if (vaerWins && baseFrom == original.FormKey.ModKey) continue;
                if (!vaerWins && !lost) continue;

                var merged = (Weapon)baseRecord.DeepCopy();
                merged.ObjectEffect.SetTo(enchantment);
                state.PatchMod.Weapons.Set(merged);
                weapons++;
                lines.Add(Describe("weapon", own, vaerWins, baseFrom, "enchantment " + enchantment));
            }

            Console.WriteLine("V.A.E.R.: " + vaer.ModKey.FileName + " (" + state.LoadOrder[Vaer].Mod!.ModHeader.MasterReferences.Count + " masters) — patched "
                + magicEffects + " magic effects, " + enchantments + " enchantments and " + weapons + " weapons");
            foreach (var line in lines) Console.WriteLine(line);
        }

        private static string Describe(string kind, IMajorRecordGetter own, bool vaerWins, ModKey baseFrom, string what) =>
            "  " + kind + " " + own.EditorID + " (" + own.FormKey + "): " + what + (vaerWins
                ? ", keeping " + baseFrom.FileName + "'s changes VAER's copy would undo"
                : ", over " + baseFrom.FileName + ", which wins it");

        // The record's providers: the highest-priority version that is not VAER's (the base to patch), the defining
        // plugin's version, whether VAER's copy is the winner, and which plugin the base comes from
        private static (TGetter? Base, TGetter? Original, bool VaerWins, ModKey BaseFrom) Stack<TSetter, TGetter>(ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, FormKey formKey)
            where TSetter : class, IMajorRecord, TGetter
            where TGetter : class, IMajorRecordGetter
        {
            var contexts = linkCache.ResolveAllContexts<TSetter, TGetter>(formKey).ToList();
            if (contexts.Count < 2) return (null, null, false, default);
            var baseContext = contexts.FirstOrDefault(c => c.ModKey != Vaer);
            if (baseContext == null) return (null, null, false, default);
            return (baseContext.Record, contexts[^1].Record, contexts[0].ModKey == Vaer, baseContext.ModKey);
        }
    }
}
