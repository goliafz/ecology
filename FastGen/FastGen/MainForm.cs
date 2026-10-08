using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using FastGen.Core;

namespace FastGen
{
    /// <summary>
    /// Главное окно. Вкладки разнесены по файлам:
    ///   MainForm.Repro.cs      — «Размножение» (основная работа с шаблоном);
    ///   MainForm.QuickSpin.cs  — «Быстрое размножение»;
    ///   MainForm.Collector.cs  — «Сборщик» (анализ шаблонов и сборка GoldBase).
    /// </summary>
    public partial class MainForm : Form
    {
        // ---------------- WinAPI ----------------

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

        private const int WM_SETREDRAW = 0x000B;
        private const int EM_GETSCROLLPOS = 0x0400 + 221;
        private const int EM_SETSCROLLPOS = 0x0400 + 222;

        // ---------------- Общие данные ----------------

        private readonly AppSettings _settings;
        private readonly SynonymStore _store;

        private StyledTabControl tabControl;
        private TabPage tabCollector;
        private TabPage tabEditor;
        private TabPage tabRepro;

        /// <summary>Идёт программное изменение выделения — обработчики выделения ничего не делают.</summary>
        private bool _adjustingSelection;

        /// <summary>Если RTF-подсветка однажды дала расхождение текста — используем медленную, но надёжную.</summary>
        private bool _rtfHighlightBroken;

        private static string AppDir { get { return Application.StartupPath; } }

        public MainForm()
        {
            _settings = new AppSettings(Path.Combine(AppDir, "FastGen.ini"));
            _store = new SynonymStore
            {
                GoldPath = Path.Combine(AppDir, "GoldBase.txt"),
                UserPath = Path.Combine(AppDir, "UserBase.txt"),
                UsagePath = Path.Combine(AppDir, "syn_usage.txt"),
                RejectedPath = Path.Combine(AppDir, "syn_rejected.txt")
            };

            InitializeComponent();
            KeyPreview = false;

            LoadSmallBases();

            Shown += MainForm_Shown;
            FormClosing += MainForm_FormClosing;
        }

        private void LoadSmallBases()
        {
            try { _store.LoadGold(); }
            catch (Exception ex) { ShowWarning("Не удалось прочитать GoldBase.txt:\r\n" + ex.Message); }

            try { _store.LoadUser(); }
            catch (Exception ex) { ShowWarning("Не удалось прочитать UserBase.txt:\r\n" + ex.Message); }

            try { _store.LoadRejected(); }
            catch (Exception ex) { ShowWarning("Не удалось прочитать syn_rejected.txt:\r\n" + ex.Message); }

            try { _store.LoadUsage(); }
            catch (Exception ex) { ShowWarning("Не удалось прочитать syn_usage.txt:\r\n" + ex.Message); }
        }

        private void InitializeComponent()
        {
            Text = "FastGen — редактор шаблонов размножения";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1150, 700);
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            WindowState = FormWindowState.Maximized;

            tabControl = new StyledTabControl { Dock = DockStyle.Fill };

            tabCollector = new TabPage("Сборщик");
            tabEditor = new TabPage("Быстрое размножение");
            tabRepro = new TabPage("Размножение");

            BuildCollectorTab(tabCollector);
            BuildEditorTab(tabEditor);
            BuildReproductionTab(tabRepro);

            tabControl.TabPages.Add(tabCollector);
            tabControl.TabPages.Add(tabEditor);
            tabControl.TabPages.Add(tabRepro);
            tabControl.SelectedTab = tabRepro;

            Controls.Add(tabControl);
        }

        private void MainForm_Shown(object sender, EventArgs e)
        {
            RestoreReproSession();
            StartContextLoad();
            StartDictLoad();
            txtReproEditor.Focus();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            // таймер мог не успеть сохранить последние правки
            FlushReproAutoSave();

            try { _store.SaveUsage(); } catch { /* не критично */ }

            SaveReproSettings();
            _settings.Save();
        }

        // ------------------------------------------------------------------
        // Фоновая загрузка DICT.DBF
        // ------------------------------------------------------------------

