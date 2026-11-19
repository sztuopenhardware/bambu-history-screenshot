using System;
using System.Drawing;
using System.Windows.Forms;

namespace BambuHistoryScreenshot
{
    public class CloudLoginForm : Form
    {
        private readonly BambuCloudClient _client;
        private readonly AppConfig _config;

        private ComboBox _cmbRegion;
        private TabControl _tabs;

        // Tab 0: SMS Code Login
        private ComboBox _cmbAreaCode;
        private TextBox _txtSmsPhone;
        private TextBox _txtSmsCode;
        private Button _btnSendSms;
        private System.Windows.Forms.Timer _smsCooldownTimer;
        private int _smsCooldownSec;

        // Tab 1: Account + Password (+ VerifyCode / 2FA)
        private TextBox _txtAccount;
        private TextBox _txtPassword;
        private Label _lblCodePrompt;
        private TextBox _txtCode;
        private Button _btnResendCode;

        // Tab 2: Manual Token Login
        private TextBox _txtToken;

        private Label _lblStatus;
        private Button _btnLogin;
        private Button _btnCancel;

        private CloudLoginState _pendingChallenge;
        private string _pendingTfaKey;

        public CloudLoginForm(BambuCloudClient client, AppConfig config)
        {
            _client = client;
            _config = config;
            _pendingChallenge = CloudLoginState.Failed;
            _pendingTfaKey = string.Empty;
            _smsCooldownSec = 0;

            InitializeUi();
        }

        private void InitializeUi()
        {
            Text = "登录拓竹云端账号（获取耗材种类与重量）";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(482, 408);
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9f);

            Label lblRegion = new Label();
            lblRegion.Text = "服务器区域:";
            lblRegion.AutoSize = true;
            lblRegion.Location = new Point(22, 20);
            lblRegion.ForeColor = Color.FromArgb(51, 65, 85);

            _cmbRegion = new ComboBox();
            _cmbRegion.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbRegion.Location = new Point(108, 16);
            _cmbRegion.Width = 336;
            _cmbRegion.Items.Add("中国大陆 (api.bambulab.cn)");
            _cmbRegion.Items.Add("国际区 (api.bambulab.com)");
            _cmbRegion.SelectedIndex = (_config.CloudRegion == "global") ? 1 : 0;

            _tabs = new TabControl();
            _tabs.Location = new Point(20, 52);
            _tabs.Size = new Size(426, 232);

            // === Tab 0: Mobile SMS Verification Code Login ===
            TabPage tabSms = new TabPage("手机短信验证码登录");
            tabSms.BackColor = Color.White;
            tabSms.Padding = new Padding(12);

            Label lblSmsPhone = new Label();
            lblSmsPhone.Text = "手机号码:";
            lblSmsPhone.AutoSize = true;
            lblSmsPhone.Location = new Point(14, 24);

            _cmbAreaCode = new ComboBox();
            _cmbAreaCode.DropDownStyle = ComboBoxStyle.DropDown;
            _cmbAreaCode.Location = new Point(92, 20);
            _cmbAreaCode.Width = 68;
            _cmbAreaCode.Items.AddRange(new object[] { "+86", "+852", "+853", "+886", "+1", "+44", "+81" });
            _cmbAreaCode.Text = "+86";

            _txtSmsPhone = new TextBox();
            _txtSmsPhone.Location = new Point(166, 20);
            _txtSmsPhone.Width = 228;
            if (!string.IsNullOrWhiteSpace(_config.CloudAccount) && BambuCloudClient.IsPhoneLike(_config.CloudAccount))
            {
                _txtSmsPhone.Text = _config.CloudAccount;
            }

            Label lblSmsCode = new Label();
            lblSmsCode.Text = "短信验证码:";
            lblSmsCode.AutoSize = true;
            lblSmsCode.Location = new Point(14, 64);

            _txtSmsCode = new TextBox();
            _txtSmsCode.Location = new Point(92, 60);
            _txtSmsCode.Width = 176;

            _btnSendSms = new Button();
            _btnSendSms.Text = "获取验证码";
            _btnSendSms.Location = new Point(276, 59);
            _btnSendSms.Size = new Size(118, 25);
            _btnSendSms.FlatStyle = FlatStyle.Flat;
            _btnSendSms.BackColor = Color.FromArgb(241, 245, 249);
            _btnSendSms.ForeColor = Color.FromArgb(15, 23, 42);
            _btnSendSms.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            _btnSendSms.Cursor = Cursors.Hand;
            _btnSendSms.Click += OnSendSmsClick;

