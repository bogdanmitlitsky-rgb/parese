using Microsoft.Extensions.Configuration;

namespace Hotline_Main_Parsing.common
{
    public sealed class AntiDumpingSettings
    {
        public bool Enabled { get; set; } = true;
        public decimal Percent { get; set; } = 10m;
        public int MinOffers { get; set; } = 3;

        public static AntiDumpingSettings FromConfig(IConfiguration config)
        {
            var settings = new AntiDumpingSettings();

            if (bool.TryParse(config["AntiDumpingEnabled"], out bool enabled))
            {
                settings.Enabled = enabled;
            }

            if (decimal.TryParse(config["AntiDumpingPercent"], out decimal percent) && percent > 0)
            {
                settings.Percent = percent;
            }

            if (int.TryParse(config["AntiDumpingMinOffers"], out int minOffers) && minOffers > 1)
            {
                settings.MinOffers = minOffers;
            }

            return settings;
        }
    }
}
