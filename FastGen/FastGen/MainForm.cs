using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Runtime.InteropServices;




namespace FastGen
{
    public class MainForm : Form
    {

        // Отключение/включение перерисовки RichTextBox, чтобы убрать мерцание
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int WM_SETREDRAW = 0x000B;

        private void SetRedraw(Control c, bool enable)
        {
            if (c == null || !c.IsHandleCreated) return;
            SendMessage(c.Handle, WM_SETREDRAW, enable ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
            if (enable) c.Invalidate();
        }


        private StyledTabControl tabControl;
        private bool _adjustingSelection;


        // --- Собственный Undo только для поля Размножения ---
        private struct ReproUndoState
        {
            public string Text;
            public int SelStart;
            public int SelLength;
        }

        private readonly Stack<ReproUndoState> _reproUndoStack = new Stack<ReproUndoState>();
        private bool _reproInUndo;          // сейчас выполняется Undo — не пушим состояние
        private string _reproLastText = ""; // предыдущий текст до изменения
        private int _reproLastSelStart;     // предыдущая позиция каретки
        private int _reproLastSelLength;


        private bool _skipHighlightOnce;   // пропустить одну подсветку после Undo

        private Timer _reproHighlightTimer;  // таймер для отложенной подсветки/автосохранения




        // фоновая загрузка словаря
        private bool _synonymLoadingStarted;
        private bool _synonymsLoaded;
        private string _synonymLoadError;

        // базовое слово, для которого сейчас показан список синонимов
        private string _currentSynBaseWord;

        // для выделения по словам (Shift+стрелки)
        private int _wordSelectionOrigin = -1; // где начали тянуть выделение
        private int _wordSelectionCaret = -1; // «двигающийся» край выделения


        // для выделения по словам (Ctrl+Shift+стрелки)
        private int _wordSelectionAnchor = -1;

        // Частота использования синонимов: базовое слово -> (синоним -> счётчик)
        private Dictionary<string, Dictionary<string, int>> _synUsage =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        private string SynUsageFilePath =>
            Path.Combine(Application.StartupPath, "syn_usage.txt");




        private struct TextSpan
        {
            public int Start; // включительно
            public int End;   // исключительно
        }


        // Анализ (слева)
        private TextBox txtFolderPath;
        private TextBox txtMask;
        private TextBox txtCollectorStatus;   // левое большое поле

        // Сборка (справа)
        private TextBox txtResultFilePath;
        private TextBox txtBuildResult;       // правое большое поле
        private Label lblBuildStatus;

        private CheckBox chkAppendBase;

        // Вкладка "Быстрое размножение"
        private RadioButton rbGoldBase;
        private RadioButton rbBigBase;
        private RichTextBox txtEditorSource;
        private RichTextBox txtEditorResult;

        // Вкладка "Размножение"
        private TextBox txtReproFilePath;
        private RichTextBox txtReproEditor;
        private CheckedListBox lstReproSynonyms;

        private Label lblReproStatus;
        private CheckBox chkReproReplace;




        // Словарь из DICT.DBF
        private Dictionary<string, string[]> _dbfSynonyms;
        private string _lastSynonymWord = string.Empty;



        private static readonly HashSet<string> NoSynonymWords =
    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "в",
        "на",
        "за",
        "под",
        "над",
        "из",
        "к",
        "о",
        "по",
        "с",
        "у",
        "для",
        "до",
        "без",
        "при"
    };


        public MainForm()
        {
            InitializeComponent();

            _reproHighlightTimer = new Timer();
            _reproHighlightTimer.Interval = 100;                 // мс паузы “тишины” после набора
            _reproHighlightTimer.Tick += ReproHighlightTimer_Tick;


            LoadSynUsage();
            this.FormClosing += MainForm_FormClosing;


            // запускаем загрузку DICT.DBF в фоне, чтобы не тормозить UI
            StartSynonymsBackgroundLoad();

        }

        private void PushReproUndoState(string oldText, int oldSelStart, int oldSelLength)
        {
            if (_reproInUndo) return;

            if (oldText == null)
                oldText = string.Empty;

            // Храним именно предыдущее состояние
            _reproUndoStack.Push(new ReproUndoState
            {
                Text = oldText,
                SelStart = Math.Max(0, oldSelStart),
                SelLength = Math.Max(0, oldSelLength)
            });
        }

        private void ResetReproUndoState()
        {
            _reproUndoStack.Clear();
            _reproInUndo = false;

            if (txtReproEditor != null)
            {
                _reproLastText = txtReproEditor.Text;
                _reproLastSelStart = txtReproEditor.SelectionStart;
                _reproLastSelLength = txtReproEditor.SelectionLength;
            }
            else
            {
                _reproLastText = string.Empty;
                _reproLastSelStart = 0;
                _reproLastSelLength = 0;
            }
        }

        private void DoReproUndo()
        {
            if (txtReproEditor == null)
                return;

            if (_reproUndoStack.Count == 0)
                return;

            var state = _reproUndoStack.Pop();

            _reproInUndo = true;
            try
            {
                txtReproEditor.Text = state.Text ?? string.Empty;

                int selStart = Math.Max(0, Math.Min(state.SelStart, txtReproEditor.TextLength));
                int selLength = Math.Max(0, Math.Min(state.SelLength, txtReproEditor.TextLength - selStart));

                txtReproEditor.SelectionStart = selStart;
                txtReproEditor.SelectionLength = selLength;

                // обновляем "последнее" состояние для следующего шага undo
                _reproLastText = txtReproEditor.Text;
                _reproLastSelStart = selStart;
                _reproLastSelLength = selLength;
            }
            finally
            {
                _reproInUndo = false;
            }

            // после отката — подсветка и обновление списка синонимов
            HighlightSynConstructions(txtReproEditor);
            UpdateReproSynonymsForCaret();
        }



        private void StartSynonymsBackgroundLoad()
        {
            if (_synonymLoadingStarted)
                return;

            _synonymLoadingStarted = true;

            // НУЖЕН using System.Threading.Tasks;
            Task.Run(() =>
            {
                try
                {
                    // тяжёлая загрузка DICT.DBF – идёт в отдельном потоке
                    LoadDbfSynonyms();
                }
                catch (Exception ex)
                {
                    _synonymLoadError = ex.Message;
                }
                finally
                {
                    _synonymsLoaded = true;
                }
            });
        }

        private void LoadSynUsage()
        {
            _synUsage.Clear();

            if (!File.Exists(SynUsageFilePath))
                return;

            foreach (var line in File.ReadAllLines(SynUsageFilePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // формат строки: base|synonym|count
                var parts = line.Split('|');
                if (parts.Length != 3)
                    continue;

                string baseWord = parts[0].Trim();
                string synonym = parts[1].Trim();
                if (!int.TryParse(parts[2], out int count))
                    continue;

                if (string.IsNullOrEmpty(baseWord) || string.IsNullOrEmpty(synonym))
                    continue;

                if (!_synUsage.TryGetValue(baseWord, out var dict))
                {
                    dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    _synUsage[baseWord] = dict;
                }

                dict[synonym] = count;
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSynUsage();
        }


        private void SaveSynUsage()
        {
            try
            {
                var lines = new List<string>();

                foreach (var kvBase in _synUsage)
                {
                    string baseWord = kvBase.Key;
                    foreach (var kvSyn in kvBase.Value)
                    {
                        string synonym = kvSyn.Key;
                        int count = kvSyn.Value;
                        if (count <= 0) continue;

                        lines.Add($"{baseWord}|{synonym}|{count}");
                    }
                }

                File.WriteAllLines(SynUsageFilePath, lines, new UTF8Encoding(false));
            }
            catch
            {
                // тихо игнорируем ошибки записи
            }
        }


        private void InitializeComponent()
        {
            this.Text = "Seo Tools";
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(1150, 700);
            this.Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            this.WindowState = FormWindowState.Maximized;

            tabControl = new StyledTabControl
            {
                Dock = DockStyle.Fill
            };

            var tabCollector = new TabPage("Сборщик");
            var tabEditor = new TabPage("Быстрое размножение");
            var tabRepro = new TabPage("Размножение");

            BuildCollectorTab(tabCollector);
            BuildEditorTab(tabEditor);   // <-- новая вкладка
            BuildReproductionTab(tabRepro);

            tabControl.TabPages.Add(tabCollector);
            tabControl.TabPages.Add(tabEditor);
            tabControl.TabPages.Add(tabRepro);

            this.Controls.Add(tabControl);


        }

        /// <summary>
        /// Вкладка "Размножение" в режиме split-screen:
        /// сверху – выбор файла, ниже слева текст, справа панель синонимов.
        /// </summary>
        private void BuildReproductionTab(TabPage tabPage)
        {
            tabPage.Padding = new Padding(8);

            // === ГЛАВНЫЙ ЛЕЙАУТ: 2 строки (Файл + основная область) ===
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // выбор файла
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // кнопки вариантов
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // вся оставшаяся высота под splitLayout



            // === ВЕРХНЯЯ ПАНЕЛЬ: "Файл: [путь] [...]" ===

            // -------- строка выбора файла ----------
            var fileRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 5)
            };

            var lblFile = new Label
            {
                Text = "Файл:",
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, 8, 5, 0)
            };

