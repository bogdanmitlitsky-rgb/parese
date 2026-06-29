namespace Hotline_Main_Parsing.common
{
    public static class AntiDumpingService
    {
        public static AntiDumpingResult Analyze(IEnumerable<Shop> shops, AntiDumpingSettings settings)
        {
            var ordered = shops
                .Where(shop => shop.Price > 0 && !shop.IsDiscounted)
                .OrderBy(shop => shop.Price)
                .ToList();

            var result = new AntiDumpingResult
            {
                ShopsForPrice = ordered
            };

            if (!settings.Enabled)
            {
                return result;
            }

            var competitors = ordered
                .Where(shop => !IsOwnShop(shop.Name))
                .OrderBy(shop => shop.Price)
                .ToList();

            if (competitors.Count < settings.MinOffers)
            {
                return result;
            }

            var lowest = competitors[0];
            int sameLowPriceCount = competitors.Count(shop => Math.Abs(shop.Price - lowest.Price) <= 1);
            if (sameLowPriceCount > 1)
            {
                return result;
            }

            var market = competitors.Skip(1).FirstOrDefault();
            if (market == null || market.Price <= 0)
            {
                return result;
            }

            decimal gapPercent = Math.Round((market.Price - lowest.Price) / market.Price * 100m, 2);
            if (gapPercent < settings.Percent)
            {
                return result;
            }

            result.IsDumping = true;
            result.DumpingShop = lowest;
            result.MarketShop = market;
            result.DumpingPercent = gapPercent;
            result.ShopsForPrice = ordered
                .Where(shop => !ReferenceEquals(shop, lowest))
                .ToList();

            return result;
        }

        public static bool IsOwnShop(string? shopName)
        {
            return IsTehnoBitShop(shopName) || IsOneUaShop(shopName);
        }

        public static bool IsTehnoBitShop(string? shopName)
        {
            string normalized = NormalizeShopName(shopName);
            return normalized.Contains("TEHNO-BIT.COM.UA") ||
                   normalized.Contains("TEHNO-BIT") ||
                   normalized.Contains("TEHNOBIT");
        }

        public static bool IsOneUaShop(string? shopName)
        {
            string normalized = NormalizeShopName(shopName);
            return normalized.Contains("1UA.IN") ||
                   normalized == "1UA";
        }

        private static string NormalizeShopName(string? shopName)
        {
            if (string.IsNullOrWhiteSpace(shopName))
            {
                return string.Empty;
            }

            return shopName
                .Trim()
                .ToUpperInvariant()
                .Replace(" ", "")
                .Replace("\t", "")
                .Replace("\r", "")
                .Replace("\n", "");
        }
    }
}
