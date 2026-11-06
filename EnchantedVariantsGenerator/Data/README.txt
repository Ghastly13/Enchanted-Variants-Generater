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
