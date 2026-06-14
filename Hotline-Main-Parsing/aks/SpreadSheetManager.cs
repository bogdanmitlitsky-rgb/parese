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

        public void UploadDataToTables(List<ProductInSheet> products)
        {
            //var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            //bool mainWorking = Convert.ToBoolean(config["mainWorking"]);
            //bool aksWorking = Convert.ToBoolean(config["aksWorking"]);

            //if (mainWorking)
                UploadDataToHotline(products);
            //if (aksWorking)
                UploadDataToBit(products);
        }

        private async void UploadDataToHotline(List<ProductInSheet> products)
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

        private static string[] ReadFirstColumn(ValueRange values)
        {
            if (values.Values == null)
            {
                return Array.Empty<string>();
            }

            return values.Values.Select(v => v[0]).Cast<string>().ToArray();
        }

    }
}