            // --- панель с кнопками вариантов (как на Быстром размножении) ---
            var reproButtonsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 5)
            };

            // Вариант 1
            var btnReproVariant1 = new Button
            {
                Text = "Вариант 1 ( ,  ; )",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 5, 0)
            };
            btnReproVariant1.Click += BtnReproVariant1_Click;

            // Вариант 2
            var btnReproVariant2 = new Button
            {
                Text = "Вариант 2 (и, или)",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 5, 0)
            };
            btnReproVariant2.Click += BtnReproVariant2_Click;

            // Ротация
            var btnReproRotate = new Button
            {
                Text = "Ротация",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 5, 0)
            };
            btnReproRotate.Click += BtnReproRotate_Click;

            // Текст
            var btnReproWordCase = new Button
            {
                Text = "Текст",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 5, 0)
            };
            btnReproWordCase.Click += BtnReproWordCase_Click;

            // ТЕКСТ
            var btnReproUpperLower = new Button
            {
                Text = "ТЕКСТ",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 5, 0)
            };
            btnReproUpperLower.Click += BtnReproUpperLower_Click;

            // {слово|*}
            var btnReproTemplateStar = new Button
            {
                Text = "{слово|*}",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 0, 0)
            };
            btnReproTemplateStar.Click += BtnReproTemplateStar_Click;

            // {слово|слово}
            var btnReproTemplateDuplicate = new Button
            {
                Text = "{слово|слово}",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(5, 0, 0, 0)
            };
            btnReproTemplateDuplicate.Click += BtnReproTemplateDuplicate_Click;

            reproButtonsPanel.Controls.Add(btnReproVariant1);
            reproButtonsPanel.Controls.Add(btnReproVariant2);
            reproButtonsPanel.Controls.Add(btnReproRotate);
            reproButtonsPanel.Controls.Add(btnReproWordCase);
            reproButtonsPanel.Controls.Add(btnReproUpperLower);
            reproButtonsPanel.Controls.Add(btnReproTemplateStar);
            reproButtonsPanel.Controls.Add(btnReproTemplateDuplicate);



            txtReproFilePath = new TextBox
            {
                Width = 500,
                Margin = new Padding(0, 5, 5, 0),
                ReadOnly = false
            };

            // статус автосохранения (между полем и кнопкой Очистить)
            lblReproStatus = new Label
            {
                AutoSize = true,
                Margin = new Padding(8, 8, 0, 0),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point)
            };

            // при любом изменении пути – обновляем статус
            txtReproFilePath.TextChanged += (s, e) => UpdateReproStatusLabel();

            // путь по умолчанию m:\PROGONS\год\мм_месяц\
            string defaultDir = GetDefaultReproDirectory();
            txtReproFilePath.Text = defaultDir + Path.DirectorySeparatorChar;
            UpdateReproStatusLabel(); // сразу показать "Файл не выбран"


            // чекбокс "Заменить"
            chkReproReplace = new CheckBox
            {
                Text = "Заменить",
                Checked = true,                     // по умолчанию включён
                AutoSize = true,
                Margin = new Padding(5, 7, 0, 0),
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point)
            };




            var btnReproBrowse = new Button
            {
                Text = "...",
                Width = 32,
                Height = 26,
                Margin = new Padding(0, 5, 0, 0)
            };
            btnReproBrowse.Click += BtnReproBrowse_Click;

            // красная кнопка "Очистить"
            var btnReproClear = new Button
            {
                Text = "Очистить",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Padding = new Padding(8, 2, 8, 2),
                Margin = new Padding(8, 5, 0, 0),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.Red
            };
            btnReproClear.FlatAppearance.BorderSize = 0;
            btnReproClear.Click += BtnReproClear_Click;

            fileRow.Controls.Add(lblFile);
            fileRow.Controls.Add(txtReproFilePath);
            fileRow.Controls.Add(chkReproReplace);
            fileRow.Controls.Add(btnReproBrowse);
            fileRow.Controls.Add(lblReproStatus);
            fileRow.Controls.Add(btnReproClear);


            // === НИЖНЯЯ ОБЛАСТЬ: SPLIT-SCREEN (2 колонки) ===

            var splitLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };
            // слева 75% ширины под текст, справа 25% под синонимы
            splitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75F));
            splitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            splitLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // --- ЛЕВАЯ КОЛОНКА: ТЕКСТ ---
            txtReproEditor = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Courier New", 14F, FontStyle.Regular, GraphicsUnit.Point),
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                HideSelection = false      // ← ВАЖНО: не скрывать выделение при потере фокуса
            };

            txtReproEditor.TextChanged += TxtReproEditor_TextChanged;
            txtReproEditor.SelectionChanged += TxtReproEditor_SelectionChanged;
            txtReproEditor.KeyDown += TxtReproEditor_KeyDown;
            txtReproEditor.KeyUp += TxtReproEditor_KeyUp;
            txtReproEditor.MouseUp += TxtReproEditor_MouseUp;   // подрезаем пробелы в конце выделения, если у тебя есть этот метод

            ResetReproUndoState();


            // --- ПРАВАЯ КОЛОНКА: СИНОНИМЫ ---
            lstReproSynonyms = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
                IntegralHeight = false,
                CheckOnClick = false,           // управляем галочками пробелом
                BorderStyle = BorderStyle.FixedSingle,
                DrawMode = DrawMode.OwnerDrawFixed  // будем рисовать сами, чтобы сделать жирный шрифт
            };
            lstReproSynonyms.KeyDown += LstReproSynonyms_KeyDown;
            lstReproSynonyms.DrawItem += LstReproSynonyms_DrawItem;

            splitLayout.Controls.Add(txtReproEditor, 0, 0);

            // правая панель: сверху кнопка "Добавить", ниже список
            var rightPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            rightPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            rightPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // строка с кнопкой
            rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // сам список

            var rightTopPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 3)
            };

            var btnAddFrequent = new Button
            {
                Text = "Добавить",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 0)
            };
            btnAddFrequent.Click += BtnAddFrequent_Click;

            rightTopPanel.Controls.Add(btnAddFrequent);
            rightPanel.Controls.Add(rightTopPanel, 0, 0);
            rightPanel.Controls.Add(lstReproSynonyms, 0, 1);

            splitLayout.Controls.Add(rightPanel, 1, 0);


            // === СБОРКА ВСЕГО ВКЛАДКИ ===

            // 0-я строка – выбор файла
            layout.Controls.Add(fileRow, 0, 0);
            // 1-я строка – кнопки "Вариант 1/2, Ротация, Текст, ТЕКСТ"
            layout.Controls.Add(reproButtonsPanel, 0, 1);
            // 2-я строка – split-screen: слева текст, справа панель синонимов
            layout.Controls.Add(splitLayout, 0, 2);

            tabPage.Controls.Add(layout);

        }

        private void UpdateReproStatusLabel()
        {
            if (lblReproStatus == null || txtReproFilePath == null)
                return;

            string path = txtReproFilePath.Text.Trim();

            // Путь пустой или только папка -> файла нет
            if (string.IsNullOrEmpty(path) ||
                path.EndsWith("\\") || path.EndsWith("/"))
            {
                lblReproStatus.Text = "Файл не выбран";
                lblReproStatus.ForeColor = Color.Red;
            }
            else
            {
                lblReproStatus.Text = "Автосохранение";
                lblReproStatus.ForeColor = Color.Green;
            }
        }


        private void BtnReproClear_Click(object sender, EventArgs e)
        {
            if (txtReproFilePath == null)
                return;

            // вернуть путь к папке по умолчанию (без имени файла)
            string defaultDir = GetDefaultReproDirectory();
            txtReproFilePath.Text = defaultDir + Path.DirectorySeparatorChar;

            // очистить текст в редакторе
            txtReproEditor?.Clear();
            ResetReproUndoState();

            UpdateReproStatusLabel();
        }

        private void TxtReproEditor_MouseUp(object sender, MouseEventArgs e)
        {
            if (_adjustingSelection) return;

            int start = txtReproEditor.SelectionStart;
            int length = txtReproEditor.SelectionLength;

            // если ничего не выделено — ничего не делаем
            if (length <= 0)
            {
                UpdateReproSynonymsForCaret();
                return;
            }

            string text = txtReproEditor.Text;
            int end = start + length;

            // подрезаем только пробелы/таб/неразрывный пробел в конце
            while (length > 0 && end > 0)
            {
                char c = text[end - 1];

                // перенос строки оставляем как есть
                if (c == '\r' || c == '\n')
                    break;

                if (!char.IsWhiteSpace(c))
                    break;

                end--;
                length--;
            }

            _adjustingSelection = true;
            txtReproEditor.SelectionStart = start;
            txtReproEditor.SelectionLength = length;
            _adjustingSelection = false;
        }


        private void TxtReproEditor_KeyDown(object sender, KeyEventArgs e)
        {


            // SHIFT + Delete → Вариант 1
            if (e.Shift && e.KeyCode == Keys.Delete)
            {
                ApplyCommaSemicolonVariant();
                txtReproEditor.Focus();
                e.Handled = true;
                return;
            }

            // SHIFT + End → Вариант 2 (и, или)
            if (e.Shift && e.KeyCode == Keys.End)
            {
                ApplyAndOrVariant();
                txtReproEditor.Focus();
                e.Handled = true;
                return;
            }

            // SHIFT + PageDown → Ротация
            if (e.Shift && e.KeyCode == Keys.PageDown)
            {
                ApplyRotation();
                txtReproEditor.Focus();
                e.Handled = true;
                return;
            }






            // SHIFT + Enter → Быстрая вставка частых синонимов
            if (e.Shift && !e.Control && e.KeyCode == Keys.Enter)
            {
                InsertFrequentSynonyms();
                txtReproEditor.Focus();
                e.Handled = true;
                return;
            }


            // Shift+стрелки влево/вправо – выделение по словам
            if (e.Shift && !e.Control && (e.KeyCode == Keys.Right || e.KeyCode == Keys.Left))
            {
                string textAll = txtReproEditor.Text;

                int selStart = txtReproEditor.SelectionStart;
                int selLength = txtReproEditor.SelectionLength;

                // Инициализируем режим "выделение по словам"
                if (_wordSelectionOrigin < 0)
                {
                    if (selLength == 0)
                    {
                        // ещё ничего не выделено – начальная точка = текущая каретка
                        _wordSelectionOrigin = selStart;
                        _wordSelectionCaret = selStart;
                    }
                    else
                    {
                        // уже есть выделение – считаем, что начали с левого края
                        _wordSelectionOrigin = selStart;
                        _wordSelectionCaret = selStart + selLength;
                    }
                }

                int newCaretPos = (e.KeyCode == Keys.Right)
                    ? MoveCaretWordRight(textAll, _wordSelectionCaret)
                    : MoveCaretWordLeft(textAll, _wordSelectionCaret);

                _wordSelectionCaret = newCaretPos;

                int start = Math.Min(_wordSelectionOrigin, _wordSelectionCaret);
                int end = Math.Max(_wordSelectionOrigin, _wordSelectionCaret);

                // 1) сначала обрезаем хвост до последнего "буквенного" символа
                int endTrimmed = end;
                while (endTrimmed > start && !IsWordChar(textAll[endTrimmed - 1]))
                    endTrimmed--;

                int finalEnd = endTrimmed;

                // 2) спец-случай: если сразу после последнего слова стоят "}" и/или ".", "!", "?"
                if (endTrimmed > start)
                {
                    int i = endTrimmed;

                    // пропускаем пробелы
                    while (i < textAll.Length && char.IsWhiteSpace(textAll[i]))
                        i++;

                    if (i < textAll.Length && textAll[i] == '}')
                    {
                        // включаем '}'
                        finalEnd = i + 1;

                        int j = i + 1;
                        if (j < textAll.Length &&
                            (textAll[j] == '.' || textAll[j] == '!' || textAll[j] == '?'))
                        {
                            // и, если сразу за '}' стоит точка/!/?, включаем и её
                            finalEnd = j + 1;
                        }
                    }
                }

                int length = Math.Max(0, finalEnd - start);

                _adjustingSelection = true;
                txtReproEditor.SelectionStart = start;
                txtReproEditor.SelectionLength = length;
                _adjustingSelection = false;

                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }





            // Ctrl+Enter – вставка {слово|синоним1|...}
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                ApplySynonymPattern();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // Ctrl+стрелки влево/вправо – шаг по словам
            if (e.Control && (e.KeyCode == Keys.Right || e.KeyCode == Keys.Left))
            {
                string text = txtReproEditor.Text;
                int pos = txtReproEditor.SelectionStart;
                int newPos = pos;

                if (e.KeyCode == Keys.Right)
                    newPos = MoveCaretWordRight(text, pos);
                else // Left
                    newPos = MoveCaretWordLeft(text, pos);

                _adjustingSelection = true;
                txtReproEditor.SelectionStart = newPos;
                txtReproEditor.SelectionLength = 0;   // ставим курсор в начало слова
                _adjustingSelection = false;

                e.Handled = true;
                e.SuppressKeyPress = true;

                // сразу подсвечиваем целое слово и обновляем список синонимов
                UpdateReproSynonymsForCaret();
                return;
            }

            // Обычные стрелки вверх/вниз – переключаемся в список синонимов
            if (!e.Control && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
            {
                if (lstReproSynonyms != null && lstReproSynonyms.Items.Count > 0)
                {
                    int index = lstReproSynonyms.Items.Count > 1 ? 1 : 0; // 2-я строка, если есть
                    lstReproSynonyms.Focus();
                    lstReproSynonyms.SelectedIndex = index;

                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            }
        }

        private void TxtReproEditor_KeyUp(object sender, KeyEventArgs e)
        {
            // как только Shift отпущен – выходим из режима "выделение по словам"
            if (!ModifierKeys.HasFlag(Keys.Shift))
            {
                _wordSelectionOrigin = -1;
                _wordSelectionCaret = -1;
            }
        }




        private void LstReproSynonyms_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+стрелка влево/вправо – перейти к предыдущему/следующему слову в тексте
            if (e.Control && (e.KeyCode == Keys.Right || e.KeyCode == Keys.Left))
            {
                if (txtReproEditor != null)
                {
                    string text = txtReproEditor.Text;
                    int pos = txtReproEditor.SelectionStart;
                    int newPos = pos;

                    if (e.KeyCode == Keys.Right)
                        newPos = MoveCaretWordRight(text, pos);
                    else // Keys.Left
                        newPos = MoveCaretWordLeft(text, pos);

                    txtReproEditor.Focus();

                    _adjustingSelection = true;
                    txtReproEditor.SelectionStart = newPos;
                    txtReproEditor.SelectionLength = 0;
                    _adjustingSelection = false;

                    // сразу подсвечиваем новое слово и подгружаем синонимы
                    UpdateReproSynonymsForCaret();
                }

                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // Ctrl+Shift+Enter – частые синонимы
            if (e.Control && e.Shift && e.KeyCode == Keys.Enter)
            {
                ApplyFrequentSynonyms();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            // Ctrl+Enter – вставка шаблона из списка
            if (e.Control && e.KeyCode == Keys.Enter)
            {
                ApplySynonymPattern();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }


        private void ApplySynonymPattern()
        {
            if (txtReproEditor == null || lstReproSynonyms == null)
                return;

            if (lstReproSynonyms.Items.Count == 0)
                return;

            // 1. Базовое слово – как оно выглядит в тексте (с сохранением регистра)
            var parts = new List<string>();

            string baseWord = StripMarker(lstReproSynonyms.Items[0]?.ToString() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(baseWord))
                return;

            parts.Add(baseWord);

            // 2. Остальные – только помеченные галочкой, с приведением регистра под baseWord
            for (int i = 1; i < lstReproSynonyms.Items.Count; i++)
            {
                if (!lstReproSynonyms.GetItemChecked(i))
                    continue;

                string raw = StripMarker(lstReproSynonyms.Items[i]?.ToString());
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                // DICT.DBF мы читали в нижний регистр, но на всякий ещё раз понизим:
                string adjusted = ApplyCase(baseWord, raw.ToLower());
                parts.Add(adjusted);
            }

            // убираем пустое и дубли (без учёта регистра)
            parts = parts
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // если остался только один элемент (базовое слово) – ничего не вставляем
            if (parts.Count < 2)
                return;

            // 3. Обновляем статистику использования частых синонимов
            // ВСЁ в syn_usage.txt храним в нижнем регистре
            string baseWordKey = parts[0].ToLower();

            if (!_synUsage.TryGetValue(baseWordKey, out var freqDict))
            {
                freqDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _synUsage[baseWordKey] = freqDict;
            }

            for (int i = 1; i < parts.Count; i++)
            {
                string synKey = parts[i].ToLower();   // всегда мелкими
                if (string.IsNullOrWhiteSpace(synKey))
                    continue;

                if (!freqDict.TryGetValue(synKey, out int c))
                    c = 0;

                freqDict[synKey] = c + 1;
            }

            SaveSynUsage();

            // 4. Формируем {слово|синоним1|синоним2}
            string pattern = "{" + string.Join("|", parts) + "}";

            string fullText = txtReproEditor.Text;

            // определяем диапазон замены (слово под курсором или текущее выделение)
            int start = txtReproEditor.SelectionStart;
            int length = txtReproEditor.SelectionLength;

            if (length == 0)
            {
                var span = GetWordSpanUnderCaret(txtReproEditor);
                start = span.Start;
                length = span.End - span.Start;
            }

            if (start < 0 || start > fullText.Length)
                return;

            // 5. Заменяем слово на конструкцию
            _adjustingSelection = true;
            txtReproEditor.SelectionStart = start;
            txtReproEditor.SelectionLength = length;
            txtReproEditor.SelectedText = pattern;
            _adjustingSelection = false;

            int insertStart = start;
            int insertLength = pattern.Length;

            // локальная подсветка конструкции
            if (pattern.StartsWith("{") && pattern.EndsWith("}"))
            {
                HighlightSingleConstruction(txtReproEditor, insertStart, insertLength);
            }

            // 6. Ставим курсор в конец конструкции…
            _adjustingSelection = true;
            txtReproEditor.SelectionStart = insertStart + insertLength;
            txtReproEditor.SelectionLength = 0;
            _adjustingSelection = false;

            // …и сразу прыгаем на следующее слово (как Ctrl+стрелка вправо)
            string newText = txtReproEditor.Text;
            int caretPos = txtReproEditor.SelectionStart;
            int nextPos = MoveCaretWordRight(newText, caretPos);

            _adjustingSelection = true;
            txtReproEditor.SelectionStart = nextPos;
            txtReproEditor.SelectionLength = 0;
            _adjustingSelection = false;

            txtReproEditor.Focus();
            UpdateReproSynonymsForCaret();
        }


        private bool IsFrequentSynonym(string baseWord, string synonym)
        {
            if (string.IsNullOrEmpty(baseWord) || string.IsNullOrEmpty(synonym))
                return false;

            if (_synUsage == null)
                return false;

            string baseKey = baseWord.ToLower();
            string synKey = synonym.ToLower();

            if (_synUsage.TryGetValue(baseKey, out var dict))
            {
                if (dict.TryGetValue(synKey, out int count) && count > 0)
                    return true;
            }

            return false;
        }


        private string StripMarker(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            if (s.StartsWith("★ "))
                return s.Substring(2);

            return s;
        }


        private void LstReproSynonyms_DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;

            var list = (CheckedListBox)sender;
            string text = list.Items[e.Index].ToString();

            // фон и выделение
            e.DrawBackground();
            bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;

            // базовое слово (первая строка) оставляем обычным шрифтом
            bool isFrequent = false;
            string raw = StripMarker(text); // убираем "★ " если есть

            if (e.Index > 0 && !string.IsNullOrEmpty(_currentSynBaseWord))
            {
                isFrequent = IsFrequentSynonym(_currentSynBaseWord, raw);
            }


            Font font = e.Font;
            if (isFrequent)
                font = new Font(e.Font, FontStyle.Bold);

            Color foreColor = isSelected ? SystemColors.HighlightText : list.ForeColor;

            TextRenderer.DrawText(
                e.Graphics,
                text,
                font,
                e.Bounds,
                foreColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            );

            e.DrawFocusRectangle();

            if (isFrequent)
                font.Dispose();
        }

        private void BtnAddFrequent_Click(object sender, EventArgs e)
        {
            ApplyFrequentSynonyms();
        }


        private void ApplyFrequentSynonyms()
        {
            if (lstReproSynonyms == null || lstReproSynonyms.Items.Count == 0)
                return;

            if (string.IsNullOrEmpty(_currentSynBaseWord))
                return;

            // снимаем все галочки (кроме базового слова)
            for (int i = 1; i < lstReproSynonyms.Items.Count; i++)
                lstReproSynonyms.SetItemChecked(i, false);

            int added = 0;

            // ставим галочки только частым
            for (int i = 1; i < lstReproSynonyms.Items.Count; i++)
            {
                string display = lstReproSynonyms.Items[i]?.ToString();
                string syn = StripMarker(display);

                if (IsFrequentSynonym(_currentSynBaseWord, syn))
                {
                    lstReproSynonyms.SetItemChecked(i, true);
                    added++;
                }
            }

            if (added == 0)
                return;

            ApplySynonymPattern();
        }


        private bool IsWordChar(char c)
        {
            // считаем словами только буквы (рус/лат) и, например, дефис/апостроф
            return char.IsLetter(c) || c == '-' || c == '\'';
        }

        private int MoveCaretWordRight(string text, int pos)
        {
            int length = text.Length;
            if (pos < 0) pos = 0;
            if (pos > length) pos = length;

            // 1) если стоим внутри слова – доходим до конца текущего слова
            while (pos < length && IsWordChar(text[pos]))
                pos++;

            // 2) теперь ПРЫГАЕМ через пунктуацию и пробелы
            while (pos < length && !IsWordChar(text[pos]))
                pos++;

            // результат: позиция на первой букве следующего слова (или в конец текста)
            return pos;
        }

        private int MoveCaretWordLeft(string text, int pos)
        {
            int length = text.Length;
            if (pos < 0) pos = 0;
            if (pos > length) pos = length;

            // отступаем на один символ влево, чтобы не застревать между словами
            if (pos > 0) pos--;

            // 1) пропускаем слева пунктуацию и пробелы
            while (pos > 0 && !IsWordChar(text[pos]))
                pos--;

            // 2) теперь мы внутри слова – отматываем к его началу
            while (pos > 0 && IsWordChar(text[pos - 1]))
                pos--;

            // результат: позиция на первой букве предыдущего слова (или 0)
            return pos;
        }


        private string GetDefaultReproDirectory()
        {
            var now = DateTime.Now;
            int year = now.Year;
            int month = now.Month;

            string[] monthFolders =
            {
                "01_январь",
                "02_февраль",
                "03_март",
                "04_апрель",
                "05_май",
                "06_июнь",
                "07_июль",
                "08_август",
                "09_сентябрь",
                "10_октябрь",
                "11_ноябрь",
                "12_декабрь"
            };

            string monthFolder = monthFolders[month - 1];

            // m:\PROGONS\2025\11_ноябрь
            string dir = Path.Combine(@"m:\PROGONS", year.ToString(), monthFolder);
            return dir;
        }


        private void BtnReproBrowse_Click(object sender, EventArgs e)
        {
            string initialDir = GetDefaultReproDirectory();

            try
            {
                if (!string.IsNullOrWhiteSpace(txtReproFilePath.Text))
                {
                    string candidate = txtReproFilePath.Text;
                    if (!candidate.EndsWith("\\") && !candidate.EndsWith("/"))
                    {
                        string dir = Path.GetDirectoryName(candidate);
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                            initialDir = dir;
                    }
                }
            }
            catch
            {
                // если не удалось разобрать путь – просто игнорируем
            }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Выберите текстовый файл для сохранения";
                dlg.Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*";
                dlg.InitialDirectory = initialDir;
                dlg.FileName = "Шаблон размножения.txt";

                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    txtReproFilePath.Text = dlg.FileName;
                    UpdateReproStatusLabel();

                    bool replace = chkReproReplace == null || chkReproReplace.Checked;

                    try
                    {
                        if (replace)
                        {
                            // Режим "Заменить":
                            //  - текст в поле оставляем как есть
                            //  - сразу перезаписываем файл текущим текстом
                            AutoSaveReproText();
                        }
                        else
                        {
                            // Режим "не Заменять":
                            //  - если файл существует — загружаем его содержимое в поле
                            //  - если файла нет — текст в поле не трогаем
                            if (File.Exists(dlg.FileName))
                            {
                                txtReproEditor.Text = File.ReadAllText(
                                    dlg.FileName,
                                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                                ResetReproUndoState();

                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this,
                            "Не удалось обработать файл:\r\n" + ex.Message,
                            "Ошибка",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }
            }
        }

        private void ReproHighlightTimer_Tick(object sender, EventArgs e)
        {
            _reproHighlightTimer.Stop();

            // автосохранение и подсветка выполняются не на каждый символ,
            // а только когда пользователь сделал паузу
            AutoSaveReproText();
            HighlightSynConstructions(txtReproEditor);
        }

        private void TxtReproEditor_TextChanged(object sender, EventArgs e)
        {
            // Во время Undo не пишем в стек
            if (!_reproInUndo)
            {
                // сохраняем предыдущее состояние
                PushReproUndoState(_reproLastText, _reproLastSelStart, _reproLastSelLength);

                // фиксируем новое состояние как "текущее"
                _reproLastText = txtReproEditor.Text;
                _reproLastSelStart = txtReproEditor.SelectionStart;
                _reproLastSelLength = txtReproEditor.SelectionLength;
            }

            // подсветка + автосохранение через таймер (как было)
            if (_reproHighlightTimer != null)
            {
                _reproHighlightTimer.Stop();
                _reproHighlightTimer.Start();
            }
        }









        private void AutoSaveReproText()
        {
            if (txtReproFilePath == null) return;

            string path = txtReproFilePath.Text.Trim();
            if (string.IsNullOrEmpty(path))
                return;

            // Если сейчас в поле только папка с обратным слешем – не сохраняем
            if (path.EndsWith("\\") || path.EndsWith("/"))
                return;

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(
                    path,
                    txtReproEditor.Text,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)); // UTF-8 без BOM
            }
            catch
            {
                // без сообщений, чтобы не раздражать при каждом вводе
            }
        }

        /// <summary>
        /// Загружаем DICT.DBF из папки программы без всяких драйверов.
        /// Все непустые символьные поля строки считаются взаимными синонимами.
        /// </summary>
        private void LoadDbfSynonyms()
        {
            _dbfSynonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            try
            {
                string dbfPath = Path.Combine(Application.StartupPath, "DICT.DBF");

                if (!File.Exists(dbfPath))
                {
                    MessageBox.Show(this,
                        "Файл DICT.DBF не найден в папке программы.",
                        "Ошибка подключения словаря",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                // DICT.DBF в кодировке cp866 (dBASE RUS cp866)
                Encoding enc;
                try
                {
                    enc = Encoding.GetEncoding(866);
                }
                catch
                {
                    // на всякий случай – если кодировка недоступна
                    enc = Encoding.Default;
                }

                var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                using (var fs = File.OpenRead(dbfPath))
                using (var br = new BinaryReader(fs))
                {
                    // ----- заголовок DBF -----
                    byte version = br.ReadByte();          // 0
                    br.ReadBytes(3);                       // дата (YY,MM,DD)
                    int recordCount = br.ReadInt32();      // 4–7
                    short headerLen = br.ReadInt16();      // 8–9
                    short recordLen = br.ReadInt16();      // 10–11
                    br.ReadBytes(20);                      // 12–31, не используем

                    // ----- описатели полей -----
                    int fieldsDescriptorLength = headerLen - 32 - 1; // без заголовка и байта 0x0D
                    int fieldCount = fieldsDescriptorLength / 32;

                    var fields = new List<(string Name, char Type, int Length, int Offset)>();
                    int offset = 1; // первый байт записи – признак удаления, далее поля

                    for (int i = 0; i < fieldCount; i++)
                    {
                        byte[] nameBytes = br.ReadBytes(11); // имя
                        int nameEnd = Array.IndexOf(nameBytes, (byte)0);
                        if (nameEnd < 0) nameEnd = nameBytes.Length;
                        string fieldName = Encoding.ASCII.GetString(nameBytes, 0, nameEnd).Trim();

                        char fieldType = (char)br.ReadByte(); // тип
                        br.ReadInt32();                       // адрес поля, не используем
                        byte fieldLen = br.ReadByte();        // длина
                        byte decCount = br.ReadByte();        // дробная часть, не нужна
                        br.ReadBytes(14);                     // прочее, не нужно

                        if (fieldType == 'C' && fieldLen > 0)
                        {
                            fields.Add((fieldName, fieldType, fieldLen, offset));
                        }

                        offset += fieldLen;
                    }

                    // байт-терминатор заголовка
                    br.ReadByte(); // 0x0D

                    // буфер под запись
                    var recBuffer = new byte[recordLen];

                    // ----- читаем все записи -----
                    for (int r = 0; r < recordCount; r++)
                    {
                        int read = br.Read(recBuffer, 0, recordLen);
                        if (read < recordLen)
                            break; // неожиданное окончание файла

                        // первый байт – признак удаления
                        if (recBuffer[0] == (byte)'*')
                            continue; // помеченная на удаление запись

                        var rowWords = new List<string>();

                        foreach (var f in fields)
                        {
                            var slice = new byte[f.Length];
                            Array.Copy(recBuffer, f.Offset, slice, 0, f.Length);

                            string value = enc.GetString(slice).Trim();
                            if (!string.IsNullOrEmpty(value))
                            {
                                rowWords.Add(value.ToLower());
                            }
                        }

                        if (rowWords.Count < 2)
                            continue;

                        // все слова строки считаем взаимными синонимами
                        foreach (var w in rowWords)
                        {
                            if (!result.TryGetValue(w, out var list))
                            {
                                list = new List<string>();
                                result[w] = list;
                            }

                            foreach (var s in rowWords)
                            {
                                if (string.Equals(w, s, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                if (!list.Contains(s, StringComparer.OrdinalIgnoreCase))
                                    list.Add(s);
                            }
                        }
                    }
                }

                // переводим List<string> в string[]
                _dbfSynonyms = result.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _dbfSynonyms = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

                MessageBox.Show(this,
                    "Не удалось загрузить DICT.DBF.\r\n" +
                    "Ошибка разбора файла DBF: " + ex.Message,
                    "Ошибка подключения словаря",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }


        private void TxtReproEditor_SelectionChanged(object sender, EventArgs e)
        {
            if (_adjustingSelection) return;
            // Больше не обновляем слово при каждом срабатывании SelectionChanged,
            // иначе во время набора мы подсвечиваем целое слово и следующая буква
            // перезаписывает его. Всё нужное вызывается из клавиш (Ctrl+стрелки)
            // и из MouseUp.
        }





        private void UpdateReproSynonymsForCaret()
        {
            if (txtReproEditor == null || lstReproSynonyms == null)
                return;

            string fullText = txtReproEditor.Text;
            int caret = txtReproEditor.SelectionStart;

            // стоит ли курсор на букве
            bool caretOnLetter = false;
            if (fullText.Length > 0)
            {
                int idx = caret;
                if (idx > 0 && idx == fullText.Length)
                    idx--;

                if (idx >= 0 && idx < fullText.Length)
                    caretOnLetter = IsWordChar(fullText[idx]);
            }

            // слово под курсором
            var span = GetWordSpanUnderCaret(txtReproEditor);
            string word = string.Empty;

            if (span.End > span.Start)
                word = fullText.Substring(span.Start, span.End - span.Start);

            // базовое слово в "сыром" виде (с регистром как в тексте)
            _currentSynBaseWord = string.IsNullOrWhiteSpace(word) ? null : word;

            // выделяем слово только если курсор реально стоит на букве
            if (caretOnLetter && !string.IsNullOrWhiteSpace(word))
            {
                if (txtReproEditor.SelectionStart != span.Start ||
                    txtReproEditor.SelectionLength != (span.End - span.Start))
                {
                    _adjustingSelection = true;
                    txtReproEditor.SelectionStart = span.Start;
                    txtReproEditor.SelectionLength = span.End - span.Start;
                    _adjustingSelection = false;
                }
            }

            // ВАЖНО: сравниваем БЕЗ IgnoreCase.
            // Если изменился только регистр (Компания -> компания),
            // мы всё равно должны перечитать синонимы.
            if (string.Equals(word, _lastSynonymWord, StringComparison.Ordinal))
                return;

            _lastSynonymWord = word;

            lstReproSynonyms.BeginUpdate();
            lstReproSynonyms.Items.Clear();

            if (!string.IsNullOrWhiteSpace(word))
            {
                // 1-я строка – исходное слово, всегда с галочкой
                lstReproSynonyms.Items.Add(word, true);

                if (_dbfSynonyms != null &&
                    _dbfSynonyms.TryGetValue(word.ToLower(), out var syns))
                {
                    var distinctSyns = syns
                        .Where(s => !string.IsNullOrWhiteSpace(s) &&
                                    !string.Equals(s, word, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // сортировка по частоте
                    if (_synUsage.TryGetValue(word.ToLower(), out var freqDict))
                    {
                        distinctSyns = distinctSyns
                            .OrderByDescending(s =>
                            {
                                if (freqDict.TryGetValue(s.ToLower(), out int c))
                                    return c;
                                return 0;
                            })
                            .ThenBy(s => s, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                    }
                    else
                    {
                        distinctSyns = distinctSyns
                            .OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();
                    }

                    // подстройка регистра отображения под исходное слово
                    bool baseAllUpper = IsAllUpper(word);
                    bool baseCapitalized = IsCapitalized(word);

                    foreach (var s in distinctSyns)
                    {
                        string displaySyn = s; // из DICT.DBF приходят в нижнем регистре

                        if (baseAllUpper)
                            displaySyn = s.ToUpper();
                        else if (baseCapitalized)
                            displaySyn = CapitalizeEachWord(s);

                        bool isFrequent = IsFrequentSynonym(word, s); // тут s без "★"
                        string display = isFrequent ? "★ " + displaySyn : displaySyn;

                        lstReproSynonyms.Items.Add(display, false);
                    }
                }
            }

            lstReproSynonyms.EndUpdate();
        }






        private TextSpan GetWordSpanUnderCaret(RichTextBox box)
        {
            var span = new TextSpan { Start = 0, End = 0 };

            if (box.TextLength == 0)
                return span;

            string text = box.Text;
            int pos = box.SelectionStart;

            if (pos > 0 && pos == text.Length)
                pos--;

            if (pos < 0 || pos >= text.Length)
                return span;

            int start = pos;
            while (start > 0 && IsWordChar(text[start - 1]))
                start--;

            int end = pos;
            while (end < text.Length && IsWordChar(text[end]))
                end++;

            span.Start = start;
            span.End = end;
            return span;
        }




        /// <summary>
        /// Слева – Анализ, справа – Сборка.
        /// Слева и справа большие поля одинакового размера.
        /// </summary>
        private void BuildCollectorTab(TabPage tabPage)
        {
            tabPage.Padding = new Padding(8);

            var rootLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1
            };

            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F)); // Анализ
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F)); // Сборка

            // ====== ЛЕВАЯ ЧАСТЬ: АНАЛИЗ ======
            var leftLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Папка
            leftLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Маска + Анализ
            leftLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // Лог (большое поле)

            // --- строка "Папка" ---
            var folderRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 5)
            };

            var lblFolder = new Label
            {
                Text = "Папка:",
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, 8, 5, 0)
            };

            txtFolderPath = new TextBox
            {
                Width = 400,
                ReadOnly = true,
                Margin = new Padding(0, 5, 5, 0),
                Text = @"M:\PROGONS\"    // путь по умолчанию
            };

            var btnBrowse = new Button
            {
                Text = "Выбрать...",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 11F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(5, 5, 5, 0)
            };
            btnBrowse.Click += BtnBrowse_Click;

            folderRow.Controls.Add(lblFolder);
            folderRow.Controls.Add(txtFolderPath);
            folderRow.Controls.Add(btnBrowse);

            // --- строка "Маска + Анализ (справа)" ---
            var maskRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 5)
            };

            var lblMask = new Label
            {
                Text = "Маска:",
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, 8, 5, 0)
            };

            txtMask = new TextBox
            {
                Width = 250,
                Margin = new Padding(0, 5, 5, 0),
                Text = "Шаблон размножения*.txt"
            };

            var btnAnalyze = new Button
            {
                Text = "Анализ",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(10, 5, 5, 0),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(46, 204, 113)
            };
            btnAnalyze.FlatAppearance.BorderSize = 0;
            btnAnalyze.Click += BtnAnalyze_Click;

            maskRow.Controls.Add(lblMask);
            maskRow.Controls.Add(txtMask);
            maskRow.Controls.Add(btnAnalyze);

            // --- строка "Левое большое поле" ---
            var logRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0)
            };

            txtCollectorStatus = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Width = 460,      // ширина примерно до кнопки "Выбрать"
                Height = 400,     // высота — можно менять под себя
                Margin = new Padding(0, 0, 0, 0)
            };

            logRow.Controls.Add(txtCollectorStatus);

            leftLayout.Controls.Add(folderRow, 0, 0);
            leftLayout.Controls.Add(maskRow, 0, 1);
            leftLayout.Controls.Add(logRow, 0, 2);

            // ====== ПРАВАЯ ЧАСТЬ: СБОРКА ======
            var rightLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5
            };
            rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // Файл базы
            rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // чекбокс
            rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // Сборка
            rightLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));  // правое поле
            rightLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // статус


            // строка "Файл базы"
            var resultRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 5)
            };

            var appendRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 5)
            };

            chkAppendBase = new CheckBox
            {
                Text = "Дополнить базу",
                AutoSize = true,
                Margin = new Padding(10, 0, 0, 0)
            };

            appendRow.Controls.Add(chkAppendBase);


            var lblResultFile = new Label
            {
                Text = "Файл базы:",
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, 8, 5, 0)
            };

            txtResultFilePath = new TextBox
            {
                Width = 350,
                Margin = new Padding(0, 5, 5, 0),
                Text = Path.Combine(Application.StartupPath, "ResultDB.txt")
            };

            var btnBrowseResult = new Button
            {
                Text = "Выбрать...",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 11F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(5, 5, 5, 0)
            };
            btnBrowseResult.Click += BtnBrowseResult_Click;

            resultRow.Controls.Add(lblResultFile);
            resultRow.Controls.Add(txtResultFilePath);
            resultRow.Controls.Add(btnBrowseResult);

            // строка "Сборка"
            var buildRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 5)
            };

            var btnBuild = new Button
            {
                Text = "Сборка",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 5, 5, 0),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(46, 204, 113)
            };
            btnBuild.FlatAppearance.BorderSize = 0;
            btnBuild.Click += BtnBuild_Click; // пока заглушка

            buildRow.Controls.Add(btnBuild);

            // строка "Правое большое поле"
            var buildTextRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 0),
                Padding = new Padding(0)
            };

            txtBuildResult = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Width = 500,
                Height = 400,
                Margin = new Padding(0, 0, 0, 0)
            };

            buildTextRow.Controls.Add(txtBuildResult);

            // строка "Статус сборки"
            lblBuildStatus = new Label
            {
                Text = "",
                AutoSize = true,
                Margin = new Padding(10, 5, 0, 0)
            };

            rightLayout.Controls.Add(resultRow, 0, 0);
            rightLayout.Controls.Add(appendRow, 0, 1);
            rightLayout.Controls.Add(buildRow, 0, 2);
            rightLayout.Controls.Add(buildTextRow, 0, 3);
            rightLayout.Controls.Add(lblBuildStatus, 0, 4);


            // всё на вкладку
            rootLayout.Controls.Add(leftLayout, 0, 0);
            rootLayout.Controls.Add(rightLayout, 1, 0);

            tabPage.Controls.Add(rootLayout);
        }

        /// <summary>
        /// Вкладка "Быстрое размножение":
        ///   сверху: База: [GoldBase] [BigBase] [Размножить] [Скопировать] [Очистить]
        ///   ниже: два больших поля – верхнее исходный текст, нижнее результат.
        /// </summary>
        private void BuildEditorTab(TabPage tabPage)
        {
            tabPage.Padding = new Padding(8);

            var mainLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2
            };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // верхняя панель
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F)); // поля

            // --- верхняя панель ---
            var topRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 5)
            };

            var lblBase = new Label
            {
                Text = "База:",
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Margin = new Padding(0, 8, 5, 0)
            };

            rbGoldBase = new RadioButton
            {
                Text = "GoldBase",
                AutoSize = true,
                Checked = true,
                Margin = new Padding(0, 8, 10, 0)
            };

            rbBigBase = new RadioButton
            {
                Text = "BigBase",
                AutoSize = true,
                Margin = new Padding(0, 8, 20, 0)
            };

            var btnReturn = new Button
            {
                Text = "Вернуть",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(10, 0, 5, 0),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.DimGray
            };
            btnReturn.FlatAppearance.BorderSize = 0;
            btnReturn.Click += BtnEditorReturn_Click;

            var btnMultiply = new Button
            {
                Text = "Размножить",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 5, 5, 0),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(46, 204, 113)
            };
            btnMultiply.FlatAppearance.BorderSize = 0;
            btnMultiply.Click += BtnEditorMultiply_Click;

            var btnCopy = new Button
            {
                Text = "Скопировать",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(5, 5, 5, 0),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.RoyalBlue
            };
            btnCopy.FlatAppearance.BorderSize = 0;
            btnCopy.Click += BtnEditorCopy_Click;

            var btnClear = new Button
            {
                Text = "Очистить",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(5, 5, 5, 0),
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.Red
            };
            btnClear.FlatAppearance.BorderSize = 0;
            btnClear.Click += BtnEditorClear_Click;

            topRow.Controls.Add(lblBase);
            topRow.Controls.Add(rbGoldBase);
            topRow.Controls.Add(rbBigBase);
            topRow.Controls.Add(btnMultiply);
            topRow.Controls.Add(btnCopy);
            topRow.Controls.Add(btnClear);



            // --- два больших поля ---
            // --- текстовые поля + кнопки вариантов ---
            var textLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            textLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            textLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));   // верхнее поле
            textLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // ряд кнопок
            textLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));   // нижнее поле

            // верхнее поле — исходный текст
            txtEditorSource = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Courier New", 14F, FontStyle.Regular, GraphicsUnit.Point),
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            txtEditorSource.KeyDown += TxtEditorSource_KeyDown;      // вставка Ctrl+V
            txtEditorSource.KeyDown += CommonRichTextBox_KeyDown;    // Ctrl+Z

            // нижнее поле — результат
            txtEditorResult = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Courier New", 14F, FontStyle.Regular, GraphicsUnit.Point),
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            txtEditorResult.SelectionChanged += TxtEditorResult_SelectionChanged;
            txtEditorResult.MouseDown += TxtEditorResult_MouseDown;
            txtEditorResult.KeyDown += CommonRichTextBox_KeyDown;


            // средняя панель — кнопки в один ряд
            var middleButtonsPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 5, 0, 5)
            };

            // кнопки Вариант 1 / Вариант 2 / Ротация / Текст / ТЕКСТ
            var btnVariant1 = new Button
            {
                Text = "Вариант 1 ( ,  ; )",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 5, 0)
            };
            btnVariant1.Click += BtnVariant1_Click;

            var btnVariant2 = new Button
            {
                Text = "Вариант 2 (и, или)",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 5, 0)
            };
            btnVariant2.Click += BtnVariant2_Click;

            var btnRotate = new Button
            {
                Text = "Ротация",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 5, 0)
            };
            btnRotate.Click += BtnRotate_Click;

            var btnWordCase = new Button
            {
                Text = "Текст",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 5, 0)
            };
            btnWordCase.Click += BtnWordCase_Click;

            var btnUpperLower = new Button
            {
                Text = "ТЕКСТ",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                Padding = new Padding(8),
                Margin = new Padding(0, 0, 0, 0)
            };
            btnUpperLower.Click += BtnUpperLower_Click;


            // добавляем кнопки в среднюю панель
            middleButtonsPanel.Controls.Add(btnVariant1);
            middleButtonsPanel.Controls.Add(btnVariant2);
            middleButtonsPanel.Controls.Add(btnRotate);
            middleButtonsPanel.Controls.Add(btnWordCase);
            middleButtonsPanel.Controls.Add(btnUpperLower);
            middleButtonsPanel.Controls.Add(btnReturn);   // ← новая строка


            // раскладываем всё по строкам
            textLayout.Controls.Add(txtEditorSource, 0, 0);
            textLayout.Controls.Add(middleButtonsPanel, 0, 1);
            textLayout.Controls.Add(txtEditorResult, 0, 2);

            // добавляем в основную разметку вкладки
            mainLayout.Controls.Add(topRow, 0, 0);
            mainLayout.Controls.Add(textLayout, 0, 1);


            tabPage.Controls.Add(mainLayout);

        }



        // ---------- Обработчики ----------
        private void TxtEditorResult_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;

            HandleRightClickSynonymRemoval(e.Location);
        }
        private void HandleRightClickSynonymRemoval(Point location)
        {
            if (txtEditorResult == null) return;

            string text = txtEditorResult.Text;
            if (string.IsNullOrEmpty(text)) return;

            // позиция символа под курсором
            int index = txtEditorResult.GetCharIndexFromPosition(location);
            if (index < 0 || index >= text.Length) return;

            // ищем ближайшие { слева и } справа
            int open = text.LastIndexOf('{', index);
            int close = text.IndexOf('}', index);

            if (open < 0 || close < 0 || close <= open) return;
            if (index <= open || index >= close) return; // клик не внутри тела {}

            int innerStart = open + 1;
            int innerEnd = close; // позиция '}' (не включительно)

            // режем внутренний текст по | на сегменты
            var segments = new List<(int Start, int End, string Text)>();
            int segStart = innerStart;

            for (int i = innerStart; i <= innerEnd; i++)
            {
                if (i == innerEnd || text[i] == '|')
                {
                    int segEnd = i; // [segStart, segEnd)
                    if (segEnd > segStart)
                    {
                        string seg = text.Substring(segStart, segEnd - segStart);
                        string trimmed = seg.Trim();
                        if (trimmed.Length > 0)
                            segments.Add((segStart, segEnd, trimmed));
                    }
                    segStart = i + 1;
                }
            }

            if (segments.Count <= 1) return;

            // определяем, по какому сегменту кликнули
            int clickedIndex = -1;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                if (index >= s.Start && index < s.End)
                {
                    clickedIndex = i;
                    break;
                }
            }
            if (clickedIndex == -1) return; // кликнули по '|' или пробелу между

            // удаляем выбранный сегмент
            segments.RemoveAt(clickedIndex);

            string replacement;
            if (segments.Count == 1)
            {
                // один вариант – схлопываем конструкцию
                replacement = segments[0].Text;
            }
            else
            {
                string innerNew = string.Join("|", segments.Select(s => s.Text));
                replacement = "{" + innerNew + "}";
            }

            // ЗАМЕНЯЕМ ТОЛЬКО ЭТУ КОНСТРУКЦИЮ, а не весь текст
            txtEditorResult.SuspendLayout();

            int oldSelStart = txtEditorResult.SelectionStart;
            int oldSelLength = txtEditorResult.SelectionLength;

            int oldLen = close - open + 1;
            txtEditorResult.Select(open, oldLen);
            txtEditorResult.SelectedText = replacement;

            // локальная подсветка
            if (replacement.StartsWith("{") && replacement.EndsWith("}"))
            {
                HighlightSingleConstruction(txtEditorResult, open, replacement.Length);
            }
            else
            {
                // конструкция схлопнулась – просто обычный текст
                txtEditorResult.Select(open, replacement.Length);
                txtEditorResult.SelectionColor = Color.Black;
                txtEditorResult.SelectionFont = new Font(txtEditorResult.Font, FontStyle.Regular);
            }

            // возвращаем выделение пользователя
            txtEditorResult.SelectionStart = oldSelStart;
            txtEditorResult.SelectionLength = oldSelLength;

            txtEditorResult.ResumeLayout();
        }


        private void BtnRotate_Click(object sender, EventArgs e)
        {
            ApplyTextTransformation(selected =>
            {
                if (string.IsNullOrWhiteSpace(selected))
                    return selected;

                // сохраняем пробелы по краям
                int left = 0, right = selected.Length - 1;
                while (left <= right && char.IsWhiteSpace(selected[left])) left++;
                while (right >= left && char.IsWhiteSpace(selected[right])) right--;

                if (left > right) return selected;

                string prefix = selected.Substring(0, left);
                string core = selected.Substring(left, right - left + 1);
                string suffix = selected.Substring(right + 1);

                // 1) определяем разделитель в приоритете: " или ", " и ", ", ", "; "
                string delimWithSpaces = null;

                if (core.Contains(" или "))
                    delimWithSpaces = " или ";
                else if (core.Contains(" и "))
                    delimWithSpaces = " и ";
                else if (core.Contains(", "))
                    delimWithSpaces = ", ";
                else if (core.Contains("; "))
                    delimWithSpaces = "; ";
                else
                    return selected; // ничего подходящего не нашли

                var parts = core.Split(new[] { delimWithSpaces }, StringSplitOptions.None);
                if (parts.Length != 2)
                    return selected;

                string first = parts[0].Trim();
                string second = parts[1].Trim();
                if (first.Length == 0 || second.Length == 0)
                    return selected;

                // для ", " и "; " сохраняем пробел после знака
                bool firstStartsUpper = first.Length > 0 && char.IsUpper(first[0]);

                string firstRight = first;
                string secondRight = second;

                if (firstStartsUpper)
                {
                    // Справа: "Семечки, кокосы"
                    secondRight = CapitalizeFirst(second);
                    firstRight = LowerFirst(first);
                }

                string rotated = "{" +
                                 first + delimWithSpaces + second +
                                 "|" +
                                 secondRight + delimWithSpaces + firstRight +
                                 "}";

                return prefix + rotated + suffix;

            });
        }

        // Горячие клавиши на вкладке "Размножение" вызывают те же действия,
        // что и соответствующие кнопки.

        /// <summary>
        /// Вариант 1 ( ,  ; ) — SHIFT+Delete.
        /// </summary>
        private void ApplyCommaSemicolonVariant()
        {
            // Просто вызываем обработчик кнопки Вариант 1
            BtnReproVariant1_Click(this, EventArgs.Empty);
        }

        /// <summary>
        /// Вариант 2 (и, или) — SHIFT+End.
        /// </summary>
        private void ApplyAndOrVariant()
        {
            // Обработчик кнопки Вариант 2
            BtnReproVariant2_Click(this, EventArgs.Empty);
        }

        /// <summary>
        /// Ротация — SHIFT+PageDown.
        /// </summary>
        private void ApplyRotation()
        {
            // Обработчик кнопки "Ротация"
            BtnReproRotate_Click(this, EventArgs.Empty);
        }

        /// <summary>
        /// Быстрая вставка частых синонимов — SHIFT+Enter.
        /// </summary>
        private void InsertFrequentSynonyms()
        {
            // Используем уже существующую логику ApplyFrequentSynonyms
            ApplyFrequentSynonyms();
        }


        // --------- Кнопки на вкладке "Размножение" ---------

        private void BtnReproVariant1_Click(object sender, EventArgs e)
        {
            if (txtReproEditor == null) return;

            string text = txtReproEditor.SelectionLength > 0
                ? txtReproEditor.SelectedText
                : txtReproEditor.Text;

            text = text ?? string.Empty;
            text = text.Trim();
            if (text.Length == 0) return;

            string separator = DetectSeparator(text);

            var elements = text
                .Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            if (elements.Count == 0) return;

            string result = $"[<{separator}> " + string.Join(" | ", elements) + " ]";
            ApplyVariantResultTo(txtReproEditor, result);

            txtReproEditor.Focus();
        }

        private void BtnReproVariant2_Click(object sender, EventArgs e)
        {
            if (txtReproEditor == null) return;

            string text = txtReproEditor.SelectionLength > 0
                ? txtReproEditor.SelectedText
                : txtReproEditor.Text;

            text = text ?? string.Empty;
            text = text.Trim();
            if (text.Length == 0) return;

            string mainSep = DetectSeparator(text);
            string lastSep = null;

            if (text.Contains(" или "))
                lastSep = "или";
            else if (text.Contains(" и "))
                lastSep = "и";

            if (lastSep != null)
            {
                var parts = text.Split(new[] { " " + lastSep + " " }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    var firstPart = parts[0]
                        .Split(new[] { mainSep }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .ToList();

                    var lastPart = parts[1].Trim();

                    if (firstPart.Count > 0 && lastPart.Length > 0)
                    {
                        string result = $"[<{mainSep}> " +
                                        string.Join(" | ", firstPart) +
                                        $" <{lastSep}>| {lastPart} ]";
                        ApplyVariantResultTo(txtReproEditor, result);
                        return;
                    }
                }
            }

            // fallback – как Вариант 1
            var elementsFallback = text
                .Split(new[] { mainSep }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            if (elementsFallback.Count == 0) return;

            string resultFallback = $"[<{mainSep}> " + string.Join(" | ", elementsFallback) + " ]";
            ApplyVariantResultTo(txtReproEditor, resultFallback);

            txtReproEditor.Focus();
        }

        private void BtnReproRotate_Click(object sender, EventArgs e)
        {
            ApplyTextTransformationTo(txtReproEditor, selected =>
            {
                if (string.IsNullOrWhiteSpace(selected))
                    return selected;

                int left = 0, right = selected.Length - 1;
                while (left <= right && char.IsWhiteSpace(selected[left])) left++;
                while (right >= left && char.IsWhiteSpace(selected[right])) right--;

                if (left > right) return selected;

                string prefix = selected.Substring(0, left);
                string core = selected.Substring(left, right - left + 1);
                string suffix = selected.Substring(right + 1);

                string delimWithSpaces = null;

                if (core.Contains(" или "))
                    delimWithSpaces = " или ";
                else if (core.Contains(" и "))
                    delimWithSpaces = " и ";
                else if (core.Contains(", "))
                    delimWithSpaces = ", ";
                else if (core.Contains("; "))
                    delimWithSpaces = "; ";
                else
                    return selected;

                var parts = core.Split(new[] { delimWithSpaces }, StringSplitOptions.None);
                if (parts.Length != 2)
                    return selected;

                string first = parts[0].Trim();
                string second = parts[1].Trim();
                if (first.Length == 0 || second.Length == 0)
                    return selected;

                bool firstStartsUpper = first.Length > 0 && char.IsUpper(first[0]);

                string firstRight = first;
                string secondRight = second;

                if (firstStartsUpper)
                {
                    secondRight = CapitalizeFirst(second);
                    firstRight = LowerFirst(first);
                }

                string rotated = "{" +
                                 first + delimWithSpaces + second +
                                 "|" +
                                 secondRight + delimWithSpaces + firstRight +
                                 "}";

                return prefix + rotated + suffix;

            });

            txtReproEditor.Focus();
        }

        private void BtnReproWordCase_Click(object sender, EventArgs e)
        {
            ApplyTextTransformationTo(txtReproEditor, selected =>
            {
                if (string.IsNullOrEmpty(selected))
                    return selected;

                int left = 0, right = selected.Length - 1;
                while (left <= right && char.IsWhiteSpace(selected[left])) left++;
                while (right >= left && char.IsWhiteSpace(selected[right])) right--;

                if (left > right) return selected;

                string prefix = selected.Substring(0, left);
                string core = selected.Substring(left, right - left + 1);
                string suffix = selected.Substring(right + 1);

                string capitalized = CapitalizeEachWord(core.ToLower());
                string newCore = (core == capitalized) ? core.ToLower() : capitalized;

                return prefix + newCore + suffix;
            });

            txtReproEditor.Focus();
        }

        private void BtnReproUpperLower_Click(object sender, EventArgs e)
        {
            ApplyTextTransformationTo(txtReproEditor, selected =>
            {
                if (string.IsNullOrEmpty(selected))
                    return selected;

                int left = 0, right = selected.Length - 1;
                while (left <= right && char.IsWhiteSpace(selected[left])) left++;
                while (right >= left && char.IsWhiteSpace(selected[right])) right--;

                if (left > right) return selected;

                string prefix = selected.Substring(0, left);
                string core = selected.Substring(left, right - left + 1);
                string suffix = selected.Substring(right + 1);

                var letters = core.Where(char.IsLetter).ToArray();
                if (letters.Length == 0) return selected;

                bool allUpper = letters.All(ch => char.IsUpper(ch));
                string newCore = allUpper ? core.ToLower() : core.ToUpper();

                return prefix + newCore + suffix;
            });

            txtReproEditor.Focus();
        }

        private void BtnReproTemplateStar_Click(object sender, EventArgs e)
        {
            if (txtReproEditor == null)
                return;

            // Должно быть выделено слово / словосочетание
            if (txtReproEditor.SelectionLength <= 0)
                return;

            int selStart = txtReproEditor.SelectionStart;
            string selected = txtReproEditor.SelectedText;

            // {слово|} – без звёздочки
            string insertion = "{" + selected + "|}";

            // заменяем выделение конструкцией
            txtReproEditor.SelectedText = insertion;

            // ставим курсор сразу после '|'
            int caretPos = selStart + 1 + selected.Length + 1; // { + слово + |
            if (caretPos < 0) caretPos = 0;
            if (caretPos > txtReproEditor.TextLength) caretPos = txtReproEditor.TextLength;

            txtReproEditor.SelectionStart = caretPos;
            txtReproEditor.SelectionLength = 0;

            txtReproEditor.Focus();
        }

        private void BtnReproTemplateDuplicate_Click(object sender, EventArgs e)
        {
            if (txtReproEditor == null)
                return;

            if (txtReproEditor.SelectionLength <= 0)
                return;

            int selStart = txtReproEditor.SelectionStart;
            string selected = txtReproEditor.SelectedText;

            // {слово|слово}
            string insertion = "{" + selected + "|" + selected + "}";

            txtReproEditor.SelectedText = insertion;

            // курсор ставим сразу после конструкции
            int caretPos = selStart + insertion.Length;
            if (caretPos > txtReproEditor.TextLength)
                caretPos = txtReproEditor.TextLength;

            txtReproEditor.SelectionStart = caretPos;
            txtReproEditor.SelectionLength = 0;

            txtReproEditor.Focus();
        }



        private string NormalizeSpaces(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            var sb = new StringBuilder(text.Length);
            bool prevSpace = false;

            foreach (char c in text)
            {
                if (c == ' ')
                {
                    if (!prevSpace)
                    {
                        sb.Append(' ');
                        prevSpace = true;
                    }
                }
                else
                {
                    sb.Append(c);
                    prevSpace = false;
                }
            }

            return sb.ToString();
        }

        private void TxtEditorResult_SelectionChanged(object sender, EventArgs e)
        {
            if (_adjustingSelection) return;              // чтобы не зациклиться
            if (txtEditorResult == null) return;
            if (!txtEditorResult.Focused) return;         // игнорируем программные Select()

            int start = txtEditorResult.SelectionStart;
            int length = txtEditorResult.SelectionLength;
            if (length <= 0) return;

            string text = txtEditorResult.Text;
            int end = start + length;
            if (end > text.Length) end = text.Length;

            int newLength = length;

            // режем пробелы справа
            while (newLength > 0 && end > start && char.IsWhiteSpace(text[end - 1]))
            {
                newLength--;
                end--;
            }

            if (newLength != length)
            {
                _adjustingSelection = true;
                txtEditorResult.SelectionStart = start;
                txtEditorResult.SelectionLength = newLength;
                _adjustingSelection = false;
            }
        }

        private void ApplyTextTransformation(Func<string, string> transformer)
        {
            ApplyTextTransformationTo(txtEditorResult, transformer);
        }


        private void ApplyTextTransformationTo(RichTextBox box, Func<string, string> transformer)
        {
            if (box == null) return;
            if (box.SelectionLength == 0) return;

            int start = box.SelectionStart;
            string selected = box.SelectedText;

            string transformed = transformer(selected) ?? selected;
            transformed = NormalizeSpaces(transformed);

            // Вставляем преобразованный текст
            box.SelectedText = transformed;

            // ----- Вкладка "Размножение" -----
            if (box == txtReproEditor)
            {
                string allText = box.Text;

                // ставим каретку сразу после вставленного фрагмента
                int caret = start + transformed.Length;
                if (caret < 0) caret = 0;
                if (caret > allText.Length) caret = allText.Length;

                // прыгаем на СЛЕДУЮЩЕЕ слово после конструкции
                int nextPos = MoveCaretWordRight(allText, caret);

                _adjustingSelection = true;
                box.SelectionStart = nextPos;
                box.SelectionLength = 0;
                _adjustingSelection = false;

                // обновляем синонимы под новым словом
                UpdateReproSynonymsForCaret();
                // подсветку конструкций в txtReproEditor оставляем таймеру (ReproHighlightTimer_Tick)
            }
            // ----- Все остальные редакторы (Быстрое размножение) -----
            else
            {
                // старое поведение – выделяем преобразованный участок и подсвечиваем
                _adjustingSelection = true;
                box.SelectionStart = start;
                box.SelectionLength = transformed.Length;
                _adjustingSelection = false;

                HighlightSynConstructions(box);
            }
        }




        private void BtnWordCase_Click(object sender, EventArgs e)
        {
            ApplyTextTransformation(selected =>
            {
                if (string.IsNullOrEmpty(selected))
                    return selected;

                // Сохраняем ведущие и хвостовые пробелы как есть
                int left = 0, right = selected.Length - 1;
                while (left <= right && char.IsWhiteSpace(selected[left])) left++;
                while (right >= left && char.IsWhiteSpace(selected[right])) right--;

                if (left > right) return selected;

                string prefix = selected.Substring(0, left);
                string core = selected.Substring(left, right - left + 1);
                string suffix = selected.Substring(right + 1);

                // Берём вариант "каждое слово с заглавной"
                string capitalized = CapitalizeEachWord(core.ToLower());
                string newCore;

                // Если уже так – считаем, что надо вернуть в нижний регистр
                if (core == capitalized)
                    newCore = core.ToLower();
                else
                    newCore = capitalized;

                return prefix + newCore + suffix;
            });
        }

        private void BtnUpperLower_Click(object sender, EventArgs e)
        {
            ApplyTextTransformation(selected =>
            {
                if (string.IsNullOrEmpty(selected))
                    return selected;

                // Сохраняем ведущие/хвостовые пробелы
                int left = 0, right = selected.Length - 1;
                while (left <= right && char.IsWhiteSpace(selected[left])) left++;
                while (right >= left && char.IsWhiteSpace(selected[right])) right--;

                if (left > right) return selected;

                string prefix = selected.Substring(0, left);
                string core = selected.Substring(left, right - left + 1);
                string suffix = selected.Substring(right + 1);

                // Берём только буквы, чтобы понять "всё ли уже UPPER"
                var letters = core.Where(char.IsLetter).ToArray();
                if (letters.Length == 0) return selected;

                bool allUpper = letters.All(ch => char.IsUpper(ch));

                string newCore = allUpper ? core.ToLower() : core.ToUpper();

                return prefix + newCore + suffix;
            });
        }

        private void CommonRichTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            // здесь больше ничего не делаем – даём RichTextBox сам обработать Ctrl+Z
        }




        private void TxtEditorSource_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+V
            if (e.Control && e.KeyCode == Keys.V)
            {
                string clipText = Clipboard.GetText();
                if (!string.IsNullOrEmpty(clipText))
                {
                    // выделяем всё и заменяем на текст из буфера
                    txtEditorSource.SelectAll();
                    txtEditorSource.SelectedText = clipText;
                }

                // отменяем стандартную вставку, чтобы не вставилось второй раз
                e.SuppressKeyPress = true;
            }
        }


        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Выберите папку с файлами шаблонов";
                if (Directory.Exists(txtFolderPath.Text))
                    dlg.SelectedPath = txtFolderPath.Text;

                if (dlg.ShowDialog(this) == DialogResult.OK)
                    txtFolderPath.Text = dlg.SelectedPath;
            }
        }

        private void BtnBrowseResult_Click(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Выберите файл базы";
                dlg.Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*";
                dlg.FileName = "ResultDB.txt";
                dlg.InitialDirectory = Application.StartupPath;

                if (dlg.ShowDialog(this) == DialogResult.OK)
                    txtResultFilePath.Text = dlg.FileName;
            }
        }



        private void BtnAnalyze_Click(object sender, EventArgs e)
        {
            RunAnalyze();
        }

        private void BtnBuild_Click(object sender, EventArgs e)
        {
            BuildSynonymBase();
        }

        private void BtnEditorClear_Click(object sender, EventArgs e)
        {
            if (txtEditorSource != null) txtEditorSource.Clear();
            if (txtEditorResult != null) txtEditorResult.Clear();
            txtEditorSource?.Focus();
        }

        private void BtnEditorReturn_Click(object sender, EventArgs e)
        {
            if (txtEditorResult == null) return;

            string fullText = txtEditorResult.Text;
            if (string.IsNullOrEmpty(fullText)) return;

            // позиция для поиска скобок — берём середину выделения, если есть,
            // иначе текущий курсор
            int caret = txtEditorResult.SelectionStart;
            if (txtEditorResult.SelectionLength > 0)
            {
                caret = txtEditorResult.SelectionStart + txtEditorResult.SelectionLength / 2;
            }

            if (caret < 0 || caret > fullText.Length) return;

            // ищем ближайшие '{' слева и '}' справа
            int open = fullText.LastIndexOf('{', caret);
            int close = fullText.IndexOf('}', caret);

            if (open < 0 || close < 0 || close <= open)
                return;

            string inner = fullText.Substring(open + 1, close - open - 1);
            if (string.IsNullOrWhiteSpace(inner))
                return;

            // первый вариант до '|'
            int pipeIndex = inner.IndexOf('|');
            string first = pipeIndex >= 0 ? inner.Substring(0, pipeIndex) : inner;
            first = first.Trim();

            var sb = new StringBuilder();
            sb.Append(fullText, 0, open);                                  // текст до {
            sb.Append(first);                                              // первый вариант
            sb.Append(fullText, close + 1, fullText.Length - close - 1);   // текст после }

            txtEditorResult.Text = sb.ToString();
            txtEditorResult.SelectionStart = open + first.Length;
            txtEditorResult.SelectionLength = 0;

            // обновляем подсветку ({} уже нет, но на всякий)
            HighlightSynConstructions(txtEditorResult);
        }


        private void BtnEditorMultiply_Click(object sender, EventArgs e)
        {
            if (txtEditorSource == null || txtEditorResult == null) return;

            string basePath;

            if (rbGoldBase != null && rbGoldBase.Checked)
            {
                basePath = Path.Combine(Application.StartupPath, "GoldBase.txt");
            }
            else
            {
                // пока просто BigBase.txt рядом с программой;
                // позже подключим DBF
                basePath = Path.Combine(Application.StartupPath, "BigBase.txt");
            }

            if (!File.Exists(basePath))
            {
                MessageBox.Show(this,
                    "Файл базы не найден:\r\n" + basePath,
                    "База не найдена",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            var dict = LoadSynonymBase(basePath);
            if (dict.Count == 0)
            {
                MessageBox.Show(this,
                    "В выбранной базе нет валидных записей.",
                    "Пустая база",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // Подгружаем BadWord.txt и Exceptions.txt
            string badPath = Path.Combine(Application.StartupPath, "BadWord.txt");
            string excPath = Path.Combine(Application.StartupPath, "Exceptions.txt");

            var badPhrases = LoadBadPhrases(badPath);
            var exceptions = LoadExceptions(excPath);

            string sourceText = txtEditorSource.Text;
            string result = ExpandTextWithSynonyms(sourceText, dict, exceptions, badPhrases);
            result = NormalizeSpaces(result);

            txtEditorResult.Text = result;
            HighlightSynConstructions(txtEditorResult);
        }

        private void BtnVariant1_Click(object sender, EventArgs e)
        {
            if (txtEditorResult == null) return;

            string text = txtEditorResult.SelectionLength > 0
                ? txtEditorResult.SelectedText
                : txtEditorResult.Text;

            text = text ?? string.Empty;
            text = text.Trim();
            if (text.Length == 0) return;

            string separator = DetectSeparator(text);

            var elements = text
                .Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            if (elements.Count == 0) return;

            string result = $"[<{separator}> " + string.Join(" | ", elements) + " ]";
            ApplyVariantResult(result);
        }

        private void BtnVariant2_Click(object sender, EventArgs e)
        {
            if (txtEditorResult == null) return;

            string text = txtEditorResult.SelectionLength > 0
                ? txtEditorResult.SelectedText
                : txtEditorResult.Text;

            text = text ?? string.Empty;
            text = text.Trim();
            if (text.Length == 0) return;

            string mainSep = DetectSeparator(text);
            string lastSep = null;

            if (text.Contains(" или "))
                lastSep = "или";
            else if (text.Contains(" и "))
                lastSep = "и";

            if (lastSep != null)
            {
                var parts = text.Split(new[] { " " + lastSep + " " }, StringSplitOptions.None);
                if (parts.Length == 2)
                {
                    var firstPart = parts[0]
                        .Split(new[] { mainSep }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .Where(x => x.Length > 0)
                        .ToList();

                    var lastPart = parts[1].Trim();

                    if (firstPart.Count > 0 && lastPart.Length > 0)
                    {
                        string result = $"[<{mainSep}> " + string.Join(" | ", firstPart) + $" <{lastSep}>| {lastPart} ]";
                        ApplyVariantResult(result);
                        return;
                    }
                }
            }

            // если не нашли "и/или" – fallback к варианту 1
            var elementsFallback = text
                .Split(new[] { mainSep }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            if (elementsFallback.Count == 0) return;

            string resultFallback = $"[<{mainSep}> " + string.Join(" | ", elementsFallback) + " ]";
            ApplyVariantResult(resultFallback);
        }

        private void ApplyVariantResult(string result)
        {
            if (txtEditorResult == null) return;

            result = NormalizeSpaces(result);

            if (txtEditorResult.SelectionLength > 0)
            {
                int selStart = txtEditorResult.SelectionStart;
                txtEditorResult.SelectedText = result;
                txtEditorResult.SelectionStart = selStart;
                txtEditorResult.SelectionLength = result.Length;
            }
            else
            {
                txtEditorResult.Text = result;
                txtEditorResult.SelectionStart = 0;
                txtEditorResult.SelectionLength = result.Length;
            }

            HighlightSynConstructions(txtEditorResult);
        }

        void ApplyVariantResultTo(RichTextBox box, string result)
        {
            if (box == null) return;

            result = NormalizeSpaces(result);

            // позиция, с которой вставляем результат
            int insertStart;

            if (box.SelectionLength > 0)
            {
                insertStart = box.SelectionStart;
                box.SelectedText = result;
            }
            else
            {
                insertStart = 0;
                box.Text = result;
            }

            // ----- Поведение для вкладки "Размножение" -----
            if (box == txtReproEditor)
            {
                string allText = box.Text;

                // ставим каретку сразу после вставленной конструкции
                int caret = insertStart + result.Length;
                if (caret < 0) caret = 0;
                if (caret > allText.Length) caret = allText.Length;

                // прыжок на СЛЕДУЮЩЕЕ слово после конструкции
                int nextPos = MoveCaretWordRight(allText, caret);

                _adjustingSelection = true;
                box.SelectionStart = nextPos;
                box.SelectionLength = 0;
                _adjustingSelection = false;

                // обновляем список синонимов и подсветку
                UpdateReproSynonymsForCaret();
                HighlightSynConstructions(box);
            }
            // ----- Поведение для остальных редакторов (Быстрое размножение) -----
            else
            {
                // как и раньше: выделяем весь результат
                _adjustingSelection = true;
                box.SelectionStart = insertStart;
                box.SelectionLength = result.Length;
                _adjustingSelection = false;

                HighlightSynConstructions(box);
            }
        }




        private string DetectSeparator(string text)
        {
            if (text.Contains(";"))
                return ";";
            return ",";
        }


        private void BtnEditorCopy_Click(object sender, EventArgs e)
        {
            if (txtEditorResult == null) return;

            string text = txtEditorResult.Text;
            if (string.IsNullOrEmpty(text)) return;

            Clipboard.SetText(text);
        }

        /// <summary>
        /// Подсветка конструкций {…|…}: скобки и '|' красные, текст внутри – синий.
        /// </summary>
        private void HighlightSynConstructions(RichTextBox box)
        {
            if (box == null || box.TextLength == 0) return;

            int selStart = box.SelectionStart;
            int selLength = box.SelectionLength;

            SetRedraw(box, false);

            string text = box.Text;

            using (var regularFont = new Font(box.Font, FontStyle.Regular))
            using (var boldFont = new Font(box.Font, FontStyle.Bold))
            {
                // сначала всё — чёрным обычным шрифтом
                box.Select(0, text.Length);
                box.SelectionColor = Color.Black;
                box.SelectionFont = regularFont;

                var regex = new Regex(@"\{[^}]+\}");

                foreach (Match m in regex.Matches(text))
                {
                    int start = m.Index;
                    int length = m.Length;
                    if (length < 2) continue;

                    int innerStart = start + 1;
                    int innerLen = length - 2;
                    if (innerLen <= 0) continue;

                    int innerEnd = innerStart + innerLen;
                    string inner = text.Substring(innerStart, innerLen);

                    // первый вариант до первого '|' считаем оригиналом
                    int pipeIndex = inner.IndexOf('|');
                    int origLen = pipeIndex >= 0 ? pipeIndex : innerLen;
                    int origStart = innerStart;

                    // 1) вся внутренняя часть {} — синяя, обычный шрифт
                    box.Select(innerStart, innerLen);
                    box.SelectionColor = Color.Blue;
                    box.SelectionFont = regularFont;

                    // 2) фигурные скобки — красные и жирные
                    box.Select(start, 1);
                    box.SelectionColor = Color.Red;
                    box.SelectionFont = boldFont;

                    box.Select(start + length - 1, 1);
                    box.SelectionColor = Color.Red;
                    box.SelectionFont = boldFont;


                    // 3) символы '|' — красные
                    for (int i = innerStart; i < innerEnd; i++)
                    {
                        if (text[i] == '|')
                        {
                            box.Select(i, 1);
                            box.SelectionColor = Color.Red;
                        }
                    }

                    // 4) оригинальный вариант — синий ЖИРНЫЙ
                    if (origLen > 0)
                    {
                        box.Select(origStart, origLen);
                        box.SelectionColor = Color.Blue;
                        box.SelectionFont = boldFont;
                    }
                }

                // 5) " и " и " или " — зелёным
                var connectors = new[] { " и ", " или " };
                foreach (var pat in connectors)
                {
                    int index = 0;
                    while ((index = text.IndexOf(pat, index, StringComparison.OrdinalIgnoreCase)) >= 0)
                    {
                        box.Select(index, pat.Length);
                        box.SelectionColor = Color.Green;
                        box.SelectionFont = regularFont; // обычный шрифт
                        index += pat.Length;
                    }
                }

                // 6) квадратные скобки [ ] — фиолетовым
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '[' || c == ']')
                    {
                        box.Select(i, 1);
                        box.SelectionColor = Color.Magenta;
                        box.SelectionFont = boldFont;
                    }
                }

                // 7) конструкции в угловых скобках <...> — красным
                var angleRegex = new Regex(@"<[^>]+>");
                foreach (Match mAngle in angleRegex.Matches(text))
                {
                    box.Select(mAngle.Index, mAngle.Length);
                    box.SelectionColor = Color.Red;
                    box.SelectionFont = boldFont;
                }

                // 8) все символы '|' — красные (внутри {} и внутри [])
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] == '|')
                    {
                        box.Select(i, 1);
                        box.SelectionColor = Color.Red;
                        box.SelectionFont = regularFont;
                    }
                }

            }

            // возвращаем выделение пользователя
            box.Select(selStart, selLength);

            SetRedraw(box, true);
        }


        /// <summary>
        /// Подсветка одной конкретной конструкции { ... } в заданном диапазоне.
        /// Используется при удалении варианта правым кликом, чтобы не перекрашивать весь текст.
        /// </summary>
        private void HighlightSingleConstruction(RichTextBox box, int start, int length)
        {
            if (box == null || length <= 0) return;

            string text = box.Text;
            if (start < 0 || start + length > text.Length) return;
            if (length < 2 || text[start] != '{' || text[start + length - 1] != '}') return;

            int selStart = box.SelectionStart;
            int selLength = box.SelectionLength;

            int innerStart = start + 1;
            int innerLen = length - 2;
            if (innerLen <= 0) return;

            int innerEnd = innerStart + innerLen;
            string inner = text.Substring(innerStart, innerLen);

            SetRedraw(box, false);   // выключаем перерисовку

            using (var regularFont = new Font(box.Font, FontStyle.Regular))
            using (var boldFont = new Font(box.Font, FontStyle.Bold))
            {
                // сбрасываем формат в этом диапазоне
                box.Select(start, length);
                box.SelectionColor = Color.Black;
                box.SelectionFont = regularFont;

                // скобки — красные
                box.Select(start, 1);
                box.SelectionColor = Color.Red;
                box.Select(start + length - 1, 1);
                box.SelectionColor = Color.Red;

                // вся внутренняя часть — синяя, обычный шрифт
                box.Select(innerStart, innerLen);
                box.SelectionColor = Color.Blue;
                box.SelectionFont = regularFont;

                // разделители '|' — красные
                for (int i = innerStart; i < innerEnd; i++)
                {
                    if (text[i] == '|')
                    {
                        box.Select(i, 1);
                        box.SelectionColor = Color.Red;
                    }
                }

                // первый вариант — "оригинал" — синяя и жирная
                int pipeIndex = inner.IndexOf('|');
                int origLen = pipeIndex >= 0 ? pipeIndex : innerLen;
                if (origLen > 0)
                {
                    box.Select(innerStart, origLen);
                    box.SelectionColor = Color.Blue;
                    box.SelectionFont = boldFont;
                }

                // " и " / " или " внутри этой конструкции — зелёные
                var connectors = new[] { " и ", " или " };
                foreach (var pat in connectors)
                {
                    int searchStart = innerStart;
                    int rangeEnd = start + length;
                    while (searchStart < rangeEnd)
                    {
                        int idx = text.IndexOf(pat, searchStart,
                            rangeEnd - searchStart, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) break;

                        box.Select(idx, pat.Length);
                        box.SelectionColor = Color.Green;
                        box.SelectionFont = regularFont;

                        searchStart = idx + pat.Length;
                    }
                }
            }

            box.Select(selStart, selLength);
            SetRedraw(box, true);    // включаем и перерисовываем один раз
        }





        /// <summary>
        /// Анализ: поиск файлов, чтение в UTF-8/1251, фильтр по русскому тексту,
        /// запись в ResultDB.txt (UTF-8, в папке программы) и удаление пустых строк.
        /// </summary>
        private void RunAnalyze()
        {
            var folder = txtFolderPath.Text.Trim();
            var mask = txtMask.Text.Trim();

            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                MessageBox.Show(this, "Укажите существующую папку.", "Ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(mask))
            {
                MessageBox.Show(this, "Укажите маску файла.", "Ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string resultPath = Path.Combine(Application.StartupPath, "ResultDB.txt");
            txtResultFilePath.Text = resultPath;

            try
            {
                txtCollectorStatus.Clear();
                txtCollectorStatus.AppendText("Поиск файлов...\r\n");
                this.Cursor = Cursors.WaitCursor;
                Application.DoEvents();

                string[] files = Directory.GetFiles(folder, mask, SearchOption.AllDirectories);
                int totalFiles = files.Length;
                int writtenFiles = 0;

                txtCollectorStatus.AppendText($"Найдено файлов по маске: {totalFiles}\r\n");
                txtCollectorStatus.AppendText("Обработка файлов...\r\n");
                ScrollCollectorLog();

                using (var writer = new StreamWriter(
                    resultPath, false,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                {
                    int processed = 0;
                    foreach (var file in files)
                    {
                        processed++;

                        string text = ReadFileAsUtf8(file);
                        if (string.IsNullOrWhiteSpace(text))
                            continue;

                        string snippet = text.Length > 2000 ? text.Substring(0, 2000) : text;
                        if (!IsRussianText(snippet))
                            continue;

                        text = text.Replace("\r\n", "\n").Replace("\r", "\n");

                        writer.WriteLine(text.TrimEnd());
                        writer.WriteLine();

                        writtenFiles++;

                        if (processed % 50 == 0 || processed == totalFiles)
                        {
                            txtCollectorStatus.AppendText(
                                $"Обработано: {processed}/{totalFiles}, записано: {writtenFiles}\r\n");
                            ScrollCollectorLog();
                            Application.DoEvents();
                        }
                    }
                }

                var allLines = File.ReadAllLines(resultPath, new UTF8Encoding(false));
                var nonEmptyLines = allLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                File.WriteAllLines(resultPath, nonEmptyLines, new UTF8Encoding(false));

                txtCollectorStatus.AppendText(
                    $"Готово. Найдено файлов: {totalFiles}, записано: {writtenFiles}.\r\nФайл: {resultPath}\r\n");
                ScrollCollectorLog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Ошибка при анализе: " + ex.Message, "Ошибка",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                txtCollectorStatus.AppendText("Ошибка.\r\n");
                ScrollCollectorLog();
            }
            finally
            {
                this.Cursor = Cursors.Default;
            }
        }

        /// <summary>
        /// Сборка базы синонимов из ResultDB.txt в GoldBase.txt.
        /// Берём только «листовые» конструкции {a|b|c} без вложенных { } внутри.
        /// Отбрасываем:
        /// - варианты длиннее 4 слов;
        /// - варианты с запятыми/точкой с запятой;
        /// - варианты с точкой (с.|село и т.п.);
        /// - варианты с кавычками;
        /// - варианты, содержащие слово «это»;
        /// - варианты, содержащие слова «и» или «или»;
        /// - варианты с адресами сайтов (something.tld);
        /// - варианты, содержащие круглые скобки ( ... );
        /// - наборы, где хотя бы один вариант не проходит эти проверки.
        /// Уникальность: внутри набора сортируем и приводим к нижнему регистру.
        /// Если есть набор-надмножество (большой|огромный|широкий) и его подмножество
        /// (большой|огромный) — оставляем только надмножество.
        /// Чекбокс «Дополнить базу» добавляет к существующему GoldBase.txt, не теряя старые строки.
        /// </summary>
        private void BuildSynonymBase()
        {
            string resultPath = Path.Combine(Application.StartupPath, "ResultDB.txt");
            string goldPath = Path.Combine(Application.StartupPath, "GoldBase.txt");

            if (!File.Exists(resultPath))
            {
                MessageBox.Show(this,
                    "Не найден файл ResultDB.txt в папке программы.\r\nСначала выполните Анализ.",
                    "Файл не найден",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            bool append = chkAppendBase != null && chkAppendBase.Checked;

            try
            {
                lblBuildStatus.Text = "Сборка базы синонимов...";
                txtBuildResult.Clear();
                Cursor = Cursors.WaitCursor;
                Application.DoEvents();

                // множество наборов синонимов (строка вида "слово1|слово2|слово3")
                var synonymSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // если нужно дополнить базу — подгружаем существующий GoldBase.txt
                if (append && File.Exists(goldPath))
                {
                    foreach (var line in File.ReadAllLines(goldPath, new UTF8Encoding(false)))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0)
                            synonymSets.Add(trimmed);
                    }
                }

                string text = File.ReadAllText(resultPath, new UTF8Encoding(false));

                int totalBraces = 0;
                int acceptedSets = 0;

                var stack = new Stack<int>();

                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];

                    if (c == '{')
                    {
                        stack.Push(i);
                    }
                    else if (c == '}' && stack.Count > 0)
                    {
                        int start = stack.Pop();
                        int innerStart = start + 1;
                        int len = i - innerStart;
                        if (len <= 0) continue;

                        string inside = text.Substring(innerStart, len);

                        // берём только «листовые» блоки: внутри не должно быть ещё { или }
                        if (inside.IndexOf('{') >= 0 || inside.IndexOf('}') >= 0)
                            continue;

                        if (!inside.Contains("|"))
                            continue; // не перебор

                        totalBraces++;

                        var variantsRaw = inside.Split('|');
                        if (variantsRaw.Length < 2)
                            continue;

                        var cleanedVariants = new List<string>();
                        bool badSet = false;

                        foreach (var raw in variantsRaw)
                        {
                            // сразу отсекаем, если в исходном варианте есть точка
                            if (raw.IndexOf('.') >= 0)
                            {
                                badSet = true;
                                break;
                            }

                            string cleaned = NormalizeVariant(raw);
                            if (string.IsNullOrEmpty(cleaned))
                            {
                                badSet = true;
                                break;
                            }

                            // не должно быть круглых скобок
                            if (cleaned.IndexOf('(') >= 0 || cleaned.IndexOf(')') >= 0)
                            {
                                badSet = true;
                                break;
                            }

                            // длина в словах
                            int wordCount = CountWords(cleaned);
                            if (wordCount == 0 || wordCount > 4)
                            {
                                badSet = true;
                                break;
                            }

                            // не должно быть запятых / точек с запятой
                            if (cleaned.IndexOf(',') >= 0 || cleaned.IndexOf(';') >= 0)
                            {
                                badSet = true;
                                break;
                            }

                            // не должно быть кавычек
                            if (cleaned.IndexOf('"') >= 0 ||
                                cleaned.IndexOf('«') >= 0 ||
                                cleaned.IndexOf('»') >= 0 ||
                                cleaned.IndexOf('\'') >= 0)
                            {
                                badSet = true;
                                break;
                            }

                            string lower = cleaned.ToLower();

                            // не должно быть слова "это"
                            var words = SplitWords(lower);
                            if (words.Contains("это"))
                            {
                                badSet = true;
                                break;
                            }

                            // не должно быть "и" / "или" между словами
                            if (words.Contains("и") || words.Contains("или"))
                            {
                                badSet = true;
                                break;
                            }

                            // не должно быть адресов сайтов вида something.tld
                            if (Regex.IsMatch(cleaned, @"[A-Za-z0-9\-]+\.[A-Za-z0-9\-]{2,}"))
                            {
                                badSet = true;
                                break;
                            }

                            cleanedVariants.Add(cleaned);
                        }

                        if (badSet)
                            continue;

                        // приведение к нижнему регистру, удаление дублей внутри набора
                        var lowered = cleanedVariants
                            .Select(v => v.ToLower())
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (lowered.Count < 2)
                            continue;

                        lowered.Sort(StringComparer.Ordinal);  // чтобы набор был в одном виде

                        var newSetWords = new HashSet<string>(lowered, StringComparer.Ordinal);
                        var toRemove = new List<string>();
                        bool skipNew = false;

                        // Логика "оставить самую длинную конструкцию"
                        foreach (var existing in synonymSets)
                        {
                            var existingWords = existing.Split('|');
                            var existingSet = new HashSet<string>(existingWords, StringComparer.Ordinal);

                            bool existingIsSubsetOfNew = existingSet.All(w => newSetWords.Contains(w));
                            bool newIsSubsetOfExisting = newSetWords.All(w => existingSet.Contains(w));

                            if (newIsSubsetOfExisting)
                            {
                                // новый набор не шире существующего — игнорируем его
                                skipNew = true;
                                break;
                            }

                            if (existingIsSubsetOfNew)
                            {
                                // существующий короче — удалим его,
                                //а новый (более длинный) потом добавим
                                toRemove.Add(existing);
                            }
                        }

                        if (skipNew)
                            continue;

                        foreach (var rem in toRemove)
                            synonymSets.Remove(rem);

                        string key = string.Join("|", lowered);
                        if (synonymSets.Add(key))
                            acceptedSets++;
                    }
                }

                // сохраняем итоговую базу (старые + новые, без дублей)
                using (var writer = new StreamWriter(goldPath, false, new UTF8Encoding(false)))
                {
                    foreach (var set in synonymSets.OrderBy(s => s))
                    {
                        writer.WriteLine(set);
                    }
                }

                // превью в правом поле
                var previewLines = synonymSets
                    .OrderBy(s => s)
                    .Take(200)
                    .ToArray();

                txtBuildResult.Lines = previewLines;

                lblBuildStatus.Text =
                    $"Готово. Найдено конструкций {{}}: {totalBraces}, " +
                    $"уникальных наборов: {acceptedSets}. Файл: {goldPath}";
            }
            catch (Exception ex)
            {
                lblBuildStatus.Text = "Ошибка при сборке.";
                MessageBox.Show(this, "Ошибка при сборке базы: " + ex.Message,
                    "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        /// <summary>
        /// Загружает текстовую базу синонимов (GoldBase / BigBase).
        /// Формат строки: слово1|слово2|слово3 (обычно в нижнем регистре).
        /// На каждое слово строится ссылка на весь набор.
        /// </summary>
        private Dictionary<string, string[]> LoadSynonymBase(string path)
        {
            var dict = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in File.ReadAllLines(path, new UTF8Encoding(false)))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var parts = trimmed
                    .Split('|')
                    .Select(p => p.Trim())
                    .Where(p => p.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (parts.Length < 2)
                    continue;

                // база хранит слова в нижнем регистре, но на всякий случай понизим
                var lowered = parts.Select(p => p.ToLower()).ToArray();

                foreach (var w in lowered)
                {
                    if (!dict.ContainsKey(w))
                        dict[w] = lowered;
                }
            }

            return dict;
        }

        /// <summary>
        /// Загружает список "плохих" фраз, которые нельзя синонимизировать.
        /// Каждая строка в файле BadWord.txt – отдельная фраза.
        /// </summary>
        private List<string> LoadBadPhrases(string path)
        {
            var list = new List<string>();
            if (!File.Exists(path)) return list;

            foreach (var line in File.ReadAllLines(path, new UTF8Encoding(false)))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    list.Add(trimmed);
            }

            return list;
        }

        /// <summary>
        /// Загружает исключения вида "рф=РФ".
        /// Ключ – левая часть (в нижнем регистре), значение – правая часть (как есть).
        /// </summary>
        private Dictionary<string, string> LoadExceptions(string path)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return dict;

            foreach (var line in File.ReadAllLines(path, new UTF8Encoding(false)))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var parts = trimmed.Split(new[] { '=' }, 2);
                if (parts.Length != 2) continue;

                var from = parts[0].Trim();
                var to = parts[1].Trim();
                if (from.Length == 0 || to.Length == 0) continue;

                dict[from.ToLower()] = to;
            }

            return dict;
        }


        private List<TextSpan> FindBlockedSpans(string text, List<string> phrases)
        {
            var spans = new List<TextSpan>();
            if (string.IsNullOrEmpty(text) || phrases == null) return spans;

            foreach (var phrase in phrases)
            {
                var trimmed = phrase.Trim();
                if (trimmed.Length == 0) continue;

                // разбиваем фразу из BadWord по пробелам/табам и строим regex,
                // чтобы "буквально    в" и "буквально в" воспринимались одинаково
                var words = Regex.Split(trimmed, @"\s+")
                                 .Where(w => w.Length > 0)
                                 .ToArray();
                if (words.Length == 0) continue;

                // \bбуквально\s+в\b
                string pattern = @"\b" + string.Join(@"\s+", words.Select(Regex.Escape)) + @"\b";

                foreach (Match m in Regex.Matches(
                             text,
                             pattern,
                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    spans.Add(new TextSpan
                    {
                        Start = m.Index,
                        End = m.Index + m.Length
                    });
                }
            }

            return spans;
        }




        private bool IsInsideBlockedSpans(List<TextSpan> spans, int start, int length)
        {
            if (spans == null || spans.Count == 0) return false;

            int end = start + length;
            foreach (var span in spans)
            {
                if (start < span.End && end > span.Start)
                    return true;
            }

            return false;
        }


        /// <summary>
        /// Заменяет слова в тексте на конструкции {вариант1|вариант2|...},
        /// используя словарь синонимов. Сохраняет регистр:
        /// - если слово в тексте с маленькой буквы – всё в нижнем;
        /// - если "Компания" – каждое слово в наборе с заглавных букв;
        /// - если ВСЕ буквы заглавные – весь набор в ВЕРХНЕМ РЕГИСТРЕ.
        /// </summary>
        private string ExpandTextWithSynonyms(
            string text,
            Dictionary<string, string[]> baseDict,
            Dictionary<string, string> exceptions,
            List<string> badPhrases)
        {
            if (string.IsNullOrEmpty(text) || baseDict == null || baseDict.Count == 0)
                return text;

            var blockedSpans = FindBlockedSpans(text, badPhrases);

            var sb = new StringBuilder();
            int lastIndex = 0;

            // слова: буквы (любой алфавит) + дефис
            var regex = new Regex(@"\b[\p{L}\-]+\b", RegexOptions.CultureInvariant);

            foreach (Match m in regex.Matches(text))
            {
                sb.Append(text, lastIndex, m.Index - lastIndex);

                int tokenStart = m.Index;
                int tokenLen = m.Length;

                string originalWord = m.Value;
                string wordForOutput = originalWord;

                string lowerOriginal = originalWord.ToLower();
                bool isNoSynonymWord = NoSynonymWords.Contains(lowerOriginal);


                bool blocked = IsInsideBlockedSpans(blockedSpans, tokenStart, tokenLen);

                // применяем Exceptions: рф -> РФ, и т.п.
                if (exceptions != null && exceptions.TryGetValue(originalWord.ToLower(), out var canonical))
                {
                    wordForOutput = canonical;
                }

                if (blocked || isNoSynonymWord)

                {
                    // эта позиция попала в одну из фраз из BadWord.txt – ничего не синонимизируем
                    sb.Append(wordForOutput);
                }
                else
                {
                    string key = wordForOutput.ToLower();

                    if (baseDict.TryGetValue(key, out var synonyms))
                    {
                        string originalLower = key; // то, что было в тексте (с учётом Exceptions), в нижнем регистре

                        var formattedList = new List<string>();

                        // 1) первым всегда идёт исходное слово из текста, с правильным регистром
                        string originalFormatted = ApplyCase(wordForOutput, originalLower);
                        formattedList.Add(originalFormatted);

                        // 2) затем добавляем остальные синонимы из базы, кроме дубликата оригинала
                        foreach (var s in synonyms)
                        {
                            if (string.Equals(s, originalLower, StringComparison.OrdinalIgnoreCase))
                                continue; // это уже оригинал – он у нас стоит первым

                            string candidate = ApplyCase(wordForOutput, s);

                            if (!formattedList.Contains(candidate, StringComparer.Ordinal))
                                formattedList.Add(candidate);
                        }

                        string replacement = "{" + string.Join("|", formattedList) + "}";
                        sb.Append(replacement);
                    }
                    else
                    {
                        sb.Append(wordForOutput);
                    }

                }

                lastIndex = tokenStart + tokenLen;
            }

            sb.Append(text, lastIndex, text.Length - lastIndex);
            return sb.ToString();
        }


        private string ApplyCase(string template, string synonym)
        {
            if (string.IsNullOrEmpty(synonym))
                return synonym;

            if (IsAllUpper(template))
                return synonym.ToUpper(); // АББРЕВИАТУРЫ и т.п.

            if (IsCapitalized(template))
                return CapitalizeEachWord(synonym); // Компания -> Компания / Российская Федерация

            return synonym; // всё остальное – как в базе (обычно нижний регистр)
        }

        private bool IsAllUpper(string s)
        {
            bool hasLetter = false;
            foreach (char c in s)
            {
                if (char.IsLetter(c))
                {
                    hasLetter = true;
                    if (!char.IsUpper(c))
                        return false;
                }
            }
            return hasLetter;
        }

        private bool IsCapitalized(string s)
        {
            bool firstLetterSeen = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetter(c))
                {
                    if (!firstLetterSeen)
                    {
                        firstLetterSeen = true;
                        if (!char.IsUpper(c)) return false;
                    }
                    else
                    {
                        // если все буквы заглавные – это не Capitalized, а AllUpper
                        if (char.IsUpper(c))
                            return false;
                    }
                }
            }
            return firstLetterSeen;
        }

        private string CapitalizeFirst(string word)
        {
            if (string.IsNullOrEmpty(word))
                return word;

            word = word.ToLower();
            return char.ToUpper(word[0]) + (word.Length > 1 ? word.Substring(1) : "");
        }

        private string LowerFirst(string word)
        {
            if (string.IsNullOrEmpty(word))
                return word;

            return char.ToLower(word[0]) + (word.Length > 1 ? word.Substring(1) : "");
        }

        private string CapitalizeEachWord(string text)
        {
            var parts = text.Split(' ');
            for (int i = 0; i < parts.Length; i++)
            {
                var word = parts[i];
                if (word.Length == 0) continue;

                // обрабатываем части с дефисом отдельно: "санкт-петербург" -> "Санкт-Петербург"
                var hyParts = word.Split('-');
                for (int j = 0; j < hyParts.Length; j++)
                {
                    var hp = hyParts[j];
                    if (hp.Length == 0) continue;
                    hyParts[j] = char.ToUpper(hp[0]) + (hp.Length > 1 ? hp.Substring(1) : "");
                }
                parts[i] = string.Join("-", hyParts);
            }
            return string.Join(" ", parts);
        }


        /// <summary>
        /// Чистим вариант: убираем лишние пробелы и внешнюю пунктуацию.
        /// </summary>
        private string NormalizeVariant(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            string s = raw.Trim();

            char[] trimChars =
            {
        ' ', '\t', '\r', '\n',
        ',', '.', '!', '?', ';', ':',
        '«', '»', '"', '\'', '(', ')'
    };

            s = s.Trim(trimChars);

            return s;
        }

        /// <summary>
        /// Подсчёт количества "слов" по пробелам/разделителям.
        /// </summary>
        private int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            var parts = SplitWords(text);
            return parts.Length;
        }

        /// <summary>
        /// Разбиваем строку на слова (для проверки "это", "и", "или").
        /// </summary>
        private string[] SplitWords(string text)
        {
            return text
                .Split(new[]
                {
            ' ', '\t', '\r', '\n',
            ',', '.', '!', '?', ';', ':',
            '(', ')', '«', '»', '"', '\''
                }, StringSplitOptions.RemoveEmptyEntries);
        }





        private void ScrollCollectorLog()
        {
            txtCollectorStatus.SelectionStart = txtCollectorStatus.Text.Length;
            txtCollectorStatus.ScrollToCaret();
        }

        /// <summary>
        /// UTF-8 (строгий) → при ошибке считаем win-1251.
        /// </summary>
        private string ReadFileAsUtf8(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

            try
            {
                return utf8Strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                Encoding win1251 = Encoding.GetEncoding(1251);
                return win1251.GetString(bytes);
            }
        }

        /// <summary>
        /// Простая эвристика: кириллица против латиницы.
        /// </summary>
        private bool IsRussianText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            int cyr = 0;
            int latin = 0;

            foreach (char ch in text)
            {
                if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
                    latin++;
                else if ((ch >= '\u0400' && ch <= '\u04FF') || (ch >= '\u0500' && ch <= '\u052F'))
                    cyr++;
            }

            if (cyr == 0)
                return false;

            return cyr >= latin && cyr >= 10;
        }
    }

    /// <summary>
    /// Стилизованный TabControl.
    /// </summary>
    public class StyledTabControl : TabControl
    {
        private readonly Color activeBackColor = Color.FromArgb(33, 150, 243);
        private readonly Color inactiveBackColor = Color.FromArgb(224, 236, 248);
        private readonly Color inactiveForeColor = Color.FromArgb(60, 60, 60);

        public StyledTabControl()
        {
            this.DrawMode = TabDrawMode.OwnerDrawFixed;
            this.ItemSize = new Size(140, 40);   // ширина / высота вкладок
            this.SizeMode = TabSizeMode.Fixed;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            base.OnDrawItem(e);

            var g = e.Graphics;
            var rect = GetTabRect(e.Index);
            bool selected = (e.Index == this.SelectedIndex);

            Color backColor = selected ? activeBackColor : inactiveBackColor;
            Color foreColor = selected ? Color.White : inactiveForeColor;

            using (var b = new SolidBrush(backColor))
                g.FillRectangle(b, rect);

            string text = this.TabPages[e.Index].Text;
            using (var font = new Font("Segoe UI", 11F, FontStyle.Regular, GraphicsUnit.Point))
            using (var textBrush = new SolidBrush(foreColor))
            {
                var sf = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                g.DrawString(text, font, textBrush, rect, sf);
            }
        }
    }
}
