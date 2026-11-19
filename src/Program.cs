using System;
using System.Windows.Forms;

namespace BambuHistoryScreenshot
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            NativeMethods.EnablePerMonitorDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show("运行出错：\n" + e.Exception.Message, "拓竹打印历史截图工具", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.Run(new MainForm());
        }
    }
}
