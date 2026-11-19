using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace BambuHistoryScreenshot
{
    public class CaptureFilter
    {
        public string StatusFilter { get; set; } // "", "success", "printing", "canceled"
        public bool EnableDateFilter { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string Keyword { get; set; }

        public CaptureFilter()
        {
            StatusFilter = string.Empty;
            EnableDateFilter = false;
            StartDate = DateTime.Today.AddMonths(-1);
            EndDate = DateTime.Today;
            Keyword = string.Empty;
        }

        public bool Matches(PrintHistoryCard card)
        {
            if (card == null) return false;

            if (!string.IsNullOrEmpty(StatusFilter))
            {
                string st = (card.Status ?? string.Empty).ToLowerInvariant();
                if (StatusFilter == "success")
                {
                    if (!st.Contains("成功") && !st.Contains("完成") && !st.Contains("success") && !st.Contains("finish") && !st.Contains("complete"))
                        return false;
                }
                else if (StatusFilter == "printing")
                {
                    if (!st.Contains("打印中") && !st.Contains("正在打印") && !st.Contains("printing") && !st.Contains("running"))
                        return false;
                }
                else if (StatusFilter == "canceled")
                {
                    if (!st.Contains("取消") && !st.Contains("失败") && !st.Contains("cancel") && !st.Contains("fail"))
                        return false;
                }
            }

            if (EnableDateFilter && card.PrintDate != DateTime.MinValue)
            {
                DateTime day = card.PrintDate.Date;
                if (day < StartDate.Date || day > EndDate.Date)
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(Keyword))
            {
                string kw = Keyword.Trim();
                bool hit = (card.Name != null && card.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (card.Device != null && card.Device.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (card.Plate != null && card.Plate.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        || (card.Status != null && card.Status.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!hit) return false;
            }

            return true;
        }
    }

    public class CaptureSummary
    {
        public int SavedCount { get; set; }
        public int SkippedCount { get; set; }
        public int FailedCount { get; set; }
        public string LastSavedPath { get; set; }
    }

    public static class ScreenshotCapturer
    {
        public static Bitmap CaptureScreenRegion(Rectangle screenRect)
        {
            if (screenRect.Width <= 0 || screenRect.Height <= 0)
            {
                return null;
            }

            Bitmap bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(screenRect.Left, screenRect.Top, 0, 0, screenRect.Size, CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }

        public static CaptureSummary CaptureCardsFromViewport(
            Rectangle viewportRect,
            List<PrintHistoryCard> cards,
            AppConfig config,
            HashSet<string> alreadyCapturedKeys,
            Action<PrintHistoryCard, string> onCardProcessed)
        {
            CaptureSummary summary = new CaptureSummary();
            if (cards == null || cards.Count == 0 || viewportRect.Width <= 0 || viewportRect.Height <= 0)
            {
                return summary;
            }

            Directory.CreateDirectory(config.OutputFolder);

            using (Bitmap vpBitmap = CaptureScreenRegion(viewportRect))
            {
                if (vpBitmap == null)
                {
                    return summary;
                }

                for (int i = 0; i < cards.Count; i++)
                {
                    PrintHistoryCard card = cards[i];
                    if (!card.IsFullyVisible)
                    {
                        continue;
                    }

                    if (alreadyCapturedKeys != null && alreadyCapturedKeys.Contains(card.UniqueKey))
                    {
                        continue;
                    }

                    try
                    {
                        Rectangle localRect = new Rectangle(
                            card.Bounds.X - viewportRect.X,
                            card.Bounds.Y - viewportRect.Y,
                            card.Bounds.Width,
                            card.Bounds.Height);

                        if (card.NeedsVisualTopRefinement)
                        {
                            localRect = RefineVisualThumbnailBox(vpBitmap, localRect);
                        }

                        int pad = Math.Max(0, config.PaddingPixels);
                        int x1 = Math.Max(0, localRect.Left - pad);
                        int y1 = Math.Max(0, localRect.Top - pad);
                        int x2 = Math.Min(vpBitmap.Width, localRect.Right + pad);
                        int y2 = Math.Min(vpBitmap.Height, localRect.Bottom + pad);

                        Rectangle cropRect = Rectangle.FromLTRB(x1, y1, x2, y2);
                        if (cropRect.Width < 40 || cropRect.Height < 40)
                        {
                            card.CaptureState = "裁剪区域无效";
                            summary.FailedCount++;
                            continue;
                        }

                        string desiredName = FilenameHelper.FormatFileName(card, config.NamingTemplate);
                        bool shouldSkip;
                        string targetPath = FilenameHelper.ResolveTargetPath(
                            config.OutputFolder,
                            desiredName,
                            config.DuplicateMode,
                            out shouldSkip);

                        if (shouldSkip)
                        {
                            card.SavedFilePath = targetPath;
                            card.CaptureState = "已跳过(重名)";
                            summary.SkippedCount++;
                            if (alreadyCapturedKeys != null) alreadyCapturedKeys.Add(card.UniqueKey);
                            if (onCardProcessed != null) onCardProcessed(card, targetPath);
                            continue;
                        }

                        using (Bitmap cropped = vpBitmap.Clone(cropRect, PixelFormat.Format32bppArgb))
                        {
                            cropped.Save(targetPath, ImageFormat.Png);
                        }

                        card.SavedFilePath = targetPath;
                        card.CaptureState = "已保存";
                        summary.SavedCount++;
                        summary.LastSavedPath = targetPath;
                        if (alreadyCapturedKeys != null) alreadyCapturedKeys.Add(card.UniqueKey);
                        if (onCardProcessed != null) onCardProcessed(card, targetPath);
                    }
                    catch (Exception ex)
                    {
                        card.CaptureState = "失败: " + ex.Message;
                        summary.FailedCount++;
                    }
                }
            }

            return summary;
        }

        // Detects Bambu Studio's .PrintHistoryImg background (#E4E4E4 light / #575757 dark) if UIA didn't expose the image bounds
        private static Rectangle RefineVisualThumbnailBox(Bitmap bmp, Rectangle estimatedLocal)
        {
            try
            {
                int probeX = Math.Max(0, Math.Min(bmp.Width - 1, estimatedLocal.Left + estimatedLocal.Width / 2));
                int startY = Math.Max(0, Math.Min(bmp.Height - 1, estimatedLocal.Bottom - 70));
                int minSearchY = Math.Max(0, estimatedLocal.Top - 80);

                int boxBottom = -1;
                int boxTop = -1;
                Color boxColor = Color.Empty;

                for (int y = startY; y >= minSearchY; y--)
                {
                    Color c = bmp.GetPixel(probeX, y);
                    bool isBambuGray = (IsNearColor(c, 228, 228, 228, 12) || IsNearColor(c, 87, 87, 87, 12));
                    if (isBambuGray)
                    {
                        if (boxBottom < 0)
                        {
                            boxBottom = y;
                            boxColor = c;
                        }
                        boxTop = y;
                    }
                    else if (boxBottom >= 0 && (boxBottom - y) > 40)
                    {
                        // Check if we exited the top of the box (check a few pixels left/right to avoid model pixels inside box)
                        Color cLeft = bmp.GetPixel(Math.Max(0, estimatedLocal.Left + 25), y);
                        Color cRight = bmp.GetPixel(Math.Min(bmp.Width - 1, estimatedLocal.Right - 25), y);
                        bool leftStillBox = IsNearColor(cLeft, boxColor.R, boxColor.G, boxColor.B, 14);
                        bool rightStillBox = IsNearColor(cRight, boxColor.R, boxColor.G, boxColor.B, 14);
                        if (!leftStillBox && !rightStillBox)
                        {
                            break;
                        }
                        boxTop = y;
                    }
                }

                if (boxTop >= 0 && boxBottom > boxTop + 80)
                {
                    int midBoxY = boxTop + 20;
                    int left = probeX;
                    while (left > 0 && IsNearColor(bmp.GetPixel(left - 1, midBoxY), boxColor.R, boxColor.G, boxColor.B, 18))
                    {
                        left--;
                    }
                    int right = probeX;
                    while (right < bmp.Width - 1 && IsNearColor(bmp.GetPixel(right + 1, midBoxY), boxColor.R, boxColor.G, boxColor.B, 18))
                    {
                        right++;
                    }

                    if (right - left >= 120)
                    {
                        return Rectangle.FromLTRB(
                            Math.Min(left, estimatedLocal.Left),
                            boxTop,
                            Math.Max(right, estimatedLocal.Right),
                            estimatedLocal.Bottom);
                    }
                }
            }
            catch
            {
            }

            return estimatedLocal;
        }

        private static bool IsNearColor(Color c, int r, int g, int b, int tol)
        {
            return Math.Abs(c.R - r) <= tol && Math.Abs(c.G - g) <= tol && Math.Abs(c.B - b) <= tol;
        }
    }
}
