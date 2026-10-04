namespace URLChecker
{
    partial class FormProxy
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
            this.chkEnabled = new System.Windows.Forms.CheckBox();
            this.groupServer = new System.Windows.Forms.GroupBox();
            this.lHint = new System.Windows.Forms.Label();
            this.nPort = new System.Windows.Forms.NumericUpDown();
            this.label3 = new System.Windows.Forms.Label();
            this.tHost = new System.Windows.Forms.TextBox();
            this.label2 = new System.Windows.Forms.Label();
            this.cbType = new System.Windows.Forms.ComboBox();
            this.label1 = new System.Windows.Forms.Label();
            this.groupAuth = new System.Windows.Forms.GroupBox();
            this.chkShowPassword = new System.Windows.Forms.CheckBox();
            this.tPassword = new System.Windows.Forms.TextBox();
            this.label5 = new System.Windows.Forms.Label();
            this.tUser = new System.Windows.Forms.TextBox();
            this.label4 = new System.Windows.Forms.Label();
            this.bCheck = new System.Windows.Forms.Button();
            this.lCheck = new System.Windows.Forms.Label();
            this.bSave = new System.Windows.Forms.Button();
            this.bCancel = new System.Windows.Forms.Button();
            this.groupServer.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nPort)).BeginInit();
            this.groupAuth.SuspendLayout();
            this.SuspendLayout();
            // 
            // chkEnabled
            // 
            this.chkEnabled.AutoSize = true;
            this.chkEnabled.Location = new System.Drawing.Point(15, 12);
            this.chkEnabled.Name = "chkEnabled";
            this.chkEnabled.Size = new System.Drawing.Size(132, 17);
            this.chkEnabled.TabIndex = 0;
            this.chkEnabled.Text = "Использовать прокси";
            this.chkEnabled.UseVisualStyleBackColor = true;
            // 
            // groupServer
            // 
            this.groupServer.Controls.Add(this.lHint);
            this.groupServer.Controls.Add(this.nPort);
            this.groupServer.Controls.Add(this.label3);
            this.groupServer.Controls.Add(this.tHost);
            this.groupServer.Controls.Add(this.label2);
            this.groupServer.Controls.Add(this.cbType);
            this.groupServer.Controls.Add(this.label1);
            this.groupServer.Location = new System.Drawing.Point(12, 38);
            this.groupServer.Name = "groupServer";
            this.groupServer.Size = new System.Drawing.Size(360, 112);
            this.groupServer.TabIndex = 1;
            this.groupServer.TabStop = false;
            this.groupServer.Text = "Сервер";
            // 
            // label1
            // 
            this.label1.AutoSize = true;
            this.label1.Location = new System.Drawing.Point(10, 25);
            this.label1.Name = "label1";
            this.label1.Size = new System.Drawing.Size(29, 13);
            this.label1.TabIndex = 0;
            this.label1.Text = "Тип:";
            // 
            // cbType
            // 
            this.cbType.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cbType.FormattingEnabled = true;
            this.cbType.Items.AddRange(new object[] {
            "HTTP / HTTPS",
            "SOCKS4",
            "SOCKS5"});
            this.cbType.Location = new System.Drawing.Point(65, 22);
            this.cbType.Name = "cbType";
            this.cbType.Size = new System.Drawing.Size(140, 21);
            this.cbType.TabIndex = 1;
            // 
            // label2
            // 
            this.label2.AutoSize = true;
            this.label2.Location = new System.Drawing.Point(10, 54);
            this.label2.Name = "label2";
            this.label2.Size = new System.Drawing.Size(41, 13);
            this.label2.TabIndex = 2;
            this.label2.Text = "Адрес:";
            // 
            // tHost
            // 
            this.tHost.Location = new System.Drawing.Point(65, 51);
            this.tHost.Name = "tHost";
            this.tHost.Size = new System.Drawing.Size(170, 20);
            this.tHost.TabIndex = 3;
            this.tHost.Leave += new System.EventHandler(this.tHost_Leave);
            // 
            // label3
            // 
            this.label3.AutoSize = true;
            this.label3.Location = new System.Drawing.Point(245, 54);
            this.label3.Name = "label3";
            this.label3.Size = new System.Drawing.Size(35, 13);
            this.label3.TabIndex = 4;
            this.label3.Text = "Порт:";
            // 
            // nPort
            // 
            this.nPort.Location = new System.Drawing.Point(284, 51);
            this.nPort.Maximum = new decimal(new int[] {
            65535,
            0,
            0,
            0});
            this.nPort.Minimum = new decimal(new int[] {
            1,
            0,
            0,
            0});
            this.nPort.Name = "nPort";
            this.nPort.Size = new System.Drawing.Size(64, 20);
            this.nPort.TabIndex = 5;
            this.nPort.Value = new decimal(new int[] {
            8080,
            0,
            0,
            0});
            // 
            // lHint
            // 
            this.lHint.ForeColor = System.Drawing.SystemColors.GrayText;
            this.lHint.Location = new System.Drawing.Point(10, 78);
            this.lHint.Name = "lHint";
            this.lHint.Size = new System.Drawing.Size(340, 28);
            this.lHint.TabIndex = 6;
            this.lHint.Text = "В поле «Адрес» можно вставить строку вида ip:порт или ip:порт:логин:пароль — она разберётся сама.";
            // 
            // groupAuth
            // 
            this.groupAuth.Controls.Add(this.chkShowPassword);
            this.groupAuth.Controls.Add(this.tPassword);
            this.groupAuth.Controls.Add(this.label5);
            this.groupAuth.Controls.Add(this.tUser);
            this.groupAuth.Controls.Add(this.label4);
            this.groupAuth.Location = new System.Drawing.Point(12, 156);
            this.groupAuth.Name = "groupAuth";
            this.groupAuth.Size = new System.Drawing.Size(360, 82);
            this.groupAuth.TabIndex = 2;
            this.groupAuth.TabStop = false;
            this.groupAuth.Text = "Авторизация (оставьте пустым, если не нужна)";
            // 
            // label4
            // 
            this.label4.AutoSize = true;
            this.label4.Location = new System.Drawing.Point(10, 25);
            this.label4.Name = "label4";
            this.label4.Size = new System.Drawing.Size(41, 13);
            this.label4.TabIndex = 0;
            this.label4.Text = "Логин:";
            // 
            // tUser
            // 
            this.tUser.Location = new System.Drawing.Point(65, 22);
            this.tUser.Name = "tUser";
            this.tUser.Size = new System.Drawing.Size(170, 20);
            this.tUser.TabIndex = 1;
            // 
            // label5
            // 
            this.label5.AutoSize = true;
            this.label5.Location = new System.Drawing.Point(10, 52);
            this.label5.Name = "label5";
            this.label5.Size = new System.Drawing.Size(48, 13);
            this.label5.TabIndex = 2;
            this.label5.Text = "Пароль:";
            // 
            // tPassword
            // 
            this.tPassword.Location = new System.Drawing.Point(65, 49);
            this.tPassword.Name = "tPassword";
            this.tPassword.Size = new System.Drawing.Size(170, 20);
            this.tPassword.TabIndex = 3;
            this.tPassword.UseSystemPasswordChar = true;
            // 
            // chkShowPassword
            // 
            this.chkShowPassword.AutoSize = true;
            this.chkShowPassword.Location = new System.Drawing.Point(245, 51);
            this.chkShowPassword.Name = "chkShowPassword";
            this.chkShowPassword.Size = new System.Drawing.Size(76, 17);
            this.chkShowPassword.TabIndex = 4;
            this.chkShowPassword.Text = "Показать";
            this.chkShowPassword.UseVisualStyleBackColor = true;
            this.chkShowPassword.CheckedChanged += new System.EventHandler(this.chkShowPassword_CheckedChanged);
            // 
            // bCheck
            // 
            this.bCheck.Location = new System.Drawing.Point(12, 248);
            this.bCheck.Name = "bCheck";
            this.bCheck.Size = new System.Drawing.Size(90, 25);
            this.bCheck.TabIndex = 3;
            this.bCheck.Text = "Проверить";
            this.bCheck.UseVisualStyleBackColor = true;
            this.bCheck.Click += new System.EventHandler(this.bCheck_Click);
            // 
            // lCheck
            // 
            this.lCheck.Location = new System.Drawing.Point(108, 244);
            this.lCheck.Name = "lCheck";
            this.lCheck.Size = new System.Drawing.Size(264, 34);
            this.lCheck.TabIndex = 4;
            this.lCheck.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // bSave
            // 
            this.bSave.Location = new System.Drawing.Point(196, 288);
            this.bSave.Name = "bSave";
            this.bSave.Size = new System.Drawing.Size(85, 25);
            this.bSave.TabIndex = 5;
            this.bSave.Text = "Сохранить";
            this.bSave.UseVisualStyleBackColor = true;
            this.bSave.Click += new System.EventHandler(this.bSave_Click);
            // 
            // bCancel
            // 
            this.bCancel.DialogResult = System.Windows.Forms.DialogResult.Cancel;
            this.bCancel.Location = new System.Drawing.Point(287, 288);
            this.bCancel.Name = "bCancel";
            this.bCancel.Size = new System.Drawing.Size(85, 25);
            this.bCancel.TabIndex = 6;
            this.bCancel.Text = "Отмена";
            this.bCancel.UseVisualStyleBackColor = true;
            // 
            // FormProxy
            // 
            this.AcceptButton = this.bSave;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.CancelButton = this.bCancel;
            this.ClientSize = new System.Drawing.Size(384, 325);
            this.Controls.Add(this.bCancel);
            this.Controls.Add(this.bSave);
            this.Controls.Add(this.lCheck);
            this.Controls.Add(this.bCheck);
            this.Controls.Add(this.groupAuth);
            this.Controls.Add(this.groupServer);
            this.Controls.Add(this.chkEnabled);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "FormProxy";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Настройки прокси";
            this.Shown += new System.EventHandler(this.FormProxy_Shown);
            this.groupServer.ResumeLayout(false);
            this.groupServer.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nPort)).EndInit();
            this.groupAuth.ResumeLayout(false);
            this.groupAuth.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.CheckBox chkEnabled;
        private System.Windows.Forms.GroupBox groupServer;
        private System.Windows.Forms.Label lHint;
        private System.Windows.Forms.NumericUpDown nPort;
        private System.Windows.Forms.Label label3;
        private System.Windows.Forms.TextBox tHost;
        private System.Windows.Forms.Label label2;
        private System.Windows.Forms.ComboBox cbType;
        private System.Windows.Forms.Label label1;
        private System.Windows.Forms.GroupBox groupAuth;
        private System.Windows.Forms.CheckBox chkShowPassword;
        private System.Windows.Forms.TextBox tPassword;
        private System.Windows.Forms.Label label5;
        private System.Windows.Forms.TextBox tUser;
        private System.Windows.Forms.Label label4;
        private System.Windows.Forms.Button bCheck;
        private System.Windows.Forms.Label lCheck;
        private System.Windows.Forms.Button bSave;
        private System.Windows.Forms.Button bCancel;
    }
}
