using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml.Serialization;
using URLChecker.Properties;
using xNet;

namespace URLChecker
{
    public partial class Form1 : Form
    {
        string _VERSION = "0.11 + прокси от 04.10.2026";
        Thread thread;

        static bool _haveBrokeLink = false;
        public bool haveBrokenLink
        {
            get { return _haveBrokeLink; }
            set
            {
                if (value)
                    toolStripStatusLabel1.Text = "Имеются битые ссылки";
                else
                    toolStripStatusLabel1.Text = "";
                _haveBrokeLink = value;
            }
        }

        string projectName = "";
        List<Column> columns = new List<Column>();

        public int indexCol=0;
        public bool asc = true;

        bool stop = false;
        int count_all;

        Stack<int> stack = new Stack<int>();

        Parser parser = new Parser();

        List<string> WL = new List<string>();

        static object locker = new object();
        Stack<int> numbers = new Stack<int>();
        Stack<int> numbers_ = new Stack<int>();

        static int countChecked = 0;
        bool ctrl = false;

        public Form1()
        {
            InitializeComponent();
            projectName = "default";

            this.Text += " " + _VERSION;
            grid.AutoGenerateColumns = false;
            LoadColumns();
            LoadProject(projectName);
            thread = new Thread(Work);
            toolStripButton9.Image = Resources.broken_link_off;

            grid.DefaultCellStyle.SelectionBackColor = Settings.Default.selectionColor;

            if (Settings.Default.filtrBroken)
                toolStripButton9.Image = Resources.broken_link;
            if (Settings.Default.podsvetka)
                bPodsvetka.Image = Resources.lamp_light;
            else
                bPodsvetka.Image = Resources.lamp;

            if (Settings.Default.podsvetkaWL)
                bWL.Image = Resources.wlyes;
            else
                bWL.Image = Resources.wlno;

            Network.ReloadProxy();
            UpdateProxyButton();
        }

        private void UpdateProxyButton()
        {
            ProxyConfig proxy = Network.ActiveProxy;
            if (proxy != null)
            {
                bProxy.Image = Resources.proxy_on;
                bProxy.ToolTipText = "Прокси включен (" + proxy.Address + "). Нажмите, чтобы выключить";
                toolStripProxyStatus.ForeColor = Color.Green;
                toolStripProxyStatus.Text = "Прокси: " + proxy.Address;
            }
            else
            {
                bProxy.Image = Resources.proxy_off;
                bProxy.ToolTipText = "Прокси выключен. Нажмите, чтобы включить";
                toolStripProxyStatus.ForeColor = SystemColors.ControlText;
                toolStripProxyStatus.Text = "Без прокси";
            }
        }

        private void bProxy_ButtonClick(object sender, EventArgs e)
        {
            if (!Settings.Default.proxyEnabled && Settings.Default.proxyHost.Trim() == "")
            {
                // Прокси ещё не настроен - сразу открываем настройки
                ShowProxySettings(true);
                return;
            }

            Settings.Default.proxyEnabled = !Settings.Default.proxyEnabled;
            Settings.Default.Save();
            Network.ReloadProxy();
            UpdateProxyButton();
        }

        private void настройкиПроксиToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ShowProxySettings(false);
        }

        private void ShowProxySettings(bool enableOnOpen)
        {
            using (FormProxy formProxy = new FormProxy())
            {
                formProxy.EnableOnOpen = enableOnOpen;
                formProxy.ShowDialog(this);
            }
            Network.ReloadProxy();
            UpdateProxyButton();
        }

