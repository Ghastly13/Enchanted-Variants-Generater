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
  name they give it there. Armor in a weight class vanilla never makes for its material (heavy Elven, heavy Glass, light
  Daedric) is found by its material keyword all the same and follows that material in the other class: heavy Elven
  boots get light Elven boots' enchantments and go where those are, mirrored into the heavy lists. An armor's weight
  class is its ArmorHeavy/ArmorLight keyword, else its armor type (shields, vanilla's included, carry no such keyword).
  Its name must name its material: "Netch Leather" counts for Leather or Hide, as mods tag it either way, and Netch
  Leather tagged Hide (Mihail's shield) is taken as Leather. Ancient Nord weapons follow the Draugr loadout
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
  SKSE\Plugins\SkyPatcher\leveledList\zz Enchanted Variants Generator\zz\...\<output plugin>.ini. Anything that draws
  from the vanilla lists, Open World Loot included, then hands them out.
  The vanilla lists are read as the game holds them: after Paths of Empyrean's leveled-list INIs (SKSE\Plugins\POE\
  LeveledList, which it applies before SkyPatcher) and SkyPatcher's.
  SkyPatcher applies the INIs of a folder by how deeply they are nested, shallowest first (WACCF's
  leveledList\Weapons Armor Clothing and Clutter Fixes\... runs before OWL's leveledList\OWL\Main\...), then by path.
  So the patcher nests its INIs (this one, and its weapon and armor name INIs) in "zz" folders one level deeper than
  any other mod's INI in the same folder: they apply last, after every clear and removal, whatever is installed.
    Armor     the tier list for the base's material, weight class and slot (where its own vanilla variants are, if it
              has any), at the enchantment's tier or the nearest the material has.
    Weapons   the material and type's list for the enchantment's effect (Fire, Frost, ...). Where the material and type
              have no list for that effect, a stand-in named as vanilla would name it is added beside their other ones.
    Ancient Nord   (Draugr and honed) has no such lists: vanilla hands its frost variants out through the Draugr loadout
              lists (LItemDraugr02BossWeapon1HMagic, LItemDraugr05Weapon2HMagic, ...), one per Draugr rank and weapon group
              (one-handed, two-handed, bow), each holding the group's types on a ladder of levels by tier. Each variant
              goes in every list of its group whose ladder has its material and tier, at those levels, and the new list
              goes in as often as one vanilla weapon of each material does.
              Bows: the bow list is WACCF's (LItemDraugr02BossWeaponBowMagic). Without it, the patcher makes a copy,
              LItemDraugr02BossWeaponBowMagic_EVG: the vanilla Ancient Nord bow and supple bow with frost 1-3 on WACCF's
              ladder and flags, added once to LItemDraugr02WeaponBowSublist at level 16 and LItemDraugr05WeaponBowSublist
              at level 1, where Open World Loot's WACCF patch puts WACCF's.
    Shields a material has no lists for   vanilla enchants Leather armor but no Leather shields (likewise Scaled, Steel
              Plate, Gilded Elven). Where the load order has none either, the patcher makes stand-ins named as vanilla would
              (SublistEnchArmorLeatherShield01-03), one per tier of the material's cuirass lists, with the enchantments the
              load order puts on shields of the same weight class at that tier, and adds them to the shield lists the way the
              cuirass lists are in the cuirass lists: SublistEnchArmorLeatherCuirass01 is in LItemEnchArmorLightCuirass at
              level 7, so SublistEnchArmorLeatherShield01_EVG goes in LItemEnchArmorLightShield at level 7 (and the NoDragon,
              DLC2 and Open World Loot counterparts). Paths of Empyrean's missing shields add Leather shield lists
              (PoE_SublistEnchArmorLeatherShield01-03, in LItemEnchArmorLightShield at 1, 4 and 6): with it installed, those
              are used instead, and they and its other missing shields' lists (light Dwarven, Ebony, Orcish, Bonemold and
              Nordic, heavy Elven, Glass, Chitin and Stalhrim), which hold only the five vanilla shield enchantments per
              tier, also get every enchantment the game's shield lists of the same weight class have at their tier (about
              50 per kind instead of 15). They also go where the game puts the material's other armor: in each list
              holding one, at the level the material's cuirass list of the same tier has in the matching cuirass list.
              Paths of Empyrean puts its Leather shield lists at Hide's levels (1, 4, 6); the patcher's INI moves them to
              Leather's (7, 9, 11), removing them and adding them back. Shields whose material has no cuirass lists in
              that weight class (light Dwarven, Ebony, Orcish) keep its levels. Every move is listed in "distributed
              items.txt".
    Other weight class   armor in a weight class vanilla never enchants for its material (light Daedric, heavy Glass)
              follows that material's lists in the other class, placed in the matching lists for its own class; lists in
              between, such as the Daedric wrappers, are copied for the new class.
    Solstheim only   items of the plugins (or EditorIDs) in setting "Only in Dragonborn (DLC2) lists", by default Netch
              Leather: the Creation Club's (ccbgssse041-netchleather.esl) and Mihail's Netch Leather Armor
              (mihailnetcharmor.esp). Netch Leather shields use the Leather shield lists above; where those are only in the
              rest of Skyrim's lists (Paths of Empyrean's), they go in the Dragonborn counterparts (DLC2LItemEnchArmorLightShield)
              at the same levels. Netch Leather is Leather, whose lists the rest
              of Skyrim uses too, so its variants get lists of their own, DLC2SublistEnchArmorLeatherCuirass01_EVG and so
              on, added beside SublistEnchArmorLeatherCuirass01 only in the lists with DLC2 in their name that hold it:
              DLC2LItemEnchArmorLightCuirass and ...NoDragon at level 7, and Open World Loot's
              OWL_Special_DLC2LItemEnchArmorLightCuirassLow. Each such list is one entry beside the whole Leather list
              there, as Open World Loot's Netch Leather patch does for the unenchanted pieces. Where the twins' list is
              itself a DLC2 one (Chitin, Stalhrim), the variants go in it as usual.
  The input files' LeveledLists entries now only give each enchantment's tier (the digits of its first non-NPC
  suffix). Each run writes "distributed items.txt" here: every list made and where it went, and every variant left
  out with the reason.

V.A.E.R. - Visual Animated Enchants Reborn
  When VAEReborn.esp is in the load order (either edition: the Creation Club one or the Vanilla one, whichever MO2
  serves), setting "V.A.E.R.: keep its visuals against the load order" patches it. V.A.E.R. overrides records built
  against the base game and Creation Club only, so mods later in the load order (Thaumaturgy, Summermyst, CACO) erase
  its visuals, and where it wins, its copies undo fixes from mods before it (USSEP, ...). For every record it overrides,
  the patch writes the highest-priority version that is not V.A.E.R.'s with V.A.E.R.'s own changes on top:
    magic effects   its enchant art and enchant shader (and hit and casting visuals, should it set any)
    enchantments    its animated copies of the effects, swapped in where the original ones are, keeping magnitudes
    weapons         its enchantment
  Records where nothing would be lost are left alone. Every patched record is listed in the Synthesis log. Since the
  generated variants use the same enchantments, they get V.A.E.R.'s visuals too.
