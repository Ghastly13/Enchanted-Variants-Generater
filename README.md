# Enchanted Variants Generator

A [Synthesis](https://github.com/Mutagen-Modding/Synthesis) patcher for Skyrim Special Edition that generates enchanted
variants of weapons and armor and hands them out as loot. A fork of ExpensiveLadder's
[Enchanted Variants Generator](https://github.com/ExpensiveLadder/Enchanted-Variants-Generater).

## What it does

- **Finds the items and enchantments itself.** The base weapons and armor in the load order are found by material, type
  and slot, and each gets the enchantments the load order already puts on enchanted items of its kind, with the tier,
  charge and name it gives them there. No input files are needed; hand-written input files still work, as an option.
- **Covers what vanilla leaves out:** armor in a weight class vanilla never enchants for its material (heavy Elven, light
  Daedric, ...), shields of materials with no enchanted shields (Leather and Netch Leather included), and Ancient Nord
  weapons through the Draugr loadout lists.
- **Enchants modded jewelry.** Plain rings, necklaces and circlets that mods add (the Creation Club's Nordic Jewelry,
  ...) get every enchantment vanilla, Thaumaturgy and Summermyst put on enchanted jewelry of their slot ("Nordic Ring of
  Minor Destruction", "Jester's Nordic Ring"), handed out beside those items at the level their list gives them. The
  game's own jewelry is left as it is.
- **Makes no duplicates.** A variant that already exists in the load order is not made again, and base items whose model
  file is missing are left out.
- **Hands the variants out beside their vanilla twins.** For each vanilla enchanted list a variant belongs in, it makes a
  list of the variants at the same tiers and adds it to the vanilla list through SkyPatcher INIs that apply after every
  other mod's. Anything that draws from the vanilla lists, Open World Loot included, then hands them out. Netch Leather
  variants only go in Dragonborn (Solstheim) lists. Weapons a mod hands out in pairs (Animated Armoury's claws and left
  claws) go out in pairs with the same enchantment, among 4 enchanted daggers of that enchantment for each pair.
- **Names them the way the load order does,** SkyPatcher and Paths of Empyrean renames included, and can rename existing
  enchanted items to match.
- **Patches V.A.E.R. - Visual Animated Enchants Reborn** against the load order, so its visuals survive later mods.

Each run writes `discovered items.txt` and `distributed items.txt` to the patcher's data folder: every item and
enchantment found, every list made and where it went, and everything left out, with the reason.

## Requirements

- [SkyPatcher](https://www.nexusmods.com/skyrimspecialedition/mods/106659)
- [powerofthree's Tweaks](https://www.nexusmods.com/skyrimspecialedition/mods/51073) with Load EditorIDs on (its
  default): the INIs name forms by EditorID.

## Installing

In Synthesis, add a Git Repository patcher with the URL `https://github.com/Ghastly13/Enchanted-Variants-Generator` and
pick the `EnchantedVariantsGenerator` project.

The generated items and lists go in the Synthesis patch. The SkyPatcher INIs are written to
`Data\SKSE\Plugins\SkyPatcher\<type>\zz Enchanted Variants Generator\...`; under Mod Organizer they land in Overwrite (or
the mod Synthesis's new files go to).

## Settings

On the patcher's Settings page in Synthesis, each with a tooltip:

- **Items and enchantments:** detection from the load order, input files, and the materials, plugins and items to leave
  out.
- **Jewelry:** variants of modded jewelry, the plugins besides the game whose enchantments it gets (Thaumaturgy and
  Summermyst), and the most pieces a plugin may add before it is left out.
- **Distribution:** handing variants out beside their vanilla twins, skipped duplicates, the plugins whose items only
  go in Dragonborn lists, and how many twins go beside each pair of weapons handed out in pairs.
- **Names:** using SkyPatcher and Paths of Empyrean renames, matching enchantment names to the load order, and renaming
  existing enchanted items.
- **V.A.E.R.**, reusing records from earlier runs, and verbose logging.

[EnchantedVariantsGenerator/Data/README.txt](EnchantedVariantsGenerator/Data/README.txt) explains in detail how items
are found, which lists the variants go in and why.
