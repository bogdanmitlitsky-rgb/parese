using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Hotline_Main_Parsing.common
{
    internal static class CompetitorExcelExporter
    {
        private static readonly string[] Headers =
        {
            "\u0420\u0430\u0437\u0434\u0435\u043b",
            "\u0422\u043e\u0432\u0430\u0440",
            "Hotline URL",
            "\u0422\u0432\u043e\u044f \u0446\u0435\u043d\u0430",
            "\u0420\u044b\u043d\u043e\u043a",
            "\u0420\u0430\u0437\u043d\u0438\u0446\u0430 %",
            "\u041c\u0438\u043d. \u043c\u0430\u0433\u0430\u0437\u0438\u043d",
            "\u041c\u0438\u043d. \u0446\u0435\u043d\u0430",
            "\u0414\u0435\u043c\u043f\u0438\u043d\u0433",
            "\u041a\u0442\u043e \u0434\u0435\u043c\u043f\u0438\u043d\u0433\u0443\u0435\u0442",
            "\u0414\u0435\u043c\u043f. \u0446\u0435\u043d\u0430",
            "\u0414\u0435\u043c\u043f. %",
            "\u041c\u043e\u0436\u043d\u043e \u043f\u043e\u0434\u043d\u044f\u0442\u044c",
            "\u041f\u0440\u0435\u0434\u043b\u043e\u0436\u0435\u043d\u0438\u0439",
            "\u0414\u0430\u0442\u0430"
        };

        public static void WriteReport(string path, IReadOnlyList<CompetitorInsight> rows)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var hyperlinks = BuildHyperlinks(rows);
            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
            AddEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
            AddEntry(archive, "_rels/.rels", BuildRootRelsXml());
            AddEntry(archive, "docProps/app.xml", BuildAppXml());
            AddEntry(archive, "docProps/core.xml", BuildCoreXml());
            AddEntry(archive, "xl/workbook.xml", BuildWorkbookXml());
            AddEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
            AddEntry(archive, "xl/worksheets/sheet1.xml", BuildSheetXml(rows, hyperlinks));
            if (hyperlinks.Count > 0)
            {
                AddEntry(archive, "xl/worksheets/_rels/sheet1.xml.rels", BuildSheetRelsXml(hyperlinks));
            }
        }

        private static string BuildSheetXml(IReadOnlyList<CompetitorInsight> rows, IReadOnlyList<SheetHyperlink> hyperlinks)
        {
            var builder = new StringBuilder();
            builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
            builder.AppendLine("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">""");
            builder.AppendLine("<cols>");
            builder.AppendLine("""<col min="1" max="1" width="14" customWidth="1"/>""");
            builder.AppendLine("""<col min="2" max="2" width="55" customWidth="1"/>""");
            builder.AppendLine("""<col min="3" max="3" width="65" customWidth="1"/>""");
            builder.AppendLine("""<col min="4" max="15" width="16" customWidth="1"/>""");
            builder.AppendLine("</cols>");
            builder.AppendLine("<sheetData>");

            AppendRow(builder, 1, Headers);

            for (int i = 0; i < rows.Count; i++)
            {
                var item = rows[i];
                AppendRow(builder, i + 2, new[]
                {
                    item.Section,
                    item.ProductName,
                    item.HotlineUrl,
                    FormatNumber(item.OwnPrice),
                    FormatNumber(item.MarketPrice),
                    FormatNumber(item.DifferencePercent),
                    item.LowestShop,
                    FormatNumber(item.LowestPrice),
                    item.IsDumping ? "\u0414\u0430" : "\u041d\u0435\u0442",
                    item.DumpingShop,
                    FormatNumber(item.DumpingPrice),
                    FormatNumber(item.DumpingPercent),
                    item.CanRaisePrice ? "\u0414\u0430" : "\u041d\u0435\u0442",
                    item.OffersCount.ToString(CultureInfo.InvariantCulture),
                    item.CheckedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
                });
            }

            builder.AppendLine("</sheetData>");
            if (hyperlinks.Count > 0)
            {
                builder.AppendLine("<hyperlinks>");
                foreach (var hyperlink in hyperlinks)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"<hyperlink ref=\"C{hyperlink.RowNumber}\" r:id=\"{hyperlink.RelationshipId}\"/>");
                }
                builder.AppendLine("</hyperlinks>");
            }
            builder.AppendLine("</worksheet>");
            return builder.ToString();
        }

        private static List<SheetHyperlink> BuildHyperlinks(IReadOnlyList<CompetitorInsight> rows)
        {
            var hyperlinks = new List<SheetHyperlink>();
            for (int i = 0; i < rows.Count; i++)
            {
                string url = rows[i].HotlineUrl;
                if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
                {
                    continue;
                }

                hyperlinks.Add(new SheetHyperlink(i + 2, url, $"rId{hyperlinks.Count + 1}"));
            }

            return hyperlinks;
        }

        private static void AppendRow(StringBuilder builder, int rowNumber, IReadOnlyList<string> values)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowNumber}\">");
            for (int i = 0; i < values.Count; i++)
            {
                string cellReference = GetColumnName(i + 1) + rowNumber.ToString(CultureInfo.InvariantCulture);
                AppendInlineStringCell(builder, cellReference, values[i]);
            }
            builder.AppendLine("</row>");
        }

        private static void AppendInlineStringCell(StringBuilder builder, string cellReference, string value)
        {
            builder.Append(CultureInfo.InvariantCulture, $"<c r=\"{cellReference}\" t=\"inlineStr\"><is><t>");
            builder.Append(EscapeXml(value));
            builder.Append("</t></is></c>");
        }

        private static string GetColumnName(int index)
        {
            var name = new StringBuilder();
            while (index > 0)
            {
                index--;
                name.Insert(0, (char)('A' + index % 26));
                index /= 26;
            }

            return name.ToString();
        }

        private static string FormatNumber(decimal value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string FormatNumber(decimal? value)
        {
            return value.HasValue ? value.Value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty;
        }

        private static void AddEntry(ZipArchive archive, string name, string content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(content);
        }

        private static string EscapeXml(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sanitized = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (XmlConvert.IsXmlChar(character))
                {
                    sanitized.Append(character);
                }
            }

            return sanitized
                .ToString()
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }

        private static string BuildContentTypesXml()
        {
            return """
                   <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                   <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                     <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                     <Default Extension="xml" ContentType="application/xml"/>
                     <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
                     <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
                     <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                     <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                   </Types>
                   """;
        }

        private static string BuildRootRelsXml()
        {
            return """
                   <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                   <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                     <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                     <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
                     <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
                   </Relationships>
                   """;
        }

        private static string BuildWorkbookXml()
        {
            return """
                   <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                   <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                     <sheets>
                       <sheet name="&#1050;&#1086;&#1085;&#1082;&#1091;&#1088;&#1077;&#1085;&#1090;&#1099;" sheetId="1" r:id="rId1"/>
                     </sheets>
                   </workbook>
                   """;
        }

        private static string BuildWorkbookRelsXml()
        {
            return """
                   <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                   <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                     <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                   </Relationships>
                   """;
        }

        private static string BuildSheetRelsXml(IReadOnlyList<SheetHyperlink> hyperlinks)
        {
            var builder = new StringBuilder();
            builder.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
            builder.AppendLine("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
            foreach (var hyperlink in hyperlinks)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"<Relationship Id=\"{hyperlink.RelationshipId}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"{EscapeXml(hyperlink.Url)}\" TargetMode=\"External\"/>");
            }
            builder.AppendLine("</Relationships>");
            return builder.ToString();
        }

        private static string BuildAppXml()
        {
            return """
                   <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                   <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
                     <Application>Hotline Parser</Application>
                   </Properties>
                   """;
        }

        private static string BuildCoreXml()
        {
            string createdAt = XmlConvert.ToString(DateTime.UtcNow, XmlDateTimeSerializationMode.Utc);
            return $$"""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:dcmitype="http://purl.org/dc/dcmitype/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                      <dc:creator>Hotline Parser</dc:creator>
                      <cp:lastModifiedBy>Hotline Parser</cp:lastModifiedBy>
                      <dcterms:created xsi:type="dcterms:W3CDTF">{{createdAt}}</dcterms:created>
                      <dcterms:modified xsi:type="dcterms:W3CDTF">{{createdAt}}</dcterms:modified>
                    </cp:coreProperties>
                    """;
        }

        private sealed record SheetHyperlink(int RowNumber, string Url, string RelationshipId);
    }
}
