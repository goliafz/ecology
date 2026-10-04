namespace URLChecker
{
    partial class Form1
    {
        /// <summary>
        /// Обязательная переменная конструктора.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Освободить все используемые ресурсы.
        /// </summary>
        /// <param name="disposing">истинно, если управляемый ресурс должен быть удален; иначе ложно.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Код, автоматически созданный конструктором форм Windows

        /// <summary>
        /// Требуемый метод для поддержки конструктора — не изменяйте 
        /// содержимое этого метода с помощью редактора кода.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.Windows.Forms.DataGridViewCellStyle dataGridViewCellStyle1 = new System.Windows.Forms.DataGridViewCellStyle();
            System.Windows.Forms.DataGridViewCellStyle dataGridViewCellStyle2 = new System.Windows.Forms.DataGridViewCellStyle();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
            this.notifyIcon1 = new System.Windows.Forms.NotifyIcon(this.components);
            this.mainMenu = new System.Windows.Forms.MenuStrip();
            this.файлToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.новыйПроектToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.сохранитьПроектToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.открытьПроектToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.выходToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.настройкиToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.statusMenu = new System.Windows.Forms.StatusStrip();
            this.statusLabel = new System.Windows.Forms.ToolStripStatusLabel();
            this.toolStripStatus = new System.Windows.Forms.ToolStripStatusLabel();
            this.toolStripStatusLabel1 = new System.Windows.Forms.ToolStripStatusLabel();
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage1 = new System.Windows.Forms.TabPage();
            this.grid = new URLChecker.MyDGV();
            this.ColumnN = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.ColumnURL = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.ColumnLink = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.ColumnBackURL = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.ColumnAnkor = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.ColumnNoindex = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.ColumnNofollow = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.ColumnNoYR = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.ColumnNoGR = new System.Windows.Forms.DataGridViewTextBoxColumn();
            this.contextMenuStrip1 = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.добавитьToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.удалитьИзСпискаToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.очиститьToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripSeparator1 = new System.Windows.Forms.ToolStripSeparator();
            this.проверяемыйURLToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.обратныйURLToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.анкорToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.обратныйURLАнкорToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.bPodsvetka = new System.Windows.Forms.ToolStripButton();
            this.toolStrip1 = new System.Windows.Forms.ToolStrip();
            this.toolStripButton1 = new System.Windows.Forms.ToolStripButton();
            this.toolStripButton2 = new System.Windows.Forms.ToolStripButton();
            this.toolStripButton3 = new System.Windows.Forms.ToolStripButton();
            this.toolStripButton4 = new System.Windows.Forms.ToolStripButton();
            this.toolStripButton8 = new System.Windows.Forms.ToolStripButton();
            this.toolStripButton5 = new System.Windows.Forms.ToolStripButton();
            this.toolStripButton6 = new System.Windows.Forms.ToolStripButton();
            this.toolStripButton7 = new System.Windows.Forms.ToolStripButton();
            this.toolStripButton9 = new System.Windows.Forms.ToolStripButton();
            this.bWL = new System.Windows.Forms.ToolStripButton();
            this.bProxy = new System.Windows.Forms.ToolStripSplitButton();
            this.настройкиПроксиToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripProxyStatus = new System.Windows.Forms.ToolStripStatusLabel();
            this.ExportFileDialog = new System.Windows.Forms.SaveFileDialog();
            this.imageList1 = new System.Windows.Forms.ImageList(this.components);
            this.mainMenu.SuspendLayout();
            this.statusMenu.SuspendLayout();
            this.tabControl1.SuspendLayout();
            this.tabPage1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.grid)).BeginInit();
            this.contextMenuStrip1.SuspendLayout();
            this.toolStrip1.SuspendLayout();
            this.SuspendLayout();
            // 
            // notifyIcon1
            // 
            this.notifyIcon1.Text = "notifyIcon1";
            this.notifyIcon1.Visible = true;
            // 
            // mainMenu
            // 
            this.mainMenu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.файлToolStripMenuItem,
            this.настройкиToolStripMenuItem});
            this.mainMenu.Location = new System.Drawing.Point(0, 0);
            this.mainMenu.Name = "mainMenu";
            this.mainMenu.RenderMode = System.Windows.Forms.ToolStripRenderMode.System;
            this.mainMenu.Size = new System.Drawing.Size(800, 24);
            this.mainMenu.TabIndex = 0;
            this.mainMenu.Text = "menuStrip1";
            // 
            // файлToolStripMenuItem
            // 
            this.файлToolStripMenuItem.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.новыйПроектToolStripMenuItem,
            this.сохранитьПроектToolStripMenuItem,
            this.открытьПроектToolStripMenuItem,
            this.выходToolStripMenuItem});
            this.файлToolStripMenuItem.Name = "файлToolStripMenuItem";
            this.файлToolStripMenuItem.Size = new System.Drawing.Size(48, 20);
            this.файлToolStripMenuItem.Text = "Файл";
            // 
            // новыйПроектToolStripMenuItem
            // 
            this.новыйПроектToolStripMenuItem.Name = "новыйПроектToolStripMenuItem";
            this.новыйПроектToolStripMenuItem.Size = new System.Drawing.Size(173, 22);
            this.новыйПроектToolStripMenuItem.Text = "Новый проект";
            // 
            // сохранитьПроектToolStripMenuItem
            // 
            this.сохранитьПроектToolStripMenuItem.Name = "сохранитьПроектToolStripMenuItem";
            this.сохранитьПроектToolStripMenuItem.Size = new System.Drawing.Size(173, 22);
            this.сохранитьПроектToolStripMenuItem.Text = "Сохранить проект";
            this.сохранитьПроектToolStripMenuItem.Click += new System.EventHandler(this.сохранитьПроектToolStripMenuItem_Click);
            // 
            // открытьПроектToolStripMenuItem
            // 
            this.открытьПроектToolStripMenuItem.Name = "открытьПроектToolStripMenuItem";
            this.открытьПроектToolStripMenuItem.Size = new System.Drawing.Size(173, 22);
            this.открытьПроектToolStripMenuItem.Text = "Открыть проект";
            // 
            // выходToolStripMenuItem
            // 
            this.выходToolStripMenuItem.Name = "выходToolStripMenuItem";
            this.выходToolStripMenuItem.Size = new System.Drawing.Size(173, 22);
            this.выходToolStripMenuItem.Text = "Выход";
            // 
            // настройкиToolStripMenuItem
            // 
            this.настройкиToolStripMenuItem.Name = "настройкиToolStripMenuItem";
            this.настройкиToolStripMenuItem.Size = new System.Drawing.Size(79, 20);
            this.настройкиToolStripMenuItem.Text = "Настройки";
            this.настройкиToolStripMenuItem.Click += new System.EventHandler(this.настройкиToolStripMenuItem_Click);
            // 
            // statusMenu
            // 
            this.statusMenu.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.statusLabel,
            this.toolStripStatus,
            this.toolStripStatusLabel1,
            this.toolStripProxyStatus});
            this.statusMenu.Location = new System.Drawing.Point(0, 428);
            this.statusMenu.Name = "statusMenu";
            this.statusMenu.Size = new System.Drawing.Size(800, 22);
            this.statusMenu.TabIndex = 1;
            this.statusMenu.Text = "statusStrip1";
            // 
            // statusLabel
            // 
            this.statusLabel.Name = "statusLabel";
            this.statusLabel.Size = new System.Drawing.Size(0, 17);
            // 
            // toolStripStatus
            // 
            this.toolStripStatus.Name = "toolStripStatus";
            this.toolStripStatus.Size = new System.Drawing.Size(0, 17);
            // 
            // toolStripStatusLabel1
            // 
            this.toolStripStatusLabel1.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(192)))), ((int)(((byte)(0)))), ((int)(((byte)(0)))));
            this.toolStripStatusLabel1.Name = "toolStripStatusLabel1";
            this.toolStripStatusLabel1.Size = new System.Drawing.Size(0, 17);
            // 
            // toolStripProxyStatus
            // 
            this.toolStripProxyStatus.Name = "toolStripProxyStatus";
            this.toolStripProxyStatus.Size = new System.Drawing.Size(0, 17);
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabPage1);
            this.tabControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl1.Location = new System.Drawing.Point(0, 91);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(800, 337);
            this.tabControl1.TabIndex = 3;
            // 
            // tabPage1
            // 
            this.tabPage1.Controls.Add(this.grid);
            this.tabPage1.Location = new System.Drawing.Point(4, 22);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Padding = new System.Windows.Forms.Padding(3);
            this.tabPage1.Size = new System.Drawing.Size(792, 311);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "Работа с данными";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // grid
            // 
            this.grid.AllowUserToAddRows = false;
            this.grid.AllowUserToResizeRows = false;
            this.grid.ClipboardCopyMode = System.Windows.Forms.DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            dataGridViewCellStyle1.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleLeft;
            dataGridViewCellStyle1.BackColor = System.Drawing.SystemColors.Control;
            dataGridViewCellStyle1.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(204)));
            dataGridViewCellStyle1.ForeColor = System.Drawing.SystemColors.WindowText;
            dataGridViewCellStyle1.SelectionBackColor = System.Drawing.SystemColors.Highlight;
            dataGridViewCellStyle1.SelectionForeColor = System.Drawing.SystemColors.Control;
            dataGridViewCellStyle1.WrapMode = System.Windows.Forms.DataGridViewTriState.False;
            this.grid.ColumnHeadersDefaultCellStyle = dataGridViewCellStyle1;
            this.grid.ColumnHeadersHeight = 32;
            this.grid.Columns.AddRange(new System.Windows.Forms.DataGridViewColumn[] {
            this.ColumnN,
            this.ColumnURL,
            this.ColumnLink,
            this.ColumnBackURL,
            this.ColumnAnkor,
            this.ColumnNoindex,
            this.ColumnNofollow,
            this.ColumnNoYR,
            this.ColumnNoGR});
            this.grid.ContextMenuStrip = this.contextMenuStrip1;
            dataGridViewCellStyle2.Alignment = System.Windows.Forms.DataGridViewContentAlignment.MiddleLeft;
            dataGridViewCellStyle2.BackColor = System.Drawing.SystemColors.Window;
            dataGridViewCellStyle2.Font = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(204)));
            dataGridViewCellStyle2.ForeColor = System.Drawing.SystemColors.ControlText;
            dataGridViewCellStyle2.SelectionBackColor = System.Drawing.SystemColors.ControlLight;
            dataGridViewCellStyle2.SelectionForeColor = System.Drawing.Color.Black;
            dataGridViewCellStyle2.WrapMode = System.Windows.Forms.DataGridViewTriState.False;
            this.grid.DefaultCellStyle = dataGridViewCellStyle2;
            this.grid.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grid.GridColor = System.Drawing.SystemColors.Control;
            this.grid.Location = new System.Drawing.Point(3, 3);
            this.grid.Name = "grid";
            this.grid.ReadOnly = true;
            this.grid.RowHeadersVisible = false;
            this.grid.SelectionMode = System.Windows.Forms.DataGridViewSelectionMode.FullRowSelect;
            this.grid.Size = new System.Drawing.Size(786, 305);
            this.grid.TabIndex = 0;
            this.grid.VirtualMode = true;
            this.grid.CellDoubleClick += new System.Windows.Forms.DataGridViewCellEventHandler(this.grid_CellDoubleClick);
            this.grid.CellPainting += new System.Windows.Forms.DataGridViewCellPaintingEventHandler(this.grid_CellPainting);
            this.grid.CellValueNeeded += new System.Windows.Forms.DataGridViewCellValueEventHandler(this.grid_CellValueNeeded);
            this.grid.ColumnHeaderMouseClick += new System.Windows.Forms.DataGridViewCellMouseEventHandler(this.grid_ColumnHeaderMouseClick);
            this.grid.RowPrePaint += new System.Windows.Forms.DataGridViewRowPrePaintEventHandler(this.grid_RowPrePaint);
            this.grid.SelectionChanged += new System.EventHandler(this.grid_SelectionChanged);
            this.grid.KeyDown += new System.Windows.Forms.KeyEventHandler(this.grid_KeyDown);
            // 
            // ColumnN
            // 
            this.ColumnN.DataPropertyName = "ColumnN";
            this.ColumnN.HeaderText = "№";
            this.ColumnN.Name = "ColumnN";
            this.ColumnN.ReadOnly = true;
            // 
            // ColumnURL
            // 
            this.ColumnURL.DataPropertyName = "ColumnURL";
            this.ColumnURL.HeaderText = "URL";
            this.ColumnURL.Name = "ColumnURL";
            this.ColumnURL.ReadOnly = true;
            // 
            // ColumnLink
            // 
            this.ColumnLink.DataPropertyName = "ColumnLink";
            this.ColumnLink.HeaderText = "Проверяемая сылка";
            this.ColumnLink.Name = "ColumnLink";
            this.ColumnLink.ReadOnly = true;
            // 
            // ColumnBackURL
            // 
            this.ColumnBackURL.DataPropertyName = "ColumnBackURL";
            this.ColumnBackURL.HeaderText = "Обратная ссылка";
            this.ColumnBackURL.Name = "ColumnBackURL";
            this.ColumnBackURL.ReadOnly = true;
            // 
            // ColumnAnkor
            // 
            this.ColumnAnkor.DataPropertyName = "ColumnAnkor";
            this.ColumnAnkor.HeaderText = "Анкор";
            this.ColumnAnkor.Name = "ColumnAnkor";
            this.ColumnAnkor.ReadOnly = true;
            // 
            // ColumnNoindex
            // 
            this.ColumnNoindex.DataPropertyName = "ColumnNoindex";
            this.ColumnNoindex.HeaderText = "Noindex";
            this.ColumnNoindex.Name = "ColumnNoindex";
            this.ColumnNoindex.ReadOnly = true;
            // 
            // ColumnNofollow
            // 
            this.ColumnNofollow.DataPropertyName = "ColumnNofollow";
            this.ColumnNofollow.HeaderText = "Nofollow";
            this.ColumnNofollow.Name = "ColumnNofollow";
            this.ColumnNofollow.ReadOnly = true;
            // 
            // ColumnNoYR
            // 
            this.ColumnNoYR.DataPropertyName = "ColumnNoYR";
            this.ColumnNoYR.HeaderText = "No YR";
            this.ColumnNoYR.Name = "ColumnNoYR";
            this.ColumnNoYR.ReadOnly = true;
            // 
            // ColumnNoGR
            // 
            this.ColumnNoGR.DataPropertyName = "ColumnNoGR";
            this.ColumnNoGR.HeaderText = "No GR";
            this.ColumnNoGR.Name = "ColumnNoGR";
            this.ColumnNoGR.ReadOnly = true;
            // 
            // contextMenuStrip1
            // 
            this.contextMenuStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.добавитьToolStripMenuItem,
            this.удалитьИзСпискаToolStripMenuItem,
            this.очиститьToolStripMenuItem,
            this.toolStripSeparator1,
            this.проверяемыйURLToolStripMenuItem,
            this.обратныйURLToolStripMenuItem,
            this.анкорToolStripMenuItem,
            this.обратныйURLАнкорToolStripMenuItem});
            this.contextMenuStrip1.Name = "contextMenuStrip1";
            this.contextMenuStrip1.Size = new System.Drawing.Size(203, 164);
            // 
            // добавитьToolStripMenuItem
            // 
            this.добавитьToolStripMenuItem.Name = "добавитьToolStripMenuItem";
            this.добавитьToolStripMenuItem.Size = new System.Drawing.Size(202, 22);
            this.добавитьToolStripMenuItem.Text = "Добавить";
            this.добавитьToolStripMenuItem.Click += new System.EventHandler(this.добавитьToolStripMenuItem_Click);
            // 
            // удалитьИзСпискаToolStripMenuItem
            // 
            this.удалитьИзСпискаToolStripMenuItem.Name = "удалитьИзСпискаToolStripMenuItem";
            this.удалитьИзСпискаToolStripMenuItem.Size = new System.Drawing.Size(202, 22);
            this.удалитьИзСпискаToolStripMenuItem.Text = "Удалить из списка";
            this.удалитьИзСпискаToolStripMenuItem.Click += new System.EventHandler(this.удалитьИзСпискаToolStripMenuItem_Click);
            // 
            // очиститьToolStripMenuItem
            // 
            this.очиститьToolStripMenuItem.Name = "очиститьToolStripMenuItem";
            this.очиститьToolStripMenuItem.Size = new System.Drawing.Size(202, 22);
            this.очиститьToolStripMenuItem.Text = "Сбросить результаты";
            this.очиститьToolStripMenuItem.Click += new System.EventHandler(this.очиститьToolStripMenuItem_Click);
            // 
            // toolStripSeparator1
            // 
            this.toolStripSeparator1.Name = "toolStripSeparator1";
            this.toolStripSeparator1.Size = new System.Drawing.Size(199, 6);
            // 
            // проверяемыйURLToolStripMenuItem
            // 
            this.проверяемыйURLToolStripMenuItem.Name = "проверяемыйURLToolStripMenuItem";
            this.проверяемыйURLToolStripMenuItem.Size = new System.Drawing.Size(202, 22);
            this.проверяемыйURLToolStripMenuItem.Text = "Проверяемый URL";
            this.проверяемыйURLToolStripMenuItem.Click += new System.EventHandler(this.проверяемыйURLToolStripMenuItem_Click);
            // 
            // обратныйURLToolStripMenuItem
            // 
            this.обратныйURLToolStripMenuItem.Name = "обратныйURLToolStripMenuItem";
            this.обратныйURLToolStripMenuItem.Size = new System.Drawing.Size(202, 22);
            this.обратныйURLToolStripMenuItem.Text = "Обратный URL";
            this.обратныйURLToolStripMenuItem.Click += new System.EventHandler(this.обратныйURLToolStripMenuItem_Click);
            // 
            // анкорToolStripMenuItem
            // 
            this.анкорToolStripMenuItem.Name = "анкорToolStripMenuItem";
            this.анкорToolStripMenuItem.Size = new System.Drawing.Size(202, 22);
            this.анкорToolStripMenuItem.Text = "Анкор";
            this.анкорToolStripMenuItem.Click += new System.EventHandler(this.анкорToolStripMenuItem_Click);
            // 
            // обратныйURLАнкорToolStripMenuItem
            // 
            this.обратныйURLАнкорToolStripMenuItem.Name = "обратныйURLАнкорToolStripMenuItem";
            this.обратныйURLАнкорToolStripMenuItem.Size = new System.Drawing.Size(202, 22);
            this.обратныйURLАнкорToolStripMenuItem.Text = "Обратный URL + анкор";
            this.обратныйURLАнкорToolStripMenuItem.Click += new System.EventHandler(this.обратныйURLАнкорToolStripMenuItem_Click);
            // 
            // bPodsvetka
            // 
            this.bPodsvetka.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.bPodsvetka.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.bPodsvetka.Name = "bPodsvetka";
            this.bPodsvetka.Size = new System.Drawing.Size(68, 64);
            this.bPodsvetka.Text = "Подсветка";
            this.bPodsvetka.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.bPodsvetka.ToolTipText = "Включить подсветку строк";
            this.bPodsvetka.Click += new System.EventHandler(this.toolStripButton5_Click);
            // 
            // toolStrip1
            // 
            this.toolStrip1.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.toolStripButton1,
            this.toolStripButton2,
            this.toolStripButton3,
            this.toolStripButton4,
            this.toolStripButton8,
            this.bPodsvetka,
            this.toolStripButton5,
            this.toolStripButton6,
            this.toolStripButton7,
            this.toolStripButton9,
            this.bWL,
            this.bProxy});
            this.toolStrip1.Location = new System.Drawing.Point(0, 24);
            this.toolStrip1.Name = "toolStrip1";
            this.toolStrip1.RenderMode = System.Windows.Forms.ToolStripRenderMode.System;
            this.toolStrip1.Size = new System.Drawing.Size(800, 67);
            this.toolStrip1.TabIndex = 2;
            this.toolStrip1.Text = "toolStrip1";
            // 
            // toolStripButton1
            // 
            this.toolStripButton1.Image = global::URLChecker.Properties.Resources.play1;
            this.toolStripButton1.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripButton1.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton1.Name = "toolStripButton1";
            this.toolStripButton1.Size = new System.Drawing.Size(72, 64);
            this.toolStripButton1.Text = "Заупустить";
            this.toolStripButton1.TextAlign = System.Drawing.ContentAlignment.BottomCenter;
            this.toolStripButton1.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.toolStripButton1.Click += new System.EventHandler(this.toolStripButton1_Click);
            // 
            // toolStripButton2
            // 
            this.toolStripButton2.Enabled = false;
            this.toolStripButton2.Image = global::URLChecker.Properties.Resources.stop2;
            this.toolStripButton2.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripButton2.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton2.Name = "toolStripButton2";
            this.toolStripButton2.Size = new System.Drawing.Size(75, 64);
            this.toolStripButton2.Text = "Остановить";
            this.toolStripButton2.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.toolStripButton2.Click += new System.EventHandler(this.toolStripButton2_Click);
            // 
            // toolStripButton3
            // 
            this.toolStripButton3.Image = global::URLChecker.Properties.Resources.domen;
            this.toolStripButton3.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripButton3.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton3.Name = "toolStripButton3";
            this.toolStripButton3.Size = new System.Drawing.Size(49, 64);
            this.toolStripButton3.Text = "Домен";
            this.toolStripButton3.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.toolStripButton3.Click += new System.EventHandler(this.toolStripButton3_Click);
            // 
            // toolStripButton4
            // 
            this.toolStripButton4.Image = global::URLChecker.Properties.Resources.filter1;
            this.toolStripButton4.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripButton4.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton4.Name = "toolStripButton4";
            this.toolStripButton4.Size = new System.Drawing.Size(56, 64);
            this.toolStripButton4.Text = "Фильтр";
            this.toolStripButton4.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.toolStripButton4.ToolTipText = "Фильтр";
            this.toolStripButton4.Click += new System.EventHandler(this.toolStripButton4_Click);
            // 
            // toolStripButton8
            // 
            this.toolStripButton8.Image = global::URLChecker.Properties.Resources.filter_no;
            this.toolStripButton8.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripButton8.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton8.Name = "toolStripButton8";
            this.toolStripButton8.Size = new System.Drawing.Size(64, 64);
            this.toolStripButton8.Text = "Сбросить";
            this.toolStripButton8.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.toolStripButton8.ToolTipText = "Фильтр";
            this.toolStripButton8.Click += new System.EventHandler(this.toolStripButton8_Click_1);
            // 
            // toolStripButton5
            // 
            this.toolStripButton5.Image = global::URLChecker.Properties.Resources.clear;
            this.toolStripButton5.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripButton5.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton5.Name = "toolStripButton5";
            this.toolStripButton5.Size = new System.Drawing.Size(63, 64);
            this.toolStripButton5.Text = "Очистить";
            this.toolStripButton5.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.toolStripButton5.Click += new System.EventHandler(this.toolStripButton5_Click_1);
            // 
            // toolStripButton6
            // 
            this.toolStripButton6.Image = global::URLChecker.Properties.Resources.import;
            this.toolStripButton6.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripButton6.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton6.Name = "toolStripButton6";
            this.toolStripButton6.Size = new System.Drawing.Size(55, 64);
            this.toolStripButton6.Text = "Импорт";
            this.toolStripButton6.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.toolStripButton6.Click += new System.EventHandler(this.toolStripButton6_Click);
            // 
            // toolStripButton7
            // 
            this.toolStripButton7.Image = global::URLChecker.Properties.Resources.export2;
            this.toolStripButton7.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripButton7.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton7.Name = "toolStripButton7";
            this.toolStripButton7.Size = new System.Drawing.Size(56, 64);
            this.toolStripButton7.Text = "Экспорт";
            this.toolStripButton7.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.toolStripButton7.Click += new System.EventHandler(this.toolStripButton7_Click);
            // 
            // toolStripButton9
            // 
            this.toolStripButton9.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.toolStripButton9.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.toolStripButton9.Name = "toolStripButton9";
            this.toolStripButton9.Size = new System.Drawing.Size(45, 64);
            this.toolStripButton9.Text = "Битые";
            this.toolStripButton9.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.toolStripButton9.Click += new System.EventHandler(this.toolStripButton9_Click);
            // 
            // bWL
            // 
            this.bWL.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.bWL.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.bWL.Name = "bWL";
            this.bWL.Size = new System.Drawing.Size(60, 64);
            this.bWL.Text = "White list";
            this.bWL.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.bWL.Click += new System.EventHandler(this.toolStripButton8_Click);
            // 
            // bProxy
            // 
            this.bProxy.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.настройкиПроксиToolStripMenuItem});
            this.bProxy.ImageScaling = System.Windows.Forms.ToolStripItemImageScaling.None;
            this.bProxy.ImageTransparentColor = System.Drawing.Color.Magenta;
            this.bProxy.Name = "bProxy";
            this.bProxy.Size = new System.Drawing.Size(64, 64);
            this.bProxy.Text = "Прокси";
            this.bProxy.TextImageRelation = System.Windows.Forms.TextImageRelation.ImageAboveText;
            this.bProxy.ButtonClick += new System.EventHandler(this.bProxy_ButtonClick);
            // 
            // настройкиПроксиToolStripMenuItem
            // 
            this.настройкиПроксиToolStripMenuItem.Name = "настройкиПроксиToolStripMenuItem";
            this.настройкиПроксиToolStripMenuItem.Size = new System.Drawing.Size(180, 22);
            this.настройкиПроксиToolStripMenuItem.Text = "Настройки прокси...";
            this.настройкиПроксиToolStripMenuItem.Click += new System.EventHandler(this.настройкиПроксиToolStripMenuItem_Click);
            // 
            // ExportFileDialog
            // 
            this.ExportFileDialog.DefaultExt = "*.csv";
            this.ExportFileDialog.Filter = "CSV файлы|*.csv";
            // 
            // imageList1
            // 
            this.imageList1.ImageStream = ((System.Windows.Forms.ImageListStreamer)(resources.GetObject("imageList1.ImageStream")));
            this.imageList1.TransparentColor = System.Drawing.Color.Transparent;
            this.imageList1.Images.SetKeyName(0, "down.png");
            this.imageList1.Images.SetKeyName(1, "up.png");
            // 
            // Form1
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.tabControl1);
            this.Controls.Add(this.toolStrip1);
            this.Controls.Add(this.statusMenu);
            this.Controls.Add(this.mainMenu);
            this.MainMenuStrip = this.mainMenu;
            this.Name = "Form1";
            this.Text = "URLChecker";
            this.WindowState = System.Windows.Forms.FormWindowState.Maximized;
            this.Activated += new System.EventHandler(this.Form1_Activated);
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Form1_FormClosing);
            this.Shown += new System.EventHandler(this.Form1_Shown);
            this.Enter += new System.EventHandler(this.Form1_Enter);
            this.mainMenu.ResumeLayout(false);
            this.mainMenu.PerformLayout();
            this.statusMenu.ResumeLayout(false);
            this.statusMenu.PerformLayout();
            this.tabControl1.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.grid)).EndInit();
            this.contextMenuStrip1.ResumeLayout(false);
            this.toolStrip1.ResumeLayout(false);
            this.toolStrip1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.NotifyIcon notifyIcon1;
        private System.Windows.Forms.MenuStrip mainMenu;
        private System.Windows.Forms.StatusStrip statusMenu;
        private System.Windows.Forms.ToolStripMenuItem файлToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem новыйПроектToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem открытьПроектToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem выходToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem настройкиToolStripMenuItem;
        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage1;        
        private System.Windows.Forms.ContextMenuStrip contextMenuStrip1;
        private System.Windows.Forms.ToolStripMenuItem добавитьToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem удалитьИзСпискаToolStripMenuItem;
        private System.Windows.Forms.ToolStripStatusLabel statusLabel;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator1;
        private System.Windows.Forms.ToolStripMenuItem проверяемыйURLToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem обратныйURLToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem анкорToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem обратныйURLАнкорToolStripMenuItem;
        private System.Windows.Forms.ToolStripMenuItem очиститьToolStripMenuItem;
        private System.Windows.Forms.ToolStripStatusLabel toolStripStatus;
        private System.Windows.Forms.ToolStripMenuItem сохранитьПроектToolStripMenuItem;
        private MyDGV grid;
        private System.Windows.Forms.DataGridViewTextBoxColumn ColumnN;
        private System.Windows.Forms.DataGridViewTextBoxColumn ColumnURL;
        private System.Windows.Forms.DataGridViewTextBoxColumn ColumnLink;
        private System.Windows.Forms.DataGridViewTextBoxColumn ColumnBackURL;
        private System.Windows.Forms.DataGridViewTextBoxColumn ColumnAnkor;
        private System.Windows.Forms.DataGridViewTextBoxColumn ColumnNoindex;
        private System.Windows.Forms.DataGridViewTextBoxColumn ColumnNofollow;
        private System.Windows.Forms.DataGridViewTextBoxColumn ColumnNoYR;
        private System.Windows.Forms.DataGridViewTextBoxColumn ColumnNoGR;
        private System.Windows.Forms.ToolStripButton toolStripButton1;
        private System.Windows.Forms.ToolStripButton toolStripButton2;
        private System.Windows.Forms.ToolStripButton toolStripButton3;
        private System.Windows.Forms.ToolStripButton toolStripButton4;
        private System.Windows.Forms.ToolStripButton bPodsvetka;
        private System.Windows.Forms.ToolStripButton toolStripButton5;
        private System.Windows.Forms.ToolStrip toolStrip1;
        private System.Windows.Forms.ToolStripButton toolStripButton6;
        private System.Windows.Forms.ToolStripButton toolStripButton7;
        private System.Windows.Forms.SaveFileDialog ExportFileDialog;
        private System.Windows.Forms.ToolStripButton bWL;
        private System.Windows.Forms.ToolStripSplitButton bProxy;
        private System.Windows.Forms.ToolStripMenuItem настройкиПроксиToolStripMenuItem;
        private System.Windows.Forms.ToolStripStatusLabel toolStripProxyStatus;
        private System.Windows.Forms.ToolStripButton toolStripButton8;
        private System.Windows.Forms.ToolStripStatusLabel toolStripStatusLabel1;
        private System.Windows.Forms.ToolStripButton toolStripButton9;
        private System.Windows.Forms.ImageList imageList1;
    }
}