        private void настройкиToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FormOptions formOptions = new FormOptions();
            formOptions.ShowDialog();
            grid.DefaultCellStyle.SelectionBackColor = Settings.Default.selectionColor;
            grid.DefaultCellStyle.Font = new Font(Settings.Default.font, Convert.ToInt32(Settings.Default.sizeFont));
            grid.Refresh();
        }


        delegate void UpdateGridDelegate(string text);

        private void UpdateGrid(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new UpdateGridDelegate(UpdateGrid), new object[] { text });
            }
            else
            {
                if (haveBrokenLink)
                    toolStripStatusLabel1.Text = "Имеются битые ссылки";
                else
                    toolStripStatusLabel1.Text = "";
                toolStripStatus.Text = text;
                grid.Refresh();
            }
        }

        delegate void UpdateButtonDelegate();
        private void UpdateButton()
        {
            if (InvokeRequired)
            {
                BeginInvoke(new UpdateButtonDelegate(UpdateButton), new object[] { });
            }
            else
            {                
                toolStripButton1.Enabled = true;
                toolStripButton2.Enabled = false;
                toolStripStatus.Text = "";
            }
        }


        public void SaveProject(string fileName)
        {
            BinaryFormatter formatter = new BinaryFormatter();
            FileStream writer = new FileStream(Application.StartupPath + "\\" + fileName + ".urlchecker", FileMode.Create, FileAccess.Write, FileShare.None);
            //formatter.Serialize(writer, parser.links);
            formatter.Serialize(writer, parser);
            writer.Close();
        }

        public void LoadProject(string fileName)
        {
            try
            {
                FileStream reader = new FileStream(Application.StartupPath + "\\" + fileName + ".urlchecker", FileMode.Open);
                BinaryFormatter formatter = new BinaryFormatter();
                parser = (Parser)formatter.Deserialize(reader);
                reader.Close();
                if (parser.ActiveFilter == null)
                    parser.ActiveFilter = new filter();
                statusLabel.Text = "Всего записей: " + parser.links.Count;
                parser.ClearFilter();
                grid.RowCount = parser.GetCountRow();

            }
            catch (Exception E)
            {
                parser = new Parser();
            }

        }

        void MakeColumnFile()
        {
            columns.Clear();
            for (int x = 0; x < grid.Columns.Count; x++)
            {
                Column col = new Column();
                col.name = grid.Columns[x].Name;
                col.caption = grid.Columns[x].HeaderText;
                col.position = x;
                col.visible = grid.Columns[x].Visible;
                col.width = grid.Columns[x].Width;
                columns.Add(col);
            }
            SaveColumns();

        }

        public void SaveColumns()
        {
            BinaryFormatter formatter = new BinaryFormatter();
            FileStream writer = new FileStream(Application.StartupPath + "\\columns.dat", FileMode.Create);

            for (int x = 0; x < grid.Columns.Count; x++)
            {
                columns[x].width = grid.Columns[x].Width;
                columns[x].visible = grid.Columns[x].Visible;
            }
            formatter.Serialize(writer, columns);
            writer.Close();
        }

        public void LoadColumns()
        {
            try
            {
                BinaryFormatter formatter = new BinaryFormatter();
                FileStream writer = new FileStream(Application.StartupPath + "\\columns.dat", FileMode.Open);
                columns = (List<Column>)formatter.Deserialize(writer);
                writer.Close();
                for (int i = 0; i < columns.Count; i++)
                {
                    grid.Columns[i].Width = columns[i].width;
                    grid.Columns[i].Visible = columns[i].visible;
                }

            }
            catch (Exception E)
            {
                MakeColumnFile();
            }
        }

        private void добавитьToolStripMenuItem_Click(object sender, EventArgs e)
        {
            FormImportLinks formImportLinks = new FormImportLinks();
            formImportLinks.ShowDialog();
            for (int i = 0; i < formImportLinks.links.Count; i++)
            {
                Link tmp_link = new Link();
                tmp_link.url = formImportLinks.links[i];
                parser.AddLink(tmp_link);
                grid.RowCount = parser.GetCountRow();
            }
            grid.Refresh();
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveColumns();
            SaveProject("default");
        }

        private void toolStripButton1_Click(object sender, EventArgs e)
        {
            //Ищем дубликаты
            bool start = true;
            List<Link> dubl = parser.CheckDuplicates();
            if (dubl.Count > 0)
            {
                FormDuble formDuble = new FormDuble();
                formDuble.listBox1.Items.Clear();
                for (int i = 0; i < dubl.Count; i++)
                {
                    formDuble.listBox1.Items.Add(dubl[i].url);
                }
                formDuble.ShowDialog();
                if (formDuble.ok_delete)
                {
                    for (int i = 0; i < dubl.Count; i++)
                    {
                        parser.DeleteLinkUnique(dubl[i].unique);
                    }
                }
                grid.RowCount = parser.GetCountRow();
                start = formDuble.ok_delete;
            }
            if (start)
            {
                numbers.Clear();
                //   for (int i = 0; i < parser.links.Count; i++)               
                //   numbers.Push(i);

                for (int i = parser.links.Count - 1; i >= 0; i--)
                {
                    if (parser.links[i].backURL == null)
                        parser.links[i].backURL = "";
                    if (parser.links[i].backURL=="")
                        numbers.Push(i);
                }
                count_all = numbers.Count;
                toolStripButton1.Enabled = false;
                toolStripButton2.Enabled = true;
                stop = false;
                //thread = null;

                countChecked = 0;
                int countThread = Convert.ToInt32(Settings.Default.countThread);
                if (countThread > numbers.Count)
                    countThread = numbers.Count;

                ThreadPool.SetMaxThreads(countThread, 0);
                for (int i = 0; i < countThread; i++)
                {
                    ThreadPool.QueueUserWorkItem(Work);
                }
            }
        }

        private void удалитьИзСпискаToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (grid.SelectedRows.Count > 0)
            {
                if (MessageBox.Show("Записи будут удалены. Вы уверены?", "Предупреждение",  MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    List<DataRow> rows = new List<DataRow>();
                    for (int i = 0; i < grid.SelectedRows.Count; i++)
                    {
                        //parser.DeleteLink(Convert.ToInt32(grid.SelectedRows[i].Cells[0].Value));
                        parser.DeleteLink(Convert.ToInt32(grid.SelectedRows[i].Index));
                    }
                    parser.Rebuild();
                    grid.ClearSelection();
                    grid.RowCount = parser.GetCountRow();
                    grid.Refresh();

                }
            }
        }


        private void toolStripButton2_Click(object sender, EventArgs e)
        {
            toolStripButton1.Enabled = false;
            toolStripButton2.Enabled = false;
            stop = true;
        }

        private void Work(Object state)
        {
            haveBrokenLink = false;
            int i = 0;
            UpdateGrid("Проверка начата.... Всего " + count_all + "...");
            while ((countChecked != count_all)&&(!stop))
            {
                if (numbers.Count > 0)
                {
                    lock (locker)
                    {

                        i = numbers.Pop();
                    }                    
                    Link link = parser.GetInfoByURL(parser.links[i].url, parser.links[i].checkedURL);
                    parser.links[i].backURL = link.backURL;
                    parser.links[i].anchor = link.anchor;
                    parser.links[i].noindex = link.noindex;
                    parser.links[i].nofollow = link.nofollow;
                    parser.links[i].noGR = link.noGR;
                    parser.links[i].noYR = link.noYR;
                    parser.links[i].error = link.error;
                    parser.links[i].noAll = link.noAll;
                    parser.links[i].errorHTTP = link.errorHTTP;
                    parser.links[i].allGood = link.allGood;
                    if (link.error)
                        haveBrokenLink = true;
                    countChecked++;
                    UpdateGrid("Проверено " + Convert.ToString(countChecked) + " из " + count_all + "...");
                }
                if (stop)
                {
                    UpdateButton();
                    thread.Abort();
                }
            }
            UpdateButton();
            UpdateGrid("Проверено");
        }

        private void grid_CellValueNeeded(object sender, DataGridViewCellValueEventArgs e)
        {
            if (parser.links == null)
                return;
            if (e.RowIndex > parser.links.Count - 1)
                return;

            int position = parser.GetPositionOrderInList(e.RowIndex);
            if (position < 0)
                return;
            if (position > parser.links.Count)
                return;


            switch (e.ColumnIndex)
            {
                case 0: //№
                    e.Value = parser.links[position].N_;
                    break;
                case 1: //URL
                    e.Value = parser.links[position].url;
                    break;
                case 2: //проверяемая ссылка
                    if (parser.links[position].checkedURL.IndexOf("\n")>0)
                        e.Value = "[Список доменов]";
                    else
                        e.Value = parser.links[position].checkedURL;
                    break;
                case 3: //обратная ссылка
                    e.Value = parser.links[position].backURL;
                    break;
                case 4: //Анкор
                    e.Value = parser.links[position].anchor;
                    break;
                case 5: //Noindex
                    e.Value = parser.links[position].noindex;
                    break;
                case 6: //Nofollow
                    e.Value = parser.links[position].nofollow;
                    break;
                case 7: //NoYR
                    e.Value = parser.links[position].noYR;
                    break;
                case 8: //NoGR
                    e.Value = parser.links[position].noGR;
                    break;
            }

        }

        private void grid_SelectionChanged(object sender, EventArgs e)
        {
            if (parser.links.Count == parser._pos.Length)
                statusLabel.Text = "Выделено " + grid.SelectedRows.Count + "/" + parser.links.Count + " записей";
            else
                statusLabel.Text = "Выделено " + grid.SelectedRows.Count + "/" + parser._pos.Length+"/"+parser.links.Count + " записей";
        }

        private void проверяемыйURLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (grid.SelectedRows.Count <= 0)
                return;

            var newline = System.Environment.NewLine;
            var clipboard_string = new StringBuilder();

            int count = 0;
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                if (grid.Rows[i].Selected)
                {
                    if (count==0)
                        clipboard_string.Append(grid.Rows[i].Cells[1].Value);
                    else
                        clipboard_string.Append(newline + grid.Rows[i].Cells[1].Value );
                    count++;
                }
            }
            Clipboard.SetText(clipboard_string.ToString());
        }

        private void обратныйURLToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (grid.SelectedRows.Count <= 0)
                return;

            var newline = System.Environment.NewLine;
            var clipboard_string = new StringBuilder();

            int count = 0;
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                if (grid.Rows[i].Selected)
                {
                    if (count == 0)
                        clipboard_string.Append(grid.Rows[i].Cells[3].Value);
                    else
                        clipboard_string.Append(newline + grid.Rows[i].Cells[3].Value);
                    count++;
                }
            }
            Clipboard.SetText(clipboard_string.ToString());
        }

        private void анкорToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (grid.SelectedRows.Count <= 0)
                return;

            var newline = System.Environment.NewLine;
            var clipboard_string = new StringBuilder();

            int count = 0;
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                if (grid.Rows[i].Selected)
                {
                    if (count == 0)
                        clipboard_string.Append(grid.Rows[i].Cells[4].Value);
                    else
                        clipboard_string.Append(newline + grid.Rows[i].Cells[4].Value);
                    count++;
                }
            }

            Clipboard.SetText(clipboard_string.ToString());
        }

        private void обратныйURLАнкорToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (grid.SelectedRows.Count <= 0)
                return;

            var newline = System.Environment.NewLine;
            var tab = "\t";
            var clipboard_string = new StringBuilder();

            int count = 0;
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                if (grid.Rows[i].Selected)
                {
                    if (count >= grid.Rows.Count)
                    {
                        clipboard_string.Append(grid.Rows[i].Cells[3].Value + tab);
                        clipboard_string.Append(grid.Rows[i].Cells[4].Value);
                    }
                    else
                    {
                        clipboard_string.Append(grid.Rows[i].Cells[3].Value + tab);
                        clipboard_string.Append(grid.Rows[i].Cells[4].Value + newline);
                    }
                    count++;
                }
            }

            Clipboard.SetText(clipboard_string.ToString());
        }

        private void grid_KeyDown(object sender, KeyEventArgs e)
        {

            if (e.Control && e.KeyCode == Keys.A)
            {
                //e.Handled = true;
            }
            if (grid.SelectedRows.Count <= 0)
                return;
            if ((e.Control && e.KeyCode == Keys.Insert) || (e.Control && e.KeyCode == Keys.C))            //Копирование и вставка выделенных ячеек
            {
                var newline = System.Environment.NewLine;
                var clipboard_string = new StringBuilder();

                int count = 0;
                for (int i = 0; i < grid.Rows.Count; i++)
                {
                    if (grid.Rows[i].Selected)
                    {
                        if (count == 0)
                            clipboard_string.Append(grid.Rows[i].Cells[1].Value);
                        else
                            clipboard_string.Append(newline + grid.Rows[i].Cells[1].Value);
                        count++;
                    }
                }
                Clipboard.SetText(clipboard_string.ToString());
                e.Handled = true;
            }
        }

        private void grid_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex >= 0)
            {
                if (indexCol == e.ColumnIndex)
                {
                    asc = !asc;
                }
                else
                {
                    asc = true;
                    indexCol = e.ColumnIndex;
                }
                //sorting[e.ColumnIndex].asc = !sorting[e.ColumnIndex].asc;
                parser.Sorting(e.ColumnIndex, asc);
            }

            grid.Refresh();
        }

        private void grid_RowPrePaint(object sender, DataGridViewRowPrePaintEventArgs e)
        {
            if (parser.links.Count <= 0)
                return;
            if (e.RowIndex > grid.Rows.Count - 1)
                return;

            int position = parser.GetPositionOrderInList(e.RowIndex);
            if (Settings.Default.podsvetka)
            {
                if (parser.links[position].WL)
                {
                    if (Settings.Default.podsvetkaWL)
                        ((DataGridView)sender).Rows[e.RowIndex].DefaultCellStyle.BackColor = Settings.Default.colorWL;
                    else
                        ((DataGridView)sender).Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.White;
                }
                else
                if ((parser.links[position].noGR == "Да") || (parser.links[position].noYR == "Да"))
                    ((DataGridView)sender).Rows[e.RowIndex].DefaultCellStyle.BackColor = Settings.Default.colorRobots;
                else
                if ((parser.links[position].noindex == "Да") || (parser.links[position].nofollow == "Да"))
                    ((DataGridView)sender).Rows[e.RowIndex].DefaultCellStyle.BackColor = Settings.Default.colorNoindex;

                else
                    if ((parser.links[position].errorHTTP))
                    ((DataGridView)sender).Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.Red;

                else
                {
                    ((DataGridView)sender).Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.White;
                    ((DataGridView)sender).Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.Black;
                }
            }
            else
            {
                ((DataGridView)sender).Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.White;
                ((DataGridView)sender).Rows[e.RowIndex].DefaultCellStyle.ForeColor = Color.Black;
            }
        }


        private void toolStripButton3_Click(object sender, EventArgs e)
        {
            FormAddDomen formAddDomen = new FormAddDomen();
            formAddDomen.rich.Text = parser.domen;
            formAddDomen.ShowDialog();
            if (formAddDomen.go)
            {
                for (int i = 0; i < parser.links.Count; i++)
                {
                    parser.links[i].checkedURL = formAddDomen.domen;
                }
                parser.domen = formAddDomen.domen;
                grid.Refresh();
            }
            else
            {
                parser.domen = "";
                for (int i = 0; i < parser.links.Count; i++)
                {
                    parser.links[i].checkedURL = parser.domen;
                }
                grid.Refresh();
            }
        }

        private void очиститьToolStripMenuItem_Click(object sender, EventArgs e)
        {

            haveBrokenLink = false;

            if (grid.SelectedRows.Count <= 0)
                return;

            foreach (DataGridViewRow row in grid.SelectedRows)
            {
                int position = parser.GetPositionOrderInList(row.Index);
                parser.links[position].error = false;
                parser.links[position].errorHTTP = false;
                parser.links[position].allGood = true;
                parser.links[position].backURL = "";
                parser.links[position].anchor = "";
                parser.links[position].nofollow = "";
                parser.links[position].noindex = "";
                parser.links[position].noGR = "";
                parser.links[position].noYR = "";
                //parser.links[position]. = "";
            }
            parser.haveBroken = false;
            grid.Refresh();


        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            grid.DefaultCellStyle.Font = new Font(Settings.Default.font, Convert.ToInt32(Settings.Default.sizeFont));
        }

        private void Form1_Enter(object sender, EventArgs e)
        {

        }

        private void Form1_Activated(object sender, EventArgs e)
        {
            //if (started)
            //  this.          ();
        }

        private void toolStripButton4_Click(object sender, EventArgs e)
        {
            FormFiltr formFiltr = new FormFiltr();
            formFiltr.parser = parser;
            formFiltr.ShowDialog();
            grid.RowCount = parser.GetCountRow();
            grid_SelectionChanged(this, null);
            
        }

        private void сохранитьПроектToolStripMenuItem_Click(object sender, EventArgs e)
        {
            SaveProject(projectName);
        }

        private void toolStripButton5_Click(object sender, EventArgs e)
        {
            Settings.Default.podsvetka = !Settings.Default.podsvetka;
            Settings.Default.Save();

            if (Settings.Default.podsvetka)
            {
                bPodsvetka.ToolTipText = "Выключить подсветку";
                bPodsvetka.Image = Resources.lamp_light;
            }
            else
            {
                bPodsvetka.ToolTipText = "Включить подсветку";
                bPodsvetka.Image = Resources.lamp;
            }
            grid.Refresh();
        }

        private void grid_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0)
                return;

            //parser.DeleteLink(Convert.ToInt32(grid.SelectedRows[i].Index));

            //int position = parser.GetPositionOrderInList(e.RowIndex);
            if (Convert.ToString(grid.Rows[e.RowIndex].Cells[1].Value)!="")
                Process.Start(Convert.ToString(grid.Rows[e.RowIndex].Cells[1].Value));
        }

        private void grid_DoubleClick(object sender, EventArgs e)
        {

        }

        private void toolStripButton5_Click_1(object sender, EventArgs e)
        {
            if ( MessageBox.Show("Таблица станет пустой. Очистить?", "Предупреждение!",MessageBoxButtons.YesNo)== DialogResult.Yes)
            {
                haveBrokenLink = false;
                parser.Clear();
                grid.RowCount = parser.GetCountRow();
                grid.Refresh();
            }
        }

        private void toolStripButton6_Click(object sender, EventArgs e)
        {
            FormImportLinks formImportLinks = new FormImportLinks();
            formImportLinks.ShowDialog();
            for (int i = 0; i < formImportLinks.links.Count; i++)
            {
                Link tmp_link = new Link();
                tmp_link.url = formImportLinks.links[i];
                parser.AddLink(tmp_link);
                grid.RowCount = parser.GetCountRow();
            }
            grid.Refresh();
        }

        public void export(string path)
        {
            var csv = new StringBuilder();
            var header = "";
            if (!Settings.Default.ankorExport)
            {
                for (int i = 0; i < grid.Columns.Count; i++)
                {
                    if (i + 1 < grid.Columns.Count)
                        header += grid.Columns[i].HeaderText + ";";
                    else
                        header += grid.Columns[i].HeaderText;
                }
                csv.AppendLine(header);

                for (int z = 0; z < grid.Rows.Count; z++)
                {
                    var row = "";
                    for (int i = 0; i < grid.Columns.Count; i++)
                    {
                        if (i + 1 < grid.Columns.Count)
                            row += grid.Rows[z].Cells[i].Value + ";";
                        else
                            row += grid.Rows[z].Cells[i].Value;
                    }
                    csv.AppendLine(row);
                }
            }
            else                //Если стоит галка Обртаный URL + анкор
            {
                for (int z = 0; z < grid.Rows.Count; z++)
                    csv.AppendLine(grid.Rows[z].Cells[3].Value + ";" + grid.Rows[z].Cells[4].Value);
            }


            //Suggestion made by KyleMit
            File.WriteAllText(path, csv.ToString(), Encoding.UTF8);
        }
        private void toolStripButton7_Click(object sender, EventArgs e)
        {
            if (Settings.Default.saveAddress)
            {
                export(Settings.Default.path);
                FormMessage formMessage = new FormMessage();
                formMessage.ShowDialog();
                if (formMessage.open)
                {
                    Process.Start(Settings.Default.path);
                }
            }
            else
                if (ExportFileDialog.ShowDialog() == DialogResult.OK)
            {
                export(ExportFileDialog.FileName);
            }
        }

        private void toolStripButton8_Click(object sender, EventArgs e)
        {
            Settings.Default.podsvetkaWL = !Settings.Default.podsvetkaWL;
            Settings.Default.Save();

            if (Settings.Default.podsvetkaWL)
            {
                bWL.ToolTipText = "Выключить подсветку";
                bWL.Image = Resources.wlyes;

                try
                {

                    using (StreamReader sr = new StreamReader(File.Open("WL.txt", FileMode.Open)))
                    {
                        while (!sr.EndOfStream)
                            WL.Add(sr.ReadLine());
                    }
                    for (int i = 0; i < parser.links.Count; i++)
                    {
                        for (int z = 0; z < WL.Count; z++)
                        {
                            if (parser.AnalyzeURLWL(parser.links[i].url, WL[z]))
                            {
                                parser.links[i].WL = true;
                            }
                        }                        
                    }

                }
                catch (Exception E)
                {
                    Settings.Default.podsvetkaWL = false;
                    bWL.ToolTipText = "Включить подсветку";
                    bWL.Image = Resources.wlno;
                }

            }
            else
            {
                bWL.ToolTipText = "Включить подсветку";
                bWL.Image = Resources.wlno;
            }
            grid.Refresh();
        }

        private void toolStripButton8_Click_1(object sender, EventArgs e)
        {
            parser.ActiveFilter.error = false;
            parser.ActiveFilter.okAllGood = false;
            parser.ActiveFilter.okNofollow = false; 
            parser.ActiveFilter.okNoindex = false;
            parser.ActiveFilter.closeRobotsAll = false;
            parser.ActiveFilter.openRobotsAll = false;
            parser.ActiveFilter.openRobotsYandex = false;
            parser.ActiveFilter.closeRobotsYandex = false;
            parser.ActiveFilter.openRobotsGoogle = false;
            parser.ActiveFilter.closeRobotsGoogle = false;
            parser.ActiveFilter.closeOr = false;
            parser.ActiveFilter.openOr = false;
            parser.ApplyFilter();
            grid.RowCount = parser.GetCountRow();
            grid_SelectionChanged(this, null);

        }

        private void toolStripButton9_Click(object sender, EventArgs e)
        {
            Settings.Default.filtrBroken = !Settings.Default.filtrBroken;
            Settings.Default.Save();

            parser.ActiveFilter.error = Settings.Default.filtrBroken;
            if (!parser.ActiveFilter.error)
            {
                toolStripButton8_Click_1(this, null);
                toolStripButton9.Image = Resources.broken_link_off;
            }
            else
            {
                parser.ApplyFilterError();
                grid.RowCount = parser.GetCountRow();
                grid.Refresh();
                toolStripButton9.Image = Resources.broken_link;
            }
            if (parser.haveBroken)
                toolStripStatusLabel1.Text = "Имеются битые ссылки";
            else
                toolStripStatusLabel1.Text = "";

        }

        private void grid_CellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if ((e.ColumnIndex >=0) && (e.ColumnIndex == indexCol) && (e.RowIndex ==-1))
            {
                e.PaintBackground(e.ClipBounds, false);
                Point pt = e.CellBounds.Location;  // where you want the bitmap in the cell
                if (asc)
                    this.imageList1.Draw(e.Graphics, new Point(e.CellBounds.Right - 20, e.CellBounds.Location.Y + e.CellBounds.Height / 2 - 10), 0);
                else
                    this.imageList1.Draw(e.Graphics, new Point(e.CellBounds.Right - 20, e.CellBounds.Location.Y + e.CellBounds.Height/2-10), 1);
                SolidBrush brush = new SolidBrush(Color.Black);
                e.Graphics.DrawString(e.Value.ToString(), ((MyDGV)sender).ColumnHeadersDefaultCellStyle.Font, brush, new Point(e.CellBounds.Location.X + 4, e.CellBounds.Location.Y + e.CellBounds.Height/2-7));

                e.Handled = true;
            }

        }
    }
}
