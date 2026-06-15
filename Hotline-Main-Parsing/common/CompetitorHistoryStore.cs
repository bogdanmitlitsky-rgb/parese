using Newtonsoft.Json;
using System.IO;
using System.Net;
using System.Text;

namespace Hotline_Main_Parsing.common
{
    public static class CompetitorHistoryStore
    {
        private static readonly object FileLock = new();

        public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "competitors");
        public static string HistoryPath => Path.Combine(DirectoryPath, "competitors_history.jsonl");
        public static string LatestPath => Path.Combine(DirectoryPath, "competitors_latest.json");

        public static void SaveInsights(IEnumerable<CompetitorInsight> insights)
        {
            var list = insights.ToList();
            if (list.Count == 0)
            {
                return;
            }

            Directory.CreateDirectory(DirectoryPath);

            lock (FileLock)
            {
                var lines = list.Select(item => JsonConvert.SerializeObject(item));
                File.AppendAllLines(HistoryPath, lines, Encoding.UTF8);

                var latest = ReadLatestUnsafe()
                    .GroupBy(item => BuildKey(item.Section, item.ProductId))
                    .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.CheckedAt).First());

                foreach (var item in list)
                {
                    latest[BuildKey(item.Section, item.ProductId)] = item;
                }

                var latestList = latest.Values
                    .OrderByDescending(item => item.SoftPriceDropApplied)
                    .ThenByDescending(item => item.IsDumping)
                    .ThenByDescending(item => item.OwnIsHigherThanMarket)
                    .ThenByDescending(item => item.CanRaisePrice)
                    .ThenByDescending(item => item.CheckedAt)
                    .ToList();

                File.WriteAllText(LatestPath, JsonConvert.SerializeObject(latestList, Formatting.Indented), Encoding.UTF8);
            }
        }

        public static List<CompetitorInsight> ReadLatest()
        {
            Directory.CreateDirectory(DirectoryPath);

            lock (FileLock)
            {
                return ReadLatestUnsafe()
                    .OrderByDescending(item => item.SoftPriceDropApplied)
                    .ThenByDescending(item => item.IsDumping)
                    .ThenByDescending(item => item.OwnIsHigherThanMarket)
                    .ThenByDescending(item => item.CanRaisePrice)
                    .ThenByDescending(item => item.CheckedAt)
                    .ToList();
            }
        }

        public static string ExportLatestToExcel()
        {
            var latest = ReadLatest();
            Directory.CreateDirectory(DirectoryPath);

            string path = Path.Combine(DirectoryPath, $"competitors_report_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.xlsx");
            CompetitorExcelExporter.WriteReport(path, latest);
            return path;
        }

        public static string BuildMorningReport(DateTime date)
        {
            return BuildMorningReport(date, useHtmlLinks: false);
        }

        public static string BuildMorningReportHtml(DateTime date)
        {
            return BuildMorningReport(date, useHtmlLinks: true);
        }

        private static string BuildMorningReport(DateTime date, bool useHtmlLinks)
        {
            var latest = ReadLatest()
                .Where(item => item.CheckedAt.Date == date.Date)
                .ToList();

            if (latest.Count == 0)
            {
                return $"Утренний отчет по Hotline за {date:dd.MM.yyyy}\nДанных за сегодня пока нет.";
            }

            int dumping = latest.Count(item => item.IsDumping);
            int ownHigher = latest.Count(item => item.OwnIsHigherThanMarket);
            int canRaise = latest.Count(item => item.CanRaisePrice);
            int softDrops = latest.Count(item => item.SoftPriceDropApplied);
            int withoutOffers = latest.Count(item => item.OffersCount == 0);

            var topDumping = latest
                .Where(item => item.IsDumping)
                .OrderByDescending(item => item.DumpingPercent ?? 0)
                .Take(5)
                .Select(item =>
                {
                    string productName = TrimProductName(item.ProductName);
                    string productText = useHtmlLinks
                        ? BuildHtmlProductLink(productName, item.HotlineUrl)
                        : productName;
                    string shopName = useHtmlLinks ? Html(item.DumpingShop) : item.DumpingShop;

                    return $"- {productText}: {shopName} {item.DumpingPrice:0} грн, ниже рынка на {item.DumpingPercent:0.##}%";
                });

            var builder = new StringBuilder();
            builder.AppendLine($"Утренний отчет по Hotline за {date:dd.MM.yyyy}");
            builder.AppendLine($"Товаров в обзоре: {latest.Count}");
            builder.AppendLine($"Подозрение на демпинг: {dumping}");
            builder.AppendLine($"Авто-снижений 1-3%: {softDrops}");
            builder.AppendLine($"Ты выше рынка: {ownHigher}");
            builder.AppendLine($"Можно поднять цену: {canRaise}");
            builder.AppendLine($"Без предложений: {withoutOffers}");

            if (dumping > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Топ демпинга:");
                foreach (var line in topDumping)
                {
                    builder.AppendLine(line);
                }
            }

            return builder.ToString().TrimEnd();
        }

        private static List<CompetitorInsight> ReadLatestUnsafe()
        {
            if (!File.Exists(LatestPath))
            {
                return new List<CompetitorInsight>();
            }

            try
            {
                return JsonConvert.DeserializeObject<List<CompetitorInsight>>(File.ReadAllText(LatestPath, Encoding.UTF8))
                    ?? new List<CompetitorInsight>();
            }
            catch
            {
                return new List<CompetitorInsight>();
            }
        }

        private static string BuildKey(string section, string productId)
        {
            return $"{section}:{productId}";
        }

        private static string TrimProductName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "без названия";
            }

            return value.Length <= 60 ? value : value[..57] + "...";
        }

        private static string BuildHtmlProductLink(string productName, string hotlineUrl)
        {
            string safeName = Html(productName);
            if (string.IsNullOrWhiteSpace(hotlineUrl) ||
                !Uri.TryCreate(hotlineUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return safeName;
            }

            return $"<a href=\"{Html(uri.ToString())}\">{safeName}</a>";
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }
    }
}
