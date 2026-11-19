using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace BambuHistoryScreenshot
{
    public class BambuUiaScanner
    {
        private class UiaNode
        {
            public UiaNode Parent;
            public List<UiaNode> Children = new List<UiaNode>();
            public string Name = string.Empty;
            public ControlType ControlType;
            public Rectangle Bounds;
            public bool IsOffscreen;
            public string ClassName = string.Empty;
            public string AutomationId = string.Empty;
            public int Depth;
            public int DateAnchorCount;
        }

        private class DateMatchInfo
        {
            public UiaNode Node;
            public DateTime ParsedDate;
            public bool HasTime;
            public bool HasParentheses;
            public string RawDateMatch;
            public string LeadingPlateText;
        }

        // Matches: (2026/11/19 19:51), 2026/11/19 19:51, 2026-11-19 19:51:02, (2026/11/19), 2026年11月19日 19:51
        private static readonly Regex DateRegex = new Regex(
            @"(?<paren>\()?\s*(?<y>20\d{2})\s*[/\-年.]\s*(?<m>\d{1,2})\s*[/\-月.]\s*(?<d>\d{1,2})(?:日)?(?:\s+(?<H>\d{1,2})\s*[:：]\s*(?<M>\d{2})(?:\s*[:：]\s*(?<S>\d{2}))?)?\s*(?<cparen>\))?",
            RegexOptions.Compiled);

        private static readonly Regex DurationRegex = new Regex(
            @"^\s*\d+(?:\.\d+)?\s*(?:h|hr|hrs|min|mins|m|s|sec|小时|分钟|秒)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StatusRegex = new Regex(
            @"^(?:正在打印|打印完成|已完成|打印成功|成功|已取消|取消|打印失败|失败|已暂停|暂停|等待中|Printing|Success|Succeeded|Finished|Completed|Canceled|Cancelled|Failed|Paused|Running)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PlateRegex = new Regex(
            @"(?:第\s*\d+\s*盘|Plate\s*\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BadgeRegex = new Regex(
            @"^(?:Gcode|3MF|STL|Cloud|Local)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex FileExtRegex = new Regex(
            @"\.(?:3mf|stl|step|stp|obj|amf)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const int MaxNodesPerWebView = 6500;
        private const int MaxTreeDepth = 36;

        public ScanResult Scan(bool bringToFrontFirst)
        {
            ScanResult result = new ScanResult();
            string title;
            IntPtr mainHwnd = NativeMethods.FindBambuStudioMainWindow(out title);
            if (mainHwnd == IntPtr.Zero)
            {
                result.BambuWindowFound = false;
                result.Message = "未检测到运行中的 Bambu Studio 窗口，请先启动拓竹 Bambu Studio 并打开“打印历史”页面。";
                return result;
            }

            result.BambuWindowFound = true;
            result.MainWindowHandle = mainHwnd;
            result.WindowTitle = title;

            if (bringToFrontFirst || NativeMethods.IsIconic(mainHwnd))
            {
                NativeMethods.RestoreAndFocusWindow(mainHwnd);
                Thread.Sleep(220);
            }

            Rectangle mainRect = NativeMethods.GetWindowBounds(mainHwnd);
            List<IntPtr> webViews = NativeMethods.FindWebViewRenderWindows(mainHwnd);
            result.ScannedWebViews = webViews.Count;

            List<PrintHistoryCard> allCards = new List<PrintHistoryCard>();
            Rectangle bestViewport = mainRect;
            IntPtr bestContentHwnd = mainHwnd;
            int totalNodes = 0;

            for (int i = 0; i < webViews.Count; i++)
            {
                IntPtr wvHwnd = webViews[i];
                Rectangle wvRect = NativeMethods.GetWindowBounds(wvHwnd);
                Rectangle vp = Rectangle.Intersect(mainRect, wvRect);
                if (vp.Width < 150 || vp.Height < 120) continue;

                int nodeCount;
                List<UiaNode> flatNodes = CollectTreeWithRetry(wvHwnd, out nodeCount);
                totalNodes += nodeCount;

                List<PrintHistoryCard> cards = ExtractCardsFromTree(flatNodes, vp);
                if (cards.Count > 0)
                {
                    if (allCards.Count == 0 || cards.Count > allCards.Count)
                    {
                        bestViewport = vp;
                        bestContentHwnd = wvHwnd;
                    }
                    allCards.AddRange(cards);
                }
            }

            // Fallback: if no cards found in WebView2 panes, try scanning the main Bambu Studio window directly
            if (allCards.Count == 0)
            {
                int fallbackNodes;
                List<UiaNode> mainNodes = CollectTreeWithRetry(mainHwnd, out fallbackNodes);
                totalNodes += fallbackNodes;
                List<PrintHistoryCard> fallbackCards = ExtractCardsFromTree(mainNodes, mainRect);
                allCards.AddRange(fallbackCards);
                bestViewport = mainRect;
                bestContentHwnd = mainHwnd;
            }

            result.ScannedNodes = totalNodes;
            result.ViewportRect = bestViewport;
            result.ContentWindowHandle = bestContentHwnd;

            // Deduplicate cards detected at nearly identical positions and sort top-to-bottom, left-to-right
            allCards = DeduplicateAndSortCards(allCards);
            result.Cards = allCards;

            if (allCards.Count > 0)
            {
                int visibleCount = 0;
                for (int i = 0; i < allCards.Count; i++)
                {
                    if (allCards[i].IsFullyVisible) visibleCount++;
                }
                result.Message = string.Format("共检测到 {0} 张打印历史卡片（当前可视区完整显示 {1} 张）。", allCards.Count, visibleCount);
            }
            else
            {
                result.Message = string.Format("已连接到 Bambu Studio（扫描了 {0} 个 WebView / {1} 个元素），但未找到打印历史卡片。请确保 Bambu Studio 已切换到“主页”或“打印历史”页面并加载出历史记录。", webViews.Count, totalNodes);
            }

            return result;
        }

        public string GenerateDiagnosticReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== Bambu Studio UIA 诊断报告 ===");
            sb.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            string title;
            IntPtr mainHwnd = NativeMethods.FindBambuStudioMainWindow(out title);
            if (mainHwnd == IntPtr.Zero)
            {
                sb.AppendLine("结果: 未找到 Bambu Studio 主窗口。");
                return sb.ToString();
            }

            Rectangle mainRect = NativeMethods.GetWindowBounds(mainHwnd);
            sb.AppendLine(string.Format("主窗口: HWND=0x{0:X}, Title=\"{1}\", Rect={2}, Iconic={3}",
                mainHwnd.ToInt64(), title, mainRect, NativeMethods.IsIconic(mainHwnd)));

            List<IntPtr> webViews = NativeMethods.FindWebViewRenderWindows(mainHwnd);
            sb.AppendLine("Chrome_RenderWidgetHostHWND 数量: " + webViews.Count);

            for (int i = 0; i < webViews.Count; i++)
            {
                IntPtr wv = webViews[i];
                Rectangle r = NativeMethods.GetWindowBounds(wv);
                int count;
                List<UiaNode> nodes = CollectTreeWithRetry(wv, out count);
                sb.AppendLine(string.Format("\n--- WebView[{0}] HWND=0x{1:X} Rect={2} Nodes={3} ---",
                    i, wv.ToInt64(), r, count));

                List<PrintHistoryCard> cards = ExtractCardsFromTree(nodes, Rectangle.Intersect(mainRect, r));
                sb.AppendLine("提取到的卡片数: " + cards.Count);
                for (int c = 0; c < cards.Count; c++)
                {
                    PrintHistoryCard card = cards[c];
                    sb.AppendLine(string.Format("  Card[{0}] Tier={1} Visible={2} Rect={3} Date={4} Name=\"{5}\" Plate=\"{6}\" Dur=\"{7}\" Dev=\"{8}\" Status=\"{9}\"",
                        c, card.DetectionTier, card.IsFullyVisible, card.Bounds, card.FormatDisplayDate(),
                        card.Name, card.Plate, card.Duration, card.Device, card.Status));
                }

                int printed = 0;
                for (int n = 0; n < nodes.Count && printed < 250; n++)
                {
                    UiaNode nd = nodes[n];
                    if (string.IsNullOrWhiteSpace(nd.Name) && nd.ControlType != ControlType.Image && nd.ControlType != ControlType.Group)
                    {
                        continue;
                    }
                    string typeName = nd.ControlType != null ? nd.ControlType.ProgrammaticName.Replace("ControlType.", "") : "?";
                    sb.AppendLine(string.Format("  {0}[{1}] Off={2} Rect=({3},{4},{5}x{6}) Class=\"{7}\" Name=\"{8}\"",
                        new string(' ', Math.Min(nd.Depth, 16)),
                        typeName,
                        nd.IsOffscreen ? 1 : 0,
                        nd.Bounds.X, nd.Bounds.Y, nd.Bounds.Width, nd.Bounds.Height,
                        nd.ClassName,
                        Truncate(nd.Name, 80)));
                    printed++;
                }
            }

            return sb.ToString();
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", " ").Replace("\n", " ");
            if (s.Length <= maxLen) return s;
            return s.Substring(0, maxLen) + "...";
        }

        private List<UiaNode> CollectTreeWithRetry(IntPtr hwnd, out int nodeCount)
        {
            NativeMethods.WakeUpWebViewAccessibility(hwnd);
            List<UiaNode> nodes = new List<UiaNode>();
            nodeCount = 0;

            for (int attempt = 0; attempt < 3; attempt++)
            {
                nodes = CollectTree(hwnd);
                nodeCount = nodes.Count;
                int textCount = 0;
                for (int i = 0; i < nodes.Count; i++)
                {
                    if (!string.IsNullOrWhiteSpace(nodes[i].Name))
                    {
                        textCount++;
                    }
                }
                if (textCount >= 2)
                {
                    return nodes;
                }

                // Chromium / WebView2 may still be asynchronously building its UIA tree after WM_GETOBJECT
                NativeMethods.WakeUpWebViewAccessibility(hwnd);
                Thread.Sleep(300);
            }

            return nodes;
        }

        private List<UiaNode> CollectTree(IntPtr hwnd)
        {
            List<UiaNode> flat = new List<UiaNode>();
            try
            {
                AutomationElement rootElem = AutomationElement.FromHandle(hwnd);
                if (rootElem == null) return flat;

                CacheRequest req = new CacheRequest();
                req.TreeScope = TreeScope.Element | TreeScope.Children;
                req.TreeFilter = Automation.ControlViewCondition;
                req.AutomationElementMode = AutomationElementMode.Full;
                req.Add(AutomationElement.NameProperty);
                req.Add(AutomationElement.ControlTypeProperty);
                req.Add(AutomationElement.BoundingRectangleProperty);
                req.Add(AutomationElement.ClassNameProperty);
                req.Add(AutomationElement.AutomationIdProperty);
                req.Add(AutomationElement.IsOffscreenProperty);

                AutomationElement cachedRoot = rootElem.GetUpdatedCache(req);
                TraverseNode(cachedRoot, null, 0, req, flat);
            }
            catch
            {
                // Window might have closed or been busy during traversal
            }
            return flat;
        }

        private void TraverseNode(AutomationElement cachedElem, UiaNode parent, int depth, CacheRequest req, List<UiaNode> flat)
        {
            if (cachedElem == null || flat.Count >= MaxNodesPerWebView || depth > MaxTreeDepth)
            {
                return;
            }

            UiaNode node = new UiaNode();
            node.Parent = parent;
            node.Depth = depth;

            try
            {
                node.Name = cachedElem.Cached.Name ?? string.Empty;
                node.ControlType = cachedElem.Cached.ControlType;
                node.ClassName = cachedElem.Cached.ClassName ?? string.Empty;
                node.AutomationId = cachedElem.Cached.AutomationId ?? string.Empty;
                node.IsOffscreen = cachedElem.Cached.IsOffscreen;
                node.Bounds = ToDrawingRect(cachedElem.Cached.BoundingRectangle);
            }
            catch
            {
                return;
            }

            if (parent != null)
            {
                parent.Children.Add(node);
            }
            flat.Add(node);

            // Leaf controls (Text / Image / Separator) don't need child expansion
            if (node.ControlType == ControlType.Text ||
                node.ControlType == ControlType.Image ||
                node.ControlType == ControlType.Separator ||
                node.ControlType == ControlType.ScrollBar)
            {
                return;
            }

            AutomationElementCollection children = null;
            try
            {
                children = cachedElem.CachedChildren;
            }
            catch
            {
                return;
            }

            if (children == null || children.Count == 0)
            {
                return;
            }

            for (int i = 0; i < children.Count; i++)
            {
                if (flat.Count >= MaxNodesPerWebView) break;
                AutomationElement child = children[i];
                if (child == null) continue;

                ControlType childType = null;
                try
                {
                    childType = child.Cached.ControlType;
                }
                catch
                {
                    continue;
                }

                // Optimization: for leaf Text/Image nodes, read already-cached properties directly without another IPC call!
                if (childType == ControlType.Text || childType == ControlType.Image)
                {
                    UiaNode leaf = new UiaNode();
                    leaf.Parent = node;
                    leaf.Depth = depth + 1;
                    try
                    {
                        leaf.Name = child.Cached.Name ?? string.Empty;
                        leaf.ControlType = childType;
                        leaf.ClassName = child.Cached.ClassName ?? string.Empty;
                        leaf.AutomationId = child.Cached.AutomationId ?? string.Empty;
                        leaf.IsOffscreen = child.Cached.IsOffscreen;
                        leaf.Bounds = ToDrawingRect(child.Cached.BoundingRectangle);
                    }
                    catch
                    {
                        continue;
                    }
                    node.Children.Add(leaf);
                    flat.Add(leaf);
                    continue;
                }

                // Recurse into containers by fetching their cached children
                try
                {
                    AutomationElement expandedChild = child.GetUpdatedCache(req);
                    TraverseNode(expandedChild, node, depth + 1, req, flat);
                }
                catch
                {
                    // Skip stale child
                }
            }
        }

        private static Rectangle ToDrawingRect(Rect r)
        {
            if (r.IsEmpty || double.IsNaN(r.X) || double.IsNaN(r.Y) ||
                double.IsInfinity(r.X) || double.IsInfinity(r.Y) ||
                double.IsNaN(r.Width) || double.IsNaN(r.Height) ||
                r.Width <= 0 || r.Height <= 0)
            {
                return Rectangle.Empty;
            }

            int x = (int)Math.Round(r.X);
            int y = (int)Math.Round(r.Y);
            int w = (int)Math.Round(r.Width);
            int h = (int)Math.Round(r.Height);
            if (w <= 0 || h <= 0) return Rectangle.Empty;
            return new Rectangle(x, y, w, h);
        }

        private List<PrintHistoryCard> ExtractCardsFromTree(List<UiaNode> flatNodes, Rectangle viewport)
        {
            List<PrintHistoryCard> cards = new List<PrintHistoryCard>();
            if (flatNodes == null || flatNodes.Count == 0) return cards;

            List<DateMatchInfo> dateAnchors = new List<DateMatchInfo>();
            List<UiaNode> textNodes = new List<UiaNode>();
            List<UiaNode> imageNodes = new List<UiaNode>();

            for (int i = 0; i < flatNodes.Count; i++)
            {
                UiaNode n = flatNodes[i];
                if (n.Bounds.Width <= 0 || n.Bounds.Height <= 0) continue;

                if (n.ControlType == ControlType.Image && n.Bounds.Width >= 80 && n.Bounds.Height >= 60)
                {
                    imageNodes.Add(n);
                }

                // Consider leaf Text nodes (or leaf elements with text Name)
                bool isTextCandidate = (n.ControlType == ControlType.Text) ||
                                       (n.Children.Count == 0 && !string.IsNullOrWhiteSpace(n.Name) && n.ControlType != ControlType.Image);

                if (isTextCandidate && !string.IsNullOrWhiteSpace(n.Name))
                {
                    textNodes.Add(n);
                    DateMatchInfo dm = TryParseDateNode(n);
                    if (dm != null)
                    {
                        dateAnchors.Add(dm);
                        // Increment DateAnchorCount on all ancestors so Tier 1 never grabs a parent containing 2+ cards
                        UiaNode cur = n;
                        while (cur != null)
                        {
                            cur.DateAnchorCount++;
                            cur = cur.Parent;
                        }
                    }
                }
            }

            if (dateAnchors.Count == 0)
            {
                return cards;
            }

            for (int i = 0; i < dateAnchors.Count; i++)
            {
                DateMatchInfo anchor = dateAnchors[i];
                PrintHistoryCard card = BuildCardFromDateAnchor(anchor, textNodes, imageNodes, viewport);
                if (card != null)
                {
                    cards.Add(card);
                }
            }

            return cards;
        }

        private DateMatchInfo TryParseDateNode(UiaNode node)
        {
            string text = (node.Name ?? string.Empty).Trim();
            if (text.Length < 8 || text.Length > 60) return null;

            Match m = DateRegex.Match(text);
            if (!m.Success) return null;

            int y, mo, d, H = 0, M = 0, S = 0;
            if (!int.TryParse(m.Groups["y"].Value, out y)) return null;
            if (!int.TryParse(m.Groups["m"].Value, out mo) || mo < 1 || mo > 12) return null;
            if (!int.TryParse(m.Groups["d"].Value, out d) || d < 1 || d > 31) return null;

            bool hasTime = false;
            if (m.Groups["H"].Success && m.Groups["M"].Success)
            {
                if (!int.TryParse(m.Groups["H"].Value, out H) || H < 0 || H > 23) return null;
                if (!int.TryParse(m.Groups["M"].Value, out M) || M < 0 || M > 59) return null;
                if (m.Groups["S"].Success)
                {
                    int.TryParse(m.Groups["S"].Value, out S);
                    if (S < 0 || S > 59) S = 0;
                }
                hasTime = true;
            }

            DateTime dt;
            try
            {
                dt = new DateTime(y, mo, d, H, M, S);
            }
            catch
            {
                return null;
            }

            DateMatchInfo info = new DateMatchInfo();
            info.Node = node;
            info.ParsedDate = dt;
            info.HasTime = hasTime;
            info.HasParentheses = m.Groups["paren"].Success && m.Groups["cparen"].Success;
            info.RawDateMatch = m.Value.Trim();

            string before = text.Substring(0, m.Index).Trim();
            if (!string.IsNullOrEmpty(before))
            {
                info.LeadingPlateText = before;
            }

            return info;
        }

        private PrintHistoryCard BuildCardFromDateAnchor(
            DateMatchInfo anchor,
            List<UiaNode> allTextNodes,
            List<UiaNode> allImageNodes,
            Rectangle viewport)
        {
            UiaNode dateNode = anchor.Node;
            Rectangle cardRect = Rectangle.Empty;
            Rectangle imgRect = Rectangle.Empty;
            List<UiaNode> cardTexts = new List<UiaNode>();
            string tier = string.Empty;
            bool needsVisualTop = false;

            // === TIER 1: Exact DOM Card Ancestor (<div class="PrintHistoryItem GuideBlock">) ===
            UiaNode bestAncestor = FindBestCardAncestor(dateNode);
            if (bestAncestor != null)
            {
                cardRect = bestAncestor.Bounds;
                tier = "Tier1-DOM";
                CollectSubtreeTextsAndImage(bestAncestor, cardTexts, ref imgRect);
            }

            // === TIER 2: Spatial Image + Text Anchor ===
            if (cardRect.IsEmpty || cardRect.Height < 160)
            {
                UiaNode matchedImg = FindThumbnailImageForDate(dateNode, allImageNodes);
                if (matchedImg != null)
                {
                    imgRect = matchedImg.Bounds;
                    cardTexts.Clear();

                    bool isVerticalCard = matchedImg.Bounds.Bottom <= dateNode.Bounds.Top + 10;
                    int minX, maxX, minY, maxY;
                    if (isVerticalCard)
                    {
                        minX = imgRect.Left - 14;
                        maxX = imgRect.Right + 14;
                        minY = imgRect.Top - 6;
                        maxY = dateNode.Bounds.Bottom + 16;
                    }
                    else
                    {
                        // List-row layout: image on the left, text on the right
                        minX = imgRect.Left - 6;
                        maxX = Math.Max(dateNode.Bounds.Right + 180, imgRect.Right + 420);
                        minY = Math.Min(imgRect.Top, dateNode.Bounds.Top - 80) - 6;
                        maxY = Math.Max(imgRect.Bottom, dateNode.Bounds.Bottom) + 12;
                    }

                    Rectangle searchZone = Rectangle.FromLTRB(minX, minY, maxX, maxY);
                    Rectangle union = imgRect;

                    for (int i = 0; i < allTextNodes.Count; i++)
                    {
                        UiaNode t = allTextNodes[i];
                        System.Drawing.Point center = new System.Drawing.Point(t.Bounds.Left + t.Bounds.Width / 2, t.Bounds.Top + t.Bounds.Height / 2);
                        if (searchZone.Contains(center))
                        {
                            cardTexts.Add(t);
                            union = Rectangle.Union(union, t.Bounds);
                        }
                    }

                    cardRect = union;
                    tier = "Tier2-Image";
                }
            }

            // === TIER 3: Text-Only Block + Visual 4:3 Thumbnail Estimation ===
            if (cardRect.IsEmpty)
            {
                cardTexts.Clear();
                int estLeft = dateNode.Bounds.Left - 160;
                int estRight = dateNode.Bounds.Right + 220;
                int estTop = dateNode.Bounds.Top - 115;
                int estBottom = dateNode.Bounds.Bottom + 14;

                Rectangle searchZone = Rectangle.FromLTRB(estLeft, estTop, estRight, estBottom);
                Rectangle textUnion = dateNode.Bounds;

                for (int i = 0; i < allTextNodes.Count; i++)
                {
                    UiaNode t = allTextNodes[i];
                    System.Drawing.Point center = new System.Drawing.Point(t.Bounds.Left + t.Bounds.Width / 2, t.Bounds.Top + t.Bounds.Height / 2);
                    if (searchZone.Contains(center))
                    {
                        cardTexts.Add(t);
                        textUnion = Rectangle.Union(textUnion, t.Bounds);
                    }
                }

                // Estimate 4:3 thumbnail box directly above the text block
                int estWidth = Math.Max(textUnion.Width, 240);
                int estImgHeight = (int)Math.Round(estWidth * 0.75);
                int estCardTop = Math.Max(viewport.Top, textUnion.Top - estImgHeight - 10);
                cardRect = Rectangle.FromLTRB(textUnion.Left, estCardTop, textUnion.Left + estWidth, textUnion.Bottom);
                needsVisualTop = true;
                tier = "Tier3-Visual";
            }

            if (cardTexts.Count == 0)
            {
                cardTexts.Add(dateNode);
            }

            // Parse semantic fields from cardTexts
            PrintHistoryCard card = ParseFieldsFromCardTexts(anchor, cardTexts, cardRect, imgRect);
            if (card == null)
            {
                return null;
            }

            // Filter out "Recent Files" (.3mf) cards that are NOT Print History!
            if (!IsLikelyPrintHistoryCard(anchor, card))
            {
                return null;
            }

            card.Bounds = cardRect;
            card.ImageBounds = imgRect;
            card.NeedsVisualTopRefinement = needsVisualTop;
            card.DetectionTier = tier;
            card.UniqueKey = card.BuildUniqueKey();

            // Check if card is completely inside the visible viewport
            int tol = 3;
            card.IsFullyVisible =
                cardRect.Width >= 100 &&
                cardRect.Height >= 100 &&
                cardRect.Left >= viewport.Left - tol &&
                cardRect.Top >= viewport.Top - tol &&
                cardRect.Right <= viewport.Right + tol &&
                cardRect.Bottom <= viewport.Bottom + tol;

            card.CaptureState = card.IsFullyVisible ? "待截图" : "部分超出可视区";
            card.IsSelected = card.IsFullyVisible;

            return card;
        }

        private UiaNode FindBestCardAncestor(UiaNode dateNode)
        {
            UiaNode best = null;
            UiaNode cur = dateNode.Parent;

            while (cur != null)
            {
                // Never ascend into a container that holds multiple date anchors (that's #PrintHistoryList!)
                if (cur.DateAnchorCount > 1)
                {
                    break;
                }

                Rectangle r = cur.Bounds;
                if (r.Width >= 160 && r.Width <= 950 && r.Height >= 150 && r.Height <= 950)
                {
                    double aspect = (double)r.Width / (double)r.Height;
                    bool topIncludesImage = (dateNode.Bounds.Top - r.Top) >= 120;
                    if (aspect >= 0.45 && aspect <= 2.6 && topIncludesImage)
                    {
                        best = cur;
                    }
                }

                cur = cur.Parent;
            }

            return best;
        }

        private void CollectSubtreeTextsAndImage(UiaNode root, List<UiaNode> texts, ref Rectangle bestImgRect)
        {
            Stack<UiaNode> stack = new Stack<UiaNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                UiaNode n = stack.Pop();
                if (n.ControlType == ControlType.Image && n.Bounds.Width >= 80 && n.Bounds.Height >= 60)
                {
                    if (n.Bounds.Width * n.Bounds.Height > bestImgRect.Width * bestImgRect.Height)
                    {
                        bestImgRect = n.Bounds;
                    }
                }
                else if ((n.ControlType == ControlType.Text || n.Children.Count == 0) &&
                         !string.IsNullOrWhiteSpace(n.Name) &&
                         n.Bounds.Width > 0 && n.Bounds.Height > 0)
                {
                    texts.Add(n);
                }

                for (int i = n.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push(n.Children[i]);
                }
            }
        }

        private UiaNode FindThumbnailImageForDate(UiaNode dateNode, List<UiaNode> allImages)
        {
            UiaNode best = null;
            double bestScore = double.MaxValue;
            int dateCenterX = dateNode.Bounds.Left + dateNode.Bounds.Width / 2;

            for (int i = 0; i < allImages.Count; i++)
            {
                UiaNode img = allImages[i];
                Rectangle r = img.Bounds;
                if (r.Width < 100 || r.Height < 70 || r.Width > 900 || r.Height > 700) continue;

                // Vertical card layout: image directly above dateNode
                int verticalGap = dateNode.Bounds.Top - r.Bottom;
                bool horizAligned = dateCenterX >= r.Left - 40 && dateCenterX <= r.Right + 40;
                if (horizAligned && verticalGap >= 15 && verticalGap <= 230)
                {
                    double score = verticalGap + Math.Abs((r.Left + r.Width / 2) - dateCenterX) * 0.2;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = img;
                    }
                }
            }

            return best;
        }

        private PrintHistoryCard ParseFieldsFromCardTexts(
            DateMatchInfo anchor,
            List<UiaNode> cardTexts,
            Rectangle cardRect,
            Rectangle imgRect)
        {
            PrintHistoryCard card = new PrintHistoryCard();
            card.PrintDate = anchor.ParsedDate;
            card.HasTime = anchor.HasTime;
            card.RawDateText = anchor.RawDateMatch;
            if (!string.IsNullOrEmpty(anchor.LeadingPlateText))
            {
                card.Plate = anchor.LeadingPlateText;
            }

            // Determine image bottom boundary so we ignore overlay badges ("Gcode") sitting inside the thumbnail
            int imgBottomY = !imgRect.IsEmpty ? imgRect.Bottom - 6 : (cardRect.Top + (int)(cardRect.Height * 0.45));

            // Filter text nodes below the thumbnail (or excluding Gcode badge inside thumbnail)
            List<UiaNode> bodyTexts = new List<UiaNode>();
            for (int i = 0; i < cardTexts.Count; i++)
            {
                UiaNode t = cardTexts[i];
                string txt = (t.Name ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(txt)) continue;

                bool insideTopBadgeZone = (t.Bounds.Bottom <= imgBottomY) && BadgeRegex.IsMatch(txt);
                if (insideTopBadgeZone) continue;

                // Ignore anything purely inside the thumbnail image area
                if (!imgRect.IsEmpty && t.Bounds.Bottom <= imgRect.Bottom - 4 && t.Bounds.Top >= imgRect.Top - 4)
                {
                    continue;
                }

                bodyTexts.Add(t);
            }

            if (bodyTexts.Count == 0)
            {
                return null;
            }

            // Sort top-to-bottom, then left-to-right
            bodyTexts.Sort(delegate(UiaNode a, UiaNode b)
            {
                int dy = a.Bounds.Top - b.Bounds.Top;
                if (Math.Abs(dy) > 10) return dy;
                return a.Bounds.Left - b.Bounds.Left;
            });

            // Group into horizontal lines (items whose vertical midpoints are within 12px)
            List<List<UiaNode>> lines = new List<List<UiaNode>>();
            for (int i = 0; i < bodyTexts.Count; i++)
            {
                UiaNode t = bodyTexts[i];
                int midY = t.Bounds.Top + t.Bounds.Height / 2;
                bool placed = false;

                for (int l = 0; l < lines.Count; l++)
                {
                    UiaNode first = lines[l][0];
                    int lineMidY = first.Bounds.Top + first.Bounds.Height / 2;
                    if (Math.Abs(midY - lineMidY) <= 12)
                    {
                        lines[l].Add(t);
                        placed = true;
                        break;
                    }
                }

                if (!placed)
                {
                    List<UiaNode> newLine = new List<UiaNode>();
                    newLine.Add(t);
                    lines.Add(newLine);
                }
            }

            // Sort items within each line left-to-right
            for (int l = 0; l < lines.Count; l++)
            {
                lines[l].Sort(delegate(UiaNode a, UiaNode b) { return a.Bounds.Left.CompareTo(b.Bounds.Left); });
            }

            int dateLineIndex = -1;
            for (int l = 0; l < lines.Count; l++)
            {
                if (lines[l].Contains(anchor.Node))
                {
                    dateLineIndex = l;
                    break;
                }
            }

            // 1. Parse Date Line: [PlateName] [Date] [Status]
            if (dateLineIndex >= 0)
            {
                List<UiaNode> dateLine = lines[dateLineIndex];
                List<string> plateParts = new List<string>();
                for (int i = 0; i < dateLine.Count; i++)
                {
                    UiaNode t = dateLine[i];
                    if (t == anchor.Node) continue;
                    string txt = t.Name.Trim();
                    if (string.IsNullOrEmpty(txt)) continue;

                    if (StatusRegex.IsMatch(txt))
                    {
                        card.Status = txt;
                    }
                    else if (t.Bounds.Left < anchor.Node.Bounds.Left)
                    {
                        plateParts.Add(txt);
                    }
                    else if (string.IsNullOrEmpty(card.Status) && t.Bounds.Left >= anchor.Node.Bounds.Right - 4)
                    {
                        card.Status = txt;
                    }
                }
                if (plateParts.Count > 0)
                {
                    string joinedPlate = string.Join(" ", plateParts.ToArray()).Trim();
                    if (!string.IsNullOrEmpty(anchor.LeadingPlateText))
                    {
                        card.Plate = (joinedPlate + " " + anchor.LeadingPlateText).Trim();
                    }
                    else
                    {
                        card.Plate = joinedPlate;
                    }
                }
            }

            // 2. Parse Title (topmost line above date line) and Line 2 (duration + device)
            for (int l = 0; l < lines.Count; l++)
            {
                if (l == dateLineIndex) continue;
                List<UiaNode> line = lines[l];

                if (l == 0)
                {
                    // First text line below thumbnail is always PrintHistoryName
                    List<string> titleParts = new List<string>();
                    for (int i = 0; i < line.Count; i++)
                    {
                        string txt = line[i].Name.Trim();
                        if (!string.IsNullOrEmpty(txt) && !BadgeRegex.IsMatch(txt))
                        {
                            titleParts.Add(txt);
                        }
                    }
                    card.Name = string.Join(" ", titleParts.ToArray()).Trim();
                }
                else
                {
                    // Middle line(s): duration + device name (or plate/status if layout differs)
                    List<string> deviceParts = new List<string>();
                    for (int i = 0; i < line.Count; i++)
                    {
                        string txt = line[i].Name.Trim();
                        if (string.IsNullOrEmpty(txt)) continue;

                        if (string.IsNullOrEmpty(card.Duration) && DurationRegex.IsMatch(txt))
                        {
                            card.Duration = txt;
                        }
                        else if (string.IsNullOrEmpty(card.Status) && StatusRegex.IsMatch(txt))
                        {
                            card.Status = txt;
                        }
                        else if (string.IsNullOrEmpty(card.Plate) && PlateRegex.IsMatch(txt))
                        {
                            card.Plate = txt;
                        }
                        else
                        {
                            deviceParts.Add(txt);
                        }
                    }

                    if (deviceParts.Count > 0 && string.IsNullOrEmpty(card.Device))
                    {
                        card.Device = string.Join(" ", deviceParts.ToArray()).Trim();
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(card.Name))
            {
                return null;
            }

            return card;
        }

        private bool IsLikelyPrintHistoryCard(DateMatchInfo anchor, PrintHistoryCard card)
        {
            // Strong positive signals of a Bambu Studio Print History card
            if (anchor.HasParentheses) return true;
            if (!string.IsNullOrEmpty(card.Status)) return true;
            if (!string.IsNullOrEmpty(card.Duration)) return true;
            if (!string.IsNullOrEmpty(card.Plate)) return true;
            if (!string.IsNullOrEmpty(card.Device)) return true;

            // Negative signal: "Recent Files" cards only have a .3mf/.stl filename and a bare date without parentheses
            if (FileExtRegex.IsMatch(card.Name ?? string.Empty))
            {
                return false;
            }

            return true;
        }

        private List<PrintHistoryCard> DeduplicateAndSortCards(List<PrintHistoryCard> cards)
        {
            List<PrintHistoryCard> deduped = new List<PrintHistoryCard>();
            for (int i = 0; i < cards.Count; i++)
            {
                PrintHistoryCard c = cards[i];
                bool duplicate = false;
                for (int j = 0; j < deduped.Count; j++)
                {
                    PrintHistoryCard existing = deduped[j];
                    int dx = Math.Abs(c.Bounds.X - existing.Bounds.X);
                    int dy = Math.Abs(c.Bounds.Y - existing.Bounds.Y);
                    if (dx < 25 && dy < 25)
                    {
                        duplicate = true;
                        break;
                    }
                }
                if (!duplicate)
                {
                    deduped.Add(c);
                }
            }

            // Sort row-by-row (cards within 40px vertically are on the same row), then left-to-right
            deduped.Sort(delegate(PrintHistoryCard a, PrintHistoryCard b)
            {
                int dy = a.Bounds.Top - b.Bounds.Top;
                if (Math.Abs(dy) > 40) return dy;
                return a.Bounds.Left - b.Bounds.Left;
            });

            return deduped;
        }
    }
}
