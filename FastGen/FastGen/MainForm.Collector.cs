using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using FastGen.Core;

namespace FastGen
{
    /// <summary>Вкладка «Сборщик»: слева — сбор шаблонов из папки, справа — сборка GoldBase.txt.</summary>
    public partial class MainForm
    {
        private TextBox txtFolderPath;
        private TextBox txtMask;
        private TextBox txtCollectorStatus;
        private TextBox txtResultFilePath;
        private TextBox txtBuildResult;
        private Label lblBuildStatus;
        private CheckBox chkAppendBase;
        private Button btnAnalyze;
        private Button btnBuild;

        private void BuildCollectorTab(TabPage tabPage)
        {
            tabPage.Padding = new Padding(8);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            // ===== Анализ =====
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            var folderRow = NewFlowRow();
            txtFolderPath = new TextBox
            {
                Width = 400,
                ReadOnly = true,
                Margin = new Padding(0, 5, 5, 0),
                Text = _settings.GetString("CollectorFolder", @"M:\PROGONS\")
            };
            var btnBrowse = MakeButton("Выбрать...", BtnBrowse_Click);
            btnBrowse.Margin = new Padding(5, 4, 5, 0);
            folderRow.Controls.AddRange(new Control[] { BoldLabel("Папка:"), txtFolderPath, btnBrowse });

            var maskRow = NewFlowRow();
            txtMask = new TextBox { Width = 250, Margin = new Padding(0, 5, 5, 0), Text = "Шаблон размножения*.txt" };
            btnAnalyze = MakeButton("Анализ", async (s, e) => await RunAnalyzeAsync(), Color.FromArgb(46, 204, 113), true);
            btnAnalyze.Margin = new Padding(10, 4, 5, 0);
            maskRow.Controls.AddRange(new Control[] { BoldLabel("Маска:"), txtMask, btnAnalyze });

            txtCollectorStatus = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill
            };

            left.Controls.Add(folderRow, 0, 0);
            left.Controls.Add(maskRow, 0, 1);
            left.Controls.Add(txtCollectorStatus, 0, 2);

            // ===== Сборка =====
            var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            right.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var resultRow = NewFlowRow();
            txtResultFilePath = new TextBox
            {
                Width = 350,
                Margin = new Padding(0, 5, 5, 0),
                Text = Path.Combine(AppDir, "ResultDB.txt")
            };
            var btnBrowseResult = MakeButton("Выбрать...", BtnBrowseResult_Click);
            btnBrowseResult.Margin = new Padding(5, 4, 5, 0);
            resultRow.Controls.AddRange(new Control[] { BoldLabel("Файл шаблонов:"), txtResultFilePath, btnBrowseResult });

            var appendRow = NewFlowRow();
            chkAppendBase = new CheckBox { Text = "Дополнить базу (не удалять старые строки GoldBase.txt)", AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
            appendRow.Controls.Add(chkAppendBase);

            var buildRow = NewFlowRow();
            btnBuild = MakeButton("Сборка GoldBase", async (s, e) => await BuildSynonymBaseAsync(), Color.FromArgb(46, 204, 113), true);
            btnBuild.Margin = new Padding(0, 5, 5, 0);
            buildRow.Controls.Add(btnBuild);

            txtBuildResult = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill
            };

            lblBuildStatus = new Label { AutoSize = true, Margin = new Padding(0, 5, 0, 0) };

            right.Controls.Add(resultRow, 0, 0);
            right.Controls.Add(appendRow, 0, 1);
            right.Controls.Add(buildRow, 0, 2);
            right.Controls.Add(txtBuildResult, 0, 3);
            right.Controls.Add(lblBuildStatus, 0, 4);

            root.Controls.Add(left, 0, 0);
            root.Controls.Add(right, 1, 0);
            tabPage.Controls.Add(root);
        }

