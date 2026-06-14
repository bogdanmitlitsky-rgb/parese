using Google.Apis.Auth.OAuth2;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Sheets.v4;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Web;
using System.Windows.Media.Animation;
using Microsoft.Extensions.Configuration;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Collections.Immutable;
using System.Threading;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Hotline_Main_Parsing.@default
{
    public sealed class OrientirPriceUpdate
    {
        public int RowNumber { get; init; }
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string OldPrice { get; init; } = "";
        public decimal OptPrice { get; init; }
        public decimal NewPrice { get; init; }
        public string GroupKey { get; init; } = "";
    }

    public class SpreadSheetManager
    {
        private readonly string _hotlineSpreadSheetId;
        private readonly string _bitSpreadSheetId;
        private readonly string _kymPromSpreadSheetId;
        private readonly string _kymEditorSpreadSheetId;
        private readonly string _1UaPromSpreadSheetId;
        private readonly string _StokPromSpreadSheetId;
        private readonly string _SmilePromSpreadSheetId;
        private SheetsService _sheetsService;
        private List<string> ids;
        private Dictionary<string, int> _symbols;
        private readonly string _from;
        private readonly string _to;
        private readonly string _resultParsing;
        private readonly string _countPredloginiy;

        public SpreadSheetManager(string hotlineSpreadSheetId, 
            string bitSpreadSheetId, 
            string kymPromSpreadSheetId, 
            string kymEditorSpreadSheetId, 
            string UaPromSpreadSheetId, 
            string stokPromSpreadSheetId, 
            string smilePromSpreadSheetId)
        {
            ServiceInit();
            _hotlineSpreadSheetId = hotlineSpreadSheetId;
            _bitSpreadSheetId = bitSpreadSheetId;
            _kymPromSpreadSheetId = kymPromSpreadSheetId;
            _kymEditorSpreadSheetId = kymEditorSpreadSheetId;
            _1UaPromSpreadSheetId = UaPromSpreadSheetId;
            _StokPromSpreadSheetId = stokPromSpreadSheetId;
            _SmilePromSpreadSheetId = smilePromSpreadSheetId;

            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            var googleTableSymbols = config.GetSection("GoogleTableSymbols")?.AsEnumerable().Where(p => p.Value != null).Select(p => p.Value.ToString()).ToArray() ?? new string[0];
            _from = config["GoogleTableFrom"].ToString();
            _to = config["GoogleTableTo"].ToString();
            _resultParsing = config["ResultParsing"].ToString();
            _countPredloginiy = config["CountPredloginiy"].ToString();


            _symbols = CreateListSymbols(googleTableSymbols, _from, _to);
        }

        public Dictionary<string, int> GetSymbols()
        {
            return _symbols;
        }
        public void Set5Prices(List<decimal> shops, int productIndex)
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_hotlineSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "📞 Парсинг Хотлайн")!;

            var values = new List<IList<object>>();
            values.Add(shops.Take(5).Select(s => s).Cast<object>().ToList());
            var valueRange = new ValueRange();
            valueRange.Values = values;



            var req = _sheetsService.Spreadsheets.Values.Update(valueRange, _hotlineSpreadSheetId, $"{sheet.Properties.Title}!N{productIndex + 1}:R{productIndex + 1}");
            req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            req.Execute();
        }
        public ValueRange GetData()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_hotlineSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "📞 Парсинг Хотлайн")!;

            var values = _sheetsService.Spreadsheets.Values.Get(_hotlineSpreadSheetId, sheet.Properties.Title + "!A:AA").Execute();
            return values;
        }

        public IReadOnlyList<OrientirPriceUpdate> NormalizeOrientirPricesFromOpt(decimal markupPercent = 2m)
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_hotlineSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "📞 Парсинг Хотлайн")!;

            var values = _sheetsService.Spreadsheets.Values.Get(_hotlineSpreadSheetId, $"'{EscapeSheetName(sheet.Properties.Title)}'!A:AI").Execute();
            if (values.Values == null || values.Values.Count < 3)
            {
                return Array.Empty<OrientirPriceUpdate>();
            }

            var rows = new List<OrientirPriceRow>();
            for (int i = 2; i < values.Values.Count; i++)
            {
                var row = values.Values[i];
                string id = GetCell(row, 1);
                string name = GetCell(row, 2);
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (IsOrientirUpdateExcluded(GetCell(row, 34)))
                {
                    continue;
                }

                decimal? oldPrice = TryParsePrice(GetCell(row, 3), out decimal parsedOldPrice) ? parsedOldPrice : null;
                decimal? optPrice = TryParsePrice(GetCell(row, 14), out decimal parsedOptPrice) && parsedOptPrice > 0 ? parsedOptPrice : null;

                rows.Add(new OrientirPriceRow
                {
                    RowNumber = i + 1,
                    Id = id,
                    Name = name,
                    OldPriceText = GetCell(row, 3),
                    OldPrice = oldPrice,
                    OptPrice = optPrice,
                    GroupKey = BuildProductGroupKey(name)
                });
            }

            var changes = new List<OrientirPriceUpdate>();
            foreach (var group in rows.GroupBy(r => r.GroupKey).Where(g => !string.IsNullOrWhiteSpace(g.Key)))
            {
                var optPrices = group
                    .Where(r => r.OptPrice.HasValue)
                    .Select(r => Math.Round(r.OptPrice!.Value, 2))
                    .Distinct()
                    .ToList();

                if (optPrices.Count == 0)
                {
                    continue;
                }

                decimal? sharedOptPrice = optPrices.Count == 1 ? optPrices[0] : null;
                foreach (var row in group)
                {
                    decimal? optPrice = sharedOptPrice ?? row.OptPrice;
                    if (!optPrice.HasValue)
                    {
                        continue;
                    }

                    decimal newPrice = CalculateOrientirPrice(optPrice.Value, markupPercent);
                    if (row.OldPrice.HasValue && Math.Round(row.OldPrice.Value, 0, MidpointRounding.AwayFromZero) == newPrice)
                    {
                        continue;
                    }

                    changes.Add(new OrientirPriceUpdate
                    {
                        RowNumber = row.RowNumber,
                        Id = row.Id,
                        Name = row.Name,
                        OldPrice = row.OldPriceText,
                        OptPrice = optPrice.Value,
                        NewPrice = newPrice,
                        GroupKey = row.GroupKey
                    });
                }
            }

            if (changes.Count == 0)
            {
                return changes;
            }

            var request = new BatchUpdateValuesRequest
            {
                ValueInputOption = "USER_ENTERED",
                Data = changes.Select(change => new ValueRange
                {
                    Range = $"'{EscapeSheetName(sheet.Properties.Title)}'!D{change.RowNumber}",
                    Values = new List<IList<object>> { new List<object> { change.NewPrice.ToString("0", CultureInfo.InvariantCulture) } }
                }).ToList()
            };

            _sheetsService.Spreadsheets.Values.BatchUpdate(request, _hotlineSpreadSheetId).Execute();
            return changes;
        }

        public string[] GetHotlineIdsOrder()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_hotlineSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "📞 Парсинг Хотлайн")!;

            var values = _sheetsService.Spreadsheets.Values.Get(_hotlineSpreadSheetId, sheet.Properties.Title + "!B3:B").Execute();

            return ReadFirstColumn(values);
        }

        public string[] GetIdsOrder(string name, string pathGoogleSheets, string key)
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(key).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == name)!;

            var values = _sheetsService.Spreadsheets.Values.Get(key, sheet.Properties.Title + pathGoogleSheets).Execute();

            return ReadFirstColumn(values);
        }
        public ValueRange GetHotlineEditorIdsOrder()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_kymEditorSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "📱 Смартфоны(Редактор)")!;

            var values = _sheetsService.Spreadsheets.Values.Get(_kymEditorSpreadSheetId, sheet.Properties.Title + "!D2:AD").Execute();

            //var result = values.Values.Select(v => v[0]).Cast<string>().ToArray();

            return values;
        }
        public string[] GetBitIdsOrder()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_bitSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Export Products Sheet")!;

            var values = _sheetsService.Spreadsheets.Values.Get(_bitSpreadSheetId, sheet.Properties.Title + "!AB2:AB").Execute();

            return ReadFirstColumn(values);
        }

        public Dictionary<string, decimal> GetCurrentBitPrices()
        {
            var result = new Dictionary<string, decimal>();
            try
            {
                var ids = GetBitIdsOrder();
                if (ids.Length == 0) return result;
                var spreadSheet = _sheetsService.Spreadsheets.Get(_bitSpreadSheetId).Execute();
                var bitSheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Export Products Sheet")!;
                var prices = _sheetsService.Spreadsheets.Values.Get(_bitSpreadSheetId,
                    $"'{bitSheet.Properties.Title}'!I2:I{ids.Length + 1}").Execute();
                for (int i = 0; i < ids.Length; i++)
                {
                    var priceStr = prices.Values?.ElementAtOrDefault(i)?[0]?.ToString()?.Replace(" ", "").Replace(" грн.", "");
                    if (!string.IsNullOrEmpty(priceStr) && decimal.TryParse(priceStr, out decimal price) && price > 0)
                        result[ids[i]] = price;
                }
            }
            catch { }
            return result;
        }

        public int GetPercent()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_hotlineSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "📞 Парсинг Хотлайн")!;
            var request = _sheetsService.Spreadsheets.Values.Get(_hotlineSpreadSheetId, $"{sheet.Properties.Title}!L1");
            var percent = int.Parse(request.Execute().Values.First().First().ToString());
            return percent;
        }

        public void UploadDataToTables(ConcurrentBag<ProductInSheet> products)
        {
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            bool mainWorking = Convert.ToBoolean(config["mainWorking"]);

            if (mainWorking)
                UploadDataToHotline(products);

            UploadDataToBit(products);

            try
            {
                UploadDataToKymProm();
                UploadDataTo1UaProm();
                UploadDataToSmileProm();
                UploadDataToStokProm();
            }
            catch (Exception ex)
            {
                File.AppendAllText("CheckUploadDataToKymProm.txt", $"{DateTime.Now.ToString()}\tMessage - {ex.Message} InnerException - {ex.InnerException} \r\n", Encoding.UTF8);
            }
        }
        /// <summary>
        /// 
        /// </summary>
        /// <param name="count">Number of columns from Google Sheets</param>
        /// <param name="idsLength"></param>
        /// <returns></returns>
        private List<IList<Object>> GetListObjects(int count, int idsLength)
        {
            var values = new List<IList<object>>();
            for (int i = 0; i < idsLength; i++)
            {
                var row = new List<object>();
                for (int j = 0; j < count; j++)
                {
                    row.Add(null);
                }
                values.Add(row);
            }
            return values;
        }

        private void UploadTable(List<IList<Object>> values, string range, string key, SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum options)
        {
            if (values.Count == 0)
            {
                return;
            }

            var valueRange = new ValueRange();
            valueRange.Values = values;

            var req = _sheetsService.Spreadsheets.Values.Update(valueRange, key, range);

            req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            req.Execute();
        }
        public void UploadDataToSmileProm()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_SmilePromSpreadSheetId).Execute();
            var hotlineSheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Export Products Sheet")!;
            var ids = GetIdsOrder("Export Products Sheet", "!Y:Y", _SmilePromSpreadSheetId);
            var idsEditor = GetHotlineEditorIdsOrder();
            var editorRows = idsEditor.Values ?? Array.Empty<IList<object>>();
            var values = GetListObjects(38, ids.Length);

            for (int i = 0; i < ids.Length; i++)
            {
                var kymId = editorRows.FirstOrDefault(x => x[16].ToString() == ids[i]);
                if (kymId == null) { continue; }

                var row = values[i - 1];
                row[8] = kymId[18].ToString();
                row[30] = kymId[19].ToString();
                row[32] = kymId[20].ToString();
                row[15] = "'" + kymId[22].ToString();
                row[34] = kymId[23].ToString();
                row[35] = kymId[24].ToString();
            }

            UploadTable(values, $"{hotlineSheet.Properties.Title}!A2:AJ{ids.Length + 1}", _SmilePromSpreadSheetId, SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED);
        }
        public void UploadDataToStokProm()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_StokPromSpreadSheetId).Execute();
            var hotlineSheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Export Products Sheet")!;
            var ids = GetIdsOrder("Export Products Sheet", "!AC:AC", _StokPromSpreadSheetId);
            var idsEditor = GetHotlineEditorIdsOrder();
            var editorRows = idsEditor.Values ?? Array.Empty<IList<object>>();
            var values = GetListObjects(42, ids.Length); 

            for (int i = 0; i < ids.Length; i++)
            {
                var kymId = editorRows.FirstOrDefault(x => x[12].ToString() == ids[i]);
                if (kymId == null) { continue; }

                var row = values[i - 1];
                row[8] = kymId[14].ToString();
                row[34] = kymId[15].ToString();
                //row[34] = kymId[7].ToString();
                row[15] = "'" + kymId[22].ToString();
                row[38] = kymId[23].ToString();
                row[39] = kymId[24].ToString();
            }

            UploadTable(values, $"{hotlineSheet.Properties.Title}!A2:AN{ids.Length + 1}", _StokPromSpreadSheetId, SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED);
        }

        public void UploadDataTo1UaProm()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_1UaPromSpreadSheetId).Execute();
            var hotlineSheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Export Products Sheet")!;
            var ids = GetIdsOrder("Export Products Sheet", "!AA:AA", _1UaPromSpreadSheetId);
            var idsEditor = GetHotlineEditorIdsOrder();
            var editorRows = idsEditor.Values ?? Array.Empty<IList<object>>();
            var values = GetListObjects(42, ids.Length);

            for (int i = 0; i < ids.Length; i++)
            {
                var kymId = editorRows.FirstOrDefault(x => x[3].ToString() == ids[i]);
                if (kymId == null) { continue; }

                var row = values[i - 1];
                row[8] = kymId[5].ToString();
                row[32] =  kymId[6].ToString();
                row[34] = kymId[7].ToString();
                row[15] = "'" + kymId[22].ToString();   
                row[36] = kymId[23].ToString();
                row[37] = kymId[24].ToString();
            }

            UploadTable(values, $"{hotlineSheet.Properties.Title}!A2:AL{ids.Length + 1}", _1UaPromSpreadSheetId, SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED);
        }
        public void UploadDataToKymProm()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_kymPromSpreadSheetId).Execute();
            var hotlineSheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Export Products Sheet")!;
            var ids = GetIdsOrder("Export Products Sheet", "!AB:AB", _kymPromSpreadSheetId);
            var idsEditor = GetHotlineEditorIdsOrder();
            var editorRows = idsEditor.Values ?? Array.Empty<IList<object>>();
            var values = GetListObjects(39, ids.Length);

            for (int i = 0; i < ids.Length; i++)
            {
                var kymId = editorRows.FirstOrDefault(x => x[8].ToString() == ids[i]);
                if(kymId == null) { continue; }

                var row = values[i-1];
                row[8] = kymId[10].ToString();
                row[15] = "'" + kymId[22].ToString();
                row[35] = kymId[11].ToString();
            }

            UploadTable(values, $"{hotlineSheet.Properties.Title}!A2:AJ{ids.Length + 1}", _kymPromSpreadSheetId, SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED);
        }
        private Dictionary<string, int> CreateListSymbols(string[] symbols, string from, string to)
        {
            Dictionary<string, int> keyValuePairs = new Dictionary<string, int>();
            Array.Sort(symbols);
            for (int s = 0; s < symbols.Length; s++)
            {
                keyValuePairs.Add(symbols[s], s);
            }

            if(!keyValuePairs.ContainsKey(to))
            {
                for (int s = 0;s < symbols.Length; s++)
                {
                    for (int i = 0; i < symbols.Length; i++)
                    {
                        string keyValue = symbols[s] + symbols[i];
                        keyValuePairs.Add(keyValue, symbols.Length + i);
                        if (keyValuePairs.ContainsKey(to)) break;
                    }
                    if (keyValuePairs.ContainsKey(to)) break;
                }
            }

            return keyValuePairs;
        }

        private static string GetCell(IList<object> row, int index)
        {
            return row.Count > index ? row[index]?.ToString()?.Trim() ?? "" : "";
        }

        private static string[] ReadFirstColumn(ValueRange values)
        {
            if (values.Values == null)
            {
                return Array.Empty<string>();
            }

            return values.Values.Select(v => v[0]).Cast<string>().ToArray();
        }

        private static decimal CalculateOrientirPrice(decimal optPrice, decimal markupPercent)
        {
            return Math.Ceiling(optPrice * (100m + markupPercent) / 100m);
        }

        private static bool IsOrientirUpdateExcluded(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = value.Trim();
            return normalized.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
                   normalized.Contains("ноут", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParsePrice(string? value, out decimal price)
        {
            price = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = value
                .Replace("\u00A0", " ")
                .Replace("грн.", "", StringComparison.OrdinalIgnoreCase)
                .Replace("грн", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            normalized = Regex.Replace(normalized, @"[^\d,.\-]", "");
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            int commaIndex = normalized.LastIndexOf(',');
            int dotIndex = normalized.LastIndexOf('.');
            if (commaIndex >= 0 && dotIndex >= 0)
            {
                normalized = commaIndex > dotIndex
                    ? normalized.Replace(".", "").Replace(",", ".")
                    : normalized.Replace(",", "");
            }
            else if (commaIndex >= 0)
            {
                normalized = normalized.Replace(",", ".");
            }

            return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out price);
        }

        private static string BuildProductGroupKey(string name)
        {
            string result = name.ToLowerInvariant().Replace('ё', 'е');

            foreach (string phrase in ColorWords.OrderByDescending(p => p.Length))
            {
                result = Regex.Replace(
                    result,
                    $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}])",
                    " ",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }

            result = Regex.Replace(result, @"\s+", " ").Trim();
            return result;
        }

        private static string EscapeSheetName(string sheetName)
        {
            return sheetName.Replace("'", "''");
        }

        private sealed class OrientirPriceRow
        {
            public int RowNumber { get; init; }
            public string Id { get; init; } = "";
            public string Name { get; init; } = "";
            public string OldPriceText { get; init; } = "";
            public decimal? OldPrice { get; init; }
            public decimal? OptPrice { get; init; }
            public string GroupKey { get; init; } = "";
        }

        private static readonly string[] ColorWords =
        {
            "cosmic gray", "space gray", "sky blue", "glacier blue", "midnight black",
            "matte black", "starry black", "obsidian black", "graphite black", "forest green",
            "mint green", "ice blue", "ocean blue", "navy blue", "lake blue", "starlight blue",
            "aurora green", "titanium gray", "titanium grey", "dark grey", "dark gray",
            "light blue", "light green", "rose gold", "champagne gold", "silver grey",
            "silver gray", "black", "white", "gray", "grey", "blue", "green", "red",
            "pink", "purple", "violet", "gold", "silver", "orange", "yellow", "brown",
            "beige", "cream", "graphite", "midnight", "starlight", "titanium", "lavender",
            "mint", "navy", "cyan", "teal", "черный", "чёрный", "белый", "серый",
            "сірий", "голубой", "блакитний", "синий", "синій", "красный", "червоний",
            "розовый", "рожевий", "зеленый", "зелений", "фиолетовый", "фіолетовий",
            "золотой", "золотий", "серебристый", "сріблястий", "оранжевый",
            "помаранчевий", "желтый", "жовтий", "коричневый", "коричневий",
            "бежевый", "бежевий"
        };

        public void Test()
        {
            UploadDataToHotline(new ConcurrentBag<ProductInSheet>());
        }
        private void UploadDataToHotline(ConcurrentBag<ProductInSheet> products)
        {
            int resultIndex = _symbols[_resultParsing];
            int CountPredloginiyIndex = _symbols[_countPredloginiy];

            var spreadSheet = _sheetsService.Spreadsheets.Get(_hotlineSpreadSheetId).Execute();
            var hotlineSheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "📞 Парсинг Хотлайн")!;
            var ids = GetHotlineIdsOrder();
            var values =  GetListObjects(_symbols.Count, ids.Length);


            for (int i = 0; i < ids.Length; i++)
            {
                values[i][1] = ids[i]; // сохраняем ID чтобы не затереть колонку B при заливке

                var product = products.FirstOrDefault(p => p.Id == ids[i]);
                if (product == null)
                {
                    continue;
                }

                var row = values[i];
                row[resultIndex] = product.ReadyPrice;
                if (!string.IsNullOrWhiteSpace(product.PriceAvailableness))
                {
                    row[7] = FormatAvailabilityForSheet(product.PriceAvailableness);
                }
                if (product.SwitchParseMarkOldToNew)
                {
                    row[9] = false;
                    row[10] = true;
                }
                row[CountPredloginiyIndex] = product.OffersCount;
                if (product.TehnoBit != '\0') row[27] = product.TehnoBit is '+' ? "'" + product.TehnoBit : product.TehnoBit;
                else row[27] = '-';

                if (product.Ua_1 != '\0') row[28] = product.Ua_1 is '+' ? "'" + product.Ua_1 : product.Ua_1;
                else row[28] = '-';

                for (int j = 19, f = 0; j <= 25 && f < product.PriceRange.Length && f < 7 && f < product.PriceRange.Length; j++, f++)
                {
                    row[j] = product.PriceRange[f];
                }
            }
            var valueRange = new ValueRange();
            valueRange.Values = values;
            if (values.Count == 0)
            {
                return;
            }


            var range = $"{hotlineSheet.Properties.Title}!{_from}3:{_to + ids.Length + 2}";
            var req = _sheetsService.Spreadsheets.Values.Update(valueRange, _hotlineSpreadSheetId, range);

            req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            req.Execute();
        }

        private void UploadDataToBit(ConcurrentBag<ProductInSheet> products)
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_bitSpreadSheetId).Execute();
            var bitSheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Export Products Sheet")!;
            var ids = GetBitIdsOrder();
            var values = new List<IList<object?>>();
            for (int i = 0; i < ids.Length; i++)
            {
                var row = new List<object?>();
                for (int j = 0; j < 8; j++)
                {
                    row.Add(null);
                }
                values.Add(row);
            }

            for (int i = 0; i < ids.Length; i++)
            {
                var product = products.FirstOrDefault(p => p.Id == ids[i]);
                if (product == null)
                {
                    continue;
                }

                var row = values[i];
                row[0] = product.BitPrice;
                if (!string.IsNullOrWhiteSpace(product.PriceAvailableness))
                {
                    row[7] = FormatAvailabilityForSheet(product.PriceAvailableness);
                }
            }
            var valueRange = new ValueRange();
            valueRange.Values = values;
            if (values.Count == 0)
            {
                return;
            }

            var range = $"'{bitSheet.Properties.Title}'!I2:P{ids.Length + 1}";
            var req = _sheetsService.Spreadsheets.Values.Update(valueRange, _bitSpreadSheetId, range);
            req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            req.Execute();
        }

        private static string FormatAvailabilityForSheet(string availability)
        {
            string value = availability.Trim().TrimStart('\'');
            return value == "+" ? "'+" : value;
        }

        private void ServiceInit()
        {
            string[] scopes = { SheetsService.Scope.Spreadsheets };

            var credential = GoogleCredential.FromFile("credentialsServiceAccount.json").CreateScoped(scopes: scopes);
            _sheetsService = new SheetsService(new Google.Apis.Services.BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "SheetsService"
            });
        }

        public int GetSheetCount(Sheet sheet)
        {
            var getCountRequset = _sheetsService.Spreadsheets.Values.Get(_hotlineSpreadSheetId, $"{sheet.Properties.Title}!A:G");
            var count = getCountRequset.Execute().Values?.Count ?? 0;
            return count;
        }
    }
}
