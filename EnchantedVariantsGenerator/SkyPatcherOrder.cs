using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace EnchantedVariantsGenerator
{
    // The order SkyPatcher applies the INIs of one type folder (leveledList, weapon, ...) in, as its log shows: by how
    // deeply the file is nested under the type folder, shallowest first, then by path, ignoring case. So
    // leveledList\WACCF\x.ini runs before leveledList\OWL\Main\y.ini, whatever the names.
    public static class SkyPatcherOrder
    {
        private static int Depth(string relative) => relative.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);

        // Every INI under the type folder, in the order SkyPatcher applies them
        public static List<(string Full, string Relative)> InApplyOrder(string typeFolder)
        {
            if (!Directory.Exists(typeFolder)) return new();
            return Directory.EnumerateFiles(typeFolder, "*.ini", SearchOption.AllDirectories)
                .Select(f => (Full: f, Relative: Path.GetRelativePath(typeFolder, f)))
                .OrderBy(f => Depth(f.Relative))
                .ThenBy(f => f.Relative, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Where this patcher's INI goes so SkyPatcher applies it after every other INI in the type folder: under its own
        // folder, nested one level deeper than the deepest INI anyone else has there (subfolders named "zz"). Copies of
        // the same file at another depth, from earlier runs, are removed, and so are the "zz" folders they leave empty.
        public static string LastPath(string typeFolder, string ownFolder, string fileName)
        {
            int deepest = InApplyOrder(typeFolder)
                .Where(f => !f.Relative.StartsWith(ownFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Select(f => Depth(f.Relative)).DefaultIfEmpty(0).Max();
            string folder = Path.Combine(new[] { typeFolder, ownFolder }.Concat(Enumerable.Repeat("zz", deepest)).ToArray());
            string path = Path.Combine(folder, fileName);
            RemoveOtherCopies(typeFolder, ownFolder, fileName, path);
            return path;
        }

        // Removes every copy of the file under the patcher's folder (all of them when keep is null)
        public static void RemoveOtherCopies(string typeFolder, string ownFolder, string fileName, string? keep)
        {
            string own = Path.Combine(typeFolder, ownFolder);
            if (!Directory.Exists(own)) return;
            foreach (var file in Directory.EnumerateFiles(own, fileName, SearchOption.AllDirectories).ToList())
            {
                if (keep != null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
                File.Delete(file);
                Console.WriteLine("Removed " + file);
            }
            // Empty "zz" folders, deepest first
            foreach (var dir in Directory.EnumerateDirectories(own, "zz", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
        }
    }
}
