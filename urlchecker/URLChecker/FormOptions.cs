using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using URLChecker.Properties;

namespace URLChecker
{
    public partial class FormOptions : Form
    {
        public FormOptions()
        {
            InitializeComponent();

        }

        private void button1_Click(object sender, EventArgs e)
        {
            Settings.Default.timeout = numericUpDown1.Value;
            Settings.Default.countThread = numericUpDown2.Value;
            Settings.Default.Save();
            Close();
        }

        private void FormOptions_Shown(object sender, EventArgs e)
        {
            numericUpDown1.Value = Settings.Default.timeout;
            numericUpDown2.Value = Settings.Default.countThread;
            
            foreach (FontFamily item in FontFamily.Families)
                cbFonts.Items.Add(item.Name);

            label9.Font = new Font(cbFonts.Text, Convert.ToInt32(numericUpDown3.Value));
        }

        private void button2_Click(object sender, EventArgs e)
        {
            if (colorDialog1.ShowDialog()== DialogResult.OK)
            {
                button2.BackColor = colorDialog1.Color;
                Settings.Default.colorNoindex = colorDialog1.Color; 
                Settings.Default.Save();

            }
        }

        private void button3_Click(object sender, EventArgs e)
        {
            if (colorDialog1.ShowDialog() == DialogResult.OK)
            {
                button3.BackColor = colorDialog1.Color;
                Settings.Default.colorRobots = colorDialog1.Color;
                Settings.Default.Save();
            }
        }

        private void button4_Click(object sender, EventArgs e)
        {
            if (colorDialog1.ShowDialog() == DialogResult.OK)
            {
                button4.BackColor = colorDialog1.Color;
                Settings.Default.colorWL = colorDialog1.Color;
                Settings.Default.Save();
            }
        }

        private void button5_Click(object sender, EventArgs e)
        {
            if (saveFileDialog1.ShowDialog() == DialogResult.OK)
            {
                tPath.Text = saveFileDialog1.FileName;
                Settings.Default.Save();
            }
        }

        private void FormOptions_FormClosing(object sender, FormClosingEventArgs e)
        {
            Settings.Default.Save();
        }

        private void checkBox2_CheckedChanged(object sender, EventArgs e)
        {
            if (checkBox2.Checked)
            {
                if (tPath.Text.Trim()=="")
                {
                    button5_Click(this, null);
                }
            }
        }

        private void button6_Click(object sender, EventArgs e)
        {
            if (colorDialog1.ShowDialog() == DialogResult.OK)
            {
                button6.BackColor = colorDialog1.Color;
                Settings.Default.colorNoindex = colorDialog1.Color;
                Settings.Default.Save();
           }

        }

        private void cbFonts_SelectedIndexChanged(object sender, EventArgs e)
        {
            label9.Font = new Font(cbFonts.Text, Convert.ToInt32(numericUpDown3.Value));
        }

        private void numericUpDown3_ValueChanged(object sender, EventArgs e)
        {
            label9.Font = new Font(cbFonts.Text, Convert.ToInt32(numericUpDown3.Value));
        }
    }
}
