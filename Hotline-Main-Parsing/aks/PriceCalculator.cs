using Hotline_Main_Parsing.common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hotline_Main_Parsing.aks
{
    public class PriceCalculator
    {
        public static void CalculatePrices(ProductInSheet productInSheet, List<Shop> shops, decimal rangePercent)
        {
            shops = shops.Where(shop => !shop.IsDiscounted).ToList();

            decimal priceWithoutPercent = Math.Round((productInSheet.Price * (100 - rangePercent)) / 100);
            int outputPricesCount = 7;
            decimal leastPrice = 0;
            List<Shop> shopsRange = new();
            if (productInSheet.ParseMarkNew)
            {
                shopsRange = shops.Where(s => s.Price >= priceWithoutPercent).OrderBy(s => s.Price).Take(outputPricesCount + 1).ToList();
            }
            else
            {
                shopsRange = shops.OrderBy(s => s.Price).Take(outputPricesCount + 1).ToList();
            }
            productInSheet.PriceRange = shopsRange.Select(s => s.Price).ToArray();
            if (!productInSheet.ParseMarkOld && !productInSheet.ParseMarkNew)
            {
                leastPrice = productInSheet.Price;
                productInSheet.BitPrice = leastPrice;
                productInSheet.ReadyPrice = leastPrice;
                return;
            }

            if (shopsRange.Count == 0)
            {
                productInSheet.BitPrice = productInSheet.Price;
                productInSheet.ReadyPrice = productInSheet.Price;
                return;
            }

            if (productInSheet.ParseMarkOld && productInSheet.ParseMarkNew)
            {
                var competitor = shopsRange.FirstOrDefault(shop =>
                    shop.Price >= priceWithoutPercent &&
                    !AntiDumpingService.IsOwnShop(shop.Name));

                if (competitor == null)
                {
                    productInSheet.BitPrice = productInSheet.Price;
                    productInSheet.ReadyPrice = productInSheet.Price;
                    return;
                }

                decimal newPrice = competitor.Price - 1;
                if (productInSheet.BuyPriceInGRN.HasValue && newPrice < productInSheet.BuyPriceInGRN.Value)
                {
                    newPrice = productInSheet.BuyPriceInGRN.Value;
                }

                if (newPrice < priceWithoutPercent)
                {
                    newPrice = priceWithoutPercent;
                }

                productInSheet.BitPrice = newPrice;
                productInSheet.ReadyPrice = newPrice;
                return;
            }

            bool priceIsFromParsing = false;
            for (int priceIndex = 0; priceIndex < shopsRange.Count && priceIndex < outputPricesCount; priceIndex++)
            {
                leastPrice = shopsRange[priceIndex].Price;
                priceIsFromParsing = false;
                if (AntiDumpingService.IsOwnShop(shopsRange[priceIndex].Name))
                {
                    continue;
                    if (shopsRange.Count > priceIndex + 1)
                    {
                        leastPrice = shopsRange[priceIndex + 1].Price;
                    }
                    else
                    {
                        leastPrice = shopsRange[priceIndex].Price;
                    }
                }
                if (productInSheet.ParseMarkOld && productInSheet.ParseMarkNew)
                {
                    if (leastPrice >= priceWithoutPercent)
                    {
                        leastPrice = shopsRange[priceIndex].Price - 1;
                        break;
                    }
                }
                if (productInSheet.BuyPriceInGRN.HasValue)
                {
                    if (priceWithoutPercent < leastPrice - 1 && leastPrice - 1 > productInSheet.BuyPriceInGRN.Value)
                    {
                        if (AntiDumpingService.IsOwnShop(shopsRange[priceIndex].Name))
                        {
                            if (shopsRange.Count > priceIndex + 1)
                            {
                                leastPrice = shopsRange[priceIndex + 1].Price;
                            }
                            else
                            {
                                leastPrice = shopsRange[priceIndex].Price;
                            }
                        }
                        else
                        {
                            leastPrice = shopsRange[priceIndex].Price;
                        }
                        priceIsFromParsing = true;
                        break;
                    }
                    else
                    {

                        if (productInSheet.Price < productInSheet.BuyPriceInGRN)
                        {
                            leastPrice = productInSheet.BuyPriceInGRN.Value;
                        }
                        else
                        {
                            leastPrice = productInSheet.Price;
                        }
                        priceIsFromParsing = false;
                    }
                }
                else
                {
                    if (priceWithoutPercent < leastPrice - 1)
                    {
                        if (AntiDumpingService.IsOwnShop(shopsRange[priceIndex].Name))
                        {
                            if (shopsRange.Count > priceIndex + 1)
                            {
                                leastPrice = shopsRange[priceIndex + 1].Price;
                            }
                            else
                            {
                                leastPrice = shopsRange[priceIndex].Price;
                            }
                        }
                        else
                        {
                            leastPrice = shopsRange[priceIndex].Price;
                        }
                        priceIsFromParsing = true;
                        break;
                    }
                }
            }
            if (!productInSheet.ParseMarkOld || !productInSheet.ParseMarkNew)
            {
                if (productInSheet.BuyPriceInGRN.HasValue && leastPrice < productInSheet.BuyPriceInGRN && productInSheet.ParseMarkOld)
                {
                    leastPrice = productInSheet.BuyPriceInGRN.Value;
                }
                if (!productInSheet.ParseMarkOld || !productInSheet.ParseMarkNew)
                {
                    if (leastPrice < priceWithoutPercent)
                    {
                        leastPrice = priceWithoutPercent;
                    }
                }
            }
            if (!priceIsFromParsing)
            {
                productInSheet.BitPrice = leastPrice;
                productInSheet.ReadyPrice = leastPrice;
                return;
            }
            productInSheet.BitPrice = leastPrice - 1;
            productInSheet.ReadyPrice = leastPrice - 1;
            return;
        }
    }
}
