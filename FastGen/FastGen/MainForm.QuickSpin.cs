using System;
using System.Drawing;
using System.Media;
using System.Windows.Forms;
using FastGen.Core;

namespace FastGen
{
    /// <summary>Вкладка «Быстрое размножение»: вставил текст → «Размножить» → правка в нижнем поле.</summary>
    public partial class MainForm
    {
        private RadioButton rbGoldBase;
        private RadioButton rbBigBase;
        private RichTextBox txtEditorSource;
        private RichTextBox txtEditorResult;
        private Label lblQuickDictStatus;
        private Label lblQuickInfo;

        private void BuildEditorTab(TabPage tabPage)
        {
            tabPage.Padding = new Padding(8);

            var mainLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var topRow = NewFlowRow();

            var lblBase = new Label
            {
                Text = "База:",
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Margin = new Padding(0, 8, 5, 0)
            };

            bool quickDict = _settings.GetBool("QuickUseDict", false);
            rbGoldBase = new RadioButton { Text = "GoldBase + Моя база", AutoSize = true, Checked = !quickDict, Margin = new Padding(0, 8, 10, 0) };
            rbBigBase = new RadioButton { Text = "+ DICT.DBF (BigBase)", AutoSize = true, Checked = quickDict, Margin = new Padding(0, 8, 20, 0) };

            var btnMultiply = MakeButton("Размножить", BtnEditorMultiply_Click, Color.FromArgb(46, 204, 113), true);
            var btnCopy = MakeButton("Скопировать", BtnEditorCopy_Click, Color.RoyalBlue, true);
            var btnToRepro = MakeButton("В «Размножение»", BtnEditorToRepro_Click, Color.SteelBlue, true);
            var btnClear = MakeButton("Очистить", BtnEditorClear_Click, Color.Red, true);
            foreach (var b in new[] { btnMultiply, btnCopy, btnToRepro, btnClear }) b.Margin = new Padding(0, 4, 5, 0);

            lblQuickDictStatus = new Label { AutoSize = true, Margin = new Padding(10, 8, 0, 0) };

            topRow.Controls.AddRange(new Control[] { lblBase, rbGoldBase, rbBigBase, btnMultiply, btnCopy, btnToRepro, btnClear, lblQuickDictStatus });

            var textLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            textLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            textLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            textLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            textLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            txtEditorSource = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Courier New", 14F),
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = false
            };
            txtEditorSource.KeyDown += TxtEditorSource_KeyDown;

            txtEditorResult = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Courier New", 14F),
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                HideSelection = false,
                DetectUrls = false
            };
            txtEditorResult.SelectionChanged += TxtEditorResult_SelectionChanged;
            txtEditorResult.MouseDown += TxtEditorResult_MouseDown;
            DisableAutoWordSelection(txtEditorResult);

            var middle = NewFlowRow();
            middle.Margin = new Padding(0, 5, 0, 5);
            middle.Controls.AddRange(new Control[]
            {
                MakeButton("Вариант 1 ( , ; )", (s, e) => QuickEnumVariant(false)),
                MakeButton("Вариант 2 (и, или)", (s, e) => QuickEnumVariant(true)),
                MakeButton("Ротация", (s, e) => QuickTransform(TextCase.Rotate)),
                MakeButton("Текст", (s, e) => QuickTransform(TextCase.ToggleTitleCase)),
                MakeButton("ТЕКСТ", (s, e) => QuickTransform(TextCase.ToggleUpper)),
                MakeButton("Вернуть", BtnEditorReturn_Click, Color.DimGray, true)
            });
            lblQuickInfo = new Label
            {
                AutoSize = true,
                ForeColor = Color.DimGray,
                Margin = new Padding(10, 6, 0, 0),
                Text = "Правый щелчок по варианту в {…} — удалить его. «Вернуть» — оставить исходное слово."
            };
            middle.Controls.Add(lblQuickInfo);

            textLayout.Controls.Add(txtEditorSource, 0, 0);
            textLayout.Controls.Add(middle, 0, 1);
            textLayout.Controls.Add(txtEditorResult, 0, 2);

