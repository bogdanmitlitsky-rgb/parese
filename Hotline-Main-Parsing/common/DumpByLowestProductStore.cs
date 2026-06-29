using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Hotline_Main_Parsing.common
{
    public static class DumpByLowestProductStore
    {
        public const string FileName = "dump_by_lowest_products.json";

        private static readonly object FileLock = new object();
        private static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

        public static List<DumpByLowestProductRecord> Load()
        {
            lock (FileLock)
            {
                if (!File.Exists(FilePath))
                {
                    return new List<DumpByLowestProductRecord>();
                }

                try
                {
                    string json = File.ReadAllText(FilePath, Encoding.UTF8);
                    return JsonConvert.DeserializeObject<List<DumpByLowestProductRecord>>(json)
                           ?? new List<DumpByLowestProductRecord>();
                }
                catch
                {
                    return new List<DumpByLowestProductRecord>();
                }
            }
        }

        public static void Save(IEnumerable<DumpByLowestProductRecord> records)
        {
            lock (FileLock)
            {
                var normalized = records
                    .Where(record => !string.IsNullOrWhiteSpace(record.Id))
                    .GroupBy(record => BuildKey(record.Section, record.Id), StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(record => record.Section)
                    .ThenBy(record => record.Name)
                    .ToList();

                string json = JsonConvert.SerializeObject(normalized, Formatting.Indented);
                File.WriteAllText(FilePath, json, Encoding.UTF8);
            }
        }

        public static HashSet<string> ReadSelectedIds()
        {
            return Load()
                .Where(record => record.Selected)
                .Select(record => DumpByLowestSettings.NormalizeId(record.Id))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public static List<DumpByLowestProductRecord> MergeWithExisting(IEnumerable<DumpByLowestProductRecord> freshRecords)
        {
            var existing = Load();
            var selectedById = existing
                .Where(record => record.Selected)
                .Select(record => DumpByLowestSettings.NormalizeId(record.Id))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var resultByKey = existing
                .Where(record => !string.IsNullOrWhiteSpace(record.Id))
                .ToDictionary(record => BuildKey(record.Section, record.Id), record => record, StringComparer.OrdinalIgnoreCase);

            DateTime now = DateTime.UtcNow;
            foreach (var fresh in freshRecords)
            {
                string id = DumpByLowestSettings.NormalizeId(fresh.Id);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string key = BuildKey(fresh.Section, id);
                fresh.Id = id;
                fresh.Selected = selectedById.Contains(id);
                fresh.LastSeenUtc = now;
                resultByKey[key] = fresh;
            }

            return resultByKey.Values
                .OrderBy(record => record.Section)
                .ThenBy(record => record.Name)
                .ToList();
        }

        private static string BuildKey(string section, string id)
        {
            return $"{section.Trim().ToLowerInvariant()}|{DumpByLowestSettings.NormalizeId(id)}";
        }
    }
}
