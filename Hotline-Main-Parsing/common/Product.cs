using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hotline_Main_Parsing.common
{
    public class Product
    {
        public string Url { get; set; } = default!;

        public bool OffersLoaded { get; set; }

        public int DiscountedOffersSkipped { get; set; }

        public List<Shop> Shops { get; set; } = new List<Shop>();
    }
}
