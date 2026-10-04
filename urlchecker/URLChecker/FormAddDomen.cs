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
    public partial class FormAddDomen : Form
    {
        public string domen;
        public bool go = false;

        public FormAddDomen()
        {
            InitializeComponent();
        }

        private void button1_Click(object sender, EventArgs e)
        {
            go = false;
            if (rich.Lines.Length > 0)
            {
                string[] separator = new string[1] { "\n" };
                string[] domens = rich.Text.Split(separator, StringSplitOptions.RemoveEmptyEntries);
                try
                {
                    for (int i = 0; i < domens.Length; i++)
                    {
                        if (!checkBox1.Checked)
                        {
                            string tmp = domens[i].ToLower().Replace("http://", "").Replace("https://", "").Replace("www.", "");
                            if (tmp.IndexOf("/") > 0)
                                tmp = tmp.Substring(0, tmp.IndexOf("/"));
                            domen = domen + tmp + "\n";
                        }
                        else
                        {
                            string tmp = domens[i].ToLower();
                            domen = domen + tmp + "\n";
                        }
                    }
                    domen = domen.Remove(domen.Length - 1, 1);
                    go = true;
                    Close();
                }
                catch (Exception E)
                {
                    MessageBox.Show("Проверьте правильность указания доменов", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    go = false;
                }
            }
            Settings.Default.Save();

            Close();
        }
    }
}