            mainLayout.Controls.Add(topRow, 0, 0);
            mainLayout.Controls.Add(textLayout, 0, 1);
            tabPage.Controls.Add(mainLayout);
        }

        // ---------------- Размножение ----------------

        private void BtnEditorMultiply_Click(object sender, EventArgs e)
        {
            bool useDict = rbBigBase.Checked;
            if (useDict && !_store.DictLoaded && System.IO.File.Exists(System.IO.Path.Combine(AppDir, "DICT.DBF")))
            {
                ShowWarning("DICT.DBF ещё загружается — подождите несколько секунд (статус справа вверху).");
                return;
            }

            if (_store.GoldCount == 0 && _store.UserCount == 0 && !_store.DictLoaded)
            {
                ShowWarning("Базы синонимов пусты: нет GoldBase.txt, UserBase.txt и DICT.DBF в папке программы.");
                return;
            }

            var res = AutoSpinner.Spin(txtEditorSource.Text, _store, BuildSpinOptions(useDict));
            txtEditorResult.Text = TextCase.NormalizeSpaces(res.Text);
            HighlightSynConstructions(txtEditorResult);
            lblQuickInfo.Text = "Конструкций: " + res.Constructs + " · вариантов текста: " +
                                SpinSyntax.FormatCount(SpinSyntax.CountVariants(txtEditorResult.Text));
        }

        private void BtnEditorCopy_Click(object sender, EventArgs e)
        {
            if (txtEditorResult.TextLength == 0) return;
            Clipboard.SetText(txtEditorResult.Text.Replace("\n", "\r\n"));
        }

        /// <summary>Перенести результат во вкладку «Размножение» для пошаговой проверки.</summary>
        private void BtnEditorToRepro_Click(object sender, EventArgs e)
        {
            if (txtEditorResult.TextLength == 0) return;
            string text = txtEditorResult.Text;
            tabControl.SelectedTab = tabRepro;

            int start = txtReproEditor.SelectionStart;
            ReplaceRepro(start, txtReproEditor.SelectionLength, text);
            HighlightSynConstructions(txtReproEditor);
            UpdateTemplateInfo();

            var first = TextNav.NextConstruct(txtReproEditor.Text, start);
            txtReproEditor.Focus();
            if (first.IsValid) GoToToken(first);
        }

        private void BtnEditorClear_Click(object sender, EventArgs e)
        {
            txtEditorSource.Clear();
            txtEditorResult.Clear();
            txtEditorSource.Focus();
        }

        private void TxtEditorSource_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+V заменяет весь исходный текст содержимым буфера
            if (e.Control && e.KeyCode == Keys.V)
            {
                string clip = Clipboard.GetText();
                if (!string.IsNullOrEmpty(clip))
                {
                    txtEditorSource.SelectAll();
                    txtEditorSource.SelectedText = clip;
                }
                e.Handled = e.SuppressKeyPress = true;
            }
        }

        // ---------------- Правка результата ----------------

        private void TxtEditorResult_SelectionChanged(object sender, EventArgs e)
        {
            if (_adjustingSelection || !txtEditorResult.Focused) return;

            int start = txtEditorResult.SelectionStart;
            int length = txtEditorResult.SelectionLength;
            if (length <= 0) return;

            string text = txtEditorResult.Text;
            int end = Math.Min(text.Length, start + length);
            int newEnd = end;
            while (newEnd > start && char.IsWhiteSpace(text[newEnd - 1])) newEnd--;

            if (newEnd != end)
            {
                _adjustingSelection = true;
                txtEditorResult.Select(start, newEnd - start);
                _adjustingSelection = false;
            }
        }

        private void TxtEditorResult_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
                RemoveVariantAt(e.Location);
        }

        /// <summary>Правый щелчок по варианту внутри {…} удаляет этот вариант (вложенность учитывается).</summary>
        private void RemoveVariantAt(Point location)
        {
            string text = txtEditorResult.Text;
            if (text.Length == 0) return;

            int index = CharIndexAtPoint(txtEditorResult, location);
            if (!SpinSyntax.TryRemoveVariantAt(text, index, out var removal)) return;
            var range = removal.Construct;
            string replacement = removal.Replacement;

            int selStart = txtEditorResult.SelectionStart;
            _adjustingSelection = true;
            txtEditorResult.Select(range.Start, range.Length);
            txtEditorResult.SelectedText = replacement;
            _adjustingSelection = false;

            int delta = replacement.Length - range.Length;
            txtEditorResult.Select(selStart > range.Start ? Math.Max(range.Start, selStart + delta) : selStart, 0);
            HighlightSynConstructions(txtEditorResult);
        }

        /// <summary>«Вернуть»: конструкция под курсором заменяется исходным (первым) вариантом.</summary>
        private void BtnEditorReturn_Click(object sender, EventArgs e)
        {
            string text = txtEditorResult.Text;
            if (text.Length == 0) return;

            int caret = txtEditorResult.SelectionStart + txtEditorResult.SelectionLength / 2;
            if (!SpinSyntax.TryGetInnermostConstruct(text, caret, out var range))
            {
                SystemSounds.Beep.Play();
                return;
            }

            string first = SpinSyntax.GetVariants(text.Substring(range.Start, range.Length))[0];
            _adjustingSelection = true;
            txtEditorResult.Select(range.Start, range.Length);
            txtEditorResult.SelectedText = first;
            txtEditorResult.Select(range.Start + first.Length, 0);
            _adjustingSelection = false;
            HighlightSynConstructions(txtEditorResult);
        }

        private void QuickTransform(Func<string, string> transform)
        {
            if (txtEditorResult.SelectionLength == 0)
            {
                SystemSounds.Beep.Play();
                return;
            }

            int start = txtEditorResult.SelectionStart;
            string res = transform(txtEditorResult.SelectedText);
            if (res == null)
            {
                SystemSounds.Beep.Play();
                return;
            }
            res = TextCase.NormalizeSpaces(res);

            _adjustingSelection = true;
            txtEditorResult.SelectedText = res;
            txtEditorResult.Select(start, res.Length);
            _adjustingSelection = false;
            HighlightSynConstructions(txtEditorResult);
        }

        /// <summary>Вариант 1/2: без выделения преобразуется весь текст результата (список вставлен целиком).</summary>
        private void QuickEnumVariant(bool andOr)
        {
            bool whole = txtEditorResult.SelectionLength == 0;
            string src = whole ? txtEditorResult.Text : txtEditorResult.SelectedText;
            string res = andOr ? TextCase.EnumVariant2(src) : TextCase.EnumVariant1(src);
            if (res == null) return;
            res = TextCase.NormalizeSpaces(res);

            _adjustingSelection = true;
            if (whole)
            {
                txtEditorResult.Text = res;
                txtEditorResult.Select(0, res.Length);
            }
            else
            {
                int start = txtEditorResult.SelectionStart;
                txtEditorResult.SelectedText = res;
                txtEditorResult.Select(start, res.Length);
            }
            _adjustingSelection = false;
            HighlightSynConstructions(txtEditorResult);
        }
    }
}
