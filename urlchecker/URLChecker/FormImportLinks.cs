using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace URLChecker
{
    public partial class FormImportLinks : Form
    {
        public List<string> links = new List<string>();
        public FormImportLinks()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < richTextBox1.Lines.Length; i++)
            {
                links.Add(richTextBox1.Lines[i]);
            }
            Close();
        }

        private void button2_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void FormImportLinks_Resize(object sender, EventArgs e)
        {
            richTextBox1.Width = this.Width - 40;
            richTextBox1.Height = this.Height - 120;

            button1.Location = new Point(button1.Location.X, this.Height - 60);
        }
    }
}