            Label lblSmsTip = new Label();
            lblSmsTip.Text = "说明：手机短信验证码登录主要适用于中国大陆区（api.bambulab.cn）。\n点击“获取验证码”后，将收到的 6 位短信验证码填入上方并点击“登录并同步”。\n若遇服务器风控或滑块限制，可改用“账号密码登录”或“Token 登录”。";
            lblSmsTip.ForeColor = Color.FromArgb(100, 116, 139);
            lblSmsTip.Font = new Font("Microsoft YaHei UI", 8.2f);
            lblSmsTip.Location = new Point(14, 108);
            lblSmsTip.Size = new Size(384, 72);

            tabSms.Controls.Add(lblSmsPhone);
            tabSms.Controls.Add(_cmbAreaCode);
            tabSms.Controls.Add(_txtSmsPhone);
            tabSms.Controls.Add(lblSmsCode);
            tabSms.Controls.Add(_txtSmsCode);
            tabSms.Controls.Add(_btnSendSms);
            tabSms.Controls.Add(lblSmsTip);

            // === Tab 1: Account + Password (+ VerifyCode / 2FA) ===
            TabPage tabPwd = new TabPage("账号密码登录");
            tabPwd.BackColor = Color.White;
            tabPwd.Padding = new Padding(12);

            Label lblAcc = new Label();
            lblAcc.Text = "拓竹账号:";
            lblAcc.AutoSize = true;
            lblAcc.Location = new Point(14, 22);

            _txtAccount = new TextBox();
            _txtAccount.Location = new Point(92, 18);
            _txtAccount.Width = 302;
            _txtAccount.Text = _config.CloudAccount ?? string.Empty;

            Label lblPwd = new Label();
            lblPwd.Text = "登录密码:";
            lblPwd.AutoSize = true;
            lblPwd.Location = new Point(14, 58);

            _txtPassword = new TextBox();
            _txtPassword.Location = new Point(92, 54);
            _txtPassword.Width = 302;
            _txtPassword.UseSystemPasswordChar = true;

            _lblCodePrompt = new Label();
            _lblCodePrompt.Text = "验证码:";
            _lblCodePrompt.AutoSize = true;
            _lblCodePrompt.Location = new Point(14, 96);
            _lblCodePrompt.Enabled = false;

            _txtCode = new TextBox();
            _txtCode.Location = new Point(92, 92);
            _txtCode.Width = 176;
            _txtCode.Enabled = false;

            _btnResendCode = new Button();
            _btnResendCode.Text = "重发验证码";
            _btnResendCode.Location = new Point(276, 91);
            _btnResendCode.Size = new Size(118, 25);
            _btnResendCode.FlatStyle = FlatStyle.Flat;
            _btnResendCode.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            _btnResendCode.Enabled = false;
            _btnResendCode.Click += OnResendCodeClick;

            Label lblSecTip = new Label();
            lblSecTip.Text = "说明：仅直连拓竹官方接口拉取打印历史中的耗材种类与克重，\n密码不会保存到本地磁盘。如需邮箱/短信验证码，登录时将自动发送。";
            lblSecTip.ForeColor = Color.FromArgb(100, 116, 139);
            lblSecTip.Font = new Font("Microsoft YaHei UI", 8.2f);
            lblSecTip.Location = new Point(14, 136);
            lblSecTip.Size = new Size(382, 48);

            tabPwd.Controls.Add(lblAcc);
            tabPwd.Controls.Add(_txtAccount);
            tabPwd.Controls.Add(lblPwd);
            tabPwd.Controls.Add(_txtPassword);
            tabPwd.Controls.Add(_lblCodePrompt);
            tabPwd.Controls.Add(_txtCode);
            tabPwd.Controls.Add(_btnResendCode);
            tabPwd.Controls.Add(lblSecTip);

            // === Tab 2: Manual Token Login (fallback for WeChat-only accounts) ===
            TabPage tabToken = new TabPage("Token 登录（备用）");
            tabToken.BackColor = Color.White;
            tabToken.Padding = new Padding(12);

