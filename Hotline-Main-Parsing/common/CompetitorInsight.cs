namespace Hotline_Main_Parsing.common
{
    public sealed class CompetitorInsight
    {
        public DateTime CheckedAt { get; set; } = DateTime.Now;
        public string Section { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string HotlineUrl { get; set; } = string.Empty;
        public string HotlineLinkLabel => string.IsNullOrWhiteSpace(HotlineUrl) ? string.Empty : "Hotline";
        public string ProductStatus { get; set; } = string.Empty;
        public string StatusDetails { get; set; } = string.Empty;
        public decimal OwnPrice { get; set; }
        public decimal OrientirPrice { get; set; }
        public int OffersCount { get; set; }
        public int OwnRank { get; set; }
        public int CompetitorsBelowOwnCount { get; set; }
        public int CompetitorsAboveOwnCount { get; set; }
        public string LowestShop { get; set; } = string.Empty;
        public decimal? LowestPrice { get; set; }
        public string NearestLowerShop { get; set; } = string.Empty;
        public decimal? NearestLowerPrice { get; set; }
        public decimal? NearestLowerPercent { get; set; }
        public string NearestUpperShop { get; set; } = string.Empty;
        public decimal? NearestUpperPrice { get; set; }
        public decimal? NearestUpperPercent { get; set; }
        public string MarketShop { get; set; } = string.Empty;
        public decimal? MarketPrice { get; set; }
        public string DumpingShop { get; set; } = string.Empty;
        public decimal? DumpingPrice { get; set; }
        public decimal? DumpingPercent { get; set; }
        public bool IsDumping { get; set; }
        public bool SoftPriceDropApplied { get; set; }
        public string SoftPriceDropShop { get; set; } = string.Empty;
        public decimal? SoftPriceDropFromPrice { get; set; }
        public decimal? SoftPriceDropToPrice { get; set; }
        public decimal? SoftPriceDropPercent { get; set; }
        public string CompetitorsBelowOwn { get; set; } = string.Empty;
        public string CompetitorMap { get; set; } = string.Empty;
        public decimal? DifferenceAmount { get; set; }
        public decimal? DifferencePercent { get; set; }
        public bool OwnIsHigherThanMarket { get; set; }
        public bool CanRaisePrice { get; set; }
    }
}
