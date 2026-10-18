using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MasterVks.Audio;
using MasterVks.Checks;
using MasterVks.Core;
using MasterVks.Recording;
using MasterVks.Video;

namespace MasterVks.UI
{
    internal sealed class ComboItem
    {
        public readonly string Text;
        public readonly object Value;

        public ComboItem(string text, object value)
        {
            Text = text;
            Value = value;
        }

        public override string ToString()
        {
            return Text;
        }
    }

    internal sealed partial class MainForm : Form
    {
        private const int HotkeyRecord = 1;
        private const int HotkeyPause = 2;

        private readonly AppSettings _settings;
        private readonly AudioEngine _engine = new AudioEngine();
        private readonly bool _launchedByAutostart;
        private readonly uint _showMessage;
        private readonly uint _taskbarButtonCreated = TaskbarOverlay.ButtonCreatedMessage();
        private readonly TaskbarOverlay _overlay = new TaskbarOverlay();
        private readonly List<Notice> _events = new List<Notice>();
        private readonly Dictionary<string, CheckRowView> _rows = new Dictionary<string, CheckRowView>();
        private readonly Dictionary<AudioSource, SourceState> _lastSourceStates = new Dictionary<AudioSource, SourceState>();
        private readonly HashSet<AudioSource> _silenceWarned = new HashSet<AudioSource>();

        private RecordingSession _session;
        private bool _sessionStarting;
        private bool _sessionStopping;
        private bool _checking;
        private CheckReport _lastReport;
        private int _finalizing;
        private bool _closeAfterStop;
        private bool _forceClose;
        private bool _hotkeysRegistered;
        private bool _suppressEvents;
        private bool _devicesLoaded;
        private string _deviceSignature;
        private int _slowTicks;
        private AppIconState? _iconState;
        private Icon _currentIcon;
        private long _freeBytesIdle = -1;
        private string _lastNoticeFile;
        private volatile bool _previewWanted = true;
        private volatile bool _stopPreview;
        private Thread _previewThread;

        // Элементы окна
        private ComboBox _screenBox, _mic1Box, _mic2Box, _outBox;
        private SourceMeter _mic1Meter, _mic2Meter, _sysMeter;
        private TextBox _folderBox;
        private TextBox _nameBox;
        private ModernButton _folderButton;
        private SegmentedToggle _checkMode;
        private SegmentedToggle _checkTabs;
        private Panel _recordList, _healthList;
        private readonly HealthMonitor _health = new HealthMonitor();
        private ModernButton _checkButton;
        private Label _checkTime;
        private ModernButton _recordButton, _pauseButton, _openFolderButton, _settingsButton, _aboutButton, _eventsButton, _showFileButton;
        private Label _timerLabel, _recordInfo, _hintLabel, _noticeLabel;
        private ScreenPreview _preview;
        private StatusPill _pill;
        private ToolTip _tips;
        private System.Windows.Forms.Timer _fastTimer, _slowTimer;

        public MainForm(AppSettings settings, bool launchedByAutostart)
        {
            _settings = settings;
            _launchedByAutostart = launchedByAutostart;
            _showMessage = SafeRegisterMessage();

            BuildUi();
            UpdateWindowIcon();

            _engine.OffsetMs = _settings.AudioOffsetMs;
            ApplyAudioSettingsToEngine();
            _engine.Start();

            _fastTimer = new System.Windows.Forms.Timer { Interval = 60 };
            _fastTimer.Tick += (s, e) => FastTick();
            _slowTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _slowTimer.Tick += (s, e) => SlowTick();
        }