        private void StartDictLoad()
        {
            string dbfPath = Path.Combine(AppDir, "DICT.DBF");
            if (!File.Exists(dbfPath))
            {
                SetDictStatus("DICT.DBF не найден — работают GoldBase и «Моя база»", Color.DarkOrange);
                return;
            }

            SetDictStatus("DICT.DBF: загрузка…", Color.DimGray);

            Task.Run(() =>
            {
                try
                {
                    int lastPercent = -1;
                    var dict = SynonymStore.ReadDbf(dbfPath, (read, total) =>
                    {
                        int percent = total > 0 ? (int)(read * 100L / total) : 100;
                        if (percent == lastPercent) return;
                        lastPercent = percent;
                        PostToUi(() => SetDictStatus("DICT.DBF: загрузка " + percent + "%", Color.DimGray));
                    });

                    PostToUi(() =>
                    {
                        _store.SetDict(dict);
                        SetDictStatus("DICT.DBF: " + dict.Count.ToString("N0") + " слов", Color.DarkGreen);

                        // не сбрасываем список, если в нём сейчас работают
                        if (chkUseDict.Checked && !lvVariants.ContainsFocus && !txtAddVariant.Focused)
                            RefreshReproTarget(force: true);
                    });
                }
                catch (Exception ex)
                {
                    PostToUi(() =>
                    {
                        SetDictStatus("DICT.DBF: ошибка", Color.Red);
                        ShowWarning("Не удалось загрузить DICT.DBF.\r\n" + ex.Message +
                                    "\r\n\r\nПрограмма работает с GoldBase.txt и UserBase.txt.");
                    });
                }
            });
        }

        // ------------------------------------------------------------------
        // Индекс контекста (пары соседних слов из ваших шаблонов)
        // ------------------------------------------------------------------

        private volatile ContextIndex _context;
        private bool _contextLoading;

        private string ContextPath { get { return Path.Combine(AppDir, "Context.bin"); } }

        private void StartContextLoad()
        {
            string templates = ResultDbPath;
            if (!File.Exists(ContextPath) && !File.Exists(templates))
            {
                SetContextStatus("Контекст: нет ResultDB.txt — проверка соседей выключена (вкладка «Сборщик» → Анализ)", Color.DarkOrange);
                return;
            }

            SetContextStatus("Контекст: подготовка…", Color.DimGray);
            _contextLoading = true;
            Task.Run(() =>
            {
                try
                {
                    var ctx = ContextIndex.LoadOrBuild(ContextPath, templates);
                    PostToUi(() => OnContextReady(ctx));
                }
                catch (Exception ex)
                {
                    PostToUi(() =>
                    {
                        _contextLoading = false;
                        SetContextStatus("Контекст: ошибка — " + ex.Message, Color.Red);
                    });
                }
            });
        }

        private void OnContextReady(ContextIndex ctx)
        {
            _context = ctx;
            _contextLoading = false;
            if (ctx == null)
            {
                SetContextStatus("Контекст: нет данных", Color.DarkOrange);
                return;
            }
            SetContextStatus("Контекст: " + ctx.Count.ToString("N0") + " пар слов", Color.DarkGreen);
            if (!lvVariants.ContainsFocus && !txtAddVariant.Focused)
                RefreshReproTarget(force: true);
        }

