using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ViviTune
{
    public class MainForm : Form
    {
        static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
        static readonly string SettingsPath = Path.Combine(Dir, "settings.json");
        static readonly string ConfigPath = Path.Combine(Dir, "config.json");
        static readonly string SingBox = Path.Combine(Dir, "sing-box.exe");

        readonly Settings _s = Settings.Load(SettingsPath);
        readonly Runner _runner = new Runner(SingBox);
        readonly TextBox _ip = new TextBox { Bounds = new Rectangle(130, 12, 150, 23) };
        readonly NumericUpDown _port = new NumericUpDown { Bounds = new Rectangle(290, 12, 80, 23), Minimum = 1, Maximum = 65535 };
        readonly ListBox _apps = new ListBox { Bounds = new Rectangle(12, 70, 380, 150), IntegralHeight = false };
        readonly Button _add = new Button { Text = "Добавить…", Bounds = new Rectangle(400, 70, 100, 28) };
        readonly Button _remove = new Button { Text = "Удалить", Bounds = new Rectangle(400, 104, 100, 28) };
        readonly Button _toggle = new Button { Text = "Старт", Bounds = new Rectangle(12, 232, 488, 36) };
        readonly Label _status = new Label { Bounds = new Rectangle(12, 276, 488, 20) };
        readonly TextBox _log = new TextBox { Bounds = new Rectangle(12, 302, 488, 216), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };

        bool _busy; // идёт проверка прокси

        public MainForm()
        {
            Text = "ViviTune";
            ClientSize = new Size(512, 530);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            Controls.Add(new Label { Text = "Прокси SOCKS5:", Bounds = new Rectangle(12, 15, 115, 20) });
            Controls.Add(new Label { Text = ":", Bounds = new Rectangle(282, 15, 8, 20) });
            Controls.Add(new Label { Text = "Приложения:", Bounds = new Rectangle(12, 50, 200, 18) });
            Controls.AddRange(new Control[] { _ip, _port, _apps, _add, _remove, _toggle, _status, _log });

            _ip.Text = _s.Ip;
            _port.Value = Math.Max(1, Math.Min(65535, _s.Port));
            foreach (var a in _s.Apps ?? new System.Collections.Generic.List<string>()) _apps.Items.Add(a);

            _ip.TextChanged += (a, b) => Changed();
            _port.ValueChanged += (a, b) => Changed();
            _add.Click += (a, b) =>
            {
                using (var d = new OpenFileDialog { Filter = "Программы (*.exe)|*.exe" })
                    if (d.ShowDialog() == DialogResult.OK) { _apps.Items.Add(d.FileName); Changed(); }
            };
            _remove.Click += (a, b) =>
            {
                if (_apps.SelectedIndex >= 0) { _apps.Items.RemoveAt(_apps.SelectedIndex); Changed(); }
            };
            _toggle.Click += (a, b) => Toggle();
            _runner.Line += l => UI(() => Log(l));
            _runner.Exited += () => UI(() => { Log("sing-box завершился"); SetRunning(false); });
            FormClosing += (a, b) => _runner.Dispose();

            Changed();
        }

        void UI(Action a) { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }

        void Log(string line) { _log.AppendText(line + Environment.NewLine); }

        // Копирует поля в настройки, сохраняет, обновляет доступность кнопки и статус.
        void Changed()
        {
            _s.Ip = _ip.Text.Trim();
            _s.Port = (int)_port.Value;
            _s.Apps = new System.Collections.Generic.List<string>();
            foreach (var a in _apps.Items) _s.Apps.Add((string)a);
            _s.Save(SettingsPath);

            var err = _s.Validate();
            _toggle.Enabled = _runner.Running || (!_busy && err == null);
            if (!_busy) _status.Text = err ?? (_runner.Running ? "Работает" : "Готово");
        }

        void SetRunning(bool running)
        {
            _ip.Enabled = _port.Enabled = _apps.Enabled = _add.Enabled = _remove.Enabled = !running;
            _toggle.Text = running ? "Стоп" : "Старт";
            Changed();
        }

        void Toggle()
        {
            if (_runner.Running) { _runner.Stop(); return; } // событие Exited вернёт интерфейс в исходное состояние
            if (!File.Exists(SingBox)) { _status.Text = "Не найден sing-box.exe рядом с программой"; return; }

            _busy = true;
            _toggle.Enabled = false;
            _ip.Enabled = _port.Enabled = _apps.Enabled = _add.Enabled = _remove.Enabled = false;
            _status.Text = "Проверяю прокси…";
            var snap = new Settings { Ip = _s.Ip, Port = _s.Port, Apps = new System.Collections.Generic.List<string>(_s.Apps) };
            Task.Run(() => ProxyProbe.Probe(snap.Ip, snap.Port)).ContinueWith(t => UI(() =>
            {
                if (t.IsFaulted) Fail("Ошибка проверки прокси: " + t.Exception.GetBaseException().Message);
                else OnProbed(snap, t.Result);
            }));
        }

        void Fail(string msg)
        {
            _busy = false;
            SetRunning(false);
            _status.Text = msg;
        }

        void OnProbed(Settings snap, ProbeResult r)
        {
            if (!r.Reachable) { Fail("Прокси недоступен: " + r.Error); return; }
            if (!r.UdpSupported) Log("ВНИМАНИЕ: UDP через этот прокси не работает: игры и голосовая связь могут не заработать.");
            try
            {
                File.WriteAllText(ConfigPath, ConfigBuilder.Build(snap));
                _runner.Start(ConfigPath);
                _busy = false;
                SetRunning(true);
            }
            catch (Exception e)
            {
                Fail("Не удалось запустить: " + e.Message);
            }
        }
    }
}
