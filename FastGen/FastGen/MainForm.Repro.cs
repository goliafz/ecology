using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Media;
using System.Text;
using System.Windows.Forms;
using FastGen.Core;

namespace FastGen
{
    /// <summary>Вкладка «Размножение»: редактор шаблона + панель вариантов справа.</summary>
    public partial class MainForm
    {
        // ---------------- Элементы ----------------

        private TextBox txtReproFilePath;
        private CheckBox chkReproReplace;
        private Label lblReproStatus;
        private RichTextBox txtReproEditor;

        private Label lblTarget;
        private TextBox txtAddVariant;
        private ListView lvVariants;

        private NumericUpDown numMaxSynonyms;
        private CheckBox chkUseDict;
        private CheckBox chkUseContext;
        private Label lblContextStatus;
        private Label lblTemplateInfo;
        private Label lblDictStatus;
        private Label lblHint;

        private Timer _reproTimer;

        // ---------------- Undo / Redo ----------------

        private struct UndoState
        {
            public string Text;
            public int SelStart;
            public int SelLength;
        }

        private const int UndoLimit = 300;
        private readonly List<UndoState> _undo = new List<UndoState>();
        private readonly List<UndoState> _redo = new List<UndoState>();
        private bool _inUndo;
        private bool _forceUndoPush;
        private string _lastText = string.Empty;
        private int _lastSelStart;
        private int _lastSelLength;
        private DateTime _lastEditTime = DateTime.MinValue;

        // ---------------- Сохранение ----------------

        private string _lastSavedText;
        private string _lastSavedPath;

        // ---------------- Текущая цель (слово / фраза / конструкция) ----------------

        private sealed class ReproTarget
        {
            public int Start;
            public int End;
            public string Raw;        // текст в редакторе
            public string Original;   // исходное слово / первый вариант конструкции
            public bool IsConstruct;
        }

        private ReproTarget _target;

        // для выделения по словам (Shift+стрелки)
        private int _wordSelOrigin = -1;
        private int _wordSelCaret = -1;

        private int _templateErrorPos = -1;

        // ==================================================================
        // Построение вкладки
        // ==================================================================

        private void BuildReproductionTab(TabPage tabPage)
        {
            tabPage.Padding = new Padding(8);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // ---- строка файла ----
            var fileRow = NewFlowRow();

            var lblFile = new Label
            {
                Text = "Файл:",
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Margin = new Padding(0, 8, 5, 0)
            };

            txtReproFilePath = new TextBox { Width = 500, Margin = new Padding(0, 5, 5, 0) };
            txtReproFilePath.Text = GetDefaultReproDirectory() + Path.DirectorySeparatorChar;
            txtReproFilePath.TextChanged += (s, e) => UpdateReproStatusLabel();

            chkReproReplace = new CheckBox
            {
                Text = "Заменить",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(5, 7, 0, 0)
            };

            var btnBrowse = new Button { Text = "...", Width = 32, Height = 26, Margin = new Padding(0, 5, 0, 0) };
            btnBrowse.Click += BtnReproBrowse_Click;

            var btnOpen = MakeButton("Открыть", BtnReproOpen_Click);
            btnOpen.Margin = new Padding(5, 4, 0, 0);

            lblReproStatus = new Label
            {
                AutoSize = true,
                Margin = new Padding(8, 8, 0, 0),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };

            var btnClear = MakeButton("Очистить", BtnReproClear_Click, Color.Red, true);
            btnClear.Margin = new Padding(8, 4, 0, 0);

            fileRow.Controls.AddRange(new Control[] { lblFile, txtReproFilePath, chkReproReplace, btnBrowse, btnOpen, lblReproStatus, btnClear });

            // ---- строка кнопок ----
            var buttonsRow = NewFlowRow();
            buttonsRow.WrapContents = true;

            var btnSpinAll = MakeButton("Размножить всё (F5)", (s, e) => SpinAllRepro(), Color.FromArgb(46, 204, 113), true);
            var btnTrial = MakeButton("Пробная генерация (F9)", (s, e) => ShowTrialGeneration(), Color.RoyalBlue, true);

            var lblMax = new Label { Text = "Синонимов:", AutoSize = true, Margin = new Padding(10, 7, 3, 0) };
            numMaxSynonyms = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 15,
                Value = Clamp(_settings.GetInt("MaxSynonyms", 4), 1, 15),
                Width = 45,
                Margin = new Padding(0, 4, 5, 0)
            };
            chkUseDict = new CheckBox
            {
                Text = "DICT.DBF",
                AutoSize = true,
                Checked = _settings.GetBool("UseDict", false),
                Margin = new Padding(5, 6, 10, 0)
            };
            chkUseDict.CheckedChanged += (s, e) => RefreshReproTarget(force: true);
            chkUseContext = new CheckBox
            {
                Text = "Проверять соседей",
                AutoSize = true,
                Checked = _settings.GetBool("UseContext", true),
                Margin = new Padding(0, 6, 10, 0)
            };
            chkUseContext.CheckedChanged += (s, e) => RefreshReproTarget(force: true);
            new ToolTip().SetToolTip(chkUseContext,
                "Брать синоним, только если в ваших шаблонах (ResultDB.txt) он уже стоял рядом с теми же соседними словами.\r\n" +
                "Сильно повышает читаемость: «собирать кубик» не превратится в «копить кубик».");
            new ToolTip().SetToolTip(chkUseDict, "Брать синонимы из большой базы DICT.DBF (и для F5, и в списке справа)");

            buttonsRow.Controls.AddRange(new Control[]
            {
                btnSpinAll, btnTrial, lblMax, numMaxSynonyms, chkUseDict, chkUseContext,
                MakeButton("Вариант 1 ( , ; )", (s, e) => ReproEnumVariant(false)),
                MakeButton("Вариант 2 (и, или)", (s, e) => ReproEnumVariant(true)),
                MakeButton("Ротация", (s, e) => ReproRotate()),
                MakeButton("Текст", (s, e) => ReproToggleCase(TextCase.ToggleTitleCase)),
                MakeButton("ТЕКСТ", (s, e) => ReproToggleCase(TextCase.ToggleUpper)),
                MakeButton("{слово|}", (s, e) => ReproTemplateEmpty()),
                MakeButton("{слово|слово}", (s, e) => ReproTemplateDuplicate()),
                MakeButton("Клавиши (F1)", (s, e) => ShowReproHelp())
            });
            foreach (Control c in buttonsRow.Controls)
                if (c is Button) c.Margin = new Padding(0, 0, 5, 4);