            Label lblTokenTip = new Label();
            lblTokenTip.Text = "如果您的账号仅支持微信扫码登录未设置密码，可在此粘贴\n拓竹云端 Bearer Access Token（无需包含 \"Bearer \" 前缀）：";
            lblTokenTip.ForeColor = Color.FromArgb(71, 85, 105);
            lblTokenTip.Font = new Font("Microsoft YaHei UI", 8.5f);
            lblTokenTip.Location = new Point(14, 14);
            lblTokenTip.Size = new Size(384, 40);

            _txtToken = new TextBox();
            _txtToken.Location = new Point(14, 62);
            _txtToken.Size = new Size(384, 118);
            _txtToken.Multiline = true;
            _txtToken.ScrollBars = ScrollBars.Vertical;
            _txtToken.Font = new Font("Consolas", 8.5f);

            tabToken.Controls.Add(lblTokenTip);
            tabToken.Controls.Add(_txtToken);

            _tabs.TabPages.Add(tabSms);
            _tabs.TabPages.Add(tabPwd);
            _tabs.TabPages.Add(tabToken);

            // Auto-select Account+Password tab if saved account is an email; otherwise default to SMS login tab
            if (!string.IsNullOrWhiteSpace(_config.CloudAccount) && _config.CloudAccount.Contains("@"))
            {
                _tabs.SelectedIndex = 1;
            }
            else
            {
                _tabs.SelectedIndex = 0;
            }

            _tabs.SelectedIndexChanged += delegate
            {
                _btnLogin.Text = "登录并同步";
            };

            _lblStatus = new Label();
            _lblStatus.Text = string.Empty;
            _lblStatus.ForeColor = Color.FromArgb(180, 83, 9);
            _lblStatus.Location = new Point(22, 290);
            _lblStatus.Size = new Size(424, 34);
            _lblStatus.Font = new Font("Microsoft YaHei UI", 8.5f);

            _btnLogin = new Button();
            _btnLogin.Text = "登录并同步";
            _btnLogin.Location = new Point(242, 330);
            _btnLogin.Size = new Size(110, 32);
            _btnLogin.FlatStyle = FlatStyle.Flat;
            _btnLogin.BackColor = Color.FromArgb(0, 174, 66);
            _btnLogin.ForeColor = Color.White;
            _btnLogin.FlatAppearance.BorderSize = 0;
            _btnLogin.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            _btnLogin.Cursor = Cursors.Hand;
            _btnLogin.Click += OnLoginClick;

            _btnCancel = new Button();
            _btnCancel.Text = "取消";
            _btnCancel.Location = new Point(360, 330);
            _btnCancel.Size = new Size(84, 32);
            _btnCancel.FlatStyle = FlatStyle.Flat;
            _btnCancel.BackColor = Color.FromArgb(241, 245, 249);
            _btnCancel.ForeColor = Color.FromArgb(51, 65, 85);
            _btnCancel.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            _btnCancel.Cursor = Cursors.Hand;
            _btnCancel.DialogResult = DialogResult.Cancel;

            _smsCooldownTimer = new System.Windows.Forms.Timer();
            _smsCooldownTimer.Interval = 1000;
            _smsCooldownTimer.Tick += OnSmsCooldownTick;

            FormClosed += delegate
            {
                if (_smsCooldownTimer != null)
                {
                    _smsCooldownTimer.Stop();
                    _smsCooldownTimer.Dispose();
                }
            };

            AcceptButton = _btnLogin;
            CancelButton = _btnCancel;

            Controls.Add(lblRegion);
            Controls.Add(_cmbRegion);
            Controls.Add(_tabs);
            Controls.Add(_lblStatus);
            Controls.Add(_btnLogin);
            Controls.Add(_btnCancel);
        }

        private string SelectedRegionCode()
        {
            return (_cmbRegion.SelectedIndex == 1) ? "global" : "cn";
        }

        private void OnSmsCooldownTick(object sender, EventArgs e)
        {
            _smsCooldownSec--;
            if (_smsCooldownSec <= 0)
            {
                _smsCooldownTimer.Stop();
                _btnSendSms.Text = "获取验证码";
                _btnSendSms.Enabled = _btnLogin.Enabled;
            }
            else
            {
                _btnSendSms.Text = string.Format("重新获取({0}s)", _smsCooldownSec);
            }
        }

        private void StartSmsCooldown(int seconds)
        {
            _smsCooldownSec = seconds;
            _btnSendSms.Enabled = false;
            _btnSendSms.Text = string.Format("重新获取({0}s)", _smsCooldownSec);
            _smsCooldownTimer.Start();
        }

