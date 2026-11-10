Enchanted Variants Generator - data folder

Settings
  Edit the patcher's settings on its Settings page in Synthesis. They are saved here as settings.json.
  An older config.hjson is imported into settings.json on the first run and renamed config.hjson.migrated.

Where items and enchantments come from
  Two settings, either or both: "Detect enchantments and items from the load order" (on by default) and "Use input
  files" (off by default). With both on, an item and enchantment pair is made once, detection first.

  Detection (no input files needed)
  The patcher finds the base weapons and armor in the load order by material, type and slot (settings "Items: ..."),
  and gives each the enchantments the load order already hands out on enchanted items of its kind, read from the
  vanilla enchanted lists its variants go in: an Iron sword (vanilla, Heavy Armory's, Animated Armoury's) gets every
  enchantment vanilla, Summermyst and Thaumaturgy put on the Iron swords in those lists, with the tier, charge and
  name they give it there. Light Daedric armor follows heavy Daedric. Ancient Nord weapons follow the Draugr loadout
  lists (frost; the honed greatsword's fire variants are in no list, only on Red Eagle and in Labyrinthian, so they
  are not used). A kind with no enchanted items in those lists (Imperial armor) gets nothing.

  Input files
  An "input" folder here, one subfolder per group: every enchantment file in a subfolder applies to every weapon/armor
  file in that same subfolder. Files are .hjson (or .json) with RequiredMods, Weapons / Armors and Enchantments
  sections. With "Use input files: add found items to their folders" on, items found in the load order that no input
  file names are also added to the folder for their type and tier.

  Each run writes "discovered items.txt" here: every kind with its detected enchantments (tier, charge, name), every
  item found and where it went, and every candidate left out with the reason.

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
    Ancient Nord   (Draugr and honed) has no such lists: vanilla hands its frost variants out through the Draugr loadout
              lists (LItemDraugr02BossWeapon1HMagic, LItemDraugr05Weapon2HMagic, ...), one per Draugr rank and weapon group
              (one-handed, two-handed, bow), each holding the group's types on a ladder of levels by tier. Each variant
              goes in every list of its group whose ladder has its material and tier, at those levels, and the new list
              goes in as often as one vanilla weapon of each material does.
    Other weight class   armor in a weight class vanilla never enchants for its material (light Daedric, heavy Glass)
              follows that material's lists in the other class, placed in the matching lists for its own class; lists in
              between, such as the Daedric wrappers, are copied for the new class.
  The input files' LeveledLists entries now only give each enchantment's tier (the digits of its first non-NPC
  suffix). Each run writes "distributed items.txt" here: every list made and where it went, and every variant left
  out with the reason.
