using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Synthesis;
using Mutagen.Bethesda.Skyrim;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Hjson;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache.Internals.Implementations;
using Mutagen.Bethesda.Plugins.Records;

namespace EnchantedVariantsGenerator
{
    public class UnsafeThreadStuff
    {
        public List<Weapon> WeaponsToSet { get; set; } = new();
        public List<Armor> ArmorToSet { get; set; } = new();
        public List<LeveledItem> LeveledListsToSet { get; set; } = new();
        public List<GeneratedWeapon> GeneratedWeapons { get; set; } = new();
    }

    public class LeveledListInfo
    {
        public string LeveledListPrefix { get; set; } = "SublistEnch_";
        public string LeveledListSuffix { get; set; } = "";
        public string Mode { get; set; } = "Add";
    }

    public class EnchantmentInfo
    {
        public IObjectEffectGetter Enchantment { get; set; }
        public string EditorID { get; set; }
        public string? Prefix { get; set; }
        public string? Suffix { get; set; }
        public string? Sublist { get; set; }
        public ushort? EnchantmentAmount { get; set; }
        public List<LeveledListInfo>? LeveledLists { get; set; }

        public EnchantmentInfo(string editorID, IObjectEffectGetter enchantment)
        {
            Enchantment = enchantment;
            EditorID = editorID;
        }
    }

    public record ExistingVariant(FormKey FormKey, string? EditorID);

    // Enchanted variants that already exist in the load order, keyed by (base item, enchantment).
    public class DuplicateIndex
    {
        private readonly Dictionary<(FormKey BaseItem, FormKey Enchantment), List<ExistingVariant>> _variants = new();
        private int _skipped;

        public int Count { get; private set; }
        public int Skipped => _skipped;

        public void Add(FormKey baseItem, FormKey enchantment, ExistingVariant variant)
        {
            if (!_variants.TryGetValue((baseItem, enchantment), out var list))
            {
                list = new List<ExistingVariant>();
                _variants[(baseItem, enchantment)] = list;
            }
            list.Add(variant);
            Count++;
        }

        // Ignores a record carrying the EditorID this patcher would generate: that is its own output from an earlier run.
        public bool TryFind(FormKey baseItem, FormKey enchantment, string generatedEditorID, [NotNullWhen(true)] out ExistingVariant? found)
        {
            found = null;
            if (!_variants.TryGetValue((baseItem, enchantment), out var list)) return false;
            foreach (var variant in list)
            {
                if (variant.EditorID != generatedEditorID)
                {
                    found = variant;
                    return true;
                }
            }
            return false;
        }

        public void RecordSkip() => Interlocked.Increment(ref _skipped);
    }

