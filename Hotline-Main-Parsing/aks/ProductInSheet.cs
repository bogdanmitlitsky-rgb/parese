namespace Hotline_Main_Parsing.aks
{
    public class ProductInSheet
    {
        public string Code { get; set; } = default!;
        public string Id { get; set; } = default!;
        public string Name { get; set; } = default!;
        public decimal BitPrice { get; set; }
        public bool RrcBitPriceApplied { get; set; }
        public decimal? RrcBitPrice { get; set; }
        public decimal Price { get; set; }
        public decimal? SpecialPrice { get; set; }
        public decimal ReadyPrice { get; set; }
        public string? PriceAvailableness { get; set; } = null;
        public string? PriceColor { get; set; } = null;
        public string Url { get; set; } = default!;
        public bool ParseMarkOld { get; set; }
        public bool SwitchParseMarkOldToNew { get; set; }
        public bool ParseMarkRRC { get; set; }
        public decimal? BuyPriceInDollars { get; set; }
        public decimal? BuyPriceInGRN { get; set; }
        public string? Availableness { get; set; } = null;

        public decimal[] PriceRange { get; set; } = new decimal[0];
        public bool ParseMarkNew { get; set; }
        public bool DumpByLowest { get; set; }
        public string Note { get; set; } = string.Empty;

        public int OffersCount { get; set; }
        public char TehnoBit { get; set; }
        public char Ua_1 { get; set; }
    }
}
