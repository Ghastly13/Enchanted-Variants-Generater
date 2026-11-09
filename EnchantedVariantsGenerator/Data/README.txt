Enchanted Variants Generator - data folder

Settings
  Edit the patcher's settings on its Settings page in Synthesis. They are saved here as settings.json.
  An older config.hjson is imported into settings.json on the first run and renamed config.hjson.migrated.

Input
  Put the input files in an "input" folder here, one subfolder per group: every enchantment file in a subfolder
  applies to every weapon/armor file in that same subfolder. Files are .hjson (or .json) with RequiredMods,
  Weapons / Armors and Enchantments sections.

Discovered items
  Weapons and armor no input file names are found automatically and added to the folder for their type and tier
  (settings "Discover items: ..."). Each run writes "discovered items.txt" here: every item added, and every candidate
  left out with the reason. An input file naming an item always takes precedence over discovery.

Distributed items
  The variants are handed out beside their vanilla twins (setting "Distribute variants beside their vanilla twins").
  For each vanilla enchanted sublist a variant belongs in, such as SublistEnchArmorIronCuirass01 or
  SublistEnchIronSwordFire, the patcher makes <that list>_EVG holding the variants at the level the vanilla list gives
  the same tier, and adds it once to the vanilla list through
  SKSE\Plugins\SkyPatcher\leveledList\zz Enchanted Variants Generator\<output plugin>.ini. Anything that draws from the
  vanilla lists, Open World Loot included, then hands them out.
    Armor     the tier list for the base's material, weight class and slot (where its own vanilla variants are, if it
              has any), at the enchantment's tier or the nearest the material has.
    Weapons   the material and type's list for the enchantment's effect (Fire, Frost, ...). Where the material and type
              have no list for that effect, a stand-in named as vanilla would name it is added beside their other ones.
    Other weight class   armor in a weight class vanilla never enchants for its material (light Daedric, heavy Glass)
              follows that material's lists in the other class, placed in the matching lists for its own class; lists in
              between, such as the Daedric wrappers, are copied for the new class.
  The input files' LeveledLists entries now only give each enchantment's tier (the digits of its first non-NPC
  suffix). Each run writes "distributed items.txt" here: every list made and where it went, and every variant left
  out with the reason.
