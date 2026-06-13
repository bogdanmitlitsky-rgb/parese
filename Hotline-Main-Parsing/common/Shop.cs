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

        public override string ToString()
        {
            return $"{Name} - {Price}";
        }
    }
}
