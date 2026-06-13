namespace Hotline_Main_Parsing.common
{
    public sealed class AntiDumpingResult
    {
        public List<Shop> ShopsForPrice { get; set; } = new();
        public bool IsDumping { get; set; }
        public Shop? DumpingShop { get; set; }
        public Shop? MarketShop { get; set; }
        public decimal DumpingPercent { get; set; }
    }
}
