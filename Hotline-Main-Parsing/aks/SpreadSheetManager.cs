using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Microsoft.Extensions.Configuration;

namespace Hotline_Main_Parsing.aks
{
    public class SpreadSheetManager
    {
        private readonly string _hotlineSpreadSheetId;
        private readonly string _bitSpreadSheetId;
        private SheetsService _sheetsService;
        private List<string> ids;
        private Dictionary<string, int> _symbols;
        private readonly string _from;
        private readonly string _to;
        private readonly string _resultParsing;
        private readonly string _countPredloginiy;
        public SpreadSheetManager(string hotlineSpreadSheetId, string bitSpreadSheetId)
        {
            ServiceInit();
            _hotlineSpreadSheetId = hotlineSpreadSheetId;
            _bitSpreadSheetId = bitSpreadSheetId;

            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            var googleTableSymbols = config.GetSection("GoogleTableSymbols")?.AsEnumerable().Where(p => p.Value != null).Select(p => p.Value.ToString()).ToArray() ?? new string[0];
            _from = config["GoogleTableFrom"].ToString();
            _to = config["GoogleTableTo"].ToString();
            _resultParsing = config["ResultParsing"].ToString();
            _countPredloginiy = config["CountPredloginiy"].ToString();


            _symbols = CreateListSymbols(googleTableSymbols, _from, _to);
        }

        private Dictionary<string, int> CreateListSymbols(string[] symbols, string from, string to)
        {
            Dictionary<string, int> keyValuePairs = new Dictionary<string, int>();
            Array.Sort(symbols);
            for (int s = 0; s < symbols.Length; s++)
            {
                keyValuePairs.Add(symbols[s], s);
            }

            if (!keyValuePairs.ContainsKey(to))
            {
                for (int s = 0; s < symbols.Length; s++)
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
        public Dictionary<string, int> GetSymbols()
        {
            return _symbols;
        }
        public void Set5Prices(List<decimal> shops, int productIndex)
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_hotlineSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "🎧Парсинг Хотлайн Аксы")!;

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
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "🎧Парсинг Хотлайн Аксы")!;

            var values = _sheetsService.Spreadsheets.Values.Get(_hotlineSpreadSheetId, sheet.Properties.Title + "!A:V").Execute();
            return values;
        }