        private void SetUiEnabled(bool enabled)
        {
            _cmbRegion.Enabled = enabled;
            _btnLogin.Enabled = enabled;

            _cmbAreaCode.Enabled = enabled;
            _txtSmsPhone.Enabled = enabled;
            _txtSmsCode.Enabled = enabled;
            _btnSendSms.Enabled = enabled && (_smsCooldownSec <= 0);

            _txtAccount.Enabled = enabled;
            _txtPassword.Enabled = enabled;
            _txtToken.Enabled = enabled;
            bool needCode = (_pendingChallenge == CloudLoginState.NeedVerifyCode || _pendingChallenge == CloudLoginState.NeedTfa);
            _lblCodePrompt.Enabled = enabled && needCode;
            _txtCode.Enabled = enabled && needCode;
            _btnResendCode.Enabled = enabled && (_pendingChallenge == CloudLoginState.NeedVerifyCode);
        }

        private async void OnSendSmsClick(object sender, EventArgs e)
        {
            string phone = _txtSmsPhone.Text.Trim();
            string area = _cmbAreaCode.Text.Trim();
            if (string.IsNullOrEmpty(phone) || !BambuCloudClient.IsPhoneLike(phone))
            {
                _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                _lblStatus.Text = "请输入有效的手机号码。";
                _txtSmsPhone.Focus();
                return;
            }

            string region = SelectedRegionCode();
            _client.SetRegion(region);
            _config.CloudRegion = region;
            _config.CloudAccount = phone;
            _config.Save();

            _btnSendSms.Enabled = false;
            _lblStatus.ForeColor = Color.FromArgb(37, 99, 235);
            _lblStatus.Text = "正在向 " + phone + " 发送短信验证码...";

            CloudSmsSendResult smsRes = await _client.SendSmsCodeAsync(phone, area);
            if (smsRes.Success)
            {
                _lblStatus.ForeColor = Color.FromArgb(0, 148, 56);
                _lblStatus.Text = smsRes.Message;
                StartSmsCooldown(60);
                _txtSmsCode.Focus();
            }
            else
            {
                _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                _lblStatus.Text = "发送短信验证码失败（" + smsRes.Message + "）";
                _btnSendSms.Enabled = true;
            }
        }

        private async void OnResendCodeClick(object sender, EventArgs e)
        {
            string acc = _txtAccount.Text.Trim();
            _client.SetRegion(SelectedRegionCode());
            _btnResendCode.Enabled = false;
            _lblStatus.ForeColor = Color.FromArgb(37, 99, 235);

            if (acc.Contains("@"))
            {
                _lblStatus.Text = "正在重新发送邮箱验证码...";
                bool ok = await _client.SendEmailCodeAsync(acc);
                _lblStatus.ForeColor = ok ? Color.FromArgb(0, 148, 56) : Color.FromArgb(220, 38, 38);
                _lblStatus.Text = ok ? "邮箱验证码已重新发送，请查收。" : "发送失败，请稍后重试。";
            }
            else if (BambuCloudClient.IsPhoneLike(acc))
            {
                _lblStatus.Text = "正在重新发送短信验证码...";
                CloudSmsSendResult sms = await _client.SendSmsCodeAsync(acc, "+86");
                _lblStatus.ForeColor = sms.Success ? Color.FromArgb(0, 148, 56) : Color.FromArgb(220, 38, 38);
                _lblStatus.Text = sms.Success ? sms.Message : ("发送失败：" + sms.Message);
            }
            else
            {
                _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                _lblStatus.Text = "请填写有效的邮箱或手机号后再重发验证码。";
            }

            _btnResendCode.Enabled = true;
        }

