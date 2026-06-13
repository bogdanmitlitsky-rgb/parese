namespace Hotline_Main_Parsing.common
{
    public static class AntiDumpingService
    {
        private static readonly HashSet<string> OwnShopNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "TEHNO-BIT.COM.UA",
            "1UA.IN"
        };

        public static AntiDumpingResult Analyze(IEnumerable<Shop> shops, AntiDumpingSettings settings)
        {
            var ordered = shops
                .Where(shop => shop.Price > 0)
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
            return !string.IsNullOrWhiteSpace(shopName) && OwnShopNames.Contains(shopName);
        }
    }
}