            // ---- редактор + панель вариантов ----
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72F));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28F));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            txtReproEditor = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Courier New", 14F),
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                HideSelection = false,
                AcceptsTab = true,
                DetectUrls = false
            };
            txtReproEditor.TextChanged += TxtReproEditor_TextChanged;
            txtReproEditor.KeyUp += TxtReproEditor_KeyUp;
            txtReproEditor.MouseUp += TxtReproEditor_MouseUp;
            txtReproEditor.MouseDown += TxtReproEditor_MouseDown;

            split.Controls.Add(HostEditor(txtReproEditor), 0, 0);
            split.Controls.Add(BuildVariantsPanel(), 1, 0);

            // ---- строка состояния ----
            var statusRow = NewFlowRow();
            lblTemplateInfo = new Label { AutoSize = true, Margin = new Padding(0, 4, 20, 0), Cursor = Cursors.Hand };
            lblTemplateInfo.Click += (s, e) => GoToTemplateError();
            lblDictStatus = new Label { AutoSize = true, Margin = new Padding(0, 4, 20, 0) };
            lblContextStatus = new Label { AutoSize = true, Margin = new Padding(0, 4, 20, 0) };
            lblHint = new Label { AutoSize = true, Margin = new Padding(0, 4, 0, 0), ForeColor = Color.DimGray, Tag = "muted" };
            statusRow.Controls.AddRange(new Control[] { lblTemplateInfo, lblDictStatus, lblContextStatus, lblHint });

            layout.Controls.Add(fileRow, 0, 0);
            layout.Controls.Add(buttonsRow, 0, 1);
            layout.Controls.Add(split, 0, 2);
            layout.Controls.Add(statusRow, 0, 3);
            tabPage.Controls.Add(layout);

            _reproTimer = new Timer { Interval = 400 };
            _reproTimer.Tick += ReproTimer_Tick;

            ResetReproUndo();
            UpdateReproStatusLabel();
            SetHint("F5 — размножить всё, Ctrl+→ / F3 — дальше, Ctrl+Enter — записать, F1 — все клавиши");
        }

        private Control BuildVariantsPanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Margin = new Padding(3, 0, 0, 0) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            lblTarget = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Height = 40,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Text = "—"
            };

            var buttons = NewFlowRow();
            buttons.WrapContents = true;
            buttons.Controls.AddRange(new Control[]
            {
                MakeButton("Частые", (s, e) => ApplyFrequentSynonyms()),
                MakeButton("В базу", (s, e) => SaveTargetToUserBase()),
                MakeButton("Из базы", (s, e) => RefreshReproTarget(force: true)),
                MakeButton("Снять {}", (s, e) => UnwrapTarget())
            });
            var tips = new ToolTip();
            tips.SetToolTip(buttons.Controls[0], "Отметить только частые синонимы и записать (Shift+Enter)");
            tips.SetToolTip(buttons.Controls[1], "Сохранить отмеченные синонимы этого слова в «Мою базу» (Ctrl+PgDn)");
            tips.SetToolTip(buttons.Controls[2], "Перечитать варианты из баз (Ctrl+PgUp)");
            tips.SetToolTip(buttons.Controls[3], "Убрать конструкцию, оставить исходное слово (F4)");

            txtAddVariant = new TextBox { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10F), Margin = new Padding(0, 3, 0, 3) };
            txtAddVariant.KeyDown += TxtAddVariant_KeyDown;
            SetCueBanner(txtAddVariant, "Свой вариант + Enter (Insert)");

            lvVariants = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                LabelEdit = true,
                HeaderStyle = ColumnHeaderStyle.None,
                Font = new Font("Segoe UI", 11F),
                BorderStyle = BorderStyle.FixedSingle
            };
            lvVariants.Columns.Add("Вариант", 200);
            lvVariants.Resize += (s, e) => FitVariantsColumn();
            lvVariants.ItemCheck += LvVariants_ItemCheck;
            lvVariants.KeyDown += LvVariants_KeyDown;
            lvVariants.AfterLabelEdit += LvVariants_AfterLabelEdit;

            var legend = new Label
            {
                AutoSize = true,
                ForeColor = Color.DimGray,
                Tag = "muted",
                Margin = new Padding(0, 3, 0, 0),
                Text = "✓ — войдёт в шаблон · жирный — вы уже выбирали\r\nзелёный — «Моя база» · серый — DICT.DBF\r\nзачёркнутый — не встречался рядом с этими соседями"
            };

            panel.Controls.Add(lblTarget, 0, 0);
            panel.Controls.Add(buttons, 0, 1);
            panel.Controls.Add(txtAddVariant, 0, 2);
            panel.Controls.Add(lvVariants, 0, 3);
            panel.Controls.Add(legend, 0, 4);
            return panel;
        }

        private static FlowLayoutPanel NewFlowRow()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 5)
            };
        }

        private static int Clamp(int v, int min, int max)
        {
            return Math.Max(min, Math.Min(max, v));
        }

        private const int EM_SETCUEBANNER = 0x1501;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        private static void SetCueBanner(TextBox box, string text)
        {
            box.HandleCreated += (s, e) => TryWinApi(() => SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text));
        }

        private void FitVariantsColumn()
        {
            if (lvVariants.Columns.Count > 0)
                lvVariants.Columns[0].Width = Math.Max(50, lvVariants.ClientSize.Width - 4);
        }

        private void SetHint(string text)
        {
            if (lblHint != null) lblHint.Text = text;
        }

        private void SetContextStatus(string text, StatusKind kind)
        {
            _contextStatusText = text;
            _contextStatusKind = kind;
            if (lblContextStatus == null) return;
            lblContextStatus.Text = text;
            lblContextStatus.ForeColor = _theme.Status(kind);
        }

        private ContextIndex ActiveContext
        {
            get { return chkUseContext != null && chkUseContext.Checked ? _context : null; }
        }

        private void SetDictStatus(string text, StatusKind kind)
        {
            _dictStatusText = text;
            _dictStatusKind = kind;
            if (lblDictStatus == null) return;
            Color color = _theme.Status(kind);
            lblDictStatus.Text = text;
            lblDictStatus.ForeColor = color;
            if (lblQuickDictStatus != null)
            {
                lblQuickDictStatus.Text = text;
                lblQuickDictStatus.ForeColor = color;
            }
        }

        // ==================================================================
        // Горячие клавиши вкладки
        // ==================================================================

        private bool HandleReproCmdKey(Keys keyData)
        {
            bool inEditor = txtReproEditor.Focused;
            bool inList = lvVariants.Focused;
            bool inAdd = txtAddVariant.Focused;
            bool inRepro = inEditor || inList || inAdd;

            Keys key = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0;
            bool shift = (keyData & Keys.Shift) != 0;
            bool alt = (keyData & Keys.Alt) != 0;
            if (alt) return false;

            // --- клавиши, работающие на всей вкладке ---
            switch (keyData)
            {
                case Keys.F1: ShowReproHelp(); return true;
                case Keys.F5: SpinAllRepro(); return true;
                case Keys.F9: ShowTrialGeneration(); return true;
                case Keys.Control | Keys.S: SaveReproNow(); return true;
            }

            if (!inRepro) return false;

            switch (keyData)
            {
                case Keys.F3: GoToConstruct(true); return true;
                case Keys.Shift | Keys.F3: GoToConstruct(false); return true;
                case Keys.F4: UnwrapTarget(); return true;
                case Keys.Control | Keys.PageDown: SaveTargetToUserBase(); return true;
                case Keys.Control | Keys.PageUp: RefreshReproTarget(force: true); return true;
                case Keys.Control | Keys.Right: NavigateToken(true); return true;
                case Keys.Control | Keys.Left: NavigateToken(false); return true;
                case Keys.Control | Keys.Shift | Keys.Enter: ApplyFrequentSynonyms(); return true;
            }

            if (keyData == (Keys.Control | Keys.Enter))
            {
                if (inAdd) AddVariantFromBox();
                ApplyTarget();
                return true;
            }

            if (inEditor || inList)
            {
                if (keyData == (Keys.Control | Keys.Z)) { DoReproUndo(); return true; }
                if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { DoReproRedo(); return true; }
            }

            if (inEditor)
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.V:
                    case Keys.Shift | Keys.Insert:
                        PastePlainText();
                        return true;
                    case Keys.Shift | Keys.Delete: ReproEnumVariant(false); return true;
                    case Keys.Shift | Keys.End: ReproEnumVariant(true); return true;
                    case Keys.Shift | Keys.PageDown: ReproRotate(); return true;
                    case Keys.Shift | Keys.Enter: ApplyFrequentSynonyms(); return true;
                    case Keys.Shift | Keys.Right: ExtendWordSelection(true); return true;
                    case Keys.Shift | Keys.Left: ExtendWordSelection(false); return true;
                    case Keys.Up:
                    case Keys.Down:
                        // слово выделено (режим прохода по тексту) — стрелки ведут в список;
                        // просто каретка — обычное перемещение по строкам
                        if (txtReproEditor.SelectionLength > 0 && lvVariants.Items.Count > 1)
                        {
                            FocusVariant(1);
                            return true;
                        }
                        return false;
                }
                return false;
            }

            if (inList)
            {
                if (key == Keys.Tab || keyData == Keys.Escape)
                {
                    txtReproEditor.Focus();
                    return true;
                }
                if (keyData == Keys.Insert)
                {
                    txtAddVariant.Focus();
                    return true;
                }
                if (ctrl && !shift && (key == Keys.Up || key == Keys.Down))
                {
                    MoveSelectedVariant(key == Keys.Up ? -1 : 1);
                    return true;
                }
                return false;
            }

            if (inAdd && keyData == Keys.Escape)
            {
                txtAddVariant.Clear();
                txtReproEditor.Focus();
                return true;
            }

            return false;
        }

        /// <summary>Вставка из буфера только текстом — без шрифтов и картинок из Word/браузера.</summary>
        private void PastePlainText()
        {
            string clip;
            try
            {
                clip = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                clip = null; // буфер занят другой программой
            }
            if (string.IsNullOrEmpty(clip)) return;

            clip = clip.Replace("\r\n", "\n").Replace('\r', '\n');
            int start = txtReproEditor.SelectionStart;
            ReplaceRepro(start, txtReproEditor.SelectionLength, clip);
            SelectRepro(start + clip.Length, 0);
            txtReproEditor.ScrollToCaret();
            HighlightSynConstructions(txtReproEditor);
        }

        private void ShowReproHelp()
        {
            const string help =
                "ОСНОВНОЙ ЦИКЛ\r\n" +
                "  F5 — размножить весь текст (или выделенное) по базам синонимов\r\n" +
                "  F3 / Shift+F3 — следующая / предыдущая конструкция {…}\r\n" +
                "  Ctrl+→ / Ctrl+← — следующее / предыдущее слово или конструкция\r\n" +
                "  Ctrl+Enter — записать отмеченные варианты и перейти дальше\r\n" +
                "  F4 — снять конструкцию (оставить исходное слово)\r\n" +
                "  F9 — пробная генерация случайного текста\r\n\r\n" +
                "СПИСОК ВАРИАНТОВ (справа)\r\n" +
                "  ↑ / ↓ (когда слово выделено) — перейти в список;  Tab / Esc — обратно в текст\r\n" +
                "  Пробел — отметить / снять вариант\r\n" +
                "  Enter или F2 — исправить вариант;  Insert — добавить свой\r\n" +
                "  Delete — удалить строку;  Ctrl+End — удалить все строки ниже\r\n" +
                "  Ctrl+↑ / Ctrl+↓ — переместить вариант\r\n" +
                "  Shift+Enter (Ctrl+Shift+Enter) — взять только частые синонимы\r\n" +
                "  Ctrl+PgDn — записать отмеченные синонимы в «Мою базу»\r\n" +
                "  Ctrl+PgUp — перечитать варианты из баз\r\n\r\n" +
                "ТЕКСТ\r\n" +
                "  Shift+→ / Shift+← — выделение по словам (фраза → синонимы фразы)\r\n" +
                "  Shift+Delete — Вариант 1 [<,> a | b | c ]\r\n" +
                "  Shift+End — Вариант 2 (и, или)\r\n" +
                "  Shift+PgDn — ротация «a и b» → {a и b|b и a}\r\n" +
                "  Правый щелчок по варианту в {…} — стереть его: {1|2|3} → {1|3}\r\n" +
                "  Ctrl+Z / Ctrl+Y — отменить / вернуть;  Ctrl+S — сохранить сейчас\r\n" +
                "  Ctrl++ / Ctrl+− / Ctrl+0 — крупнее / мельче / обычный размер текста\r\n\r\n" +
                "Оформление (классическое, светлое, тёмное) — справа вверху окна.\r\n" +
                "Строка состояния внизу показывает число вариантов текста и ошибки скобок\r\n" +
                "(щелчок по ошибке — перейти к ней).";
            MessageBox.Show(this, help, "Горячие клавиши — Размножение", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ==================================================================
        // Редактор: события
        // ==================================================================

        private void TxtReproEditor_TextChanged(object sender, EventArgs e)
        {
            if (_inUndo) return;

            string now = txtReproEditor.Text;
            if (now == _lastText) return;   // изменилось только оформление (подсветка)

            DateTime t = DateTime.Now;
            bool push = _forceUndoPush
                        || (t - _lastEditTime).TotalMilliseconds > 1000
                        || Math.Abs(now.Length - _lastText.Length) > 1;
            if (push) PushUndo(_undo, _lastText, _lastSelStart, _lastSelLength);
            _redo.Clear();

            _forceUndoPush = false;
            _lastText = now;
            _lastSelStart = txtReproEditor.SelectionStart;
            _lastSelLength = txtReproEditor.SelectionLength;
            _lastEditTime = t;

            _reproTimer.Stop();
            _reproTimer.Start();
        }

        private void ReproTimer_Tick(object sender, EventArgs e)
        {
            _reproTimer.Stop();
            HighlightSynConstructions(txtReproEditor);
            UpdateTemplateInfo();
            AutoSaveReproText();

            // список справа следует за кареткой и после ручной правки текста
            if (txtReproEditor.Focused && txtReproEditor.SelectionLength == 0)
                RefreshReproTarget();
        }

        private void TxtReproEditor_KeyUp(object sender, KeyEventArgs e)
        {
            if (!e.Shift)
            {
                _wordSelOrigin = -1;
                _wordSelCaret = -1;
            }

            if (e.Control || e.Shift || e.Alt) return;
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Home:
                case Keys.End:
                case Keys.PageUp:
                case Keys.PageDown:
                    RefreshReproTarget();
                    break;
            }
        }

        private void TxtReproEditor_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            RemoveReproVariantAt(CharIndexAtPoint(txtReproEditor, e.Location));
        }

        /// <summary>
        /// Правый щелчок по варианту в {…} стирает его: {1|2|3} → {1|3}. Остался один вариант — скобки убираются.
        /// Отменяется Ctrl+Z. Удалённый синоним запоминается и больше не предлагается для этого слова.
        /// </summary>
        private bool RemoveReproVariantAt(int charIndex)
        {
            string text = txtReproEditor.Text;
            if (!SpinSyntax.TryRemoveVariantAt(text, charIndex, out var r)) return false;

            int selStart = txtReproEditor.SelectionStart;
            int selEnd = selStart + txtReproEditor.SelectionLength;
            Point scroll = GetScrollPos(txtReproEditor);

            ReplaceRepro(r.Construct.Start, r.Construct.Length, r.Replacement);
            HighlightSynConstructions(txtReproEditor);

            // выделение до конструкции не трогаем, после — сдвигаем, на ней — выделяем новую конструкцию
            int delta = r.Replacement.Length - r.Construct.Length;
            if (selEnd <= r.Construct.Start) SelectRepro(selStart, selEnd - selStart);
            else if (selStart >= r.Construct.End) SelectRepro(selStart + delta, selEnd - selStart);
            else SelectRepro(r.Construct.Start, r.Replacement.Length);
            SetScrollPos(txtReproEditor, scroll);

            string first = r.FirstVariant.Trim();
            if (r.RemovedIndex > 0 && r.Removed.Trim().Length > 0 && first.Length > 0 &&
                first.IndexOfAny(new[] { '{', '}', '|', '[', ']' }) < 0)
            {
                _store.RegisterRejected(first, new[] { r.Removed });
                try { _store.SaveRejected(); } catch { /* не критично */ }
            }

            RefreshReproTarget(force: true);
            UpdateTemplateInfo();
            SetHint("Стёрто: «" + Shorten(r.Removed.Trim(), 40) + "». Ctrl+Z — вернуть");
            return true;
        }

        private void TxtReproEditor_MouseUp(object sender, MouseEventArgs e)
        {
            if (_adjustingSelection) return;
            if (e.Button == MouseButtons.Right) return; // правая кнопка обработана в MouseDown

            // подрезаем пробелы в конце выделения (двойной щелчок захватывает пробел)
            int start = txtReproEditor.SelectionStart;
            int length = txtReproEditor.SelectionLength;
            if (length > 0)
            {
                string text = txtReproEditor.Text;
                int end = start + length;
                while (end > start && end <= text.Length && (text[end - 1] == ' ' || text[end - 1] == '\t' || text[end - 1] == '\u00A0'))
                    end--;
                if (end - start != length)
                    SelectRepro(start, end - start);
            }

            // щелчок не выделяет слово целиком — иначе следующая буква его затрёт
            RefreshReproTarget();
        }

        private void SelectRepro(int start, int length)
        {
            _adjustingSelection = true;
            try
            {
                txtReproEditor.Select(start, length);
            }
            finally
            {
                _adjustingSelection = false;
            }
        }

        // ==================================================================
        // Undo / Redo
        // ==================================================================

        private void PushUndo(List<UndoState> stack, string text, int selStart, int selLength)
        {
            stack.Add(new UndoState { Text = text ?? string.Empty, SelStart = selStart, SelLength = selLength });
            if (stack.Count > UndoLimit) stack.RemoveAt(0);
        }

        private void ResetReproUndo()
        {
            _undo.Clear();
            _redo.Clear();
            _forceUndoPush = false;
            _lastText = txtReproEditor.Text;
            _lastSelStart = txtReproEditor.SelectionStart;
            _lastSelLength = txtReproEditor.SelectionLength;
        }

        private void DoReproUndo()
        {
            UndoRedo(_undo, _redo);
        }

        private void DoReproRedo()
        {
            UndoRedo(_redo, _undo);
        }

        private void UndoRedo(List<UndoState> from, List<UndoState> to)
        {
            if (from.Count == 0)
            {
                SystemSounds.Beep.Play();
                return;
            }

            var state = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            PushUndo(to, txtReproEditor.Text, txtReproEditor.SelectionStart, txtReproEditor.SelectionLength);

            _inUndo = true;
            try
            {
                txtReproEditor.Text = state.Text;
                int s = Clamp(state.SelStart, 0, txtReproEditor.TextLength);
                SelectRepro(s, Clamp(state.SelLength, 0, txtReproEditor.TextLength - s));
                _lastText = txtReproEditor.Text;
                _lastSelStart = s;
                _lastSelLength = txtReproEditor.SelectionLength;
                _lastEditTime = DateTime.MinValue;
            }
            finally
            {
                _inUndo = false;
            }

            txtReproEditor.Focus();
            HighlightSynConstructions(txtReproEditor);
            txtReproEditor.ScrollToCaret();
            UpdateTemplateInfo();
            RefreshReproTarget(force: true);
            _reproTimer.Stop();
            _reproTimer.Start(); // автосохранение
        }

        /// <summary>Программная замена фрагмента — отдельный шаг отмены.</summary>
        private void ReplaceRepro(int start, int length, string newText)
        {
            _forceUndoPush = true;
            SelectRepro(start, length);
            txtReproEditor.SelectedText = newText ?? string.Empty;
            _forceUndoPush = false;
        }

        // ==================================================================
        // Цель: слово / фраза / конструкция и список вариантов
        // ==================================================================

        /// <summary>
        /// Определяет, с чем работаем: выделение (фраза или конструкция), конструкция под кареткой
        /// или слово под кареткой — и заполняет список вариантов.
        /// </summary>
        private void RefreshReproTarget(bool force = false)
        {
            if (txtReproEditor == null || lvVariants == null) return;

            var t = ComputeTarget();
            if (!force && SameTarget(t, _target)) return;

            _target = t;
            PopulateVariants();
        }

        private static bool SameTarget(ReproTarget a, ReproTarget b)
        {
            if (a == null || b == null) return a == b;
            return a.Start == b.Start && a.End == b.End && a.Raw == b.Raw;
        }

        private ReproTarget ComputeTarget()
        {
            string text = txtReproEditor.Text;
            if (text.Length == 0) return null;

            int s = txtReproEditor.SelectionStart;
            int l = txtReproEditor.SelectionLength;

            if (l > 0)
            {
                int end = Math.Min(text.Length, s + l);
                while (s < end && char.IsWhiteSpace(text[s])) s++;
                while (end > s && char.IsWhiteSpace(text[end - 1])) end--;
                if (end <= s) return null;

                string sel = text.Substring(s, end - s);
                if (sel[0] == '{' && SpinSyntax.FindMatchingClose(text, s) == end - 1)
                    return MakeConstructTarget(text, new TextRange(s, end));

                if (sel.IndexOfAny(new[] { '{', '}', '[', ']', '|' }) >= 0)
                {
                    // выделение внутри одной конструкции — работаем с ней
                    if (SpinSyntax.TryGetInnermostConstruct(text, s, out var r) && r.End >= end)
                        return MakeConstructTarget(text, r);
                    return null;
                }

                return new ReproTarget { Start = s, End = end, Raw = sel, Original = sel, IsConstruct = false };
            }

            int charPos = Math.Min(s, text.Length - 1);
            if (charPos > 0 && s <= text.Length)
            {
                char right = s < text.Length ? text[s] : ' ';
                char left = text[s - 1];
                bool rightUseful = TextNav.IsWordChar(right) || right == '{' || right == '}' || right == '|';
                if (!rightUseful && (TextNav.IsWordChar(left) || left == '}'))
                    charPos = s - 1;
            }

            if (SpinSyntax.TryGetInnermostConstruct(text, charPos, out var range))
                return MakeConstructTarget(text, range);

            var w = TextNav.WordAt(text, s);
            if (w.IsEmpty) return null;
            string word = text.Substring(w.Start, w.Length);
            return new ReproTarget { Start = w.Start, End = w.End, Raw = word, Original = word, IsConstruct = false };
        }

        private static ReproTarget MakeConstructTarget(string text, TextRange r)
        {
            string raw = text.Substring(r.Start, r.Length);
            var variants = SpinSyntax.GetVariants(raw);
            return new ReproTarget
            {
                Start = r.Start,
                End = r.End,
                Raw = raw,
                Original = variants.Count > 0 ? variants[0] : string.Empty,
                IsConstruct = true
            };
        }

        private Color UserColor { get { return _theme.ListUser; } }

        private void PopulateVariants()
        {
            lvVariants.BeginUpdate();
            try
            {
                lvVariants.Items.Clear();

                if (_target == null)
                {
                    lblTarget.Text = "—";
                    return;
                }

                string baseText = _target.Original.Trim();
                bool baseIsPlain = baseText.IndexOfAny(new[] { '{', '}', '[', ']', '|' }) < 0;
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 1) исходное слово / варианты конструкции
                if (_target.IsConstruct)
                {
                    var variants = SpinSyntax.GetVariants(_target.Raw);
                    for (int i = 0; i < variants.Count; i++)
                    {
                        string v = variants[i];
                        AddVariantItem(v, true, null, i == 0);
                        present.Add(v.Trim());
                    }
                }
                else
                {
                    AddVariantItem(_target.Original, true, null, true);
                    present.Add(baseText);
                }

                // 2) предложения из баз
                if (baseIsPlain && baseText.Length > 0)
                {
                    var cands = _store.GetCandidates(baseText, chkUseDict.Checked);
                    bool hasUser = cands.Any(c => c.Source == SynonymSource.User);
                    int autoChecks = (int)numMaxSynonyms.Value; // столько же, сколько берёт F5
                    var ctx = ActiveContext;
                    AutoSpinner.GetNeighbors(txtReproEditor.Text, _target.Start, _target.End, out string left, out string right);

                    // сначала подходящие по соседям, потом остальные
                    var ordered = cands.Where(c => AutoSpinner.FitsContext(ctx, c, left, right))
                                       .Concat(cands.Where(c => !AutoSpinner.FitsContext(ctx, c, left, right)));

                    foreach (var c in ordered)
                    {
                        string display = TextCase.ApplyCase(baseText, c.Text);
                        if (!present.Add(display.Trim())) continue;

                        bool fits = AutoSpinner.FitsContext(ctx, c, left, right);
                        bool check;
                        if (_target.IsConstruct || !fits) check = false;               // готовую конструкцию не трогаем, сомнительное не отмечаем
                        else if (hasUser) check = c.Source == SynonymSource.User;
                        else check = c.Source != SynonymSource.Dict && autoChecks > 0;
                        if (check && !hasUser) autoChecks--;

                        var item = AddVariantItem(display, check, c, false);
                        if (check) item.Name = AutoCheckedMark;
                        if (!fits) item.Font = LvStrikeFont;
                    }
                }

                int count = lvVariants.Items.Count - 1;
                lblTarget.Text = (_target.IsConstruct ? "Конструкция: " : "Слово: ") + "«" + Shorten(baseText, 40) + "»" +
                                 (count > 0 ? "  (" + count + ")" : "  — синонимов нет");
            }
            finally
            {
                lvVariants.EndUpdate();
                FitVariantsColumn();
            }
        }

        private static string Shorten(string s, int max)
        {
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private Font _lvBoldFont;

        private Font LvBoldFont
        {
            get { return _lvBoldFont ?? (_lvBoldFont = new Font(lvVariants.Font, FontStyle.Bold)); }
        }

        private const string AutoCheckedMark = "auto";
        private Font _lvStrikeFont;

        private Font LvStrikeFont
        {
            get { return _lvStrikeFont ?? (_lvStrikeFont = new Font(lvVariants.Font, FontStyle.Strikeout)); }
        }

        private ListViewItem AddVariantItem(string text, bool check, SynonymCandidate cand, bool isOriginal)
        {
            var item = new ListViewItem(text) { Checked = check, Tag = cand };
            if (isOriginal)
            {
                item.Font = LvBoldFont;
                item.ForeColor = _theme.ListOriginalFore;
                item.BackColor = _theme.ListOriginalBack;
            }
            else if (cand != null)
            {
                if (cand.UsageCount > 0) item.Font = LvBoldFont;
                if (cand.Source == SynonymSource.Dict) item.ForeColor = _theme.ListDict;
                else if (cand.Source == SynonymSource.User) item.ForeColor = UserColor;
            }
            lvVariants.Items.Add(item);
            return item;
        }

        private void LvVariants_ItemCheck(object sender, ItemCheckEventArgs e)
        {
            // исходное слово всегда входит в шаблон
            if (e.Index == 0) e.NewValue = CheckState.Checked;
        }

        private void FocusVariant(int index)
        {
            if (lvVariants.Items.Count == 0) return;
            index = Clamp(index, 0, lvVariants.Items.Count - 1);
            lvVariants.Focus();
            lvVariants.SelectedIndices.Clear();
            lvVariants.Items[index].Selected = true;
            lvVariants.Items[index].Focused = true;
            lvVariants.EnsureVisible(index);
        }

        private int SelectedVariantIndex
        {
            get { return lvVariants.SelectedIndices.Count > 0 ? lvVariants.SelectedIndices[0] : -1; }
        }

        private void LvVariants_KeyDown(object sender, KeyEventArgs e)
        {
            int idx = SelectedVariantIndex;

            if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.F2) && !e.Control && !e.Shift)
            {
                if (idx >= 0) lvVariants.Items[idx].BeginEdit();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Delete && !e.Control && idx > 0)
            {
                lvVariants.Items.RemoveAt(idx);
                FocusVariant(idx);
                e.Handled = e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.End && e.Control && idx >= 0)
            {
                int from = Math.Max(1, idx);
                for (int i = lvVariants.Items.Count - 1; i >= from; i--)
                    lvVariants.Items.RemoveAt(i);
                FocusVariant(from - 1);
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void LvVariants_AfterLabelEdit(object sender, LabelEditEventArgs e)
        {
            if (e.Label == null) return; // правку отменили

            string val = e.Label.Trim();
            if (val.Length == 0)
            {
                e.CancelEdit = true;
                int index = e.Item;
                if (index > 0)
                    BeginInvoke(new Action(() =>
                    {
                        if (index < lvVariants.Items.Count) lvVariants.Items.RemoveAt(index);
                        FocusVariant(index);
                    }));
                return;
            }

            if (val != e.Label)
            {
                e.CancelEdit = true;
                int index = e.Item;
                BeginInvoke(new Action(() => { if (index < lvVariants.Items.Count) lvVariants.Items[index].Text = val; }));
            }

            if (e.Item > 0)
            {
                int index = e.Item;
                BeginInvoke(new Action(() => { if (index < lvVariants.Items.Count) lvVariants.Items[index].Checked = true; }));
            }
        }

        private void MoveSelectedVariant(int delta)
        {
            int idx = SelectedVariantIndex;
            int to = idx + delta;
            if (idx <= 0 || to <= 0 || to >= lvVariants.Items.Count) return; // исходное слово не двигаем

            var item = lvVariants.Items[idx];
            lvVariants.BeginUpdate();
            lvVariants.Items.RemoveAt(idx);
            lvVariants.Items.Insert(to, item);
            lvVariants.EndUpdate();
            FocusVariant(to);
        }

        private void TxtAddVariant_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !e.Control && !e.Shift)
            {
                AddVariantFromBox();
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        private void AddVariantFromBox()
        {
            string v = txtAddVariant.Text.Trim();
            if (v.Length == 0) return;

            if (_target == null) RefreshReproTarget(force: true);
            if (_target == null)
            {
                SystemSounds.Beep.Play();
                return;
            }

            foreach (ListViewItem it in lvVariants.Items)
            {
                if (string.Equals(it.Text.Trim(), v, StringComparison.OrdinalIgnoreCase))
                {
                    it.Checked = true;
                    txtAddVariant.Clear();
                    return;
                }
            }

            var item = AddVariantItem(v, true, null, false);
            item.ForeColor = UserColor;
            lvVariants.EnsureVisible(item.Index);
            txtAddVariant.Clear();
        }

        // ==================================================================
        // Запись конструкции
        // ==================================================================

        /// <summary>
        /// Отмеченные варианты по порядку. У конструкции варианты берутся как есть
        /// (пустой вариант «{очень |}» и пробелы внутри — часть шаблона).
        /// </summary>
        private List<string> CollectCheckedVariants()
        {
            bool exact = _target != null && _target.IsConstruct;
            var result = new List<string>();
            var seen = new HashSet<string>(exact ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < lvVariants.Items.Count; i++)
            {
                var it = lvVariants.Items[i];
                if (i > 0 && !it.Checked) continue;
                string v = exact || i == 0 ? it.Text : it.Text.Trim();
                if (!exact && i > 0 && v.Length == 0) continue;
                if (seen.Add(exact ? v : v.Trim())) result.Add(v);
            }
            return result;
        }

        private bool TargetStillValid()
        {
            if (_target == null) return false;
            string text = txtReproEditor.Text;
            return _target.End <= text.Length &&
                   string.CompareOrdinal(text, _target.Start, _target.Raw, 0, _target.Raw.Length) == 0 &&
                   _target.Raw.Length == _target.End - _target.Start;
        }

        /// <summary>Ctrl+Enter: записывает отмеченные варианты и переходит к следующему слову.</summary>
        private void ApplyTarget()
        {
            if (!TargetStillValid())
            {
                // текст изменился после выбора слова — перечитываем, ничего не ломая
                RefreshReproTarget(force: true);
                SystemSounds.Beep.Play();
                SetHint("Текст изменился — список вариантов обновлён, нажмите Ctrl+Enter ещё раз");
                return;
            }

            var variants = CollectCheckedVariants();
            if (variants.Count == 0) return;

            string newText = SpinSyntax.BuildConstruct(variants);
            RememberRejected(variants);

            if (variants.Count > 1 && variants[0].IndexOfAny(new[] { '{', '}', '|' }) < 0)
            {
                _store.RegisterUsage(variants[0], variants.Skip(1).Where(v => v.IndexOfAny(new[] { '{', '}', '[', ']' }) < 0));
                try { _store.SaveUsage(); } catch { /* не критично */ }
            }

            int start = _target.Start;
            if (newText != _target.Raw)
            {
                ReplaceRepro(start, _target.End - start, newText);
                HighlightSynConstructions(txtReproEditor);
            }

            int after = start + newText.Length;
            var next = TextNav.NextToken(txtReproEditor.Text, after);
            if (next.IsValid)
            {
                GoToToken(next);
            }
            else
            {
                SelectRepro(after, 0);
                RefreshReproTarget(force: true);
                SetHint("Конец текста");
            }
            txtReproEditor.Focus();
        }

        /// <summary>
        /// Варианты, которые были в конструкции (или отмечены автоматически), а вы их сняли, —
        /// запоминаются и больше не предлагаются для этого слова.
        /// </summary>
        private void RememberRejected(List<string> finalVariants)
        {
            if (_target == null || finalVariants.Count == 0) return;
            string baseWord = finalVariants[0];
            if (baseWord.IndexOfAny(new[] { '{', '}', '|', '[', ']' }) >= 0) return;

            var kept = new HashSet<string>(finalVariants.Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
            var rejected = new List<string>();

            if (_target.IsConstruct)
                rejected.AddRange(SpinSyntax.GetVariants(_target.Raw).Skip(1).Where(v => v.Trim().Length > 0 && !kept.Contains(v.Trim())));

            for (int i = 1; i < lvVariants.Items.Count; i++)
            {
                var it = lvVariants.Items[i];
                if (it.Name == AutoCheckedMark && !it.Checked && !kept.Contains(it.Text.Trim())) rejected.Add(it.Text);
            }

            if (rejected.Count == 0) return;
            _store.RegisterRejected(baseWord, rejected);
            try { _store.SaveRejected(); } catch { /* не критично */ }
        }

        private void ApplyFrequentSynonyms()
        {
            if (_target == null || lvVariants.Items.Count < 2) return;

            int added = 0;
            for (int i = 1; i < lvVariants.Items.Count; i++)
            {
                var cand = lvVariants.Items[i].Tag as SynonymCandidate;
                bool frequent = cand != null && cand.UsageCount > 0;
                if (frequent) added++;
                lvVariants.Items[i].Name = string.Empty; // «только частые» — это не отказ от остальных

                // у готовой конструкции её варианты оставляем, частые только добавляем
                if (_target.IsConstruct) { if (frequent) lvVariants.Items[i].Checked = true; }
                else lvVariants.Items[i].Checked = frequent;
            }

            if (added == 0)
            {
                SystemSounds.Beep.Play();
                SetHint("Для этого слова ещё нет частых синонимов");
                return;
            }
            ApplyTarget();
        }

        private void UnwrapTarget()
        {
            if (_target == null || !_target.IsConstruct || !TargetStillValid())
            {
                SystemSounds.Beep.Play();
                return;
            }
            int start = _target.Start;
            string original = _target.Original;
            ReplaceRepro(start, _target.End - start, original);
            HighlightSynConstructions(txtReproEditor);
            SelectRepro(start, original.Length);
            RefreshReproTarget(force: true);
            txtReproEditor.Focus();
        }

        private void SaveTargetToUserBase()
        {
            if (_target == null || lvVariants.Items.Count == 0)
            {
                SystemSounds.Beep.Play();
                return;
            }

            string baseWord = lvVariants.Items[0].Text.Trim();
            if (baseWord.Length == 0 || baseWord.IndexOfAny(new[] { '{', '}', '|', '[', ']' }) >= 0)
            {
                SystemSounds.Beep.Play();
                return;
            }

            var syns = CollectCheckedVariants().Skip(1).ToList();
            try
            {
                _store.SetUserEntry(baseWord, syns);
                _store.SaveUser();
                SetHint("«Моя база»: " + baseWord.ToLowerInvariant() + " → " + syns.Count + " синонимов записано");
            }
            catch (Exception ex)
            {
                ShowWarning("Не удалось записать UserBase.txt:\r\n" + ex.Message);
            }

            // отметки пользователя сохраняем, просто перекрашиваем источники
            var checkedTexts = new HashSet<string>(syns, StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lvVariants.Items.Count; i++)
            {
                var it = lvVariants.Items[i];
                if (checkedTexts.Contains(it.Text.Trim())) it.ForeColor = UserColor;
            }
        }

        // ==================================================================
        // Навигация
        // ==================================================================

        private void GoToToken(TextToken tok)
        {
            SelectRepro(tok.Range.Start, tok.Range.Length);
            txtReproEditor.ScrollToCaret();
            RefreshReproTarget(force: true);
        }

        private void NavigateToken(bool forward)
        {
            string text = txtReproEditor.Text;
            TextToken tok;
            if (forward)
            {
                int from = _target != null && TargetStillValid() ? _target.End : txtReproEditor.SelectionStart + txtReproEditor.SelectionLength;
                tok = TextNav.NextToken(text, from);
            }
            else
            {
                int before = _target != null && TargetStillValid() ? _target.Start : txtReproEditor.SelectionStart;
                tok = TextNav.PrevToken(text, before);
            }

            txtReproEditor.Focus();
            if (!tok.IsValid)
            {
                SystemSounds.Beep.Play();
                return;
            }
            GoToToken(tok);
        }

        private void GoToConstruct(bool forward)
        {
            string text = txtReproEditor.Text;
            var blocks = TextNav.GetBlocks(text);
            TextToken tok;
            if (forward)
            {
                int from = _target != null && TargetStillValid() ? _target.End : txtReproEditor.SelectionStart + 1;
                tok = TextNav.NextConstruct(text, from, blocks);
            }
            else
            {
                int before = _target != null && TargetStillValid() ? _target.Start : txtReproEditor.SelectionStart;
                tok = TextNav.PrevConstruct(text, before, blocks);
            }

            txtReproEditor.Focus();
            if (!tok.IsValid)
            {
                SystemSounds.Beep.Play();
                SetHint(forward ? "Дальше конструкций нет" : "Выше конструкций нет");
                return;
            }
            GoToToken(tok);
        }

        /// <summary>Shift+стрелки: выделение по словам (конструкция — одно «слово»).</summary>
        private void ExtendWordSelection(bool forward)
        {
            string text = txtReproEditor.Text;
            if (_wordSelOrigin < 0)
            {
                int s = txtReproEditor.SelectionStart;
                int l = txtReproEditor.SelectionLength;
                if (l > 0)
                {
                    _wordSelOrigin = s;
                    _wordSelCaret = s + l;
                }
                else
                {
                    var w = TextNav.WordAt(text, s);
                    _wordSelOrigin = w.IsEmpty ? s : w.Start;
                    _wordSelCaret = _wordSelOrigin;
                }
            }

            int caret = _wordSelCaret;
            if (forward)
            {
                var tok = TextNav.NextToken(text, caret);
                if (tok.IsValid) caret = tok.Range.End;
            }
            else
            {
                var tok = TextNav.PrevToken(text, caret);
                if (tok.IsValid) caret = tok.Range.Start;
            }
            _wordSelCaret = caret;

            int start = Math.Min(_wordSelOrigin, caret);
            int end = Math.Max(_wordSelOrigin, caret);
            SelectRepro(start, end - start);
            RefreshReproTarget();
        }

        // ==================================================================
        // F5 — размножить всё
        // ==================================================================

        private SpinOptions BuildSpinOptions(bool includeDict)
        {
            return new SpinOptions
            {
                MaxSynonyms = (int)numMaxSynonyms.Value,
                IncludeDict = includeDict,
                Context = ActiveContext,
                BadPhrases = SpinOptions.LoadBadPhrases(Path.Combine(AppDir, "BadWord.txt")),
                Exceptions = SpinOptions.LoadExceptions(Path.Combine(AppDir, "Exceptions.txt"))
            };
        }

        private void SpinAllRepro()
        {
            if (tabControl.SelectedTab != tabRepro) tabControl.SelectedTab = tabRepro;

            string text = txtReproEditor.Text;
            if (text.Trim().Length == 0)
            {
                SetHint("Вставьте текст, затем F5");
                return;
            }
            if (_contextLoading && chkUseContext.Checked)
            {
                SystemSounds.Beep.Play();
                SetHint("Подождите пару секунд: готовится проверка соседних слов (строка состояния внизу)");
                return;
            }

            int start = 0, length = text.Length;
            if (txtReproEditor.SelectionLength > 0 && txtReproEditor.SelectionLength < text.Length)
            {
                start = txtReproEditor.SelectionStart;
                length = txtReproEditor.SelectionLength;
                if (!SpinSyntax.Validate(text.Substring(start, length)).IsValid)
                {
                    SystemSounds.Beep.Play();
                    SetHint("Выделение разрезает конструкцию {…} — выделите её целиком или снимите выделение");
                    return;
                }
            }

            SpinResult res;
            Cursor = Cursors.WaitCursor;
            try
            {
                res = AutoSpinner.Spin(text.Substring(start, length), _store, BuildSpinOptions(chkUseDict.Checked));
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            if (res.Constructs == 0 && res.Text == text.Substring(start, length))
            {
                SetHint("Новых синонимов не найдено" + (chkUseDict.Checked ? "" : " — попробуйте включить DICT.DBF"));
                SystemSounds.Beep.Play();
                return;
            }

            Point scroll = GetScrollPos(txtReproEditor);
            ReplaceRepro(start, length, res.Text);
            HighlightSynConstructions(txtReproEditor);
            SetScrollPos(txtReproEditor, scroll);
            UpdateTemplateInfo();

            var first = TextNav.NextConstruct(txtReproEditor.Text, start);
            txtReproEditor.Focus();
            if (first.IsValid) GoToToken(first);

            SetHint("Создано конструкций: " + res.Constructs + ". Проверяйте: F3 — следующая, Пробел — снять вариант, Ctrl+Enter — записать, F4 — убрать, Ctrl+Z — отменить всё");
        }

        // ==================================================================
        // Пробная генерация и статистика шаблона
        // ==================================================================

        private void UpdateTemplateInfo()
        {
            string text = txtReproEditor.Text;
            var v = SpinSyntax.Validate(text);
            int constructs = text.Count(c => c == '{');

            if (!v.IsValid)
            {
                _templateErrorPos = v.ErrorPosition;
                int line = text.Take(v.ErrorPosition).Count(c => c == '\n') + 1;
                lblTemplateInfo.ForeColor = _theme.Error;
                lblTemplateInfo.Text = "Ошибка скобок: " + v.Message + " (строка " + line + ") — щёлкните, чтобы перейти";
                return;
            }

            _templateErrorPos = -1;
            lblTemplateInfo.ForeColor = _theme.Ok;
            lblTemplateInfo.Text = "Конструкций: " + constructs + " · вариантов текста: " +
                                   SpinSyntax.FormatCount(SpinSyntax.CountVariants(text)) + " · скобки в порядке";
        }

        private void GoToTemplateError()
        {
            if (_templateErrorPos < 0 || _templateErrorPos >= txtReproEditor.TextLength) return;
            tabControl.SelectedTab = tabRepro;
            txtReproEditor.Focus();
            SelectRepro(_templateErrorPos, 1);
            txtReproEditor.ScrollToCaret();
        }

        private void ShowTrialGeneration()
        {
            string template = tabControl.SelectedTab == tabEditor && txtEditorResult != null
                ? txtEditorResult.Text
                : txtReproEditor.Text;

            if (template.Trim().Length == 0) return;

            var v = SpinSyntax.Validate(template);
            if (!v.IsValid)
            {
                ShowWarning("В шаблоне ошибка скобок: " + v.Message + ".\r\nСначала исправьте её (щелчок по строке состояния).");
                return;
            }

            var rnd = new Random();
            using (var dlg = new Form())
            {
                dlg.Text = "Пробная генерация — вариантов: " + SpinSyntax.FormatCount(SpinSyntax.CountVariants(template));
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.Size = new Size(900, 600);
                dlg.KeyPreview = true;

                var box = new TextBox
                {
                    Multiline = true,
                    ReadOnly = true,
                    ScrollBars = ScrollBars.Vertical,
                    Dock = DockStyle.Fill,
                    Font = new Font(_theme.IsClassic ? "Segoe UI" : _theme.EditorFontFamily, 12F),
                    BackColor = _theme.IsClassic ? Color.White : _theme.Card
                };

                var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(5) };
                var btnClose = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.Cancel };
                var btnCopy = new Button { Text = "Копировать", AutoSize = true };
                var btnNext = new Button { Text = "Ещё вариант (Пробел)", AutoSize = true };
                bottom.Controls.AddRange(new Control[] { btnClose, btnCopy, btnNext });

                Action gen = () => box.Text = SpinSyntax.Generate(template, rnd).Replace("\r\n", "\n").Replace("\n", "\r\n");
                btnNext.Click += (s, e) => gen();
                btnCopy.Click += (s, e) => { if (box.Text.Length > 0) Clipboard.SetText(box.Text); };
                dlg.KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Space) { gen(); e.Handled = e.SuppressKeyPress = true; }
                };

                dlg.Controls.Add(box);
                dlg.Controls.Add(bottom);
                dlg.CancelButton = btnClose;
                gen();
                if (!_theme.IsClassic)
                {
                    ApplyThemeTree(dlg);
                    box.BorderStyle = BorderStyle.None;
                    btnNext.Tag = ButtonRole.Accent;
                    StyleButton(btnNext, _theme);
                    TryWinApi(() => SetDarkTitleBar(dlg.Handle, _theme.IsDark));
                }
                try
                {
                    dlg.ShowDialog(this);
                }
                finally
                {
                    ForgetSnapshots(dlg);
                }
            }
        }

        // ==================================================================
        // Перечисления, ротация, регистр
        // ==================================================================

        private bool RequireSelection()
        {
            if (txtReproEditor.SelectionLength > 0) return true;
            SystemSounds.Beep.Play();
            SetHint("Сначала выделите фрагмент текста (Shift+стрелки или мышью)");
            return false;
        }

        private void ReplaceSelectionAndGoNext(string result)
        {
            int start = txtReproEditor.SelectionStart;
            result = TextCase.NormalizeSpaces(result);
            ReplaceRepro(start, txtReproEditor.SelectionLength, result);
            HighlightSynConstructions(txtReproEditor);

            var next = TextNav.NextToken(txtReproEditor.Text, start + result.Length);
            if (next.IsValid) GoToToken(next);
            else SelectRepro(start + result.Length, 0);
            txtReproEditor.Focus();
        }

        private void ReproEnumVariant(bool andOr)
        {
            if (!RequireSelection()) return;
            string sel = txtReproEditor.SelectedText;
            string res = andOr ? TextCase.EnumVariant2(sel) : TextCase.EnumVariant1(sel);
            if (res == null) { SystemSounds.Beep.Play(); return; }
            ReplaceSelectionAndGoNext(res);
        }

        private void ReproRotate()
        {
            if (!RequireSelection()) return;
            string res = TextCase.Rotate(txtReproEditor.SelectedText);
            if (res == null)
            {
                SystemSounds.Beep.Play();
                SetHint("Ротация: нужно ровно две части через «и», «или», «,» или «;»");
                return;
            }
            ReplaceSelectionAndGoNext(res);
        }

        private void ReproToggleCase(Func<string, string> transform)
        {
            // без выделения — работаем с текущим словом
            if (txtReproEditor.SelectionLength == 0 && _target != null && !_target.IsConstruct && TargetStillValid())
                SelectRepro(_target.Start, _target.End - _target.Start);
            if (!RequireSelection()) return;

            int start = txtReproEditor.SelectionStart;
            string res = transform(txtReproEditor.SelectedText);
            if (res == null) return;
            ReplaceRepro(start, txtReproEditor.SelectionLength, res);
            HighlightSynConstructions(txtReproEditor);
            SelectRepro(start, res.Length);      // повторное нажатие переключит обратно
            RefreshReproTarget(force: true);
            txtReproEditor.Focus();
        }

        private void ReproTemplateEmpty()
        {
            if (!RequireSelection()) return;
            int start = txtReproEditor.SelectionStart;
            string sel = txtReproEditor.SelectedText;
            ReplaceRepro(start, sel.Length, "{" + sel + "|}");
            HighlightSynConstructions(txtReproEditor);
            SelectRepro(start + sel.Length + 2, 0); // курсор после '|' — сразу печатать вариант
            txtReproEditor.Focus();
        }

        private void ReproTemplateDuplicate()
        {
            if (!RequireSelection()) return;
            int start = txtReproEditor.SelectionStart;
            string sel = txtReproEditor.SelectedText;
            string ins = "{" + sel + "|" + sel + "}";
            ReplaceRepro(start, sel.Length, ins);
            HighlightSynConstructions(txtReproEditor);
            SelectRepro(start + ins.Length, 0);
            txtReproEditor.Focus();
        }

        // ==================================================================
        // Файл: выбор, открытие, автосохранение
        // ==================================================================

        private string GetDefaultReproDirectory()
        {
            string root = _settings.GetString("ProgonsRoot", @"m:\PROGONS");
            string[] months =
            {
                "01_январь", "02_февраль", "03_март", "04_апрель", "05_май", "06_июнь",
                "07_июль", "08_август", "09_сентябрь", "10_октябрь", "11_ноябрь", "12_декабрь"
            };
            var now = DateTime.Now;
            return Path.Combine(root, now.Year.ToString(), months[now.Month - 1]);
        }

        private bool ReproPathIsFile(out string path)
        {
            path = txtReproFilePath.Text.Trim();
            return path.Length > 0 && !path.EndsWith("\\") && !path.EndsWith("/") && !Directory.Exists(path);
        }

        private void UpdateReproStatusLabel(string error = null)
        {
            if (lblReproStatus == null) return;

            if (error != null)
            {
                lblReproStatus.Text = "Не сохранено: " + error;
                lblReproStatus.ForeColor = _theme.Error;
                return;
            }

            if (!ReproPathIsFile(out _))
            {
                lblReproStatus.Text = "Файл не выбран — текст не сохраняется";
                lblReproStatus.ForeColor = _theme.Error;
                return;
            }

            if (_lastSavedText != null && _lastSavedText == txtReproEditor.Text && _lastSavedPath == txtReproFilePath.Text.Trim())
            {
                lblReproStatus.Text = "Сохранено " + DateTime.Now.ToString("HH:mm:ss");
                lblReproStatus.ForeColor = _theme.Saved;
            }
            else
            {
                lblReproStatus.Text = "Автосохранение";
                lblReproStatus.ForeColor = _theme.Saved;
            }
        }

        private void AutoSaveReproText()
        {
            if (!ReproPathIsFile(out string path))
            {
                UpdateReproStatusLabel();
                return;
            }

            string text = txtReproEditor.Text;
            if (text == _lastSavedText && path == _lastSavedPath) return;

            try
            {
                FileText.WriteSafe(path, text);
                _lastSavedText = text;
                _lastSavedPath = path;
                UpdateReproStatusLabel();
            }
            catch (Exception ex)
            {
                UpdateReproStatusLabel(ex.Message);
            }
        }

        private void FlushReproAutoSave()
        {
            if (_reproTimer != null && _reproTimer.Enabled)
                _reproTimer.Stop();
            AutoSaveReproText();
        }

        private void SaveReproNow()
        {
            if (!ReproPathIsFile(out _))
            {
                BtnReproBrowse_Click(this, EventArgs.Empty);
                return;
            }
            _lastSavedText = null; // принудительно
            AutoSaveReproText();
        }

        private void LoadReproFile(string path)
        {
            string text = FileText.ReadAuto(path);

            _inUndo = true;
            try
            {
                txtReproEditor.Text = text;
            }
            finally
            {
                _inUndo = false;
            }

            txtReproFilePath.Text = path;
            ResetReproUndo();
            _lastSavedText = txtReproEditor.Text;
            _lastSavedPath = path;

            HighlightSynConstructions(txtReproEditor);
            SelectRepro(0, 0);
            UpdateTemplateInfo();
            UpdateReproStatusLabel();
            RefreshReproTarget(force: true);
            SetHint("Открыт файл: " + Path.GetFileName(path));
        }

        private void BtnReproBrowse_Click(object sender, EventArgs e)
        {
            string initialDir = GetDefaultReproDirectory();
            try
            {
                string candidate = txtReproFilePath.Text.Trim();
                string dir = candidate.EndsWith("\\") || candidate.EndsWith("/") ? candidate : Path.GetDirectoryName(candidate);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) initialDir = dir;
            }
            catch
            {
                // путь не разобрался — берём папку по умолчанию
            }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Файл шаблона (сохранение)";
                dlg.Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*";
                dlg.InitialDirectory = Directory.Exists(initialDir) ? initialDir : string.Empty;
                dlg.FileName = "Шаблон размножения.txt";
                dlg.OverwritePrompt = false; // спрашиваем сами — понятнее

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    string file = dlg.FileName;
                    bool hasText = txtReproEditor.TextLength > 0;

                    if (File.Exists(file) && (!hasText || !chkReproReplace.Checked))
                    {
                        // в поле пусто или «Заменить» выключен — открываем файл
                        LoadReproFile(file);
                        return;
                    }

                    if (File.Exists(file))
                    {
                        var answer = MessageBox.Show(this,
                            "Файл уже существует:\r\n" + file + "\r\n\r\n" +
                            "Да — перезаписать его текстом из редактора\r\n" +
                            "Нет — открыть файл (текст в редакторе заменится)",
                            "Файл существует", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (answer == DialogResult.Cancel) return;
                        if (answer == DialogResult.No)
                        {
                            LoadReproFile(file);
                            return;
                        }
                    }

                    txtReproFilePath.Text = file;
                    _lastSavedText = null;
                    AutoSaveReproText();
                }
                catch (Exception ex)
                {
                    ShowWarning("Не удалось обработать файл:\r\n" + ex.Message);
                }
            }
        }

        private void BtnReproOpen_Click(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Открыть шаблон";
                dlg.Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*";
                string dir = GetDefaultReproDirectory();
                if (ReproPathIsFile(out string cur))
                {
                    string d = Path.GetDirectoryName(cur);
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) dir = d;
                }
                if (Directory.Exists(dir)) dlg.InitialDirectory = dir;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                if (!ReproPathIsFile(out _) && txtReproEditor.TextLength > 0)
                {
                    var answer = MessageBox.Show(this,
                        "Текущий текст не сохранён в файл и будет заменён. Продолжить?",
                        "Открыть шаблон", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                    if (answer != DialogResult.OK) return;
                }

                FlushReproAutoSave();
                try
                {
                    LoadReproFile(dlg.FileName);
                }
                catch (Exception ex)
                {
                    ShowWarning("Не удалось открыть файл:\r\n" + ex.Message);
                }
            }
        }

        private void BtnReproClear_Click(object sender, EventArgs e)
        {
            // сначала дописываем текущий файл, потом отвязываемся от него
            FlushReproAutoSave();

            txtReproFilePath.Text = GetDefaultReproDirectory() + Path.DirectorySeparatorChar;
            _lastSavedText = null;
            _lastSavedPath = null;

            if (txtReproEditor.TextLength > 0)
                ReplaceRepro(0, txtReproEditor.TextLength, string.Empty); // можно вернуть Ctrl+Z

            _target = null;
            PopulateVariants();
            UpdateTemplateInfo();
            UpdateReproStatusLabel();
            txtReproEditor.Focus();
        }

        // ==================================================================
        // Сессия
        // ==================================================================

        private void RestoreReproSession()
        {
            string last = _settings.GetString("LastFile", string.Empty);
            if (last.Length > 0 && File.Exists(last))
            {
                try
                {
                    LoadReproFile(last);
                    return;
                }
                catch
                {
                    // не открылся — начинаем с пустого
                }
            }
            UpdateTemplateInfo();
        }

        private void SaveReproSettings()
        {
            _settings.Set("LastFile", ReproPathIsFile(out string p) ? p : string.Empty);
            _settings.Set("MaxSynonyms", (int)numMaxSynonyms.Value);
            _settings.Set("UseDict", chkUseDict.Checked);
            _settings.Set("UseContext", chkUseContext.Checked);
            _settings.Set("QuickUseDict", rbBigBase != null && rbBigBase.Checked);
        }
    }
}
