namespace URLChecker
{
    partial class FormAddDomen
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
            this.rich = new System.Windows.Forms.RichTextBox();
            this.button1 = new System.Windows.Forms.Button();
            this.checkBox1 = new System.Windows.Forms.CheckBox();
            this.SuspendLayout();
            // 
            // rich
            // 
            this.rich.Location = new System.Drawing.Point(12, 30);
            this.rich.Name = "rich";
            this.rich.Size = new System.Drawing.Size(251, 125);
            this.rich.TabIndex = 0;
            this.rich.Text = "";
            // 
            // button1
            // 
            this.button1.Location = new System.Drawing.Point(97, 161);
            this.button1.Name = "button1";
            this.button1.Size = new System.Drawing.Size(66, 30);
            this.button1.TabIndex = 1;
            this.button1.Text = "Ок";
            this.button1.UseVisualStyleBackColor = true;
            this.button1.Click += new System.EventHandler(this.button1_Click);
            // 
            // checkBox1
            // 
            this.checkBox1.AutoSize = true;
            this.checkBox1.Checked = global::URLChecker.Properties.Settings.Default.noObrabotka;
            this.checkBox1.DataBindings.Add(new System.Windows.Forms.Binding("Checked", global::URLChecker.Properties.Settings.Default, "noObrabotka", true, System.Windows.Forms.DataSourceUpdateMode.OnPropertyChanged));
            this.checkBox1.Location = new System.Drawing.Point(12, 7);
            this.checkBox1.Name = "checkBox1";
            this.checkBox1.Size = new System.Drawing.Size(156, 17);
            this.checkBox1.TabIndex = 2;
            this.checkBox1.Text = "Не обрабатывать ссылки";
            this.checkBox1.UseVisualStyleBackColor = true;
            // 
            // FormAddDomen
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(275, 198);
            this.Controls.Add(this.checkBox1);
            this.Controls.Add(this.button1);
            this.Controls.Add(this.rich);
            this.MaximumSize = new System.Drawing.Size(291, 236);
            this.MinimumSize = new System.Drawing.Size(291, 236);
            this.Name = "FormAddDomen";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Домен";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion
        private System.Windows.Forms.Button button1;
        public System.Windows.Forms.RichTextBox rich;
        private System.Windows.Forms.CheckBox checkBox1;
    }
}