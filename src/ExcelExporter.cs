using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace BambuHistoryScreenshot
{
    public class ExcelExportResult
    {
        public string OutputPath { get; set; }
        public int TotalRows { get; set; }
        public int EmbeddedImageCount { get; set; }
    }

    public static class ExcelExporter
    {
        private class EmbeddedImageItem
        {
            public int RowIndex0Based; // 1 for Excel row 2, 2 for Excel row 3, ...
            public int ImageNumber;    // 1-based: image1.png, image2.png, ...
            public byte[] PngBytes;
            public long WidthEmu;
            public long HeightEmu;
            public long ColOffsetEmu;
            public long RowOffsetEmu;
        }

        private const long EmuPerPixel = 9525L;
        private const int MaxThumbWidthPx = 180;
        private const int MaxThumbHeightPx = 180;
        private const double ImageRowHeightPt = 146.0; // ~195px at 96 DPI
        private const double TextRowHeightPt = 24.0;
        private const double HeaderRowHeightPt = 28.0;

        // Matches filenames like: 2026-11-19-模型名.png, 20261119-模型名.png, 2026-11-19_1951-模型名.png
        private static readonly Regex SavedFilePattern = new Regex(
            @"^(?<y>20\d{2})[-_]?(?<m>\d{2})[-_]?(?<d>\d{2})(?:[_-](?<H>\d{2})(?<M>\d{2}))?-(?<name>.+)$",
            RegexOptions.Compiled);

        public static List<PrintHistoryCard> LoadCardsFromScreenshotFolder(string folder)
        {
            List<PrintHistoryCard> list = new List<PrintHistoryCard>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return list;
            }

            string[] files = Directory.GetFiles(folder, "*.png");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < files.Length; i++)
            {
                string filePath = files[i];
                string baseName = Path.GetFileNameWithoutExtension(filePath);
                PrintHistoryCard card = new PrintHistoryCard();
                card.SavedFilePath = filePath;
                card.CaptureState = "已保存";
                card.Status = string.Empty;

                Match m = SavedFilePattern.Match(baseName);
                if (m.Success)
                {
                    int y, mo, d, H = 0, M = 0;
                    int.TryParse(m.Groups["y"].Value, out y);
                    int.TryParse(m.Groups["m"].Value, out mo);
                    int.TryParse(m.Groups["d"].Value, out d);
                    bool hasTime = m.Groups["H"].Success && m.Groups["M"].Success;
                    if (hasTime)
                    {
                        int.TryParse(m.Groups["H"].Value, out H);
                        int.TryParse(m.Groups["M"].Value, out M);
                    }
                    try
                    {
                        card.PrintDate = new DateTime(y, mo, d, H, M, 0);
                        card.HasTime = hasTime;
                    }
                    catch
                    {
                        card.PrintDate = File.GetLastWriteTime(filePath);
                        card.HasTime = true;
                    }
                    card.Name = m.Groups["name"].Value;
                }
                else
                {
                    card.PrintDate = File.GetLastWriteTime(filePath);
                    card.HasTime = true;
                    card.Name = baseName;
                }

                card.UniqueKey = card.BuildUniqueKey();
                list.Add(card);
            }

            // Sort newest first
            list.Sort(delegate(PrintHistoryCard a, PrintHistoryCard b)
            {
                return b.PrintDate.CompareTo(a.PrintDate);
            });

            return list;
        }

        public static ExcelExportResult ExportToXlsx(
            string xlsxPath,
            List<PrintHistoryCard> cards,
            AppConfig config)
        {
            if (cards == null) cards = new List<PrintHistoryCard>();
            string dir = Path.GetDirectoryName(xlsxPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            List<EmbeddedImageItem> images = new List<EmbeddedImageItem>();
            bool[] rowHasImage = new bool[cards.Count];

            // Prepare embedded images
            for (int i = 0; i < cards.Count; i++)
            {
                PrintHistoryCard card = cards[i];
                string imgPath = ResolveCardImagePath(card, config);
                if (string.IsNullOrEmpty(imgPath) || !File.Exists(imgPath))
                {
                    continue;
                }

                try
                {
                    byte[] pngBytes = LoadAsPngBytes(imgPath);
                    int origW, origH;
                    GetImageDimensions(pngBytes, out origW, out origH);
                    if (origW <= 0 || origH <= 0) continue;

                    double scale = Math.Min((double)MaxThumbWidthPx / origW, (double)MaxThumbHeightPx / origH);
                    if (scale > 1.0) scale = 1.0;
                    int dispW = Math.Max(20, (int)Math.Round(origW * scale));
                    int dispH = Math.Max(20, (int)Math.Round(origH * scale));

                    // Center slightly inside Column D (~201px wide x ~195px tall)
                    int padLeftPx = Math.Max(6, (198 - dispW) / 2);
                    int padTopPx = Math.Max(6, (192 - dispH) / 2);

                    EmbeddedImageItem item = new EmbeddedImageItem();
                    item.RowIndex0Based = i + 1; // Row 0 is header; data row i is Excel row i+2 (0-based i+1)
                    item.ImageNumber = images.Count + 1;
                    item.PngBytes = pngBytes;
                    item.WidthEmu = dispW * EmuPerPixel;
                    item.HeightEmu = dispH * EmuPerPixel;
                    item.ColOffsetEmu = padLeftPx * EmuPerPixel;
                    item.RowOffsetEmu = padTopPx * EmuPerPixel;

                    images.Add(item);
                    rowHasImage[i] = true;
                }
                catch
                {
                    // Skip unreadable image without failing the whole export
                }
            }

            bool hasImages = images.Count > 0;

            using (FileStream fs = new FileStream(xlsxPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create, false, Encoding.UTF8))
            {
                WriteTextEntry(zip, "[Content_Types].xml", BuildContentTypesXml(hasImages));
                WriteTextEntry(zip, "_rels/.rels", BuildRootRelsXml());
                WriteTextEntry(zip, "xl/workbook.xml", BuildWorkbookXml());
                WriteTextEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
                WriteTextEntry(zip, "xl/styles.xml", BuildStylesXml());
                WriteTextEntry(zip, "xl/worksheets/sheet1.xml", BuildSheetXml(cards, rowHasImage, hasImages));

                if (hasImages)
                {
                    WriteTextEntry(zip, "xl/worksheets/_rels/sheet1.xml.rels", BuildSheetRelsXml());
                    WriteTextEntry(zip, "xl/drawings/drawing1.xml", BuildDrawingXml(images));
                    WriteTextEntry(zip, "xl/drawings/_rels/drawing1.xml.rels", BuildDrawingRelsXml(images));

                    for (int i = 0; i < images.Count; i++)
                    {
                        EmbeddedImageItem img = images[i];
                        string mediaPath = string.Format("xl/media/image{0}.png", img.ImageNumber);
                        ZipArchiveEntry entry = zip.CreateEntry(mediaPath, CompressionLevel.NoCompression);
                        using (Stream es = entry.Open())
                        {
                            es.Write(img.PngBytes, 0, img.PngBytes.Length);
                        }
                    }
                }
            }

            ExcelExportResult res = new ExcelExportResult();
            res.OutputPath = xlsxPath;
            res.TotalRows = cards.Count;
            res.EmbeddedImageCount = images.Count;
            return res;
        }

        private static string ResolveCardImagePath(PrintHistoryCard card, AppConfig config)
        {
            if (card == null) return string.Empty;
            if (!string.IsNullOrEmpty(card.SavedFilePath) && File.Exists(card.SavedFilePath))
            {
                return card.SavedFilePath;
            }

            if (config != null && !string.IsNullOrEmpty(config.OutputFolder) && Directory.Exists(config.OutputFolder))
            {
                string expectedName = FilenameHelper.FormatFileName(card, config.NamingTemplate);
                string candidate = Path.Combine(config.OutputFolder, expectedName);
                if (File.Exists(candidate))
                {
                    card.SavedFilePath = candidate;
                    return candidate;
                }
            }

            return string.Empty;
        }

        private static byte[] LoadAsPngBytes(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            // If already a valid PNG signature, return bytes directly without re-encoding
            if (raw.Length >= 8 &&
                raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47)
            {
                return raw;
            }

            using (MemoryStream inMs = new MemoryStream(raw))
            using (Image img = Image.FromStream(inMs))
            using (MemoryStream outMs = new MemoryStream())
            {
                img.Save(outMs, ImageFormat.Png);
                return outMs.ToArray();
            }
        }

        private static void GetImageDimensions(byte[] pngBytes, out int width, out int height)
        {
            width = 0;
            height = 0;
            using (MemoryStream ms = new MemoryStream(pngBytes))
            using (Image img = Image.FromStream(ms, false, false))
            {
                width = img.Width;
                height = img.Height;
            }
        }

        private static void WriteTextEntry(ZipArchive zip, string entryPath, string content)
        {
            ZipArchiveEntry entry = zip.CreateEntry(entryPath, CompressionLevel.Optimal);
            using (Stream s = entry.Open())
            using (StreamWriter sw = new StreamWriter(s, new UTF8Encoding(false)))
            {
                sw.Write(content);
            }
        }

        private static string BuildContentTypesXml(bool hasImages)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            if (hasImages)
            {
                sb.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
            }
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            if (hasImages)
            {
                sb.Append("<Override PartName=\"/xl/drawings/drawing1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
            }
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string BuildRootRelsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                 + "</Relationships>";
        }

        private static string BuildWorkbookXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
                 + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
                 + "<sheets>"
                 + "<sheet name=\"打印历史\" sheetId=\"1\" r:id=\"rId1\"/>"
                 + "</sheets>"
                 + "</workbook>";
        }

        private static string BuildWorkbookRelsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
                 + "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                 + "</Relationships>";
        }

        private static string BuildStylesXml()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

            // Fonts: 0=default, 1=header white bold, 2=green bold, 3=orange bold, 4=red bold
            sb.Append("<fonts count=\"5\">");
            sb.Append("<font><sz val=\"10\"/><color rgb=\"FF1F2937\"/><name val=\"Microsoft YaHei\"/></font>");
            sb.Append("<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Microsoft YaHei\"/></font>");
            sb.Append("<font><b/><sz val=\"10\"/><color rgb=\"FF00AE42\"/><name val=\"Microsoft YaHei\"/></font>");
            sb.Append("<font><b/><sz val=\"10\"/><color rgb=\"FFFF6F00\"/><name val=\"Microsoft YaHei\"/></font>");
            sb.Append("<font><b/><sz val=\"10\"/><color rgb=\"FFD01B1B\"/><name val=\"Microsoft YaHei\"/></font>");
            sb.Append("</fonts>");

            // Fills: 0=none, 1=gray125 (required by Excel), 2=Bambu green header
            sb.Append("<fills count=\"3\">");
            sb.Append("<fill><patternFill patternType=\"none\"/></fill>");
            sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");
            sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF00AE42\"/><bgColor indexed=\"64\"/></patternFill></fill>");
            sb.Append("</fills>");

            // Borders: 0=none, 1=thin light gray
            sb.Append("<borders count=\"2\">");
            sb.Append("<border><left/><right/><top/><bottom/><diagonal/></border>");
            sb.Append("<border>");
            sb.Append("<left style=\"thin\"><color rgb=\"FFDDE3EA\"/></left>");
            sb.Append("<right style=\"thin\"><color rgb=\"FFDDE3EA\"/></right>");
            sb.Append("<top style=\"thin\"><color rgb=\"FFDDE3EA\"/></top>");
            sb.Append("<bottom style=\"thin\"><color rgb=\"FFDDE3EA\"/></bottom>");
            sb.Append("<diagonal/>");
            sb.Append("</border>");
            sb.Append("</borders>");

            sb.Append("<cellStyleXfs count=\"1\">");
            sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/>");
            sb.Append("</cellStyleXfs>");

            // cellXfs:
            // 0 = default
            // 1 = header (white bold on Bambu green, centered)
            // 2 = name text (left, v-center, wrap)
            // 3 = centered cell (date / default status / screenshot cell)
            // 4 = status success (green bold, centered)
            // 5 = status printing (orange bold, centered)
            // 6 = status canceled/failed (red bold, centered)
            sb.Append("<cellXfs count=\"7\">");
            sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>");
            sb.Append("<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>");
            sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"left\" vertical=\"center\" wrapText=\"1\"/></xf>");
            sb.Append("<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\" wrapText=\"1\"/></xf>");
            sb.Append("<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>");
            sb.Append("<xf numFmtId=\"0\" fontId=\"3\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>");
            sb.Append("<xf numFmtId=\"0\" fontId=\"4\" fillId=\"0\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>");
            sb.Append("</cellXfs>");

            sb.Append("<cellStyles count=\"1\">");
            sb.Append("<cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/>");
            sb.Append("</cellStyles>");

            sb.Append("</styleSheet>");
            return sb.ToString();
        }

        private static string BuildSheetXml(List<PrintHistoryCard> cards, bool[] rowHasImage, bool hasImages)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ");
            sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");

            // Freeze header row
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\">");
            sb.Append("<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            sb.Append("</sheetView></sheetViews>");

            sb.Append("<sheetFormatPr defaultRowHeight=\"24\"/>");

            // Column widths: Col 1=日期, Col 2=名字, Col 3=打印状态, Col 4=截图
            sb.Append("<cols>");
            sb.Append("<col min=\"1\" max=\"1\" width=\"21\" customWidth=\"1\"/>");
            sb.Append("<col min=\"2\" max=\"2\" width=\"36\" customWidth=\"1\"/>");
            sb.Append("<col min=\"3\" max=\"3\" width=\"14\" customWidth=\"1\"/>");
            sb.Append("<col min=\"4\" max=\"4\" width=\"28\" customWidth=\"1\"/>");
            sb.Append("</cols>");

            sb.Append("<sheetData>");

            // Header row (Row 1)
            sb.AppendFormat(CultureInfo.InvariantCulture,
                "<row r=\"1\" ht=\"{0:0.##}\" customHeight=\"1\">", HeaderRowHeightPt);
            AppendInlineStringCell(sb, "A1", "日期", 1);
            AppendInlineStringCell(sb, "B1", "名字", 1);
            AppendInlineStringCell(sb, "C1", "打印状态", 1);
            AppendInlineStringCell(sb, "D1", "截图", 1);
            sb.Append("</row>");

            // Data rows
            for (int i = 0; i < cards.Count; i++)
            {
                PrintHistoryCard c = cards[i];
                int r = i + 2;
                double rowHt = rowHasImage[i] ? ImageRowHeightPt : TextRowHeightPt;

                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "<row r=\"{0}\" ht=\"{1:0.##}\" customHeight=\"1\">", r, rowHt);

                string dateText = c.FormatDisplayDate();
                string nameText = c.Name ?? string.Empty;
                string statusText = c.Status ?? string.Empty;
                int statusStyle = GetStatusStyleIndex(statusText);

                AppendInlineStringCell(sb, "A" + r, dateText, 3);
                AppendInlineStringCell(sb, "B" + r, nameText, 2);
                AppendInlineStringCell(sb, "C" + r, statusText, statusStyle);
                AppendInlineStringCell(sb, "D" + r, rowHasImage[i] ? string.Empty : "（未截图）", 3);

                sb.Append("</row>");
            }

            sb.Append("</sheetData>");

            if (hasImages)
            {
                sb.Append("<drawing r:id=\"rId1\"/>");
            }

            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private static int GetStatusStyleIndex(string status)
        {
            if (string.IsNullOrEmpty(status)) return 3;
            string s = status.ToLowerInvariant();
            if (s.Contains("成功") || s.Contains("完成") || s.Contains("success") || s.Contains("finish"))
            {
                return 4;
            }
            if (s.Contains("正在打印") || s.Contains("打印中") || s.Contains("printing") || s.Contains("running"))
            {
                return 5;
            }
            if (s.Contains("取消") || s.Contains("失败") || s.Contains("cancel") || s.Contains("fail"))
            {
                return 6;
            }
            return 3;
        }

        private static void AppendInlineStringCell(StringBuilder sb, string cellRef, string text, int styleIndex)
        {
            string safe = EscapeXml(text);
            sb.AppendFormat("<c r=\"{0}\" s=\"{1}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{2}</t></is></c>",
                cellRef, styleIndex, safe);
        }

        private static string BuildSheetRelsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
                 + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                 + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing1.xml\"/>"
                 + "</Relationships>";
        }

        private static string BuildDrawingXml(List<EmbeddedImageItem> images)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" ");
            sb.Append("xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" ");
            sb.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");

            for (int i = 0; i < images.Count; i++)
            {
                EmbeddedImageItem img = images[i];
                int picId = img.ImageNumber + 1;
                long toColOff = img.ColOffsetEmu + img.WidthEmu;
                long toRowOff = img.RowOffsetEmu + img.HeightEmu;

                // Two-cell anchor strictly inside Column D (col=3) of the item's row so it moves with the cell and preserves aspect ratio
                sb.Append("<xdr:twoCellAnchor editAs=\"oneCell\">");
                sb.AppendFormat("<xdr:from><xdr:col>3</xdr:col><xdr:colOff>{0}</xdr:colOff><xdr:row>{1}</xdr:row><xdr:rowOff>{2}</xdr:rowOff></xdr:from>",
                    img.ColOffsetEmu, img.RowIndex0Based, img.RowOffsetEmu);
                sb.AppendFormat("<xdr:to><xdr:col>3</xdr:col><xdr:colOff>{0}</xdr:colOff><xdr:row>{1}</xdr:row><xdr:rowOff>{2}</xdr:rowOff></xdr:to>",
                    toColOff, img.RowIndex0Based, toRowOff);

                sb.Append("<xdr:pic>");
                sb.Append("<xdr:nvPicPr>");
                sb.AppendFormat("<xdr:cNvPr id=\"{0}\" name=\"Picture {1}\"/>", picId, img.ImageNumber);
                sb.Append("<xdr:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></xdr:cNvPicPr>");
                sb.Append("</xdr:nvPicPr>");

                sb.Append("<xdr:blipFill>");
                sb.AppendFormat("<a:blip r:embed=\"rId{0}\"/>", img.ImageNumber);
                sb.Append("<a:stretch><a:fillRect/></a:stretch>");
                sb.Append("</xdr:blipFill>");

                sb.Append("<xdr:spPr>");
                sb.AppendFormat("<a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{0}\" cy=\"{1}\"/></a:xfrm>",
                    img.WidthEmu, img.HeightEmu);
                sb.Append("<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom>");
                sb.Append("</xdr:spPr>");
                sb.Append("</xdr:pic>");

                sb.Append("<xdr:clientData/>");
                sb.Append("</xdr:twoCellAnchor>");
            }

            sb.Append("</xdr:wsDr>");
            return sb.ToString();
        }

        private static string BuildDrawingRelsXml(List<EmbeddedImageItem> images)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < images.Count; i++)
            {
                EmbeddedImageItem img = images[i];
                sb.AppendFormat("<Relationship Id=\"rId{0}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/image{0}.png\"/>",
                    img.ImageNumber);
            }
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                // Strip illegal XML 1.0 control characters
                if (c < 0x20 && c != '\t' && c != '\n' && c != '\r')
                {
                    continue;
                }
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
