namespace Hotline_Main_Parsing.common
{
    public sealed class SoftPriceAdjustmentResult
    {
        public bool Applied { get; set; }
        public string ShopName { get; set; } = string.Empty;
        public decimal CompetitorPrice { get; set; }
        public decimal OldPrice { get; set; }
        public decimal NewPrice { get; set; }
        public decimal GapPercent { get; set; }
        public string Reason { get; set; } = string.Empty;

        public static SoftPriceAdjustmentResult None(string reason = "")
        {
            return new SoftPriceAdjustmentResult
            {
                Applied = false,
                Reason = reason
            };
        }
    }
}