        private async void OnLoginClick(object sender, EventArgs e)
        {
            string region = SelectedRegionCode();
            _client.SetRegion(region);
            _config.CloudRegion = region;

            // === Tab 0: Mobile SMS Verification Code Login ===
            if (_tabs.SelectedIndex == 0)
            {
                string phone = _txtSmsPhone.Text.Trim();
                string area = _cmbAreaCode.Text.Trim();
                string smsCode = _txtSmsCode.Text.Trim();

                if (string.IsNullOrEmpty(phone) || !BambuCloudClient.IsPhoneLike(phone))
                {
                    _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                    _lblStatus.Text = "请输入有效的手机号码。";
                    _txtSmsPhone.Focus();
                    return;
                }
                if (string.IsNullOrEmpty(smsCode))
                {
                    _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                    _lblStatus.Text = "请输入收到的短信验证码。";
                    _txtSmsCode.Focus();
                    return;
                }

                _config.CloudAccount = phone;
                _config.Save();

                SetUiEnabled(false);
                _lblStatus.ForeColor = Color.FromArgb(37, 99, 235);
                _lblStatus.Text = "正在验证短信验证码并登录 " + _client.RegionDisplayName + "...";

                CloudLoginResult smsLoginRes = await _client.LoginWithSmsCodeAsync(phone, area, smsCode);
                if (smsLoginRes.State == CloudLoginState.Success)
                {
                    DialogResult = DialogResult.OK;
                    Close();
                    return;
                }

                _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                _lblStatus.Text = smsLoginRes.Message;
                SetUiEnabled(true);
                return;
            }

            // === Tab 2: Manual Token Login ===
            if (_tabs.SelectedIndex == 2)
            {
                string tok = _txtToken.Text.Trim();
                if (string.IsNullOrEmpty(tok))
                {
                    _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                    _lblStatus.Text = "请先粘贴 Access Token。";
                    return;
                }
                _client.SetToken(tok, region, "Token用户");
                _config.Save();
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            // === Tab 1: Account + Password (+ VerifyCode / 2FA) ===
            string account = _txtAccount.Text.Trim();
            string password = _txtPassword.Text;

            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(password))
            {
                _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                _lblStatus.Text = "请输入拓竹账号和密码。";
                return;
            }

            _config.CloudAccount = account;
            _config.Save();

            SetUiEnabled(false);
            _lblStatus.ForeColor = Color.FromArgb(37, 99, 235);

            CloudLoginResult res;
            if (_pendingChallenge == CloudLoginState.NeedVerifyCode)
            {
                string code = _txtCode.Text.Trim();
                if (string.IsNullOrEmpty(code))
                {
                    _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                    _lblStatus.Text = "请输入 6 位验证码。";
                    SetUiEnabled(true);
                    _txtCode.Focus();
                    return;
                }
                _lblStatus.Text = "正在验证登录验证码...";
                res = await _client.LoginWithVerifyCodeAsync(account, password, code);
            }
            else if (_pendingChallenge == CloudLoginState.NeedTfa)
            {
                string tfaCode = _txtCode.Text.Trim();
                if (string.IsNullOrEmpty(tfaCode))
                {
                    _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
                    _lblStatus.Text = "请输入 6 位两步验证（2FA）动态码。";
                    SetUiEnabled(true);
                    _txtCode.Focus();
                    return;
                }
                _lblStatus.Text = "正在校验两步验证动态码...";
                res = await _client.VerifyTfaAsync(_pendingTfaKey, tfaCode);
            }
            else
            {
                _lblStatus.Text = "正在连接 " + _client.RegionDisplayName + " 登录...";
                res = await _client.LoginAsync(account, password);
            }

            if (res.State == CloudLoginState.Success)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            if (res.State == CloudLoginState.NeedVerifyCode)
            {
                _pendingChallenge = CloudLoginState.NeedVerifyCode;
                _lblCodePrompt.Text = BambuCloudClient.IsPhoneLike(account) ? "短信验证码:" : "邮箱验证码:";
                _lblStatus.ForeColor = Color.FromArgb(180, 83, 9);
                _lblStatus.Text = res.Message;
                _btnLogin.Text = "提交验证码";
                SetUiEnabled(true);
                _txtCode.Focus();
                return;
            }

            if (res.State == CloudLoginState.NeedTfa)
            {
                _pendingChallenge = CloudLoginState.NeedTfa;
                _pendingTfaKey = res.TfaKey;
                _lblCodePrompt.Text = "2FA 动态码:";
                _lblStatus.ForeColor = Color.FromArgb(180, 83, 9);
                _lblStatus.Text = res.Message;
                _btnLogin.Text = "提交动态码";
                SetUiEnabled(true);
                _txtCode.Focus();
                return;
            }

            // Failed
            _lblStatus.ForeColor = Color.FromArgb(220, 38, 38);
            _lblStatus.Text = res.Message;
            SetUiEnabled(true);
        }
    }
}
