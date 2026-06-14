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
        public decimal OwnPrice { get; set; }
        public decimal OrientirPrice { get; set; }
        public int OffersCount { get; set; }
        public string LowestShop { get; set; } = string.Empty;
        public decimal? LowestPrice { get; set; }
        public string MarketShop { get; set; } = string.Empty;
        public decimal? MarketPrice { get; set; }
        public string DumpingShop { get; set; } = string.Empty;
        public decimal? DumpingPrice { get; set; }
        public decimal? DumpingPercent { get; set; }
        public bool IsDumping { get; set; }
        public decimal? DifferenceAmount { get; set; }
        public decimal? DifferencePercent { get; set; }
        public bool OwnIsHigherThanMarket { get; set; }
        public bool CanRaisePrice { get; set; }
    }
}