        private static Label BoldLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Margin = new Padding(0, 8, 5, 0)
            };
        }

        private void BtnBrowse_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Выберите папку с файлами шаблонов";
                if (Directory.Exists(txtFolderPath.Text)) dlg.SelectedPath = txtFolderPath.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    txtFolderPath.Text = dlg.SelectedPath;
                    _settings.Set("CollectorFolder", dlg.SelectedPath);
                }
            }
        }

        private void BtnBrowseResult_Click(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = "Файл, куда собираются шаблоны";
                dlg.Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*";
                dlg.FileName = "ResultDB.txt";
                dlg.InitialDirectory = AppDir;
                dlg.OverwritePrompt = false;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    txtResultFilePath.Text = dlg.FileName;
            }
        }

        private string ResultDbPath
        {
            get
            {
                string p = txtResultFilePath.Text.Trim();
                return p.Length > 0 ? p : Path.Combine(AppDir, "ResultDB.txt");
            }
        }

        private void CollectorLog(string line)
        {
            txtCollectorStatus.AppendText(line + "\r\n");
        }

        private async Task RunAnalyzeAsync()
        {
            string folder = txtFolderPath.Text.Trim();
            string mask = txtMask.Text.Trim();
            string resultPath = ResultDbPath;

            if (folder.Length == 0 || !Directory.Exists(folder))
            {
                ShowWarning("Укажите существующую папку.");
                return;
            }
            if (mask.Length == 0)
            {
                ShowWarning("Укажите маску файла.");
                return;
            }

            btnAnalyze.Enabled = btnBuild.Enabled = false;
            txtCollectorStatus.Clear();
            CollectorLog("Поиск файлов...");
            var progress = new Progress<string>(CollectorLog);

            try
            {
                var counts = await Task.Run(() =>
                    GoldBaseBuilder.CollectTemplates(folder, mask, resultPath, msg => ((IProgress<string>)progress).Report(msg)));
                CollectorLog("Готово. Найдено файлов: " + counts[0] + ", записано: " + counts[1] + ".");
                CollectorLog("Файл: " + resultPath);
            }
            catch (Exception ex)
            {
                CollectorLog("Ошибка: " + ex.Message);
                ShowWarning("Ошибка при анализе: " + ex.Message);
            }
            finally
            {
                btnAnalyze.Enabled = btnBuild.Enabled = true;
            }
        }

        private async Task BuildSynonymBaseAsync()
        {
            string resultPath = ResultDbPath;
            string goldPath = _store.GoldPath;

            if (!File.Exists(resultPath))
            {
                ShowWarning("Не найден файл шаблонов:\r\n" + resultPath + "\r\nСначала выполните «Анализ».");
                return;
            }

            bool append = chkAppendBase.Checked;
            btnAnalyze.Enabled = btnBuild.Enabled = false;
            lblBuildStatus.Text = "Сборка базы синонимов...";
            txtBuildResult.Clear();

            try
            {
                var builder = await Task.Run(() =>
                {
                    var b = new GoldBaseBuilder();
                    if (append && File.Exists(goldPath))
                        b.AddExistingLines(File.ReadLines(goldPath, new UTF8Encoding(false)));
                    b.AddTemplateText(File.ReadAllText(resultPath, new UTF8Encoding(false)));
                    SynonymStore.WriteAllLinesSafe(goldPath, b.Lines);

                    // заодно пересобираем пары соседних слов
                    var ctx = ContextIndex.Build(File.ReadLines(resultPath, new UTF8Encoding(false)));
                    try { ctx.Save(ContextPath); } catch (IOException) { }
                    PostToUi(() => OnContextReady(ctx));
                    return b;
                });

                txtBuildResult.Lines = builder.Lines.Take(300).ToArray();
                lblBuildStatus.Text = "Готово. Конструкций {}: " + builder.TotalConstructs +
                                      ", новых наборов: " + builder.AcceptedSets +
                                      ", всего строк в базе: " + builder.Count + ". Файл: " + goldPath;

                _store.LoadGold();
                RefreshReproTarget(force: true);
            }
            catch (Exception ex)
            {
                lblBuildStatus.Text = "Ошибка при сборке.";
                ShowWarning("Ошибка при сборке базы: " + ex.Message);
            }
            finally
            {
                btnAnalyze.Enabled = btnBuild.Enabled = true;
            }
        }
    }
}
