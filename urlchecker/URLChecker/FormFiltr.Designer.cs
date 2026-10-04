namespace URLChecker
{
    partial class FormFiltr
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.okAllgood = new System.Windows.Forms.CheckBox();
            this.okNoindex = new System.Windows.Forms.CheckBox();
            this.okNofollow = new System.Windows.Forms.CheckBox();
            this.panel1 = new System.Windows.Forms.Panel();
            this.closeGoogle = new System.Windows.Forms.RadioButton();
            this.openGoogle = new System.Windows.Forms.RadioButton();
            this.closeYandex = new System.Windows.Forms.RadioButton();
            this.openYandex = new System.Windows.Forms.RadioButton();
            this.openAll = new System.Windows.Forms.RadioButton();
            this.closeAll = new System.Windows.Forms.RadioButton();
            this.button1 = new System.Windows.Forms.Button();
            this.button2 = new System.Windows.Forms.Button();
            this.closeOr = new System.Windows.Forms.RadioButton();
            this.openOr = new System.Windows.Forms.RadioButton();
            this.panel1.SuspendLayout();
            this.SuspendLayout();
            // 
            // okAllgood
            // 
            this.okAllgood.AutoSize = true;
            this.okAllgood.Location = new System.Drawing.Point(12, 17);
            this.okAllgood.Name = "okAllgood";
            this.okAllgood.Size = new System.Drawing.Size(166, 17);
            this.okAllgood.TabIndex = 0;
            this.okAllgood.Text = "Есть и полностью доступна";
            this.okAllgood.UseVisualStyleBackColor = true;
            // 
            // okNoindex
            // 
            this.okNoindex.AutoSize = true;
            this.okNoindex.Location = new System.Drawing.Point(12, 40);
            this.okNoindex.Name = "okNoindex";
            this.okNoindex.Size = new System.Drawing.Size(119, 17);
            this.okNoindex.TabIndex = 1;
            this.okNoindex.Text = "Есть, но в Noindex";
            this.okNoindex.UseVisualStyleBackColor = true;
            // 
            // okNofollow
            // 
            this.okNofollow.AutoSize = true;
            this.okNofollow.Location = new System.Drawing.Point(12, 63);
            this.okNofollow.Name = "okNofollow";
            this.okNofollow.Size = new System.Drawing.Size(121, 17);
            this.okNofollow.TabIndex = 2;
            this.okNofollow.Text = "Есть, но в Nofollow";
            this.okNofollow.UseVisualStyleBackColor = true;
            // 
            // panel1
            // 
            this.panel1.Controls.Add(this.closeOr);
            this.panel1.Controls.Add(this.openOr);
            this.panel1.Controls.Add(this.closeGoogle);
            this.panel1.Controls.Add(this.openGoogle);
            this.panel1.Controls.Add(this.closeYandex);
            this.panel1.Controls.Add(this.openYandex);
            this.panel1.Controls.Add(this.openAll);
            this.panel1.Controls.Add(this.closeAll);
            this.panel1.Location = new System.Drawing.Point(12, 86);
            this.panel1.Name = "panel1";
            this.panel1.Size = new System.Drawing.Size(532, 129);
            this.panel1.TabIndex = 3;
            // 
            // closeGoogle
            // 
            this.closeGoogle.AutoSize = true;
            this.closeGoogle.Location = new System.Drawing.Point(15, 60);
            this.closeGoogle.Name = "closeGoogle";
            this.closeGoogle.Size = new System.Drawing.Size(182, 17);
            this.closeGoogle.TabIndex = 5;
            this.closeGoogle.TabStop = true;
            this.closeGoogle.Text = "Закрыто в robots.txt для Google";
            this.closeGoogle.UseVisualStyleBackColor = true;
            // 
            // openGoogle
            // 
            this.openGoogle.AutoSize = true;
            this.openGoogle.Location = new System.Drawing.Point(269, 60);
            this.openGoogle.Name = "openGoogle";
            this.openGoogle.Size = new System.Drawing.Size(182, 17);
            this.openGoogle.TabIndex = 4;
            this.openGoogle.TabStop = true;
            this.openGoogle.Text = "Открыто в robots.txt для Google";
            this.openGoogle.UseVisualStyleBackColor = true;
            // 
            // closeYandex
            // 
            this.closeYandex.AutoSize = true;
            this.closeYandex.Location = new System.Drawing.Point(15, 37);
            this.closeYandex.Name = "closeYandex";
            this.closeYandex.Size = new System.Drawing.Size(184, 17);
            this.closeYandex.TabIndex = 3;
            this.closeYandex.TabStop = true;
            this.closeYandex.Text = "Закрыто в robots.txt для Yandex";
            this.closeYandex.UseVisualStyleBackColor = true;
            // 
            // openYandex
            // 
            this.openYandex.AutoSize = true;
            this.openYandex.Location = new System.Drawing.Point(269, 37);
            this.openYandex.Name = "openYandex";
            this.openYandex.Size = new System.Drawing.Size(184, 17);
            this.openYandex.TabIndex = 2;
            this.openYandex.TabStop = true;
            this.openYandex.Text = "Открыто в robots.txt для Yandex";
            this.openYandex.UseVisualStyleBackColor = true;
            // 
            // openAll
            // 
            this.openAll.AutoSize = true;
            this.openAll.Location = new System.Drawing.Point(269, 14);
            this.openAll.Name = "openAll";
            this.openAll.Size = new System.Drawing.Size(248, 17);
            this.openAll.TabIndex = 1;
            this.openAll.TabStop = true;
            this.openAll.Text = "Открыто в robots.txt для любого поисковика";
            this.openAll.UseVisualStyleBackColor = true;
            // 
            // closeAll
            // 
            this.closeAll.AutoSize = true;
            this.closeAll.Location = new System.Drawing.Point(15, 14);
            this.closeAll.Name = "closeAll";
            this.closeAll.Size = new System.Drawing.Size(248, 17);
            this.closeAll.TabIndex = 0;
            this.closeAll.TabStop = true;
            this.closeAll.Text = "Закрыто в robots.txt для любого поисковика";
            this.closeAll.UseVisualStyleBackColor = true;
            // 
            // button1
            // 
            this.button1.Location = new System.Drawing.Point(187, 221);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(75, 30);
            this.button1.TabIndex = 4;
            this.button1.Text = "Ок";
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // button2
            // 
            this.button2.Location = new System.Drawing.Point(268, 221);
            this.button2.Name = "button2";
            this.button2.Size = new System.Drawing.Size(75, 30);
            this.button2.TabIndex = 5;
            this.button2.Text = "Сбросить";
            this.button2.UseVisualStyleBackColor = true;
            this.button2.Click += new System.EventHandler(this.button2_Click);
            // 
            // closeOr
            // 
            this.closeOr.AutoSize = true;
            this.closeOr.Location = new System.Drawing.Point(15, 83);
            this.closeOr.Name = "closeOr";
            this.closeOr.Size = new System.Drawing.Size(124, 17);
            this.closeOr.TabIndex = 7;
            this.closeOr.TabStop = true;
            this.closeOr.Text = "Закрыто в robots.txt";
            this.closeOr.UseVisualStyleBackColor = true;
            // 
            // openOr
            // 
            this.openOr.AutoSize = true;
            this.openOr.Location = new System.Drawing.Point(269, 83);
            this.openOr.Name = "openOr";
            this.openOr.Size = new System.Drawing.Size(124, 17);
            this.openOr.TabIndex = 6;
            this.openOr.TabStop = true;
            this.openOr.Text = "Открыто в robots.txt";
            this.openOr.UseVisualStyleBackColor = true;
            // 
            // FormFiltr
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(556, 259);
            this.Controls.Add(this.button2);
            this.Controls.Add(this.button1);
            this.Controls.Add(this.panel1);
            this.Controls.Add(this.okNofollow);
            this.Controls.Add(this.okNoindex);
            this.Controls.Add(this.okAllgood);
            this.Name = "FormFiltr";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Фильтр";
            this.Shown += new System.EventHandler(this.FormFiltr_Shown);
            this.panel1.ResumeLayout(false);
            this.panel1.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.CheckBox okAllgood;
        private System.Windows.Forms.CheckBox okNoindex;
        private System.Windows.Forms.CheckBox okNofollow;
        private System.Windows.Forms.Panel panel1;
        private System.Windows.Forms.RadioButton closeYandex;
        private System.Windows.Forms.RadioButton openYandex;
        private System.Windows.Forms.RadioButton openAll;
        private System.Windows.Forms.RadioButton closeAll;
        private System.Windows.Forms.RadioButton closeGoogle;
        private System.Windows.Forms.RadioButton openGoogle;
        private System.Windows.Forms.Button button1;
        private System.Windows.Forms.Button button2;
        private System.Windows.Forms.RadioButton closeOr;
        private System.Windows.Forms.RadioButton openOr;
    }
}