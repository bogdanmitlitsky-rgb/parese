using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hotline_Main_Parsing.common
{
    public class Shop
    {
        public string Name { get; set; }
        public string Url { get; set; }
        public decimal Price { get; set; }
        public bool IsDiscounted { get; set; }
        public string Warranty { get; set; } = string.Empty;

        public override string ToString()
        {
            var warranty = string.IsNullOrWhiteSpace(Warranty) ? string.Empty : $" ({Warranty})";
            return $"{Name} - {Price}{warranty}";
        }
    }
}