        public string[] GetHotlineIdsOrder()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_hotlineSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "🎧Парсинг Хотлайн Аксы")!;

            var values = _sheetsService.Spreadsheets.Values.Get(_hotlineSpreadSheetId, sheet.Properties.Title + "!B3:B").Execute();

            return ReadFirstColumn(values);
        }

        public string[] GetBitIdsOrder()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_bitSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Export Products Sheet")!;

            var values = _sheetsService.Spreadsheets.Values.Get(_bitSpreadSheetId, sheet.Properties.Title + "!Y2:Y").Execute();

            return ReadFirstColumn(values);
        }

        private Dictionary<string, string> GetHotlineAvailabilityById()
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_hotlineSpreadSheetId).Execute();
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "🎧Парсинг Хотлайн Аксы")!;
            var values = _sheetsService.Spreadsheets.Values.Get(
                _hotlineSpreadSheetId,
                $"'{EscapeSheetName(sheet.Properties.Title)}'!B3:H").Execute();

            return ReadAvailabilityById(values);
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
            var sheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "🎧Парсинг Хотлайн Аксы")!;
            var request = _sheetsService.Spreadsheets.Values.Get(_hotlineSpreadSheetId, $"{sheet.Properties.Title}!A1");
            var percent = int.Parse(request.Execute().Values.First().First().ToString());
            return percent;
        }

        public void UploadDataToTables(List<ProductInSheet> products, Action<string>? progressLog = null)
        {
            //var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            //bool mainWorking = Convert.ToBoolean(config["mainWorking"]);
            //bool aksWorking = Convert.ToBoolean(config["aksWorking"]);

            progressLog?.Invoke($"Аксессуары: перенос таблиц начался, товаров: {products.Count}");
            progressLog?.Invoke("Аксессуары: перенос в рабочую Google-таблицу...");
            UploadDataToHotline(products);
            progressLog?.Invoke("Аксессуары: рабочая Google-таблица записана");

            progressLog?.Invoke("Аксессуары: перенос в Bit (цены и наличие)...");
            UploadDataToBit(products);
            progressLog?.Invoke("Аксессуары: Bit записан (цены и наличие)");
            progressLog?.Invoke("Аксессуары: перенос таблиц завершен");
        }

        private void UploadDataToHotline(List<ProductInSheet> products)
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_hotlineSpreadSheetId).Execute();
            var hotlineSheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "🎧Парсинг Хотлайн Аксы")!;
            var ids = GetHotlineIdsOrder();
            var values = new List<IList<object>>();
            for (int i = 0; i < ids.Length; i++)
            {
                var row = new List<object>();
                for (int j = 0; j < 29; j++)
                {
                    row.Add(null);
                }
                values.Add(row);
            }

            for (int i = 0; i < ids.Length; i++)
            {
                values[i][1] = ids[i]; // сохраняем ID чтобы не затереть колонку B при заливке

                var product = products.FirstOrDefault(p => p.Id == ids[i]);
                if (product == null)
                {
                    continue;
                }

                var row = values[i];
                row[5] = product.ReadyPrice;
                if (product.SwitchParseMarkOldToNew)
                {
                    row[9] = false;
                    row[10] = true;
                }
                row[18] = product.OffersCount;
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

            var range = $"{hotlineSheet.Properties.Title}!A3:AC{ids.Length + +2}";
            var req = _sheetsService.Spreadsheets.Values.Update(valueRange, _hotlineSpreadSheetId, range);

            req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            req.Execute();
        }

        private void UploadDataToBit(List<ProductInSheet> products)
        {
            var spreadSheet = _sheetsService.Spreadsheets.Get(_bitSpreadSheetId).Execute();
            var bitSheet = spreadSheet.Sheets.FirstOrDefault(s => s.Properties.Title == "Export Products Sheet")!;
            var ids = GetBitIdsOrder();
            var availabilityById = GetHotlineAvailabilityById();
            var productsById = products
                .GroupBy(product => NormalizeSheetId(product.Id))
                .ToDictionary(group => group.Key, group => group.First());
            var values = new List<IList<object?>>();
            for (int i = 0; i < ids.Length; i++)
            {
                var row = new List<object?>();
                row.Add(null);
                values.Add(row);
            }

            for (int i = 0; i < ids.Length; i++)
            {
                string id = NormalizeSheetId(ids[i]);
                if (productsById.TryGetValue(id, out ProductInSheet? product))
                {
                    values[i][0] = product.BitPrice;
                }
            }
            var valueRange = new ValueRange();
            valueRange.Values = values;
            if (values.Count == 0)
            {
                return;
            }

            var range = $"'{bitSheet.Properties.Title}'!I2:I{ids.Length + 1}";
            var req = _sheetsService.Spreadsheets.Values.Update(valueRange, _bitSpreadSheetId, range);
            req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            req.Execute();

            UploadBitAvailability(bitSheet.Properties.Title, ids, availabilityById);
        }

        private void UploadBitAvailability(string bitSheetTitle, string[] bitIds, Dictionary<string, string> availabilityById)
        {
            var currentAvailability = _sheetsService.Spreadsheets.Values.Get(
                _bitSpreadSheetId,
                $"'{bitSheetTitle}'!P2:P{bitIds.Length + 1}").Execute();

            var values = new List<IList<object?>>();
            for (int i = 0; i < bitIds.Length; i++)
            {
                object? availabilityValue;
                string id = NormalizeSheetId(bitIds[i]);
                if (availabilityById.TryGetValue(id, out string? availability))
                {
                    availabilityValue = FormatAvailabilityForBit(availability);
                }
                else
                {
                    string currentValue = currentAvailability.Values != null && currentAvailability.Values.Count > i
                        ? GetCell(currentAvailability.Values[i], 0)
                        : "";
                    availabilityValue = FormatAvailabilityForBit(currentValue);
                }

                values.Add(new List<object?> { availabilityValue });
            }

            if (values.Count == 0)
            {
                return;
            }

            var valueRange = new ValueRange
            {
                Values = values
            };

            string range = $"'{bitSheetTitle}'!P2:P{bitIds.Length + 1}";
            var req = _sheetsService.Spreadsheets.Values.Update(valueRange, _bitSpreadSheetId, range);
            req.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
            req.Execute();
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

        private static string[] ReadFirstColumn(ValueRange values)
        {
            if (values.Values == null)
            {
                return Array.Empty<string>();
            }

            return values.Values.Select(v => v[0]).Cast<string>().ToArray();
        }

        private static Dictionary<string, string> ReadAvailabilityById(ValueRange values)
        {
            var result = new Dictionary<string, string>();
            if (values.Values == null)
            {
                return result;
            }

            foreach (var row in values.Values)
            {
                string id = NormalizeSheetId(GetCell(row, 0));
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                result[id] = GetCell(row, 6);
            }

            return result;
        }

        private static string NormalizeSheetId(string? value)
        {
            return (value ?? string.Empty)
                .Trim()
                .TrimStart('\'')
                .Replace("\u00A0", string.Empty)
                .Replace(" ", string.Empty);
        }

        private static string GetCell(IList<object> row, int index)
        {
            return row.Count > index ? row[index]?.ToString()?.Trim() ?? "" : "";
        }

        private static object? FormatAvailabilityForBit(string? availability)
        {
            if (string.IsNullOrWhiteSpace(availability))
            {
                return null;
            }

            string value = availability.Trim();
            return value == "+" ? "'+" : value;
        }

        private static string EscapeSheetName(string sheetName)
        {
            return sheetName.Replace("'", "''");
        }

    }
}