        private static uint SafeRegisterMessage()
        {
            try
            {
                return Native.RegisterWindowMessage("MasterVKS.ShowWindow");
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static void SignalExistingInstance()
        {
            try
            {
                uint msg = Native.RegisterWindowMessage("MasterVKS.ShowWindow");
                Native.PostMessage(Native.HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception)
            {
            }
        }

        // ===================== Запуск окна =====================

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitToWorkingArea();
            if (_launchedByAutostart && _settings.StartMinimizedOnAutostart) WindowState = FormWindowState.Minimized;
            _fastTimer.Start();
            _health.Start();
            _slowTimer.Start();
            StartPreviewThread();
        }

        /// <summary>На ноутбуке с крупным масштабом окно не должно выходить за экран и панель задач.</summary>
        private void FitToWorkingArea()
        {
            try
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                int w = Math.Min(Width, area.Width);
                int h = Math.Min(Height, area.Height);
                MinimumSize = new Size(Math.Min(MinimumSize.Width, w), Math.Min(MinimumSize.Height, h));
                Bounds = new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось подогнать окно под экран", ex);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            AddEvent(new Notice("Программа запущена. Проверяю оборудование…", NoticeLevel.Info));
            RecoverUnfinished();
            RefreshDevicesAsync(true);
        }

        private void RecoverUnfinished()
        {
            List<PendingJournal> pending = PendingJournal.LoadAll();
            if (pending.Count == 0) return;
            string ffmpeg = Ffmpeg.Locate(_settings.FfmpegPath);
            foreach (PendingJournal job in pending)
            {
                AddEvent(new Notice("Нашлась запись от " + job.Started.ToString("dd.MM.yyyy HH:mm") + ", которая не была завершена (сбой или выключение). Восстанавливаю её…", NoticeLevel.Warning));
                PendingJournal captured = job;
                RunFinalize(ffmpeg, captured, true);
            }
        }

        // ===================== Устройства =====================

        private void RefreshDevicesAsync(bool firstTime)
        {
            Task.Run(() =>
            {
                var mics = AudioDevices.List(true);
                var outs = AudioDevices.List(false);
                List<MonitorDesc> monitors = MonitorList.Enumerate();
                string defaultMic = firstTime && !_settings.DevicesInitialized ? AudioDevices.DefaultCaptureId() : null;
                return new { mics, outs, monitors, defaultMic };
            }).ContinueWith(t =>
            {
                if (t.IsFaulted || IsDisposed) return;
                try
                {
                    BeginInvoke((Action)(() => ApplyDeviceLists(t.Result.mics, t.Result.outs, t.Result.monitors, t.Result.defaultMic, firstTime)));
                }
                catch (Exception)
                {
                }
            });
        }

        private void ApplyDeviceLists(List<AudioDeviceInfo> mics, List<AudioDeviceInfo> outs, List<MonitorDesc> monitors, string defaultMic, bool firstTime)
        {
            string signature = string.Join("|", mics.Select(m => m.Id + m.Name)) + "#" + string.Join("|", outs.Select(m => m.Id + m.Name)) + "#" +
                               string.Join("|", monitors.Select(m => m.DeviceName + m.Bounds));
            if (signature == _deviceSignature && !firstTime) return;
            _deviceSignature = signature;

            if (!_settings.DevicesInitialized && mics.Count > 0)
            {
                AudioDeviceInfo first = mics.FirstOrDefault(m => m.Id == defaultMic) ?? mics[0];
                AudioDeviceInfo second = mics.FirstOrDefault(m => m.Id != first.Id);
                _settings.Mic1Id = first.Id;
                _settings.Mic1Name = first.Name;
                _settings.Mic2Id = second != null ? second.Id : null;
                _settings.Mic2Name = second != null ? second.Name : null;
                _settings.DevicesInitialized = true;
                _settings.Save();
                ApplyAudioSettingsToEngine();
            }
            if (string.IsNullOrEmpty(_settings.MonitorDevice))
            {
                MonitorDesc primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
                if (primary != null) _settings.MonitorDevice = primary.DeviceName;
            }

            _suppressEvents = true;
            try
            {
                FillMicBox(_mic1Box, mics, _settings.Mic1Id, _settings.Mic1Name);
                FillMicBox(_mic2Box, mics, _settings.Mic2Id, _settings.Mic2Name);

                _outBox.Items.Clear();
                _outBox.Items.Add(new ComboItem("Как в Windows (устройство по умолчанию)", null));
                foreach (AudioDeviceInfo d in outs) _outBox.Items.Add(new ComboItem(d.Name, d));
                int outIndex = 0;
                if (!string.IsNullOrEmpty(_settings.OutputDeviceId))
                {
                    outIndex = IndexOf(_outBox, _settings.OutputDeviceId, _settings.OutputDeviceName);
                    if (outIndex < 0)
                    {
                        _outBox.Items.Add(new ComboItem(_settings.OutputDeviceName + " (сейчас не подключено)", new AudioDeviceInfo { Id = _settings.OutputDeviceId, Name = _settings.OutputDeviceName }));
                        outIndex = _outBox.Items.Count - 1;
                    }
                }
                _outBox.SelectedIndex = outIndex;

                _screenBox.Items.Clear();
                foreach (MonitorDesc m in monitors) _screenBox.Items.Add(new ComboItem(m.DisplayName, m));
                int screenIndex = -1;
                for (int i = 0; i < _screenBox.Items.Count; i++)
                {
                    var m = (MonitorDesc)((ComboItem)_screenBox.Items[i]).Value;
                    if (string.Equals(m.DeviceName, _settings.MonitorDevice, StringComparison.OrdinalIgnoreCase)) screenIndex = i;
                }
                if (screenIndex < 0 && _screenBox.Items.Count > 0)
                {
                    screenIndex = 0;
                    if (!string.IsNullOrEmpty(_settings.MonitorDevice) && _devicesLoaded)
                        AddEvent(new Notice("Выбранный ранее экран не найден, поэтому выбран основной.", NoticeLevel.Warning));
                }
                if (screenIndex >= 0) _screenBox.SelectedIndex = screenIndex;
            }
            finally
            {
                _suppressEvents = false;
            }

            if (firstTime)
            {
                _devicesLoaded = true;
                // Даём устройствам секунду на подключение и запускаем обязательную проверку.
                var delay = new System.Windows.Forms.Timer { Interval = 1200 };
                delay.Tick += (s, e) =>
                {
                    delay.Stop();
                    delay.Dispose();
                    RunCheck(_settings.StartupCheckMode, true);
                    WarnIfFfmpegMissing();
                };
                delay.Start();
            }
        }

        private static void FillMicBox(ComboBox box, List<AudioDeviceInfo> mics, string id, string name)
        {
            box.Items.Clear();
            box.Items.Add(new ComboItem("Не использовать", null));
            foreach (AudioDeviceInfo d in mics) box.Items.Add(new ComboItem(d.Name, d));
            int index = 0;
            if (!string.IsNullOrEmpty(id))
            {
                index = IndexOf(box, id, name);
                if (index < 0)
                {
                    box.Items.Add(new ComboItem((name ?? "Устройство") + " (сейчас не подключено)", new AudioDeviceInfo { Id = id, Name = name }));
                    index = box.Items.Count - 1;
                }
            }
            box.SelectedIndex = index;
        }

        private static int IndexOf(ComboBox box, string id, string name)
        {
            for (int i = 0; i < box.Items.Count; i++)
            {
                var d = ((ComboItem)box.Items[i]).Value as AudioDeviceInfo;
                if (d != null && d.Id == id) return i;
            }
            for (int i = 0; i < box.Items.Count; i++)
            {
                var d = ((ComboItem)box.Items[i]).Value as AudioDeviceInfo;
                if (d != null && !string.IsNullOrEmpty(name) && d.Name == name) return i;
            }
            return -1;
        }

        private void OnMicChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var box = (ComboBox)sender;
            var item = box.SelectedItem as ComboItem;
            var device = item != null ? item.Value as AudioDeviceInfo : null;
            if (box == _mic1Box)
            {
                _settings.Mic1Id = device != null ? device.Id : null;
                _settings.Mic1Name = device != null ? device.Name : null;
            }
            else
            {
                _settings.Mic2Id = device != null ? device.Id : null;
                _settings.Mic2Name = device != null ? device.Name : null;
            }
            _settings.Save();
            ApplyAudioSettingsToEngine();
            Log.Info((box == _mic1Box ? "Микрофон 1" : "Микрофон 2") + ": выбрано «" + (device != null ? device.Name : "не использовать") + "»");
        }

        private void OnOutputChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var item = _outBox.SelectedItem as ComboItem;
            var device = item != null ? item.Value as AudioDeviceInfo : null;
            _settings.OutputDeviceId = device != null ? device.Id : null;
            _settings.OutputDeviceName = device != null ? device.Name : null;
            _settings.Save();
            ApplyAudioSettingsToEngine();
            Log.Info("Звук собеседников: выбрано «" + (device != null ? device.Name : "по умолчанию") + "»");
        }

