using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BambuHistoryScreenshot
{
    public class FloatingStopBanner : Form
    {
        private Label _lblText;
        private Button _btnStop;
        public event EventHandler StopRequested;

        public FloatingStopBanner()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(28, 35, 45);
            ForeColor = Color.White;
            Size = new Size(340, 46);
            Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);

            _lblText = new Label();
            _lblText.Text = "正在自动滚动截图... (按 ESC 停止)";
            _lblText.ForeColor = Color.White;
            _lblText.AutoSize = false;
            _lblText.TextAlign = ContentAlignment.MiddleLeft;
            _lblText.Location = new Point(12, 8);
            _lblText.Size = new Size(242, 30);

            _btnStop = new Button();
            _btnStop.Text = "停止";
            _btnStop.FlatStyle = FlatStyle.Flat;
            _btnStop.FlatAppearance.BorderSize = 0;
            _btnStop.BackColor = Color.FromArgb(220, 38, 38);
            _btnStop.ForeColor = Color.White;
            _btnStop.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _btnStop.Location = new Point(260, 9);
            _btnStop.Size = new Size(68, 28);
            _btnStop.Cursor = Cursors.Hand;
            _btnStop.Click += delegate
            {
                if (StopRequested != null) StopRequested(this, EventArgs.Empty);
            };

            Controls.Add(_lblText);
            Controls.Add(_btnStop);

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - Width - 18, wa.Bottom - Height - 18);
        }

        public void UpdateStatus(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(UpdateStatus), text);
                return;
            }
            _lblText.Text = text;
        }
    }

    public class MainForm : Form
    {
        private readonly AppConfig _config;
        private readonly BambuUiaScanner _scanner;
        private readonly List<PrintHistoryCard> _allCards;
        private readonly List<PrintHistoryCard> _filteredCards;
        private ScanResult _lastScanResult;
        private bool _isBusy;
        private volatile bool _cancelRequested;

        // UI Controls
        private Label _lblConnStatus;
        private Button _btnScan;
        private Button _btnCaptureSelected;
        private Button _btnAutoScroll;
        private Button _btnOpenFolder;
        private Button _btnDiagnose;

        private TextBox _txtOutputFolder;
        private Button _btnBrowse;
        private ComboBox _cmbTemplate;
        private ComboBox _cmbDuplicate;
        private NumericUpDown _numPadding;

        private ComboBox _cmbStatusFilter;
        private CheckBox _chkDateFilter;
        private DateTimePicker _dtpStart;
        private DateTimePicker _dtpEnd;
        private TextBox _txtKeyword;
        private ComboBox _cmbScrollMode;
        private NumericUpDown _numMaxCards;

        private DataGridView _grid;
        private PictureBox _picPreview;
        private Label _lblPreviewInfo;
        private TextBox _txtLog;
        private ToolStripStatusLabel _lblFooterStatus;
        private ToolStripProgressBar _progressBar;
        private System.Windows.Forms.Timer _connTimer;

        private static readonly Color BambuGreen = Color.FromArgb(0, 174, 66);
        private static readonly Color BambuGreenHover = Color.FromArgb(0, 148, 56);
        private static readonly Color AccentBlue = Color.FromArgb(22, 119, 255);
        private static readonly Color BgGray = Color.FromArgb(245, 247, 250);
        private static readonly Color BorderGray = Color.FromArgb(226, 232, 240);

        public MainForm()
        {
            _config = AppConfig.Load();
            _scanner = new BambuUiaScanner();
            _allCards = new List<PrintHistoryCard>();
            _filteredCards = new List<PrintHistoryCard>();

            InitializeUi();
            LoadConfigToUi();
            RefreshBambuConnectionStatus();

            _connTimer = new System.Windows.Forms.Timer();
            _connTimer.Interval = 3500;
            _connTimer.Tick += delegate
            {
                if (!_isBusy) RefreshBambuConnectionStatus();
            };
            _connTimer.Start();
        }

        private void InitializeUi()
        {
            Text = "拓竹 Bambu Studio 打印历史自动截图工具";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1220, 760);
            MinimumSize = new Size(980, 620);
            BackColor = BgGray;
            Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);

            // 1. Header Panel
            Panel headerPanel = new Panel();
            headerPanel.Dock = DockStyle.Top;
            headerPanel.Height = 64;
            headerPanel.BackColor = Color.White;
            headerPanel.Padding = new Padding(16, 10, 16, 10);

            Panel headerBottomLine = new Panel();
            headerBottomLine.Dock = DockStyle.Bottom;
            headerBottomLine.Height = 1;
            headerBottomLine.BackColor = BorderGray;
            headerPanel.Controls.Add(headerBottomLine);

            Label lblTitle = new Label();
            lblTitle.Text = "拓竹打印历史截图工具";
            lblTitle.Font = new Font("Microsoft YaHei UI", 13.5f, FontStyle.Bold);
            lblTitle.ForeColor = Color.FromArgb(24, 32, 42);
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(16, 10);

            _lblConnStatus = new Label();
            _lblConnStatus.Text = "● 正在检测 Bambu Studio...";
            _lblConnStatus.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular);
            _lblConnStatus.ForeColor = Color.FromArgb(100, 116, 139);
            _lblConnStatus.AutoSize = true;
            _lblConnStatus.Location = new Point(18, 38);

            FlowLayoutPanel actionFlow = new FlowLayoutPanel();
            actionFlow.Dock = DockStyle.Right;
            actionFlow.AutoSize = true;
            actionFlow.FlowDirection = FlowDirection.LeftToRight;
            actionFlow.WrapContents = false;
            actionFlow.Padding = new Padding(0, 4, 0, 0);

            _btnScan = CreateStyledButton("扫描当前页", Color.White, Color.FromArgb(31, 41, 55), BorderGray, 106);
            _btnScan.Click += OnScanClick;

            _btnCaptureSelected = CreateStyledButton("截取勾选卡片", BambuGreen, Color.White, BambuGreen, 122);
            _btnCaptureSelected.Click += OnCaptureSelectedClick;

            _btnAutoScroll = CreateStyledButton("自动滚动批量截图", AccentBlue, Color.White, AccentBlue, 142);
            _btnAutoScroll.Click += OnAutoScrollCaptureClick;

            _btnOpenFolder = CreateStyledButton("打开保存目录", Color.White, Color.FromArgb(51, 65, 85), BorderGray, 110);
            _btnOpenFolder.Click += OnOpenFolderClick;

            _btnDiagnose = CreateStyledButton("窗口诊断", Color.White, Color.FromArgb(100, 116, 139), BorderGray, 84);
            _btnDiagnose.Click += OnDiagnoseClick;

            actionFlow.Controls.Add(_btnScan);
            actionFlow.Controls.Add(_btnCaptureSelected);
            actionFlow.Controls.Add(_btnAutoScroll);
            actionFlow.Controls.Add(_btnOpenFolder);
            actionFlow.Controls.Add(_btnDiagnose);

            headerPanel.Controls.Add(lblTitle);
            headerPanel.Controls.Add(_lblConnStatus);
            headerPanel.Controls.Add(actionFlow);

            // 2. Settings & Filter Panel
            Panel configPanel = new Panel();
            configPanel.Dock = DockStyle.Top;
            configPanel.Height = 98;
            configPanel.BackColor = Color.White;
            configPanel.Padding = new Padding(14, 8, 14, 8);

            Panel cfgBottomLine = new Panel();
            cfgBottomLine.Dock = DockStyle.Bottom;
            cfgBottomLine.Height = 1;
            cfgBottomLine.BackColor = BorderGray;
            configPanel.Controls.Add(cfgBottomLine);

            // Row 1: Output folder, naming template, duplicate policy, padding
            FlowLayoutPanel row1 = new FlowLayoutPanel();
            row1.Dock = DockStyle.Top;
            row1.Height = 40;
            row1.FlowDirection = FlowDirection.LeftToRight;
            row1.WrapContents = false;

            row1.Controls.Add(MakeInlineLabel("保存目录:"));
            _txtOutputFolder = new TextBox();
            _txtOutputFolder.Width = 260;
            _txtOutputFolder.Margin = new Padding(0, 6, 4, 0);
            _txtOutputFolder.Leave += delegate { SaveUiToConfig(); };
            row1.Controls.Add(_txtOutputFolder);

            _btnBrowse = new Button();
            _btnBrowse.Text = "浏览...";
            _btnBrowse.Width = 62;
            _btnBrowse.Height = 26;
            _btnBrowse.Margin = new Padding(0, 5, 14, 0);
            _btnBrowse.FlatStyle = FlatStyle.Flat;
            _btnBrowse.FlatAppearance.BorderColor = BorderGray;
            _btnBrowse.BackColor = BgGray;
            _btnBrowse.Click += OnBrowseFolderClick;
            row1.Controls.Add(_btnBrowse);

            row1.Controls.Add(MakeInlineLabel("命名格式:"));
            _cmbTemplate = new ComboBox();
            _cmbTemplate.Width = 215;
            _cmbTemplate.Margin = new Padding(0, 5, 14, 0);
            _cmbTemplate.DropDownStyle = ComboBoxStyle.DropDown;
            _cmbTemplate.Items.AddRange(new object[]
            {
                "{date}-{name}",
                "{date_compact}-{name}",
                "{date}_{time}-{name}",
                "{date}-{name}-{plate}",
                "{date}-{name}-{status}",
                "{datetime}-{name}"
            });
            _cmbTemplate.TextChanged += delegate
            {
                SaveUiToConfig();
                RefreshFileNamePreviewColumn();
            };
            row1.Controls.Add(_cmbTemplate);

            row1.Controls.Add(MakeInlineLabel("重名:"));
            _cmbDuplicate = new ComboBox();
            _cmbDuplicate.Width = 104;
            _cmbDuplicate.Margin = new Padding(0, 5, 14, 0);
            _cmbDuplicate.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDuplicate.Items.AddRange(new object[] { "自动加序号", "跳过已存在", "覆盖原文件" });
            _cmbDuplicate.SelectedIndexChanged += delegate { SaveUiToConfig(); };
            row1.Controls.Add(_cmbDuplicate);

            row1.Controls.Add(MakeInlineLabel("边距(px):"));
            _numPadding = new NumericUpDown();
            _numPadding.Width = 54;
            _numPadding.Minimum = 0;
            _numPadding.Maximum = 40;
            _numPadding.Margin = new Padding(0, 5, 6, 0);
            _numPadding.ValueChanged += delegate { SaveUiToConfig(); };
            row1.Controls.Add(_numPadding);

            // Row 2: Filters & Auto-scroll options
            FlowLayoutPanel row2 = new FlowLayoutPanel();
            row2.Dock = DockStyle.Top;
            row2.Height = 40;
            row2.FlowDirection = FlowDirection.LeftToRight;
            row2.WrapContents = false;

            row2.Controls.Add(MakeInlineLabel("打印状态:"));
            _cmbStatusFilter = new ComboBox();
            _cmbStatusFilter.Width = 108;
            _cmbStatusFilter.Margin = new Padding(0, 5, 14, 0);
            _cmbStatusFilter.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbStatusFilter.Items.AddRange(new object[] { "全部状态", "仅打印完成", "仅正在打印", "仅已取消/失败" });
            _cmbStatusFilter.SelectedIndex = 0;
            _cmbStatusFilter.SelectedIndexChanged += delegate { ApplyFiltersToGrid(); };
            row2.Controls.Add(_cmbStatusFilter);

            _chkDateFilter = new CheckBox();
            _chkDateFilter.Text = "按日期筛选:";
            _chkDateFilter.AutoSize = true;
            _chkDateFilter.Margin = new Padding(0, 8, 4, 0);
            _chkDateFilter.CheckedChanged += delegate
            {
                _dtpStart.Enabled = _chkDateFilter.Checked;
                _dtpEnd.Enabled = _chkDateFilter.Checked;
                ApplyFiltersToGrid();
            };
            row2.Controls.Add(_chkDateFilter);

            _dtpStart = new DateTimePicker();
            _dtpStart.Format = DateTimePickerFormat.Short;
            _dtpStart.Width = 108;
            _dtpStart.Margin = new Padding(0, 5, 4, 0);
            _dtpStart.Value = DateTime.Today.AddMonths(-1);
            _dtpStart.Enabled = false;
            _dtpStart.ValueChanged += delegate { ApplyFiltersToGrid(); };
            row2.Controls.Add(_dtpStart);

            row2.Controls.Add(MakeInlineLabel("至", 2));

            _dtpEnd = new DateTimePicker();
            _dtpEnd.Format = DateTimePickerFormat.Short;
            _dtpEnd.Width = 108;
            _dtpEnd.Margin = new Padding(0, 5, 14, 0);
            _dtpEnd.Value = DateTime.Today;
            _dtpEnd.Enabled = false;
            _dtpEnd.ValueChanged += delegate { ApplyFiltersToGrid(); };
            row2.Controls.Add(_dtpEnd);

            row2.Controls.Add(MakeInlineLabel("关键词:"));
            _txtKeyword = new TextBox();
            _txtKeyword.Width = 140;
            _txtKeyword.Margin = new Padding(0, 6, 14, 0);
            _txtKeyword.TextChanged += delegate { ApplyFiltersToGrid(); };
            row2.Controls.Add(_txtKeyword);

            row2.Controls.Add(MakeInlineLabel("滚动:"));
            _cmbScrollMode = new ComboBox();
            _cmbScrollMode.Width = 92;
            _cmbScrollMode.Margin = new Padding(0, 5, 10, 0);
            _cmbScrollMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbScrollMode.Items.AddRange(new object[] { "自动判断", "向下滚动", "向右滚动" });
            _cmbScrollMode.SelectedIndexChanged += delegate { SaveUiToConfig(); };
            row2.Controls.Add(_cmbScrollMode);

            row2.Controls.Add(MakeInlineLabel("批量上限:"));
            _numMaxCards = new NumericUpDown();
            _numMaxCards.Width = 64;
            _numMaxCards.Minimum = 1;
            _numMaxCards.Maximum = 2000;
            _numMaxCards.Margin = new Padding(0, 5, 4, 0);
            _numMaxCards.ValueChanged += delegate { SaveUiToConfig(); };
            row2.Controls.Add(_numMaxCards);

            configPanel.Controls.Add(row2);
            configPanel.Controls.Add(row1);

            // 3. Main Content SplitContainer
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Vertical;
            split.SplitterWidth = 8;
            split.BackColor = BgGray;
            split.Padding = new Padding(12, 10, 12, 6);

            // Left panel: mini toolbar + DataGridView
            Panel leftCard = new Panel();
            leftCard.Dock = DockStyle.Fill;
            leftCard.BackColor = Color.White;
            leftCard.Padding = new Padding(1);

            Panel gridToolbar = new Panel();
            gridToolbar.Dock = DockStyle.Top;
            gridToolbar.Height = 36;
            gridToolbar.BackColor = Color.FromArgb(248, 250, 252);
            gridToolbar.Padding = new Padding(8, 5, 8, 5);

            Button btnSelectAll = MakeSmallToolBtn("全选可见", 74);
            btnSelectAll.Location = new Point(8, 5);
            btnSelectAll.Click += delegate { SetAllSelection(true); };

            Button btnSelectNone = MakeSmallToolBtn("全不选", 60);
            btnSelectNone.Location = new Point(88, 5);
            btnSelectNone.Click += delegate { SetAllSelection(false); };

            Button btnInvert = MakeSmallToolBtn("反选", 52);
            btnInvert.Location = new Point(154, 5);
            btnInvert.Click += delegate { InvertSelection(); };

            Label lblTip = new Label();
            lblTip.Text = "提示：请先在 Bambu Studio 打开“打印历史”或“主页”再扫描（支持双击行查看已保存图片）";
            lblTip.ForeColor = Color.FromArgb(100, 116, 139);
            lblTip.Font = new Font("Microsoft YaHei UI", 8.5f);
            lblTip.AutoSize = true;
            lblTip.Location = new Point(218, 10);

            gridToolbar.Controls.Add(btnSelectAll);
            gridToolbar.Controls.Add(btnSelectNone);
            gridToolbar.Controls.Add(btnInvert);
            gridToolbar.Controls.Add(lblTip);

            _grid = new DataGridView();
            _grid.Dock = DockStyle.Fill;
            _grid.BackgroundColor = Color.White;
            _grid.BorderStyle = BorderStyle.None;
            _grid.RowHeadersVisible = false;
            _grid.AllowUserToAddRows = false;
            _grid.AllowUserToDeleteRows = false;
            _grid.AllowUserToResizeRows = false;
            _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _grid.MultiSelect = false;
            _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            _grid.GridColor = Color.FromArgb(238, 242, 246);
            _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            _grid.EnableHeadersVisualStyles = false;
            _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 252);
            _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(51, 65, 85);
            _grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _grid.ColumnHeadersHeight = 32;
            _grid.RowTemplate.Height = 30;
            _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(232, 247, 238);
            _grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(20, 30, 40);

            EnableDoubleBuffering(_grid);

            DataGridViewCheckBoxColumn colSel = new DataGridViewCheckBoxColumn();
            colSel.Name = "ColSel";
            colSel.HeaderText = "选";
            colSel.Width = 38;
            _grid.Columns.Add(colSel);

            _grid.Columns.Add(MakeTextCol("ColDate", "打印日期", 125));
            _grid.Columns.Add(MakeTextCol("ColName", "模型名称", 175));
            _grid.Columns.Add(MakeTextCol("ColPlate", "盘号", 74));
            _grid.Columns.Add(MakeTextCol("ColDuration", "耗时", 54));
            _grid.Columns.Add(MakeTextCol("ColDevice", "打印机", 130));
            _grid.Columns.Add(MakeTextCol("ColStatus", "打印状态", 74));
            _grid.Columns.Add(MakeTextCol("ColFileName", "自动命名 (日期-名称)", 215));
            _grid.Columns.Add(MakeTextCol("ColState", "截图状态", 92));

            _grid.CurrentCellDirtyStateChanged += delegate
            {
                if (_grid.IsCurrentCellDirty)
                {
                    _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            _grid.CellValueChanged += OnGridCellValueChanged;
            _grid.SelectionChanged += OnGridSelectionChanged;
            _grid.CellDoubleClick += OnGridCellDoubleClick;
            _grid.CellFormatting += OnGridCellFormatting;

            leftCard.Controls.Add(_grid);
            leftCard.Controls.Add(gridToolbar);
            split.Panel1.Controls.Add(leftCard);

            // Right panel: Preview + Log
            SplitContainer rightSplit = new SplitContainer();
            rightSplit.Dock = DockStyle.Fill;
            rightSplit.Orientation = Orientation.Horizontal;
            rightSplit.SplitterWidth = 8;
            rightSplit.BackColor = BgGray;

            Panel previewCard = new Panel();
            previewCard.Dock = DockStyle.Fill;
            previewCard.BackColor = Color.White;
            previewCard.Padding = new Padding(10);

            Label lblPrevTitle = new Label();
            lblPrevTitle.Text = "截图预览";
            lblPrevTitle.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            lblPrevTitle.ForeColor = Color.FromArgb(30, 41, 59);
            lblPrevTitle.Dock = DockStyle.Top;
            lblPrevTitle.Height = 24;

            _lblPreviewInfo = new Label();
            _lblPreviewInfo.Text = "尚未选择已截取的图片";
            _lblPreviewInfo.ForeColor = Color.FromArgb(100, 116, 139);
            _lblPreviewInfo.Font = new Font("Microsoft YaHei UI", 8.5f);
            _lblPreviewInfo.Dock = DockStyle.Bottom;
            _lblPreviewInfo.Height = 24;
            _lblPreviewInfo.TextAlign = ContentAlignment.MiddleLeft;

            _picPreview = new PictureBox();
            _picPreview.Dock = DockStyle.Fill;
            _picPreview.SizeMode = PictureBoxSizeMode.Zoom;
            _picPreview.BackColor = Color.FromArgb(248, 250, 252);
            _picPreview.BorderStyle = BorderStyle.FixedSingle;

            previewCard.Controls.Add(_picPreview);
            previewCard.Controls.Add(lblPrevTitle);
            previewCard.Controls.Add(_lblPreviewInfo);
            rightSplit.Panel1.Controls.Add(previewCard);

            Panel logCard = new Panel();
            logCard.Dock = DockStyle.Fill;
            logCard.BackColor = Color.White;
            logCard.Padding = new Padding(10);

            Label lblLogTitle = new Label();
            lblLogTitle.Text = "运行日志";
            lblLogTitle.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            lblLogTitle.ForeColor = Color.FromArgb(30, 41, 59);
            lblLogTitle.Dock = DockStyle.Top;
            lblLogTitle.Height = 24;

            _txtLog = new TextBox();
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.BackColor = Color.FromArgb(248, 250, 252);
            _txtLog.BorderStyle = BorderStyle.FixedSingle;
            _txtLog.Font = new Font("Consolas", 8.5f);

            logCard.Controls.Add(_txtLog);
            logCard.Controls.Add(lblLogTitle);
            rightSplit.Panel2.Controls.Add(logCard);

            split.Panel2.Controls.Add(rightSplit);

            // 4. Status Strip
            StatusStrip statusStrip = new StatusStrip();
            statusStrip.BackColor = Color.White;
            statusStrip.SizingGrip = true;

            _lblFooterStatus = new ToolStripStatusLabel();
            _lblFooterStatus.Text = "就绪 — 可用的命名占位符: {date} {date_compact} {time} {datetime} {name} {plate} {device} {status}";
            _lblFooterStatus.Spring = true;
            _lblFooterStatus.TextAlign = ContentAlignment.MiddleLeft;

            _progressBar = new ToolStripProgressBar();
            _progressBar.Width = 150;
            _progressBar.Visible = false;

            statusStrip.Items.Add(_lblFooterStatus);
            statusStrip.Items.Add(_progressBar);

            // Add top-level controls in proper docking order
            Controls.Add(split);
            Controls.Add(configPanel);
            Controls.Add(headerPanel);
            Controls.Add(statusStrip);

            Load += delegate
            {
                try
                {
                    split.SplitterDistance = (int)(split.Width * 0.68);
                    rightSplit.SplitterDistance = (int)(rightSplit.Height * 0.62);
                }
                catch
                {
                }
                AppendLog("程序已启动。请在 Bambu Studio 中打开“打印历史”页面，点击“扫描当前页”或“自动滚动批量截图”。");
            };
        }

        private static void EnableDoubleBuffering(DataGridView dgv)
        {
            try
            {
                PropertyInfo pi = typeof(DataGridView).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
                if (pi != null) pi.SetValue(dgv, true, null);
            }
            catch
            {
            }
        }

        private static Button CreateStyledButton(string text, Color back, Color fore, Color border, int width)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Width = width;
            btn.Height = 34;
            btn.Margin = new Padding(6, 0, 0, 0);
            btn.FlatStyle = FlatStyle.Flat;
            btn.BackColor = back;
            btn.ForeColor = fore;
            btn.FlatAppearance.BorderColor = border;
            btn.FlatAppearance.BorderSize = 1;
            btn.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private static Button MakeSmallToolBtn(string text, int width)
        {
            Button btn = new Button();
            btn.Text = text;
            btn.Width = width;
            btn.Height = 25;
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderColor = BorderGray;
            btn.BackColor = Color.White;
            btn.ForeColor = Color.FromArgb(51, 65, 85);
            btn.Font = new Font("Microsoft YaHei UI", 8.5f);
            btn.Cursor = Cursors.Hand;
            return btn;
        }

        private static Label MakeInlineLabel(string text, int rightPad = 4)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.AutoSize = true;
            lbl.ForeColor = Color.FromArgb(71, 85, 105);
            lbl.Margin = new Padding(0, 9, rightPad, 0);
            return lbl;
        }

        private static DataGridViewTextBoxColumn MakeTextCol(string name, string header, int width)
        {
            DataGridViewTextBoxColumn col = new DataGridViewTextBoxColumn();
            col.Name = name;
            col.HeaderText = header;
            col.Width = width;
            col.ReadOnly = true;
            return col;
        }

        private void LoadConfigToUi()
        {
            _txtOutputFolder.Text = _config.OutputFolder;
            _cmbTemplate.Text = _config.NamingTemplate;
            _cmbDuplicate.SelectedIndex = (int)_config.DuplicateMode;
            _numPadding.Value = Math.Max(_numPadding.Minimum, Math.Min(_numPadding.Maximum, _config.PaddingPixels));
            _cmbScrollMode.SelectedIndex = (int)_config.ScrollMode;
            _numMaxCards.Value = Math.Max(_numMaxCards.Minimum, Math.Min(_numMaxCards.Maximum, _config.MaxAutoScrollCards));
        }

        private void SaveUiToConfig()
        {
            if (!string.IsNullOrWhiteSpace(_txtOutputFolder.Text))
            {
                _config.OutputFolder = _txtOutputFolder.Text.Trim();
            }
            if (!string.IsNullOrWhiteSpace(_cmbTemplate.Text))
            {
                _config.NamingTemplate = _cmbTemplate.Text.Trim();
            }
            if (_cmbDuplicate.SelectedIndex >= 0)
            {
                _config.DuplicateMode = (DuplicatePolicy)_cmbDuplicate.SelectedIndex;
            }
            _config.PaddingPixels = (int)_numPadding.Value;
            if (_cmbScrollMode.SelectedIndex >= 0)
            {
                _config.ScrollMode = (ScrollDirectionMode)_cmbScrollMode.SelectedIndex;
            }
            _config.MaxAutoScrollCards = (int)_numMaxCards.Value;
            _config.Save();
        }

        private CaptureFilter BuildCurrentFilter()
        {
            CaptureFilter f = new CaptureFilter();
            switch (_cmbStatusFilter.SelectedIndex)
            {
                case 1: f.StatusFilter = "success"; break;
                case 2: f.StatusFilter = "printing"; break;
                case 3: f.StatusFilter = "canceled"; break;
                default: f.StatusFilter = string.Empty; break;
            }
            f.EnableDateFilter = _chkDateFilter.Checked;
            f.StartDate = _dtpStart.Value.Date;
            f.EndDate = _dtpEnd.Value.Date;
            f.Keyword = _txtKeyword.Text;
            return f;
        }

        private void RefreshBambuConnectionStatus()
        {
            string title;
            IntPtr hwnd = NativeMethods.FindBambuStudioMainWindow(out title);
            if (hwnd != IntPtr.Zero)
            {
                string display = string.IsNullOrEmpty(title) ? "Bambu Studio" : title;
                if (display.Length > 36) display = display.Substring(0, 36) + "...";
                _lblConnStatus.Text = "● 已检测到窗口: " + display;
                _lblConnStatus.ForeColor = BambuGreen;
            }
            else
            {
                _lblConnStatus.Text = "○ 未检测到 Bambu Studio（请先启动拓竹 Bambu Studio）";
                _lblConnStatus.ForeColor = Color.FromArgb(220, 38, 38);
            }
        }

        private void SetBusy(bool busy, string statusText)
        {
            _isBusy = busy;
            _btnScan.Enabled = !busy;
            _btnCaptureSelected.Enabled = !busy;
            _btnAutoScroll.Enabled = !busy;
            _btnDiagnose.Enabled = !busy;
            _progressBar.Visible = busy;
            if (busy)
            {
                _progressBar.Style = ProgressBarStyle.Marquee;
            }
            if (!string.IsNullOrEmpty(statusText))
            {
                _lblFooterStatus.Text = statusText;
            }
        }

        private void AppendLog(string msg)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(AppendLog), msg);
                return;
            }
            string line = string.Format("[{0}] {1}{2}", DateTime.Now.ToString("HH:mm:ss"), msg, Environment.NewLine);
            _txtLog.AppendText(line);
        }

        private void ShowPreviewImage(string path)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(ShowPreviewImage), path);
                return;
            }

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using (MemoryStream ms = new MemoryStream(bytes))
                {
                    Image loaded = Image.FromStream(ms);
                    Bitmap copy = new Bitmap(loaded);
                    loaded.Dispose();

                    Image old = _picPreview.Image;
                    _picPreview.Image = copy;
                    if (old != null) old.Dispose();

                    _lblPreviewInfo.Text = string.Format("{0}  ({1} x {2})",
                        Path.GetFileName(path), copy.Width, copy.Height);
                }
            }
            catch
            {
            }
        }

        private void ApplyFiltersToGrid()
        {
            CaptureFilter filter = BuildCurrentFilter();
            _filteredCards.Clear();
            for (int i = 0; i < _allCards.Count; i++)
            {
                if (filter.Matches(_allCards[i]))
                {
                    _filteredCards.Add(_allCards[i]);
                }
            }

            _grid.Rows.Clear();
            string tmpl = _config.NamingTemplate;

            for (int i = 0; i < _filteredCards.Count; i++)
            {
                PrintHistoryCard c = _filteredCards[i];
                string previewName = FilenameHelper.FormatFileName(c, tmpl);
                int rowIdx = _grid.Rows.Add(
                    c.IsSelected,
                    c.FormatDisplayDate(),
                    c.Name,
                    c.Plate,
                    c.Duration,
                    c.Device,
                    c.Status,
                    previewName,
                    c.CaptureState);
                _grid.Rows[rowIdx].Tag = c;
            }

            UpdateFooterCounts();
        }

        private void RefreshFileNamePreviewColumn()
        {
            string tmpl = _config.NamingTemplate;
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                PrintHistoryCard c = _grid.Rows[i].Tag as PrintHistoryCard;
                if (c != null)
                {
                    _grid.Rows[i].Cells["ColFileName"].Value = FilenameHelper.FormatFileName(c, tmpl);
                }
            }
        }

        private void UpdateFooterCounts()
        {
            int selected = 0;
            int saved = 0;
            for (int i = 0; i < _filteredCards.Count; i++)
            {
                if (_filteredCards[i].IsSelected) selected++;
                if (_filteredCards[i].CaptureState == "已保存") saved++;
            }
            _lblFooterStatus.Text = string.Format("共 {0} 张卡片（筛选显示 {1} 张，已勾选 {2} 张，本次已保存 {3} 张）",
                _allCards.Count, _filteredCards.Count, selected, saved);
        }

        private void SetAllSelection(bool selected)
        {
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                PrintHistoryCard c = _grid.Rows[i].Tag as PrintHistoryCard;
                if (c != null)
                {
                    bool val = selected && c.IsFullyVisible;
                    c.IsSelected = val;
                    _grid.Rows[i].Cells["ColSel"].Value = val;
                }
            }
            UpdateFooterCounts();
        }

        private void InvertSelection()
        {
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                PrintHistoryCard c = _grid.Rows[i].Tag as PrintHistoryCard;
                if (c != null && c.IsFullyVisible)
                {
                    c.IsSelected = !c.IsSelected;
                    _grid.Rows[i].Cells["ColSel"].Value = c.IsSelected;
                }
            }
            UpdateFooterCounts();
        }

        private void OnGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            PrintHistoryCard c = _grid.Rows[e.RowIndex].Tag as PrintHistoryCard;
            if (c != null)
            {
                object val = _grid.Rows[e.RowIndex].Cells[0].Value;
                c.IsSelected = (val is bool) && (bool)val;
                UpdateFooterCounts();
            }
        }

        private void OnGridSelectionChanged(object sender, EventArgs e)
        {
            if (_grid.SelectedRows.Count == 0) return;
            PrintHistoryCard c = _grid.SelectedRows[0].Tag as PrintHistoryCard;
            if (c != null && !string.IsNullOrEmpty(c.SavedFilePath) && File.Exists(c.SavedFilePath))
            {
                ShowPreviewImage(c.SavedFilePath);
            }
        }

        private void OnGridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            PrintHistoryCard c = _grid.Rows[e.RowIndex].Tag as PrintHistoryCard;
            if (c != null && !string.IsNullOrEmpty(c.SavedFilePath) && File.Exists(c.SavedFilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(c.SavedFilePath) { UseShellExecute = true });
                }
                catch
                {
                }
            }
        }

        private void OnGridCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.Value == null) return;
            string colName = _grid.Columns[e.ColumnIndex].Name;
            string txt = e.Value.ToString();

            if (colName == "ColStatus")
            {
                if (txt.Contains("正在打印") || txt.IndexOf("Printing", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    e.CellStyle.ForeColor = Color.FromArgb(255, 111, 0);
                    e.CellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
                }
                else if (txt.Contains("成功") || txt.Contains("完成") || txt.IndexOf("Success", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    e.CellStyle.ForeColor = BambuGreen;
                    e.CellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
                }
                else if (txt.Contains("取消") || txt.Contains("失败") || txt.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    e.CellStyle.ForeColor = Color.FromArgb(208, 27, 27);
                    e.CellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
                }
            }
            else if (colName == "ColState")
            {
                if (txt == "已保存")
                {
                    e.CellStyle.ForeColor = BambuGreen;
                    e.CellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
                }
                else if (txt.Contains("超出"))
                {
                    e.CellStyle.ForeColor = Color.FromArgb(148, 163, 184);
                }
                else if (txt.Contains("失败"))
                {
                    e.CellStyle.ForeColor = Color.FromArgb(220, 38, 38);
                }
            }
        }

        private void OnBrowseFolderClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = "选择打印历史截图的保存文件夹";
                if (Directory.Exists(_txtOutputFolder.Text))
                {
                    dlg.SelectedPath = _txtOutputFolder.Text;
                }
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _txtOutputFolder.Text = dlg.SelectedPath;
                    SaveUiToConfig();
                }
            }
        }

        private void OnOpenFolderClick(object sender, EventArgs e)
        {
            SaveUiToConfig();
            try
            {
                Directory.CreateDirectory(_config.OutputFolder);
                Process.Start("explorer.exe", _config.OutputFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开目录: " + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async void OnScanClick(object sender, EventArgs e)
        {
            if (_isBusy) return;
            SaveUiToConfig();
            SetBusy(true, "正在扫描 Bambu Studio 窗口中的打印历史卡片...");
            AppendLog("开始扫描 Bambu Studio 当前页面...");

            ScanResult res = null;
            await Task.Run(delegate
            {
                res = _scanner.Scan(false);
            });

            _lastScanResult = res;
            _allCards.Clear();
            if (res != null && res.Cards != null)
            {
                _allCards.AddRange(res.Cards);
            }

            ApplyFiltersToGrid();
            RefreshBambuConnectionStatus();
            SetBusy(false, res != null ? res.Message : "扫描完成");
            AppendLog(res != null ? res.Message : "扫描完成");

            if (res != null && (!res.BambuWindowFound || res.Cards.Count == 0))
            {
                MessageBox.Show(this, res.Message, "扫描提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private async void OnCaptureSelectedClick(object sender, EventArgs e)
        {
            if (_isBusy) return;
            SaveUiToConfig();

            // If user hasn't scanned yet, scan automatically first!
            if (_allCards.Count == 0)
            {
                SetBusy(true, "正在扫描 Bambu Studio 窗口...");
                ScanResult quickScan = null;
                await Task.Run(delegate { quickScan = _scanner.Scan(false); });
                _lastScanResult = quickScan;
                if (quickScan != null && quickScan.Cards != null)
                {
                    _allCards.AddRange(quickScan.Cards);
                }
                ApplyFiltersToGrid();
                if (_filteredCards.Count == 0)
                {
                    SetBusy(false, quickScan != null ? quickScan.Message : "未找到卡片");
                    MessageBox.Show(this, quickScan != null ? quickScan.Message : "未检测到打印历史卡片。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            List<PrintHistoryCard> targets = new List<PrintHistoryCard>();
            HashSet<string> selectedKeys = new HashSet<string>();
            for (int i = 0; i < _filteredCards.Count; i++)
            {
                if (_filteredCards[i].IsSelected && _filteredCards[i].IsFullyVisible)
                {
                    targets.Add(_filteredCards[i]);
                    selectedKeys.Add(_filteredCards[i].UniqueKey);
                }
            }

            if (targets.Count == 0)
            {
                MessageBox.Show(this, "请至少勾选 1 张“完整显示在可视区内”的卡片再截图。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetBusy(true, string.Format("正在截取 {0} 张勾选卡片...", targets.Count));
            AppendLog(string.Format("准备截取 {0} 张卡片，临时隐藏工具窗口并前置 Bambu Studio...", targets.Count));

            // Hide our window so it never covers Bambu Studio during capture
            Hide();
            CaptureSummary summary = null;
            ScanResult freshScan = null;

            try
            {
                await Task.Run(delegate
                {
                    Thread.Sleep(180);
                    // Re-scan after bringing Bambu Studio to front so coordinates are 100% fresh!
                    freshScan = _scanner.Scan(true);
                    if (!freshScan.BambuWindowFound || freshScan.Cards.Count == 0)
                    {
                        return;
                    }

                    Thread.Sleep(180);
                    List<PrintHistoryCard> matchedFresh = new List<PrintHistoryCard>();
                    for (int i = 0; i < freshScan.Cards.Count; i++)
                    {
                        PrintHistoryCard fc = freshScan.Cards[i];
                        if (fc.IsFullyVisible && selectedKeys.Contains(fc.UniqueKey))
                        {
                            matchedFresh.Add(fc);
                        }
                    }

                    summary = ScreenshotCapturer.CaptureCardsFromViewport(
                        freshScan.ViewportRect,
                        matchedFresh,
                        _config,
                        null,
                        delegate(PrintHistoryCard c, string path)
                        {
                            AppendLog(string.Format("  [{0}] {1}", c.CaptureState, Path.GetFileName(path)));
                        });

                    // Sync status back to our displayed cards
                    for (int i = 0; i < matchedFresh.Count; i++)
                    {
                        PrintHistoryCard fc = matchedFresh[i];
                        for (int j = 0; j < _allCards.Count; j++)
                        {
                            if (_allCards[j].UniqueKey == fc.UniqueKey)
                            {
                                _allCards[j].SavedFilePath = fc.SavedFilePath;
                                _allCards[j].CaptureState = fc.CaptureState;
                            }
                        }
                    }
                });
            }
            finally
            {
                Show();
                Activate();
            }

            ApplyFiltersToGrid();

            if (summary != null)
            {
                if (!string.IsNullOrEmpty(summary.LastSavedPath))
                {
                    ShowPreviewImage(summary.LastSavedPath);
                }
                string msg = string.Format("截图完成：成功保存 {0} 张，跳过 {1} 张，失败 {2} 张。保存位置：{3}",
                    summary.SavedCount, summary.SkippedCount, summary.FailedCount, _config.OutputFolder);
                AppendLog(msg);
                SetBusy(false, msg);
            }
            else
            {
                string err = (freshScan != null && !string.IsNullOrEmpty(freshScan.Message))
                    ? freshScan.Message
                    : "截图失败，请确保 Bambu Studio 处于打开状态。";
                AppendLog(err);
                SetBusy(false, err);
            }
        }

        private async void OnAutoScrollCaptureClick(object sender, EventArgs e)
        {
            if (_isBusy) return;
            SaveUiToConfig();

            string title;
            IntPtr checkHwnd = NativeMethods.FindBambuStudioMainWindow(out title);
            if (checkHwnd == IntPtr.Zero)
            {
                MessageBox.Show(this, "未检测到运行中的 Bambu Studio 窗口，请先打开 Bambu Studio 并切换到“打印历史”页面。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            CaptureFilter filter = BuildCurrentFilter();
            _cancelRequested = false;
            SetBusy(true, "正在执行自动滚动批量截图（按 ESC 可随时停止）...");
            AppendLog("=== 开始自动滚动批量截图（按 ESC 可随时停止） ===");

            FloatingStopBanner banner = new FloatingStopBanner();
            banner.StopRequested += delegate { _cancelRequested = true; };

            Hide();
            banner.Show();

            List<PrintHistoryCard> collectedAll = new List<PrintHistoryCard>();
            HashSet<string> capturedKeys = new HashSet<string>();
            int totalSaved = 0;
            int totalSkipped = 0;
            int totalFailed = 0;
            string lastSaved = string.Empty;

            try
            {
                await Task.Run(delegate
                {
                    Thread.Sleep(220);
                    int consecutiveNoNew = 0;
                    bool triedHorizontalFallback = false;
                    ScrollDirectionMode activeDir = _config.ScrollMode;
                    string lastPositionSignature = string.Empty;

                    while (!_cancelRequested && totalSaved < _config.MaxAutoScrollCards)
                    {
                        if (NativeMethods.IsEscapePressed())
                        {
                            _cancelRequested = true;
                            break;
                        }

                        // Hide tiny floating banner briefly during the actual screenshot so it never overlaps any card
                        InvokeOnBanner(banner, delegate { banner.Hide(); });
                        Thread.Sleep(55);

                        ScanResult scan = _scanner.Scan(true);
                        if (!scan.BambuWindowFound)
                        {
                            AppendLog("Bambu Studio 窗口已关闭或不可见，自动停止。");
                            break;
                        }

                        Thread.Sleep(110);
                        List<PrintHistoryCard> toCapture = new List<PrintHistoryCard>();
                        StringBuilder sig = new StringBuilder();

                        for (int i = 0; i < scan.Cards.Count; i++)
                        {
                            PrintHistoryCard c = scan.Cards[i];
                            sig.Append(c.UniqueKey).Append('@').Append(c.Bounds.X).Append(',').Append(c.Bounds.Y).Append(';');
                            if (c.IsFullyVisible && !capturedKeys.Contains(c.UniqueKey) && filter.Matches(c))
                            {
                                if (totalSaved + toCapture.Count < _config.MaxAutoScrollCards)
                                {
                                    toCapture.Add(c);
                                }
                            }
                        }

                        int newlyProcessed = 0;
                        if (toCapture.Count > 0)
                        {
                            CaptureSummary stepSum = ScreenshotCapturer.CaptureCardsFromViewport(
                                scan.ViewportRect,
                                toCapture,
                                _config,
                                capturedKeys,
                                delegate(PrintHistoryCard c, string path)
                                {
                                    collectedAll.Add(c);
                                    AppendLog(string.Format("  [{0}] {1}", c.CaptureState, Path.GetFileName(path)));
                                });

                            totalSaved += stepSum.SavedCount;
                            totalSkipped += stepSum.SkippedCount;
                            totalFailed += stepSum.FailedCount;
                            newlyProcessed = stepSum.SavedCount + stepSum.SkippedCount;
                            if (!string.IsNullOrEmpty(stepSum.LastSavedPath))
                            {
                                lastSaved = stepSum.LastSavedPath;
                            }
                        }

                        InvokeOnBanner(banner, delegate
                        {
                            banner.UpdateStatus(string.Format("已保存 {0} 张 (跳过 {1}) | 按 ESC 停止", totalSaved, totalSkipped));
                            banner.Show();
                        });

                        if (_cancelRequested || NativeMethods.IsEscapePressed() || totalSaved >= _config.MaxAutoScrollCards)
                        {
                            break;
                        }

                        string curSig = sig.ToString();
                        bool moved = (curSig != lastPositionSignature);

                        if (newlyProcessed > 0)
                        {
                            consecutiveNoNew = 0;
                        }
                        else if (!moved && !string.IsNullOrEmpty(lastPositionSignature))
                        {
                            // If vertical scroll didn't move in Auto mode, try horizontal scroll once!
                            if (activeDir == ScrollDirectionMode.Auto && !triedHorizontalFallback)
                            {
                                triedHorizontalFallback = true;
                                activeDir = ScrollDirectionMode.HorizontalRight;
                                AppendLog("向下滚动未移动，自动切换为向右横向滚动...");
                                consecutiveNoNew = 0;
                            }
                            else
                            {
                                consecutiveNoNew++;
                            }
                        }
                        else
                        {
                            consecutiveNoNew++;
                        }

                        if (consecutiveNoNew >= 3)
                        {
                            AppendLog("已滚动至列表末尾（连续 3 次无新卡片），自动结束。");
                            break;
                        }

                        lastPositionSignature = curSig;

                        // Pick a scroll point inside the Print History cards area
                        Point scrollPt = ComputeScrollPoint(scan);
                        if (activeDir == ScrollDirectionMode.HorizontalRight)
                        {
                            NativeMethods.ScrollHorizontalAt(scrollPt, 4);
                        }
                        else
                        {
                            NativeMethods.ScrollVerticalAt(scrollPt, -5);
                        }

                        // Wait in short increments so ESC feels instant
                        int waited = 0;
                        while (waited < _config.ScrollDelayMs)
                        {
                            if (_cancelRequested || NativeMethods.IsEscapePressed())
                            {
                                _cancelRequested = true;
                                break;
                            }
                            Thread.Sleep(50);
                            waited += 50;
                        }
                    }
                });
            }
            finally
            {
                try
                {
                    banner.Close();
                    banner.Dispose();
                }
                catch
                {
                }
                Show();
                Activate();
            }

            if (collectedAll.Count > 0)
            {
                _allCards.Clear();
                _allCards.AddRange(collectedAll);
                ApplyFiltersToGrid();
            }

            if (!string.IsNullOrEmpty(lastSaved))
            {
                ShowPreviewImage(lastSaved);
            }

            string doneMsg = string.Format("自动滚动批量截图完成{0}：共保存 {1} 张，跳过 {2} 张，失败 {3} 张。",
                _cancelRequested ? "（用户手动停止）" : string.Empty,
                totalSaved, totalSkipped, totalFailed);
            AppendLog(doneMsg);
            SetBusy(false, doneMsg);
        }

        private static Point ComputeScrollPoint(ScanResult scan)
        {
            if (scan != null && scan.Cards != null && scan.Cards.Count > 0)
            {
                // Scroll right over the middle of the detected cards so we hit the PrintHistory scrollable container
                int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                for (int i = 0; i < scan.Cards.Count; i++)
                {
                    Rectangle r = scan.Cards[i].Bounds;
                    if (r.Left < minX) minX = r.Left;
                    if (r.Top < minY) minY = r.Top;
                    if (r.Right > maxX) maxX = r.Right;
                    if (r.Bottom > maxY) maxY = r.Bottom;
                }
                int cx = Math.Max(scan.ViewportRect.Left + 20, Math.Min(scan.ViewportRect.Right - 20, (minX + maxX) / 2));
                int cy = Math.Max(scan.ViewportRect.Top + 20, Math.Min(scan.ViewportRect.Bottom - 20, (minY + maxY) / 2));
                return new Point(cx, cy);
            }

            if (scan != null && scan.ViewportRect.Width > 0 && scan.ViewportRect.Height > 0)
            {
                return new Point(
                    scan.ViewportRect.Left + scan.ViewportRect.Width / 2,
                    scan.ViewportRect.Top + scan.ViewportRect.Height / 2);
            }

            return new Point(Screen.PrimaryScreen.Bounds.Width / 2, Screen.PrimaryScreen.Bounds.Height / 2);
        }

        private static void InvokeOnBanner(FloatingStopBanner banner, Action action)
        {
            if (banner == null || banner.IsDisposed) return;
            try
            {
                if (banner.InvokeRequired)
                {
                    banner.Invoke(action);
                }
                else
                {
                    action();
                }
            }
            catch
            {
            }
        }

        private async void OnDiagnoseClick(object sender, EventArgs e)
        {
            if (_isBusy) return;
            SetBusy(true, "正在采集 Bambu Studio UIA 诊断信息...");
            string report = string.Empty;
            await Task.Run(delegate
            {
                report = _scanner.GenerateDiagnosticReport();
            });

            string diagPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uia_debug.txt");
            try
            {
                File.WriteAllText(diagPath, report, Encoding.UTF8);
                AppendLog("已导出诊断报告: " + diagPath);
                SetBusy(false, "诊断报告已导出: uia_debug.txt");
                DialogResult dr = MessageBox.Show(this,
                    "诊断报告已保存到：\n" + diagPath + "\n\n是否立即打开查看？",
                    "窗口诊断完成",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);
                if (dr == DialogResult.Yes)
                {
                    Process.Start(new ProcessStartInfo(diagPath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                SetBusy(false, "导出诊断报告失败");
                MessageBox.Show(this, "导出失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