        /// <summary>Выполнить действие в UI-потоке (из фонового потока).</summary>
        private void PostToUi(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
                // форма закрывается
            }
        }

        // ------------------------------------------------------------------
        // Горячие клавиши
        // ------------------------------------------------------------------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (tabControl.SelectedTab == tabRepro && HandleReproCmdKey(keyData))
                return true;

            if (tabControl.SelectedTab == tabEditor && keyData == Keys.F9)
            {
                ShowTrialGeneration();
                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------------
        // Подсветка шаблона
        // ------------------------------------------------------------------

        /// <summary>false — WinAPI недоступен (например, запуск под Mono); тогда просто без этих удобств.</summary>
        private static bool _winApi = true;

        private static void TryWinApi(Action call)
        {
            if (!_winApi) return;
            try
            {
                call();
            }
            catch (EntryPointNotFoundException) { _winApi = false; }
            catch (DllNotFoundException) { _winApi = false; }
        }

        private void SetRedraw(Control c, bool enable)
        {
            if (c == null || !c.IsHandleCreated) return;
            TryWinApi(() => SendMessage(c.Handle, WM_SETREDRAW, enable ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero));
            if (enable) c.Invalidate();
        }

        private Point GetScrollPos(RichTextBox box)
        {
            var p = Point.Empty;
            if (box.IsHandleCreated) TryWinApi(() => SendMessage(box.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref p));
            return p;
        }

        private void SetScrollPos(RichTextBox box, Point p)
        {
            if (box.IsHandleCreated) TryWinApi(() => SendMessage(box.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref p));
        }

        /// <summary>
        /// Перекрашивает весь текст. Текст, выделение и прокрутка остаются прежними.
        /// </summary>
        private void HighlightSynConstructions(RichTextBox box)
        {
            if (box == null || !box.IsHandleCreated) return;

            string text = box.Text;
            int selStart = box.SelectionStart;
            int selLength = box.SelectionLength;
            Point scroll = GetScrollPos(box);

            bool oldAdjusting = _adjustingSelection;
            _adjustingSelection = true;
            box.SuspendLayout();
            SetRedraw(box, false);
            try
            {
                bool done = false;
                bool plainText = text.All(ch => ch >= ' ' || ch == '\n' || ch == '\t');
                if (!_rtfHighlightBroken && plainText)
                {
                    box.Rtf = RtfBuilder.Build(text, box.Font.Name, box.Font.SizeInPoints);
                    string got = box.Text;
                    if (got != text && text.EndsWith("\n") && got + "\n" == text)
                    {
                        // RichEdit «съедает» последний \par — возвращаем перевод строки
                        box.Select(box.TextLength, 0);
                        box.SelectedText = "\n";
                        got = box.Text;
                    }

                    if (got == text)
                    {
                        done = true;
                    }
                    else
                    {
                        // RichTextBox понял RTF не так — возвращаем текст и больше этот способ не используем
                        _rtfHighlightBroken = true;
                        box.Text = text;
                    }
                }

                if (!done)
                    HighlightBySelection(box, text);

                box.Select(Math.Min(selStart, box.TextLength), Math.Min(selLength, Math.Max(0, box.TextLength - selStart)));
                SetScrollPos(box, scroll);
            }
            finally
            {
                SetRedraw(box, true);
                box.ResumeLayout();
                _adjustingSelection = oldAdjusting;
            }
        }

        /// <summary>
        /// Символ под точкой щелчка. GetCharIndexFromPosition возвращает ближайший символ даже для щелчка
        /// в пустом месте справа от строки — такие щелчки отбрасываем (-1).
        /// </summary>
        private static int CharIndexAtPoint(RichTextBox box, Point location)
        {
            int len = box.TextLength;
            if (len == 0) return -1;

            int index = box.GetCharIndexFromPosition(location);
            if (index < 0 || index >= len) return -1;

            Point p = box.GetPositionFromCharIndex(index);
            int lineHeight = box.Font.Height;
            if (location.Y < p.Y - 2 || location.Y > p.Y + lineHeight + 2) return -1;

            int charWidth = Math.Max(4, (int)(box.Font.SizeInPoints * 1.4f));
            int nextX = p.X + charWidth;
            if (index + 1 < len)
            {
                Point n = box.GetPositionFromCharIndex(index + 1);
                if (n.Y == p.Y && n.X > p.X) nextX = n.X;
            }
            if (location.X < p.X - 2 || location.X > nextX + 2) return -1;
            return index;
        }

        /// <summary>Запасной способ подсветки (медленный): только скобки и разделители.</summary>
        private static void HighlightBySelection(RichTextBox box, string text)
        {
            using (var regular = new Font(box.Font, FontStyle.Regular))
            using (var bold = new Font(box.Font, FontStyle.Bold))
            {
                box.Select(0, text.Length);
                box.SelectionColor = Color.Black;
                box.SelectionFont = regular;

                int depth = 0;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '{' || c == '}' || c == '|' || c == '[' || c == ']')
                    {
                        box.Select(i, 1);
                        box.SelectionColor = (c == '[' || c == ']') ? Color.Magenta : Color.Red;
                        box.SelectionFont = c == '|' ? regular : bold;
                        if (c == '{') depth++;
                        else if (c == '}' && depth > 0) depth--;
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // Мелочи
        // ------------------------------------------------------------------

        private void ShowWarning(string message)
        {
            MessageBox.Show(this, message, "FastGen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        private static Button MakeButton(string text, EventHandler onClick, Color? back = null, bool bold = false)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", bold ? 10F : 9F, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(6, 2, 6, 2),
                Margin = new Padding(0, 0, 5, 0),
                UseVisualStyleBackColor = back == null
            };
            if (back != null)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderSize = 0;
                b.BackColor = back.Value;
                b.ForeColor = Color.White;
            }
            if (onClick != null) b.Click += onClick;
            return b;
        }
    }
}