    public class InputThing
    {
        //                EditorID, Info
        public Dictionary<string, EnchantmentInfo> Enchantments { get; } = new();
        public Dictionary<string, ItemJSON> Weapons { get; } = new();
        public Dictionary<string, ItemJSON> Armors { get; } = new();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "<Pending>")]
    public class Program
    {
        // Settings edited on the patcher's Settings page in Synthesis, saved as settings.json in its data folder
        private const string SettingsFile = "settings.json";
        private static Lazy<Config> _settings = new(() => new Config());

        // The patcher's settings. An old config.hjson with no settings.json yet is imported once: written out as
        // settings.json, so Synthesis's Settings page shows those values, and renamed config.hjson.migrated so it is
        // clear it is no longer read.
        public static Config LoadSettings(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, string dataPath)
        {
            string settingsFolder = state.ExtraSettingsDataPath?.ToString() ?? dataPath;
            string settingsPath = Path.Combine(settingsFolder, SettingsFile);
            string legacyPath = Path.Combine(dataPath, "config.hjson");
            if (!File.Exists(settingsPath) && File.Exists(legacyPath))
            {
                var imported = GetConfig(legacyPath);
                Directory.CreateDirectory(settingsFolder);
                File.WriteAllText(settingsPath, JsonConvert.SerializeObject(imported, Formatting.Indented, new StringEnumConverter()));
                File.Move(legacyPath, legacyPath + ".migrated", true);
                Console.WriteLine("Imported settings from " + legacyPath + " into " + settingsPath
                    + "; edit them on the patcher's Settings page in Synthesis. The old file is now config.hjson.migrated");
                return imported;
            }
            return _settings.Value;
        }

        public static Config GetConfig(string filePath)
        {
            Config? config = JsonConvert.DeserializeObject<Config>(HjsonValue.Load(filePath).ToString());
            if (config == null) throw new Exception("config does not exist");

            if (config.CheckExistingGenerated) Console.WriteLine("CheckExistingGenerated = true");

            return config;
        }

        public static string ReadInputFile(string filePath)
        {
            string rawJSON;
            if (filePath.EndsWith(".json"))
            {
                rawJSON = File.ReadAllText(filePath);
            }
            else if (filePath.EndsWith(".hjson"))
            {
                rawJSON = HjsonValue.Load(filePath).ToString();
            }
            else
            {
                throw new Exception("Unknown file in Input directory: \"" + filePath + "\"");
            }
            return rawJSON;
        }

        public static List<InputThing> GetInputs(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, Config config, string dataPath)
        {
            Console.WriteLine("Reading input files");
            List<string> enabledMods = new();
            foreach (var mod in state.LoadOrder)
            {
                if (mod.Value.Enabled)
                {
                    if (config.VerboseLogging) Console.WriteLine("enabledmod: " + mod.Value.ModKey.ToString());
                    enabledMods.Add(mod.Value.ModKey.ToString());
                }
            }
            List<InputThing> inputs = new();
            foreach (var folder in Directory.EnumerateDirectories(Path.Combine(dataPath, "input")))
            {
                Console.WriteLine("Reading folder: " + folder.Replace(dataPath, ""));
                InputThing input = new();

                /* TODO
                var files = new SortedDictionary<int, InputJSON>();
                foreach (var file in Directory.GetFiles(folder))
                {
                    string rawJSON = ReadInputFile(file);
                    var inputJSON = JsonConvert.DeserializeObject<InputJSON>(rawJSON);
                    if (inputJSON == null) throw new Exception("Cannot read file \"" + file + "\"!");
                    if (inputJSON.RequiredMods != null && !CheckRequiredMods(enabledMods, inputJSON.RequiredMods)) continue;
                    files.Add(inputJSON.PriorityOrder, inputJSON);
                }
                */

                var files = new List<InputJSON>();
                foreach (var file in Directory.GetFiles(folder))
                {
                    string rawJSON = ReadInputFile(file);
                    Console.WriteLine("Reading input file \"" + file.Replace(dataPath, "")  + "\"");
                    var inputJSON = JsonConvert.DeserializeObject<InputJSON>(rawJSON);
                    if (inputJSON == null) throw new Exception("Cannot read file \"" + file + "\"!");

                    if (inputJSON.RequiredMods != null && !CheckRequiredMods(enabledMods, inputJSON.RequiredMods)) continue;

                    files.Add(inputJSON);
                }

                foreach (var inputJSON in files)
                {
                    if (inputJSON.Enchantments != null)
                    {
                        foreach (var item in inputJSON.Enchantments)
                        {
                            if (string.IsNullOrEmpty(item.EditorID) || string.IsNullOrEmpty(item.FormKey)) throw new Exception("ERROR: enchantment does not have a formkey or editorID specified");
                            if (input.Enchantments.ContainsKey(item.EditorID))
                            {
                                //TODO
                            }
                            else
                            {
                                IObjectEffectGetter enchantment;
                                if (state.LinkCache.TryResolve<IObjectEffectGetter>(FormKey.Factory(item.FormKey), out var enchant))
                                {
                                    enchantment = enchant;
                                }
                                else
                                {
                                    throw new Exception("Cannot find enchantment with FormKey \"" + item.FormKey.ToString() + "\"");
                                }

                                var enchantmentGetter = new EnchantmentInfo(item.EditorID, enchantment)
                                {
                                    EnchantmentAmount = (ushort?)item.EnchantmentAmount,
                                    Prefix = item.Prefix,
                                    Suffix = item.Suffix,
                                    LeveledLists = new List<LeveledListInfo>()
                                };
                                if (item.LeveledLists != null)
                                {
                                    foreach (var leveledlistJSON in item.LeveledLists)
                                    {
                                        enchantmentGetter.LeveledLists.Add(new LeveledListInfo()
                                        {
                                            LeveledListPrefix = leveledlistJSON.LeveledListPrefix,
                                            LeveledListSuffix = leveledlistJSON.LeveledListSuffix,
                                            Mode = leveledlistJSON.Mode
                                        });
                                    }
                                }

                                // Set Overwrites
                                if (item.Overwrites != null)
                                {
                                    foreach (var overwrite in item.Overwrites)
                                    {
                                        if (overwrite.RequiredMods != null && !CheckRequiredMods(enabledMods, overwrite.RequiredMods)) continue;

                                        if (overwrite.Prefix != null)
                                        {
                                            enchantmentGetter.Prefix = overwrite.Prefix;
                                        }
                                        if (overwrite.Suffix != null)
                                        {
                                            enchantmentGetter.Suffix = overwrite.Suffix;
                                        }
                                        if (overwrite.EnchantmentAmount != null)
                                        {
                                            enchantmentGetter.EnchantmentAmount = (ushort?)overwrite.EnchantmentAmount;
                                        }
                                        if (overwrite.LeveledLists != null)
                                        {
                                            foreach (var leveledlist in overwrite.LeveledLists)
                                            {
                                                if (leveledlist.Mode == "Remove")
                                                {
                                                    foreach (var oldleveledlist in enchantmentGetter.LeveledLists)
                                                    {
                                                        if (oldleveledlist.Mode == "Add" && oldleveledlist.LeveledListPrefix == leveledlist.LeveledListPrefix && oldleveledlist.LeveledListSuffix == leveledlist.LeveledListSuffix)
                                                        {
                                                            enchantmentGetter.LeveledLists.Remove(oldleveledlist);
                                                            break;
                                                        }
                                                    }
                                                }
                                                enchantmentGetter.LeveledLists.Add(new LeveledListInfo()
                                                {
                                                    LeveledListPrefix = leveledlist.LeveledListPrefix,
                                                    LeveledListSuffix = leveledlist.LeveledListSuffix,
                                                    Mode = leveledlist.Mode
                                                });
                                            }
                                        }
                                    }
                                }

                                input.Enchantments.Add(item.EditorID, enchantmentGetter);
                            }

                            if (config.VerboseLogging) Console.WriteLine("adding enchantment: " + item.EditorID);
                        }
                    }
                    if (inputJSON.Weapons != null)
                    {
                        foreach (var item in inputJSON.Weapons)
                        {
                            if (string.IsNullOrEmpty(item.EditorID) || string.IsNullOrEmpty(item.FormKey)) throw new Exception("ERROR: weapon does not have a formkey or editorID specified");

                            if (item.BlacklistedMods != null && CheckRequiredMods(enabledMods, item.BlacklistedMods))
                            {
                                if (config.VerboseLogging) Console.WriteLine("Blacklisted mod detected! Skipping item: " + item.EditorID);
                                continue;
                            }
                            else
                            {
                                input.Weapons.Add(item.EditorID, item);
                                if (config.VerboseLogging) Console.WriteLine("adding weapon: " + item.EditorID);
                            }
                            /*
                            if (input.Enchantments.ContainsKey(item.EditorID))
                            {
                                //TODO
                            }
                            else
                            {
                            }
                            */
                        }
                    }
                    if (inputJSON.Armors != null)
                    {
                        foreach (var item in inputJSON.Armors)
                        {
                            if (string.IsNullOrEmpty(item.EditorID) || string.IsNullOrEmpty(item.FormKey)) throw new Exception("ERROR: armor does not have a formkey or editorID specified");
                            if (item.BlacklistedMods != null && CheckRequiredMods(enabledMods, item.BlacklistedMods))
                            {
                                if (config.VerboseLogging) Console.WriteLine("Blacklisted mod detected! Skipping item: " + item.EditorID);
                                continue;
                            }
                            else
                            {
                                input.Armors.Add(item.EditorID, item);
                                if (config.VerboseLogging) Console.WriteLine("adding armor: " + item.EditorID);
                            }
                            /*
                            if (input.Enchantments.ContainsKey(item.EditorID))
                            {
                                //TODO
                            }
                            else
                            {
                            }
                            */
                        }
                    }
                }
                inputs.Add(input);
            }

            return inputs;
        }

        public static LeveledItemEntry CreateLeveledItemEntry(short level, FormKey reference)
        {
            var leveledItemEntry = new LeveledItemEntry
            {
                Data = new LeveledItemEntryData()
                {
                    Count = 1,
                    Level = level,
                }
            };
            leveledItemEntry.Data.Reference.SetTo(reference);
            return leveledItemEntry;
        }

        public static void AddToLeveledList(LeveledItem leveledlist, FormKey item)
        {
            if (leveledlist.Entries == null)
            {
                leveledlist.Entries = new Noggog.ExtendedList<LeveledItemEntry>();
            }
            leveledlist.Entries.Add(CreateLeveledItemEntry(1, item));
        }

        public static bool CheckRequiredMods(List<string> enabledMods, string[] requiredMods)
        {
            foreach (var requiredMod in requiredMods)
            {
                if (enabledMods.Contains(requiredMod))
                {
                    return true;
                }
            }
            return false;
        }

        public static LeveledItem GetLeveledList(ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, ISkyrimMod patchMod, string editorID, bool checkExistingGenerated, out bool alreadyExists)
        {
            LeveledItem leveledlist;

            if (checkExistingGenerated && linkCache.TryResolve<ILeveledItemGetter>(editorID, out var leveledlist_Original))
            { // Get Leveled List if it already exists
                alreadyExists = true;
                // Console.WriteLine("Leveled List \"" + editorID + "\" already exists in plugin \"" + leveledlist_Original.FormKey.ModKey.ToString() + "\", copying as override and appending changes");
                //leveledlist = linkCache.Resolve<ILeveledItemGetter>(leveledlist_Original.FormKey).DeepCopy();
                leveledlist = leveledlist_Original.DeepCopy();
            }
            else
            { // Create Leveled List
                alreadyExists = false;
                leveledlist = new LeveledItem(patchMod, editorID);
                leveledlist.Flags |= LeveledItem.Flag.CalculateForEachItemInCount;
                leveledlist.Flags |= LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer;
                leveledlist.Entries = new Noggog.ExtendedList<LeveledItemEntry>();
            }
            return leveledlist;
        }

        public static readonly Weapon.TranslationMask weapontranslationmark = new(defaultOn: false)
        {
            ObjectEffect = true,
            EnchantmentAmount = true,
            Name = true,
            BasicStats = true,
            VirtualMachineAdapter = true,
            Template = true,
            FormVersion = true,
        };

        public static Weapon GetEnchantedWeapon(ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, ISkyrimMod patchMod, string editorID, bool checkExistingGenerated, out bool alreadyExists, IFormLinkNullable<IObjectEffectGetter> enchantment, IFormLinkNullable<IWeaponGetter> template)
        {
            Weapon weapon;

            if (checkExistingGenerated && linkCache.TryResolve<IWeaponGetter>(editorID, out var weapon_Original))
            { // Get Enchanted Weapon if it already exists
                alreadyExists = true;
                weapon = weapon_Original.DeepCopy(weapontranslationmark);
            }
            else
            { // Create Enchanted Weapon
                alreadyExists = false;
                weapon = new Weapon(patchMod, editorID)
                {
                    ObjectEffect = enchantment,
                    Template = template
                };
            }
            return weapon;
        }

        public static readonly Armor.TranslationMask armortranslationmark = new(defaultOn: false)
        {
            ObjectEffect = true,
            EnchantmentAmount = true,
            VirtualMachineAdapter = true,
            Name = true,
            Value = true,
            TemplateArmor = true,
            FormVersion = true,
            WorldModel = new GenderedItem<ArmorModel.TranslationMask>(new ArmorModel.TranslationMask(defaultOn: false) { Model = new Model.TranslationMask(false) { } }, new ArmorModel.TranslationMask(defaultOn: false) { Model = new Model.TranslationMask(false) { } })
        };

        public static Armor GetEnchantedArmor(ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, ISkyrimMod patchMod, string editorID, bool checkExistingGenerated, out bool alreadyExists, IFormLinkNullable<IObjectEffectGetter> enchantment, IFormLinkNullable<IArmorGetter> template)
        {
            Armor armor;

            if (checkExistingGenerated && linkCache.TryResolve<IArmorGetter>(editorID, out var armor_Original))
            { // Get Enchanted Weapon if it already exists
                alreadyExists = true;
                armor = armor_Original.DeepCopy(armortranslationmark);
            }
            else
            { // Create Enchanted Weapo
                alreadyExists = false;
                armor = new Armor(patchMod, editorID)
                {
                    EditorID = editorID,
                    ObjectEffect = enchantment,
                    TemplateArmor = template
                };
            }
            return armor;
        }

        public static UnsafeThreadStuff GenerateWeapon(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, Config config, InputThing input, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, DuplicateIndex duplicates)
        {
            UnsafeThreadStuff stuff = new();
            // When Open World Loot distributes the generated weapons, the input files' weapon leveled lists are not needed
            bool owlDistributes = OwlIntegration.IsActive(state, config);
            foreach (var item in input.Weapons.Values)
            {
                if (config.VerboseLogging) Console.WriteLine("Processing Weapon: " + item.EditorID);
                IWeaponGetter itemGetter;
                if (linkCache.TryResolve<IWeaponGetter>(FormKey.Factory(item.FormKey), out var output))
                { // Get template item
                    itemGetter = output;
                }
                else
                {
                    throw new Exception("Could not find Weapon:" + item.EditorID + " : " + item.FormKey);
                }
                if (!AssetIndex.HasModel(itemGetter)) continue;

                Dictionary<string, LeveledItem> leveledlists = new();

                foreach (var enchantmentInfo in input.Enchantments.Values)
                {
                    var enchanted_item_EditorID = "Ench_" + item.EditorID + "_" + enchantmentInfo.EditorID;

                    if (config.SkipDuplicates && duplicates.TryFind(itemGetter.FormKey, enchantmentInfo.Enchantment.FormKey, enchanted_item_EditorID, out var duplicate))
                    { // An enchanted variant of this item with this enchantment already exists: don't make another
                        duplicates.RecordSkip();
                        if (config.VerboseLogging) Console.WriteLine("Skipping duplicate \"" + enchanted_item_EditorID + "\": already exists as \"" + duplicate.EditorID + "\" (" + duplicate.FormKey + ")");
                        if (config.AddDuplicatesToLeveledLists && !owlDistributes)
                        {
                            UpdateLeveledLists(state, config, linkCache, stuff, leveledlists, enchantmentInfo, item.EditorID!, duplicate.FormKey, duplicate.EditorID ?? duplicate.FormKey.ToString());
                        }
                        continue;
                    }
                    var enchanted_item = GetEnchantedWeapon(linkCache, state.PatchMod, enchanted_item_EditorID, config.CheckExistingGenerated, out var EnchantedItemAlreadyExists, enchantmentInfo.Enchantment.ToNullableLink(), itemGetter.ToNullableLink());
                    var enchanted_item_name = ItemNames.Compose(itemGetter.FormKey, itemGetter.Name?.String, enchantmentInfo);

                    if (!EnchantedItemAlreadyExists)
                    { // Create Enchanted Item

                        enchanted_item.Name = enchanted_item_name;
                        enchanted_item.EnchantmentAmount = enchantmentInfo.EnchantmentAmount;

                        // Set Value
                        enchanted_item.BasicStats = new();
                        if (item.Value != null)
                        {
                            enchanted_item.BasicStats.Value = (uint)item.Value;
                        }
                        else
                        {
                            if (itemGetter.BasicStats != null)
                            {
                                enchanted_item.BasicStats.Value = itemGetter.BasicStats.Value;
                            }
                        }

                        Console.WriteLine("Generating weapon \"" + enchanted_item_EditorID + "\"");
                        stuff.WeaponsToSet.Add(enchanted_item);
                    }
                    else
                    {
                        bool copyAsOverride = false;

                        // Set Value
                        if (enchanted_item.BasicStats == null)
                        {
                            if (item.Value != null)
                            {
                                enchanted_item.BasicStats = new();
                                enchanted_item.BasicStats.Value = (uint)item.Value;
                                copyAsOverride = true;
                            }
                        }
                        else
                        {
                            if (item.Value != null && enchanted_item.BasicStats.Value != item.Value)
                            {
                                enchanted_item.BasicStats.Value = (uint)item.Value;
                                copyAsOverride = true;
                            }
                            else if (itemGetter.BasicStats != null && enchanted_item.BasicStats.Value != itemGetter.BasicStats.Value)
                            {
                                enchanted_item.BasicStats.Value = itemGetter.BasicStats.Value;
                                copyAsOverride = true;
                            }
                        }

                        // Set Enchantment Amount
                        if (enchanted_item.EnchantmentAmount != enchantmentInfo.EnchantmentAmount)
                        {
                            enchanted_item.EnchantmentAmount = enchantmentInfo.EnchantmentAmount;
                            copyAsOverride = true;
                        }

                        // Set Name
                        if (enchanted_item.Name != enchanted_item_name)
                        {
                            Console.WriteLine("Renaming weapon: " + enchanted_item.Name + " to: " + enchanted_item_name);
                            enchanted_item.Name = enchanted_item_name;
                            copyAsOverride = true;
                        }

                        //Set Scripts
                        if (item.SetScripts)
                        {
                            if (enchanted_item.VirtualMachineAdapter == null)
                            {
                                if (itemGetter.VirtualMachineAdapter != null)
                                {
                                    enchanted_item.VirtualMachineAdapter = itemGetter.VirtualMachineAdapter.DeepCopy();
                                    copyAsOverride = true;
                                }
                            } else if (itemGetter.VirtualMachineAdapter == null)
                            {
                                if (enchanted_item.VirtualMachineAdapter != null)
                                {
                                    enchanted_item.VirtualMachineAdapter = null;
                                    copyAsOverride = true;
                                }
                            } else
                            {
                                var equalsmask = enchanted_item.VirtualMachineAdapter.GetEqualsMask(itemGetter.VirtualMachineAdapter);
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                                if (!equalsmask.Scripts.Overall)
                                {
                                    enchanted_item.VirtualMachineAdapter = itemGetter.VirtualMachineAdapter.DeepCopy();
                                    copyAsOverride = true;
                                }
#pragma warning restore CS8602 // Dereference of a possibly null reference.
                            }
                        }

                        if (copyAsOverride)
                        {
                            enchanted_item.EditorID = enchanted_item_EditorID;
                            Console.WriteLine("Altering " + enchanted_item_EditorID);
                            stuff.WeaponsToSet.Add(enchanted_item);
                        }
                    }

                    // Only variants this patcher generates; pre-existing duplicates are left to the mod that made them
                    stuff.GeneratedWeapons.Add(new GeneratedWeapon(enchanted_item.FormKey, itemGetter, enchantmentInfo));

                    // Set Leveled Lists
                    if (!owlDistributes)
                    {
                        UpdateLeveledLists(state, config, linkCache, stuff, leveledlists, enchantmentInfo, item.EditorID!, enchanted_item.FormKey, enchanted_item_EditorID);
                    }
                }
            }
            return stuff;
        }

        public static UnsafeThreadStuff GenerateArmor(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, Config config, InputThing input, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, DuplicateIndex duplicates)
        {
            UnsafeThreadStuff stuff = new();
            foreach (var item in input.Armors.Values)
            {
                if (config.VerboseLogging) Console.WriteLine("Processing Armor: " + item.EditorID);
                IArmorGetter itemGetter;
                if (state.LinkCache.TryResolve<IArmorGetter>(FormKey.Factory(item.FormKey), out var output))
                { // Get template item
                    itemGetter = output;
                }
                else
                {
                    throw new Exception("Could not find Armor:" + item.EditorID + " : " + item.FormKey);
                }
                if (!AssetIndex.HasModel(itemGetter)) continue;

                Dictionary<string, LeveledItem> leveledlists = new();

                foreach (var enchantmentInfo in input.Enchantments.Values)
                {
                    var enchanted_item_EditorID = "Ench_" + item.EditorID + "_" + enchantmentInfo.EditorID;

                    if (config.SkipDuplicates && duplicates.TryFind(itemGetter.FormKey, enchantmentInfo.Enchantment.FormKey, enchanted_item_EditorID, out var duplicate))
                    { // An enchanted variant of this item with this enchantment already exists: don't make another
                        duplicates.RecordSkip();
                        if (config.VerboseLogging) Console.WriteLine("Skipping duplicate \"" + enchanted_item_EditorID + "\": already exists as \"" + duplicate.EditorID + "\" (" + duplicate.FormKey + ")");
                        if (config.AddDuplicatesToLeveledLists)
                        {
                            UpdateLeveledLists(state, config, linkCache, stuff, leveledlists, enchantmentInfo, item.EditorID!, duplicate.FormKey, duplicate.EditorID ?? duplicate.FormKey.ToString());
                        }
                        continue;
                    }
                    var enchanted_item = GetEnchantedArmor(linkCache, state.PatchMod, enchanted_item_EditorID, config.CheckExistingGenerated, out var EnchantedItemAlreadyExists, enchantmentInfo.Enchantment.ToNullableLink(), itemGetter.ToNullableLink());
                    var enchanted_item_name = ItemNames.Compose(itemGetter.FormKey, itemGetter.Name?.String, enchantmentInfo);

                    if (!EnchantedItemAlreadyExists)
                    { // Create Enchanted Item

                        enchanted_item.Name = enchanted_item_name;
                        enchanted_item.EnchantmentAmount = enchantmentInfo.EnchantmentAmount;

                        // Set Value
                        if (item.Value != null)
                        {
                            enchanted_item.Value = (uint)item.Value;
                        }
                        else
                        {
                            enchanted_item.Value = itemGetter.Value;
                        }

                        Console.WriteLine("Generating armor \"" + enchanted_item_EditorID + "\"");
                        stuff.ArmorToSet.Add(enchanted_item);
                    }
                    else
                    {
                        bool copyAsOverride = false;

                        // Set Value
                        if (item.Value != null)
                        {
                            if (enchanted_item.Value != item.Value)
                            {
                                enchanted_item.Value = (uint)item.Value;
                                copyAsOverride = true;
                            }
                        }
                        else
                        {
                            if (enchanted_item.Value != itemGetter.Value)
                            {
                                enchanted_item.Value = itemGetter.Value;
                                copyAsOverride = true;
                            }
                        }

                        // Set Name
                        if (enchanted_item.Name != null && enchanted_item.Name.ToString() != enchanted_item_name)
                        {
                            Console.WriteLine("Renaming armor: " + enchanted_item.Name.ToString() + " to: " + enchanted_item_name);
                            enchanted_item.Name = enchanted_item_name;
                            copyAsOverride = true;
                        }


                        //Set Scripts
                        if (item.SetScripts)
                        {
                            if (enchanted_item.VirtualMachineAdapter == null)
                            {
                                if (itemGetter.VirtualMachineAdapter != null)
                                {
                                    enchanted_item.VirtualMachineAdapter = itemGetter.VirtualMachineAdapter.DeepCopy();
                                    copyAsOverride = true;
                                }
                            }
                            else if (itemGetter.VirtualMachineAdapter == null)
                            {
                                if (enchanted_item.VirtualMachineAdapter != null)
                                {
                                    enchanted_item.VirtualMachineAdapter = null;
                                    copyAsOverride = true;
                                }
                            }
                            else
                            {
                                var equalsmask = enchanted_item.VirtualMachineAdapter.GetEqualsMask(itemGetter.VirtualMachineAdapter);
#pragma warning disable CS8602 // Dereference of a possibly null reference.
                                if (!equalsmask.Scripts.Overall)
                                {
                                    enchanted_item.VirtualMachineAdapter = itemGetter.VirtualMachineAdapter.DeepCopy();
                                    copyAsOverride = true;
                                }
#pragma warning restore CS8602 // Dereference of a possibly null reference.
                            }
                        }

                        if (copyAsOverride)
                        {
                            enchanted_item.EditorID = enchanted_item_EditorID;
                            Console.WriteLine("Altering " + enchanted_item_EditorID);
                            stuff.ArmorToSet.Add(enchanted_item);
                        }
                    }

                    // Set Leveled Lists
                    UpdateLeveledLists(state, config, linkCache, stuff, leveledlists, enchantmentInfo, item.EditorID!, enchanted_item.FormKey, enchanted_item_EditorID);
                }
            }
            return stuff;
        }

        // Adds (or, for Mode "Remove", removes) one enchanted item in the generated leveled lists of its base item.
        public static void UpdateLeveledLists(IPatcherState<ISkyrimMod, ISkyrimModGetter> state, Config config, ImmutableLoadOrderLinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, UnsafeThreadStuff stuff, Dictionary<string, LeveledItem> leveledlists, EnchantmentInfo enchantmentInfo, string itemEditorID, FormKey entry, string entryLabel)
        {
            if (enchantmentInfo.LeveledLists == null) return;

            foreach (var leveledlistinfo in enchantmentInfo.LeveledLists)
            {
                var leveledlisteditorid = leveledlistinfo.LeveledListPrefix + itemEditorID + leveledlistinfo.LeveledListSuffix;
                bool leveledListAlreadyExists = true;
                LeveledItem leveledlist;
                if (leveledlists.TryGetValue(leveledlisteditorid, out var cached))
                {
                    leveledlist = cached;
                }
                else
                {
                    leveledlist = GetLeveledList(linkCache, state.PatchMod, leveledlisteditorid, config.CheckExistingGenerated, out var b);
                    leveledListAlreadyExists = b;
                    leveledlists.Add(leveledlisteditorid, leveledlist);
                }

                if (leveledListAlreadyExists)
                {
                    leveledlist.Entries ??= new();

                    bool addtoleveledlist = leveledlistinfo.Mode != "Remove";
                    foreach (var existingEntry in leveledlist.Entries)
                    {
                        if (existingEntry.Data?.Reference.FormKey == entry)
                        { // Check if item already exists in leveled list
                            if (leveledlistinfo.Mode == "Remove")
                            {
                                leveledlist.Entries.Remove(existingEntry);
                                Console.WriteLine("Removing item: " + entryLabel + " from leveledlist: " + leveledlist.EditorID);
                                stuff.LeveledListsToSet.Add(leveledlist);
                            }
                            else
                            {
                                addtoleveledlist = false;
                            }
                            break;
                        }
                    }
                    if (addtoleveledlist)
                    {
                        AddToLeveledList(leveledlist, entry);
                        Console.WriteLine("Adding item: " + entryLabel + " to leveledlist: " + leveledlisteditorid);
                        stuff.LeveledListsToSet.Add(leveledlist);
                    }
                }
                else if (leveledlistinfo.Mode == "Add")
                {
                    AddToLeveledList(leveledlist, entry);
                    Console.WriteLine("Adding item: " + entryLabel + " to leveledlist: " + leveledlisteditorid);
                    stuff.LeveledListsToSet.Add(leveledlist);
                }
            }
        }

        // Every playable enchanted weapon in the load order that names a template (base) weapon.
        public static DuplicateIndex IndexExistingWeaponVariants(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var index = new DuplicateIndex();
            foreach (var weapon in state.LoadOrder.PriorityOrder.Weapon().WinningOverrides())
            {
                if (weapon.Template.IsNull || weapon.ObjectEffect.IsNull) continue;
                if (weapon.Data != null && weapon.Data.Flags.HasFlag(WeaponData.Flag.NonPlayable)) continue;
                index.Add(weapon.Template.FormKey, weapon.ObjectEffect.FormKey, new ExistingVariant(weapon.FormKey, weapon.EditorID));
            }
            return index;
        }

        // Every playable enchanted armor in the load order that names a template (base) armor.
        public static DuplicateIndex IndexExistingArmorVariants(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            var index = new DuplicateIndex();
            foreach (var armor in state.LoadOrder.PriorityOrder.Armor().WinningOverrides())
            {
                if (armor.TemplateArmor.IsNull || armor.ObjectEffect.IsNull) continue;
                if (armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable)) continue;
                index.Add(armor.TemplateArmor.FormKey, armor.ObjectEffect.FormKey, new ExistingVariant(armor.FormKey, armor.EditorID));
            }
            return index;
        }

        // A local copy of this patcher gets its own data folder, named after the project. If that folder has no
        // input files, use the git patcher's folder, so both versions share one set of inputs.
        public static string ResolveDataPath(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            string own = state.ExtraSettingsDataPath?.ToString() ?? throw new Exception("Synthesis did not provide a data folder");
            if (Directory.Exists(Path.Combine(own, "input"))) return own;

            string parent = Path.GetDirectoryName(own.TrimEnd('\\', '/')) ?? own;
            // The git patcher's folder, under the corrected spelling and the one it had before
            foreach (var name in new[] { "Enchanted-Variants-Generator", "Enchanted-Variants-Generater" })
            {
                string shared = Path.Combine(parent, name);
                if (Directory.Exists(Path.Combine(shared, "input")))
                {
                    Console.WriteLine("No input folder in \"" + own + "\"; using \"" + shared + "\"");
                    return shared;
                }
            }
            throw new Exception("No input folder found in \"" + own + "\" or in \"Enchanted-Variants-Generator\" beside it");
        }

        // Removes a file this patcher wrote under its old misspelled name ("Generater"), and its folder once empty
        public static void DeleteLegacyOutput(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Console.WriteLine("Removed old output " + path);
                }
                string? folder = Path.GetDirectoryName(path);
                if (folder != null && Path.GetFileName(folder).Contains("Generater", StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    Directory.Delete(folder);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Could not remove old output " + path + ": " + e.Message);
            }
        }

        public static async Task<int> Main(string[] args)
        {
            return await SynthesisPipeline.Instance
                .AddPatch<ISkyrimMod, ISkyrimModGetter>(RunPatch)
                .SetAutogeneratedSettings("Settings", SettingsFile, out _settings)
                .SetTypicalOpen(GameRelease.SkyrimSE, "generatedenchantments.esp")
                .Run(args);
        }

        // Run Patch
        public static void RunPatch(IPatcherState<ISkyrimMod, ISkyrimModGetter> state)
        {
            // Read JSON Files
            string dataPath = ResolveDataPath(state);
            Config config = LoadSettings(state, dataPath);
            List<InputThing> inputs = GetInputs(state, config, dataPath);

            var linkcache = state.LoadOrder.ToImmutableLinkCache();
            ItemNames.Build(state, linkcache, config);
            ItemNames.WriteRuntimeNameOverrides(state, linkcache, config);

            var weaponDuplicates = new DuplicateIndex();
            var armorDuplicates = new DuplicateIndex();
            if (config.SkipDuplicates)
            {
                weaponDuplicates = IndexExistingWeaponVariants(state);
                armorDuplicates = IndexExistingArmorVariants(state);
                Console.WriteLine("Checking for duplicates against " + weaponDuplicates.Count + " existing enchanted weapons and " + armorDuplicates.Count + " existing enchanted armors");
            }

            AssetIndex.Build(state, config);

            Console.WriteLine("Running Generator");
            List<Task<UnsafeThreadStuff>> tasks = new();
            foreach (var input in inputs)
            {
                tasks.Add(Task<UnsafeThreadStuff>.Factory.StartNew(() => GenerateArmor(state, config, input, linkcache, armorDuplicates)));
                tasks.Add(Task<UnsafeThreadStuff>.Factory.StartNew(() => GenerateWeapon(state, config, input, linkcache, weaponDuplicates)));
            }
            Task.WaitAll(tasks.ToArray());
            AssetIndex.Report();
            if (config.SkipDuplicates)
            {
                Console.WriteLine("Skipped " + weaponDuplicates.Skipped + " weapons and " + armorDuplicates.Skipped + " armors that already had an enchanted variant"
                    + (config.AddDuplicatesToLeveledLists ? " (the existing variants were added to the leveled lists instead)" : ""));
            }
            Console.WriteLine("Finishing Up!");
            foreach(var task in tasks)
            {
                foreach(var weapon in task.Result.WeaponsToSet)
                {
                    state.PatchMod.Weapons.Set(weapon);
                }
                foreach (var armor in task.Result.ArmorToSet)
                {
                    state.PatchMod.Armors.Set(armor);
                }
                foreach (var leveledlist in task.Result.LeveledListsToSet)
                {
                    state.PatchMod.LeveledItems.Set(leveledlist);
                }
            }

            // Runs after all tasks finish, so its records are created on one thread
            var generatedWeapons = new List<GeneratedWeapon>();
            foreach (var task in tasks) generatedWeapons.AddRange(task.Result.GeneratedWeapons);
            OwlIntegration.Run(state, config, linkcache, generatedWeapons);
        } // End of Patching

    }
}