        private void OnScreenChanged(object sender, EventArgs e)
        {
            if (_suppressEvents) return;
            var item = _screenBox.SelectedItem as ComboItem;
            var monitor = item != null ? item.Value as MonitorDesc : null;
            if (monitor == null) return;
            _settings.MonitorDevice = monitor.DeviceName;
            _settings.Save();
            Log.Info("Экран: выбран " + monitor.DisplayName);
        }

        private void ApplyAudioSettingsToEngine()
        {
            _engine.Mic1.Configure(_settings.Mic1Id, _settings.Mic1Name, false);
            _engine.Mic2.Configure(_settings.Mic2Id, _settings.Mic2Name, false);
            if (string.IsNullOrEmpty(_settings.OutputDeviceId))
                _engine.System.Configure(null, null, true);
            else
                _engine.System.Configure(_settings.OutputDeviceId, _settings.OutputDeviceName, false);
            _engine.OffsetMs = _settings.AudioOffsetMs;
        }

        private void OnMeetingNameChanged()
        {
            if (_suppressEvents) return;
            _settings.MeetingName = _nameBox.Text;
        }

        private void ChooseFolder()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Куда сохранять записи ВКС";
                dialog.ShowNewFolderButton = true;
                try
                {
                    if (Directory.Exists(_settings.OutputFolder)) dialog.SelectedPath = _settings.OutputFolder;
                }
                catch (Exception)
                {
                }
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                _settings.OutputFolder = dialog.SelectedPath;
                _settings.Save();
                _folderBox.Text = _settings.OutputFolder;
                _freeBytesIdle = DiskSpace.FreeBytes(_settings.OutputFolder);
                Log.Info("Папка для записей: " + _settings.OutputFolder);
                AddEvent(new Notice("Записи будут сохраняться в папку " + _settings.OutputFolder, NoticeLevel.Info));
            }
        }

        // ===================== Проверка =====================

        private void RunCheck(CheckMode mode, bool startup)
        {
            if (_checking) return;
            if (_session != null)
            {
                AddEvent(new Notice("Проверку можно запустить, когда запись остановлена.", NoticeLevel.Info));
                return;
            }
            _checking = true;
            foreach (CheckItem item in SystemCheck.CreateItems())
            {
                item.Status = CheckStatus.Running;
                item.Summary = "Проверяю…";
                SetRowItem(_rows[item.Key], item);
            }
            _checkTime.Text = mode == CheckMode.Audible ? "Сейчас прозвучит короткая мелодия…" : "Тихая проверка, ничего не прозвучит";
            UpdateControls();

            AppSettings snapshot = _settings.Clone();
            Task.Run(() => new SystemCheck(snapshot, _engine, mode, item => PostToUi(() =>
            {
                CheckRowView row;
                if (_rows.TryGetValue(item.Key, out row)) SetRowItem(row, item);
            })).Run()).ContinueWith(t => PostToUi(() =>
            {
                _checking = false;
                if (t.IsFaulted)
                {
                    Log.Error("Проверка завершилась с ошибкой", t.Exception);
                    AddEvent(new Notice("Проверка прервалась из-за ошибки. Подробности есть в журнале.", NoticeLevel.Error));
                }
                else
                {
                    _lastReport = t.Result;
                    foreach (CheckItem item in _lastReport.Items) SetRowItem(_rows[item.Key], item.Copy());
                    _checkTime.Text = (startup ? "Проверено при запуске в " : "Проверено в ") + _lastReport.Time.ToString("HH:mm") +
                                      (mode == CheckMode.Audible ? " · со звуком" : " · тихо");
                    CheckStatus overall = _lastReport.Overall;
                    if (overall == CheckStatus.Ok)
                        AddEvent(new Notice("Проверка пройдена: всё готово к записи.", NoticeLevel.Success));
                    else
                    {
                        var bad = _lastReport.Items.Where(i => i.Status == CheckStatus.Error || i.Status == CheckStatus.Warning).Select(i => i.Title.ToLowerInvariant());
                        AddEvent(new Notice((overall == CheckStatus.Error ? "Проверка нашла проблемы: " : "Проверка прошла с замечаниями: ") + string.Join(", ", bad) +
                                            ". Записывать всё равно можно. Нажмите на строку, чтобы узнать подробности.",
                            overall == CheckStatus.Error ? NoticeLevel.Error : NoticeLevel.Warning));
                    }
                }
                UpdateControls();
            }));
        }

        private void OnCheckRowClick(CheckItem item)
        {
            if (item != null && item.Key == SystemCheck.KeyUpdates && item.CanFix && !_checking)
            {
                DialogResult r = MessageBox.Show(this,
                    item.Summary + ".\n\n" +
                    "Защитить компьютер от самопроизвольной перезагрузки?\n\n" +
                    "Что изменится: Windows не будет перезагружаться сама, пока вы вошли в систему, а часы активности станут с 6:00 до 24:00. " +
                    "Обновления по-прежнему скачиваются и ставятся, а перезагрузку вы делаете сами, когда удобно.\n\n" +
                    "Windows один раз попросит пароль администратора. При следующих запусках программы пароль не нужен.",
                    "Обновления Windows", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes) return;
                Task.Run(() => UpdateProtection.RunElevated()).ContinueWith(t => PostToUi(() =>
                {
                    string error = t.IsFaulted ? "неизвестная ошибка" : t.Result;
                    if (error == null)
                    {
                        AddEvent(new Notice("Защита от самопроизвольной перезагрузки включена. Проверяю ещё раз…", NoticeLevel.Success));
                        RunCheck(CheckMode.Silent, false);
                    }
                    else
                    {
                        AddEvent(new Notice("Защиту от перезагрузки включить не удалось: " + error + ".", NoticeLevel.Warning));
                    }
                }));
                return;
            }
            ShowCheckDetails(item);
        }

        /// <summary>Цветная точка на вкладке: худший результат среди её строк.</summary>
        private void UpdateTabDots()
        {
            if (_checkTabs == null) return;
            _checkTabs.SetDot(0, GroupColor(SystemCheck.GroupRecord));
            _checkTabs.SetDot(1, GroupColor(SystemCheck.GroupHealth));
        }

        private Color? GroupColor(string group)
        {
            var items = _rows.Values.Select(r => r.Item).Where(i => i != null && i.Group == group).ToList();
            if (items.Any(i => i.Status == CheckStatus.Running)) return Theme.Accent;
            if (items.Any(i => i.Status == CheckStatus.Error)) return Theme.Red;
            if (items.Any(i => i.Status == CheckStatus.Warning)) return Theme.Yellow;
            if (items.Any(i => i.Status == CheckStatus.Ok)) return Theme.Green;
            return null;
        }

        private void ShowCheckDetails(CheckItem item)
        {
            if (item == null) return;
            string text = item.Title + ": " + item.Summary;
            if (!string.IsNullOrEmpty(item.Details)) text += Environment.NewLine + Environment.NewLine + item.Details;
            MessageBoxIcon icon = item.Status == CheckStatus.Error ? MessageBoxIcon.Error
                : item.Status == CheckStatus.Warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information;
            MessageBox.Show(this, text, "Проверка: " + item.Title, MessageBoxButtons.OK, icon);
        }

        private void WarnIfFfmpegMissing()
        {
            if (Ffmpeg.Locate(_settings.FfmpegPath) != null) return;
            DialogResult r = MessageBox.Show(this,
                "Не найден модуль записи FFmpeg, а без него видео не записать.\n\n" +
                "Что сделать (один раз):\n" +
                "1. Скачайте архив ffmpeg-release-essentials.zip с сайта gyan.dev.\n" +
                "2. Откройте архив, папку bin и скопируйте файл ffmpeg.exe в папку программы:\n   " + AppPaths.ExeDirectory + "\n" +
                "3. Нажмите «Проверить».\n\n" +
                "Открыть страницу загрузки и папку программы?",
                "Нужен модуль записи", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
            {
                Shell.OpenUrl(Ffmpeg.DownloadPage);
                Shell.OpenFolder(AppPaths.ExeDirectory);
            }
        }

        // ===================== Запись =====================

        private void ToggleRecording(bool confirm)
        {
            if (_sessionStarting || _sessionStopping) return;
            if (_session == null) StartRecording();
            else StopRecording(confirm);
        }

        private void StartRecording()
        {
            if (_session != null || _sessionStarting) return;
            string ffmpeg = Ffmpeg.Locate(_settings.FfmpegPath);
            if (ffmpeg == null)
            {
                WarnIfFfmpegMissing();
                return;
            }
            try
            {
                Directory.CreateDirectory(_settings.OutputFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не получается создать папку для записей:\n" + _settings.OutputFolder + "\n\n" + ex.Message + "\n\nВыберите другую папку.",
                    "Папка недоступна", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _settings.Save();
            var session = new RecordingSession(_settings, _engine, ffmpeg);
            _sessionStarting = true;
            _session = session;
            _silenceWarned.Clear();
            UpdateControls();
            PowerHelper.KeepAwake(true);
            PowerHelper.BoostCurrentProcess(true);

            Task.Run(() => session.Start()).ContinueWith(t => PostToUi(() =>
            {
                _sessionStarting = false;
                if (t.IsFaulted)
                {
                    Exception ex = t.Exception != null ? t.Exception.GetBaseException() : null;
                    Log.Error("Не удалось начать запись", ex);
                    _session = null;
                    PowerHelper.KeepAwake(false);
                    PowerHelper.BoostCurrentProcess(false);
                    AddEvent(new Notice("Запись не началась: " + (ex != null ? ex.Message : "неизвестная ошибка"), NoticeLevel.Error));
                    MessageBox.Show(this, "Не удалось начать запись.\n\n" + (ex != null ? ex.Message : "") + "\n\nПодробности есть в журнале: «Настройки», затем «Открыть папку журнала».",
                        "Запись не началась", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    AddEvent(new Notice("Запись началась. Файл: " + session.Mp4Path, NoticeLevel.Success));
                    if (_lastReport != null && _lastReport.Overall == CheckStatus.Error)
                        AddEvent(new Notice("Обратите внимание: последняя проверка нашла проблемы. Запись идёт, но часть звука или картинки может не попасть в файл.", NoticeLevel.Warning));
                }
                UpdateControls();
            }));
        }

        private void StopRecording(bool confirm)
        {
            RecordingSession session = _session;
            if (session == null || _sessionStarting || _sessionStopping) return;
            if (confirm && _settings.ConfirmStop)
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                DialogResult r = MessageBox.Show(this,
                    "Остановить запись?\n\nИдёт уже " + Fmt.Timer(session.Elapsed) + ". После остановки файл сохранится в папку записей.",
                    "Остановка записи", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (r != DialogResult.Yes) return;
            }

            _sessionStopping = true;
            UpdateControls();
            string ffmpeg = Ffmpeg.Locate(_settings.FfmpegPath);
            Task.Run(() => session.Stop(false)).ContinueWith(t => PostToUi(() =>
            {
                _sessionStopping = false;
                _session = null;
                PowerHelper.KeepAwake(false);
                PowerHelper.BoostCurrentProcess(false);
                if (t.IsFaulted) Log.Error("Ошибка при остановке записи", t.Exception);

                var job = new PendingJournal { Mp4Path = session.Mp4Path, Parts = session.Parts, KeepMkv = _settings.KeepMkvAfterConvert, Started = session.StartedAt };
                AddEvent(new Notice("Запись остановлена (" + Fmt.Timer(session.Elapsed) + "). Готовлю файл MP4…", NoticeLevel.Info));
                RunFinalize(ffmpeg, job, false, session);
                UpdateControls();
            }));
        }

        private void RunFinalize(string ffmpeg, PendingJournal job, bool recovered, RecordingSession session = null)
        {
            Interlocked.Increment(ref _finalizing);
            UpdateControls();
            Task.Run(() =>
            {
                // У только что остановленной записи свой журнал — удалим его вместе с успешным MP4.
                PendingJournal journal = job;
                if (session != null)
                {
                    PendingJournal own = PendingJournal.LoadAll().FirstOrDefault(j => string.Equals(j.Mp4Path, job.Mp4Path, StringComparison.OrdinalIgnoreCase));
                    if (own != null) journal = own;
                }
                return PostProcessor.Finalize(ffmpeg, journal);
            }).ContinueWith(t => PostToUi(() =>
            {
                Interlocked.Decrement(ref _finalizing);
                if (t.IsFaulted)
                {
                    AddEvent(new Notice("Не удалось подготовить MP4. Исходная запись сохранена, подробности есть в журнале.", NoticeLevel.Error));
                }
                else
                {
                    FinalizeResult r = t.Result;
                    string text = recovered && r.Success ? "Запись после сбоя восстановлена. " + r.Message : r.Message;
                    var notice = new Notice(text, r.Success ? NoticeLevel.Success : NoticeLevel.Warning);
                    AddEvent(notice, r.Success ? r.Mp4Path : null);
                }
                UpdateControls();
                if (_closeAfterStop && _session == null && _finalizing == 0)
                {
                    _forceClose = true;
                    Close();
                }
            }));
        }

        private void TogglePause()
        {
            RecordingSession session = _session;
            if (session == null || _sessionStarting || _sessionStopping) return;
            if (session.State == SessionState.Paused)
            {
                session.Resume();
                AddEvent(new Notice("Запись продолжена.", NoticeLevel.Info));
            }
            else if (session.State == SessionState.Recording)
            {
                session.Pause();
                AddEvent(new Notice("Запись на паузе. Всё, что происходит сейчас, в файл не попадает.", NoticeLevel.Warning));
            }
            UpdateControls();
        }

        // ===================== Таймеры =====================

        private void FastTick()
        {
            UpdateMeter(_mic1Meter, _engine.Mic1);
            UpdateMeter(_mic2Meter, _engine.Mic2);
            UpdateMeter(_sysMeter, _engine.System);

            RecordingSession session = _session;
            if (session != null && !_sessionStarting) _timerLabel.Text = Fmt.Timer(session.Elapsed);
            if (_pill.Visible && (session != null && session.State == SessionState.Recording)) _pill.Invalidate();
            foreach (CheckRowView row in _rows.Values)
                if (row.IsSpinning) row.Invalidate();
        }

        private void UpdateMeter(SourceMeter meter, AudioSource source)
        {
            string text;
            Color color;
            bool active = source.State == SourceState.Active;
            switch (source.State)
            {
                case SourceState.Active:
                    bool silent = source.ZeroRunFrames > AudioEngine.Rate * 3;
                    text = silent ? "Полная тишина" : "Работает";
                    color = silent ? Theme.Yellow : Theme.Green;
                    break;
                case SourceState.NotSelected:
                    text = "Не используется";
                    color = Theme.Faint;
                    break;
                case SourceState.Connecting:
                    text = "Подключаюсь…";
                    color = Theme.Muted;
                    break;
                case SourceState.Lost:
                    text = "Отключён, жду";
                    color = Theme.Yellow;
                    break;
                default:
                    text = "Не работает";
                    color = Theme.Red;
                    break;
            }
            meter.SetState(source.Meter, active, text, color);
        }

        private void SlowTick()
        {
            _slowTicks++;
            RecordingSession session = _session;

            if (session != null)
            {
                Notice n;
                while (session.TryTakeNotice(out n)) AddEvent(n);
            }
            Notice healthNotice;
            while (_health.TryTakeNotice(out healthNotice)) AddEvent(healthNotice);

            WatchSources(session != null);

            if (_slowTicks % 5 == 0 || _freeBytesIdle < 0) _freeBytesIdle = DiskSpace.FreeBytes(_settings.OutputFolder);
            if (_slowTicks % 5 == 0 && _devicesLoaded && session == null && !_checking) RefreshDevicesAsync(false);

            UpdateControls();
        }

        private void WatchSources(bool recording)
        {
            foreach (AudioSource source in _engine.Sources)
            {
                SourceState current = source.State;
                SourceState previous;
                bool known = _lastSourceStates.TryGetValue(source, out previous);
                _lastSourceStates[source] = current;
                if (known && previous != current) ReportSourceChange(source, previous, current, recording);
                if (recording && current == SourceState.Active && !source.IsLoopback) WatchSilence(source);
            }
        }

        private void ReportSourceChange(AudioSource source, SourceState previous, SourceState current, bool recording)
        {
            if (previous == SourceState.Active && (current == SourceState.Lost || current == SourceState.Error))
            {
                AddEvent(new Notice(source.Title + ": устройство отключилось. " +
                                    (recording ? "Запись продолжается без него, а как только устройство вернётся, звук подхватится сам." : "Как только оно вернётся, программа подхватит его сама."),
                    NoticeLevel.Warning));
            }
            else if ((previous == SourceState.Lost || previous == SourceState.Error) && current == SourceState.Active)
            {
                AddEvent(new Notice(source.Title + " снова работает" + (source.ActiveDeviceName != null ? " («" + source.ActiveDeviceName + "»)" : "") + ".", NoticeLevel.Success));
            }
        }

        private void WatchSilence(AudioSource source)
        {
            if (source.ZeroRunFrames > AudioEngine.Rate * 10)
            {
                if (_silenceWarned.Add(source))
                    AddEvent(new Notice(source.Title + " уже 10 секунд присылает полную тишину. Проверьте, не выключен ли он и разрешён ли доступ к микрофону.", NoticeLevel.Warning));
            }
            else if (source.ZeroRunFrames == 0)
            {
                _silenceWarned.Remove(source);
            }
        }

        // ===================== Состояние окна =====================

        private bool AnySourceProblem()
        {
            return _health.HasProblem || _engine.Sources.Any(s => s.IsConfigured && s.State != SourceState.Active && s.State != SourceState.Connecting);
        }

        private void UpdateControls()
        {
            if (IsDisposed) return;
            RecordingSession session = _session;
            bool recording = session != null;
            bool paused = recording && session.State == SessionState.Paused;
            bool problem = AnySourceProblem() || (recording && !_sessionStarting && !_sessionStopping && !session.IsHealthy);

            // Кнопки записи
            if (_sessionStarting)
            {
                SetButton(_recordButton, "Запускаю запись…", ButtonKind.Danger, Glyph.Record, false);
            }
            else if (_sessionStopping)
            {
                SetButton(_recordButton, "Сохраняю запись…", ButtonKind.Primary, Glyph.Stop, false);
            }
            else if (recording)
            {
                SetButton(_recordButton, "Остановить запись", ButtonKind.Primary, Glyph.Stop, true);
            }
            else
            {
                SetButton(_recordButton, "Начать запись", ButtonKind.Danger, Glyph.Record, true);
            }
            bool canPause = recording && !_sessionStarting && !_sessionStopping;
            SetButton(_pauseButton, paused ? "Продолжить" : "Пауза", ButtonKind.Secondary, paused ? Glyph.Play : Glyph.Pause, canPause);

            _checkButton.Enabled = !_checking && !recording;
            _checkButton.Text = _checking ? "Проверяю…" : "Проверить";
            _checkMode.Enabled = !_checking && !recording;
            _screenBox.Enabled = !recording;
            _nameBox.ReadOnly = recording;
            _folderButton.Enabled = !recording;

            // Таймер и подписи
            if (!recording) _timerLabel.Text = "00:00:00";
            string info;
            if (recording && !_sessionStarting)
            {
                info = "Размер " + Fmt.Size(session.BytesWritten);
                long free = session.FreeBytes >= 0 ? session.FreeBytes : _freeBytesIdle;
                if (free >= 0)
                {
                    info += " · свободно " + Fmt.Size(free);
                    TimeSpan? left = session.RemainingDiskTime;
                    if (left.HasValue) info += " (≈ " + Fmt.Duration(left.Value) + ")";
                }
                if (session.PartCount > 1) info += " · частей: " + session.PartCount;
                if (session.Width > 0) info += " · " + session.Width + "×" + session.Height + ", " + _settings.FrameRate + " к/с";
            }
            else
            {
                info = _finalizing > 0 ? "Готовлю MP4 из последней записи…" : "Запись не идёт";
                if (_freeBytesIdle >= 0) info += " · свободно " + Fmt.Size(_freeBytesIdle);
            }
            _recordInfo.Text = info;
            _hintLabel.Text = _settings.HotkeysEnabled
                ? (_hotkeysRegistered ? "Ctrl+Alt+R: начать или остановить · Ctrl+Alt+P: пауза" : "Горячие клавиши заняты другой программой")
                : "Горячие клавиши выключены в настройках";

            // Таблетка состояния
            if (_sessionStarting) _pill.SetState("Запускаю запись…", Theme.Accent, Theme.AccentSoft, Theme.Text, false);
            else if (_sessionStopping) _pill.SetState("Сохраняю запись…", Theme.Accent, Theme.AccentSoft, Theme.Text, false);
            else if (paused) _pill.SetState("Пауза", Theme.Yellow, Theme.YellowSoft, Theme.Text, false);
            else if (recording) _pill.SetState(problem ? "Идёт запись · есть замечания" : "Идёт запись", Theme.Red, Theme.RedSoft, Theme.Text, true);
            else if (_checking) _pill.SetState("Идёт проверка…", Theme.Accent, Theme.AccentSoft, Theme.Text, false);
            else if (problem || (_lastReport != null && _lastReport.Overall != CheckStatus.Ok)) _pill.SetState("Готово, но есть замечания", Theme.Yellow, Theme.YellowSoft, Theme.Text, false);
            else _pill.SetState("Готово к записи", Theme.Green, Theme.GreenSoft, Theme.Text, false);
            LayoutHeader();

            // Превью
            if (paused) _preview.SetBadge("ПАУЗА", Theme.Yellow);
            else if (recording && !_sessionStarting) _preview.SetBadge("● ЗАПИСЬ", Theme.Red);
            else _preview.SetBadge(null, Color.Empty);

            // Заголовок окна (виден на панели задач)
            string title;
            if (paused) title = "❚❚ Пауза " + Fmt.Timer(session.Elapsed) + " · " + WindowTitleBase;
            else if (recording && !_sessionStarting) title = "● Запись " + Fmt.Timer(session.Elapsed) + " · " + WindowTitleBase;
            else title = WindowTitleBase;
            if (Text != title) Text = title;

            UpdateWindowIcon();
        }

        private static void SetButton(ModernButton button, string text, ButtonKind kind, Glyph glyph, bool enabled)
        {
            if (button.Text != text) button.Text = text;
            if (button.Kind != kind) button.Kind = kind;
            if (button.Glyph != glyph) button.Glyph = glyph;
            if (button.Enabled != enabled) button.Enabled = enabled;
        }

        private void UpdateWindowIcon()
        {
            RecordingSession session = _session;
            AppIconState state;
            if (session != null && session.State == SessionState.Paused) state = AppIconState.Paused;
            else if (session != null)
                state = AnySourceProblem() || (!_sessionStarting && !_sessionStopping && !session.IsHealthy) ? AppIconState.RecordingWarning : AppIconState.Recording;
            else if (AnySourceProblem() || (_lastReport != null && _lastReport.Overall != CheckStatus.Ok)) state = AppIconState.Warning;
            else state = AppIconState.Ready;

            if (_iconState == state) return;
            _iconState = state;
            try
            {
                Icon icon = IconFactory.Create(state);
                Icon old = _currentIcon;
                Icon = icon;
                _currentIcon = icon;
                if (old != null) old.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Не удалось обновить значок", ex);
            }
            ApplyOverlay();
        }

        private void ApplyOverlay()
        {
            if (!IsHandleCreated || !_iconState.HasValue) return;
            string description;
            switch (_iconState.Value)
            {
                case AppIconState.Recording:
                    description = "Идёт запись";
                    break;
                case AppIconState.RecordingWarning:
                    description = "Идёт запись, есть замечания";
                    break;
                case AppIconState.Paused:
                    description = "Запись на паузе";
                    break;
                case AppIconState.Warning:
                    description = "Записи нет, есть замечания";
                    break;
                default:
                    description = "Записи нет, всё готово";
                    break;
            }
            _overlay.Apply(Handle, _iconState.Value, description);
        }

        // ===================== События (лента уведомлений) =====================

        private void AddEvent(Notice notice, string filePath = null)
        {
            _events.Add(notice);
            if (_events.Count > 500) _events.RemoveAt(0);
            Color color;
            switch (notice.Level)
            {
                case NoticeLevel.Success:
                    color = Theme.Green;
                    break;
                case NoticeLevel.Warning:
                    color = Theme.Yellow;
                    break;
                case NoticeLevel.Error:
                    color = Theme.Red;
                    break;
                default:
                    color = Theme.Muted;
                    break;
            }
            _noticeLabel.ForeColor = color;
            _noticeLabel.Text = notice.Time.ToString("HH:mm") + "   " + notice.Text;
            _tips.SetToolTip(_noticeLabel, notice.Text);
            _lastNoticeFile = filePath;
            _showFileButton.Visible = filePath != null;
            if (notice.Level >= NoticeLevel.Warning && WindowState == FormWindowState.Minimized)
            {
                try
                {
                    Native.FlashWindow(Handle, true);
                }
                catch (Exception)
                {
                }
            }
        }

        private void PostToUi(Action action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Ошибка в интерфейсе", ex);
                    }
                }));
            }
            catch (Exception)
            {
            }
        }

        // ===================== Превью экрана =====================

        private void StartPreviewThread()
        {
            _previewThread = new Thread(() =>
            {
                while (!_stopPreview)
                {
                    try
                    {
                        if (_previewWanted)
                        {
                            Bitmap bmp = ScreenCapturer.CaptureThumbnail(_settings.MonitorDevice, Theme.S(320), Theme.S(180));
                            if (bmp != null) PostToUi(() => _preview.SetImage(bmp));
                        }
                    }
                    catch (Exception)
                    {
                    }
                    for (int i = 0; i < 10 && !_stopPreview; i++) Thread.Sleep(100);
                }
            }) { IsBackground = true, Name = "Превью экрана", Priority = ThreadPriority.BelowNormal };
            _previewThread.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _previewWanted = WindowState != FormWindowState.Minimized;
        }

        // ===================== Горячие клавиши и сообщения =====================

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RegisterHotkeys();
        }

        private void RegisterHotkeys()
        {
            UnregisterHotkeys();
            if (!_settings.HotkeysEnabled) return;
            try
            {
                bool a = Native.RegisterHotKey(Handle, HotkeyRecord, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.R);
                bool b = Native.RegisterHotKey(Handle, HotkeyPause, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.P);
                _hotkeysRegistered = a && b;
                if (!_hotkeysRegistered) Log.Warn("Горячие клавиши заняты другой программой");
            }
            catch (Exception ex)
            {
                _hotkeysRegistered = false;
                Log.Warn("Не удалось зарегистрировать горячие клавиши", ex);
            }
        }

        private void UnregisterHotkeys()
        {
            try
            {
                if (IsHandleCreated)
                {
                    Native.UnregisterHotKey(Handle, HotkeyRecord);
                    Native.UnregisterHotKey(Handle, HotkeyPause);
                }
            }
            catch (Exception)
            {
            }
            _hotkeysRegistered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HotkeyRecord) ToggleRecording(true);
                else if (id == HotkeyPause) TogglePause();
                return;
            }
            if (_taskbarButtonCreated != 0 && m.Msg == (int)_taskbarButtonCreated)
            {
                base.WndProc(ref m);
                ApplyOverlay();
                return;
            }
            if (_showMessage != 0 && m.Msg == (int)_showMessage)
            {
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Show();
                Activate();
                try
                {
                    Native.SetForegroundWindow(Handle);
                }
                catch (Exception)
                {
                }
                return;
            }
            base.WndProc(ref m);
        }

        // ===================== Закрытие =====================

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_forceClose)
            {
                bool systemShutdown = e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing;
                if (systemShutdown)
                {
                    // Windows выключается: быстро и аккуратно закрываем файл. MP4 будет сделан при следующем запуске.
                    RecordingSession session = _session;
                    if (session != null)
                    {
                        Log.Warn("Windows завершает работу во время записи, быстро сохраняю файл");
                        try
                        {
                            session.Stop(true);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Ошибка быстрой остановки", ex);
                        }
                        _session = null;
                    }
                }
                else if (_session != null)
                {
                    e.Cancel = true;
                    if (_closeAfterStop) return;
                    DialogResult r = MessageBox.Show(this,
                        "Сейчас идёт запись.\n\nОстановить её, сохранить файл и закрыть программу?",
                        "Закрыть программу", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (r != DialogResult.Yes) return;
                    _closeAfterStop = true;
                    StopRecording(false);
                    return;
                }
                else if (_finalizing > 0)
                {
                    DialogResult r = MessageBox.Show(this,
                        "Ещё готовлю MP4 из последней записи.\n\nЗакрыть сейчас? Ничего не пропадёт: запись уже сохранена в MKV, а MP4 будет доделан при следующем запуске программы.",
                        "Закрыть программу", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                    if (r != DialogResult.Yes)
                    {
                        e.Cancel = true;
                        return;
                    }
                }
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _stopPreview = true;
            if (_fastTimer != null) _fastTimer.Stop();
            if (_slowTimer != null) _slowTimer.Stop();
            UnregisterHotkeys();
            PowerHelper.KeepAwake(false);
            _overlay.Dispose();
            _health.Dispose();
            try
            {
                _engine.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Ошибка при остановке звукового ядра", ex);
            }
            Log.Info("Программа закрыта");
            base.OnFormClosed(e);
        }

        // ===================== Окна настроек =====================

        private void OpenSettings()
        {
            using (var form = new SettingsForm(_settings, _session != null))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                form.ApplyTo(_settings);
                _settings.Normalize();
                _settings.Save();
                Autostart.Apply(_settings.Autostart);
                ApplyAudioSettingsToEngine();
                RegisterHotkeys();
                AddEvent(new Notice("Настройки сохранены.", NoticeLevel.Info));
                UpdateControls();
            }
        }

        private void OpenAbout()
        {
            using (var form = new AboutForm()) form.ShowDialog(this);
        }

        private void OpenEvents()
        {
            using (var form = new EventsForm(_events)) form.ShowDialog(this);
        }
    }
}
