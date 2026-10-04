using System;
using System.Threading;
using System.Windows.Forms;

namespace ViviTune
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var m = new Mutex(true, "ViviTune.SingleInstance", out created))
            {
                if (!created) { MessageBox.Show("ViviTune уже запущен"); return; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }
    }
}
