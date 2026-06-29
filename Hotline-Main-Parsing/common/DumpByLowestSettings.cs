using Google.Apis.Sheets.v4.Data;
using System;
using System.Collections.Generic;
using System.IO;

namespace Hotline_Main_Parsing.common
{
    public static class DumpByLowestSettings
    {
        public const string LocalFileName = "dump_by_lowest_ids.txt";

        private static readonly string[] SheetTitles =
        {
            "По низу",
            "по низу",
            "DumpByLowest",
            "dump_by_lowest",
            "Низ"
        };

        public static bool IsSheetTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            foreach (string sheetTitle in SheetTitles)
            {
                if (title.Trim().Equals(sheetTitle, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static HashSet<string> ReadIdsFromValueRange(ValueRange values)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (values.Values == null)
            {
                return ids;
            }

            foreach (var row in values.Values)
            {
                string id = NormalizeId(GetCell(row, 0));
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string enabled = GetCell(row, 1);
                if (!IsEnabledMarker(enabled))
                {
                    continue;
                }

                ids.Add(id);
            }

            return ids;
        }

        public static HashSet<string> ReadIdsFromLocalFile()
        {
            var ids = DumpByLowestProductStore.ReadSelectedIds();
            string filePath = Path.Combine(AppContext.BaseDirectory, LocalFileName);
            if (!File.Exists(filePath))
            {
                return ids;
            }

            foreach (string rawLine in File.ReadAllLines(filePath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                {
                    continue;
                }

                string[] parts = line.Split(new[] { ';', ',', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                {
                    continue;
                }

                string id = NormalizeId(parts[0]);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        public static string NormalizeId(string? value)
        {
            return (value ?? string.Empty)
                .Trim()
                .TrimStart('\'')
                .Replace("\u00A0", string.Empty)
                .Replace(" ", string.Empty);
        }

        private static bool IsEnabledMarker(string marker)
        {
            if (string.IsNullOrWhiteSpace(marker))
            {
                return true;
            }

            string value = marker.Trim();
            return value.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("+", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("да", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetCell(IList<object> row, int index)
        {
            return row.Count > index ? row[index]?.ToString()?.Trim() ?? string.Empty : string.Empty;
        }
    }
}
